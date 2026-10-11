using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Pitstop.ResilienceDashboard;

/// <summary>
/// Groq Cloud provider, the preferred source when a key is configured.
/// </summary>
/// <remarks>
/// Verified against console.groq.com: POST https://api.groq.com/openai/v1/chat/completions,
/// bearer auth, OpenAI-compatible body, and JSON-object output.
///
/// The model receives the response contract in its system message and must
/// return one JSON object. The server parses and validates that object against
/// the fixed experiment catalog before an operator can approve anything.
///
/// GROQ_API_KEY is read at call time, never cached in a field, never logged and
/// never returned. Failures come back as results rather than exceptions so a
/// quota error or an outage degrades to the Ollama fallback instead of taking
/// a request down.
/// </remarks>
public sealed class GroqClient(HttpClient http, AiOptions options) : IAiProvider
{
    public string Name => "groq";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY"));

    public Task<ProviderStatus> GetStatusAsync(CancellationToken ct) =>
        // Deliberately does not call the API: a status check should not burn
        // quota, and "is a key present" is the only thing we can know for free.
        Task.FromResult(new ProviderStatus(
            Name,
            IsConfigured,
            options.Groq.Model,
            IsConfigured
                ? "Cloud provider configured."
                : "GROQ_API_KEY is not set, so no request will be sent to Groq."));

    /// <summary>Extracts the upstream error message, if the body is the shape we expect.</summary>
    /// <remarks>
    /// Reads one known field rather than echoing the body. Anything that does
    /// not parse, or is not a plain string, yields nothing - a missing
    /// explanation is better than passing through arbitrary upstream text.
    /// </remarks>
    private static string DescribeUpstreamError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            var error = JsonNode.Parse(raw)?["error"];
            var message = error?["message"]?.GetValue<string>();

            // Groq returns what the model actually produced here. It is model
            // output, not a secret, and without it a truncated generation is
            // indistinguishable from a malformed schema.
            var failed = error?["failed_generation"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(failed))
            {
                var tail = failed.Length > 120 ? "…" + failed[^120..] : failed;
                message = $"{message} Model output ended with: {tail}";
            }
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;

            var trimmed = message.Trim();
            if (trimmed.Length > 200) trimmed = trimmed[..200] + "…";
            return $" Upstream said: {trimmed}";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public async Task<(bool Ok, string? Content, string? Error)> CompleteAsync(
        string systemPrompt, string userPrompt, string schemaName, JsonNode jsonSchema, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Not an error worth retrying - simply nothing to send to.
            return (false, null, "GROQ_API_KEY is not set.");
        }

        // Groq's strict json_schema endpoint rejected the current recommendation
        // contract with HTTP 400 despite a valid schema. json_object reliably
        // constrains the envelope; server-side parsing and allowlist validation
        // remain the authority for every safety-relevant field.
        var contractPrompt = systemPrompt +
            "\n\nReturn exactly one valid JSON object and no markdown. Its required contract is:\n" +
            jsonSchema.ToJsonString();
        var body = new JsonObject
        {
            ["model"] = options.Groq.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = contractPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }),
            // gpt-oss models take max_completion_tokens rather than max_tokens.
            ["max_completion_tokens"] = options.Groq.MaxCompletionTokens,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_object",
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Groq.Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Groq.TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, $"Groq timed out after {options.Groq.TimeoutSeconds}s.");
        }
        catch (HttpRequestException)
        {
            return (false, null, "Could not reach Groq.");
        }

        using (response)
        {
            string raw;
            try
            {
                raw = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, null, $"Groq timed out after {options.Groq.TimeoutSeconds}s while reading its response.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return (false, null, "Could not read Groq's response.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => "Groq rate limit or quota exhausted",
                    HttpStatusCode.Unauthorized => "Groq rejected the key",
                    HttpStatusCode.Forbidden => "Groq denied access",
                    _ => $"Groq returned {(int)response.StatusCode}",
                };
                // Raw error bodies and transport messages are never echoed - they
                // can carry request evidence and are attacker-influenced text.
                // But "Groq returned 400." with no reason is close to useless,
                // and a mistyped model id is the likeliest first failure. So the
                // upstream message is taken from a known field if it parses as
                // one, bounded, and nothing else is returned.
                return (false, null, $"{reason}.{DescribeUpstreamError(raw)}");
            }

            try
            {
                var content = JsonNode.Parse(raw)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(content)
                    ? (false, null, "Groq returned no content.")
                    : (true, content, null);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException
                                      or InvalidOperationException or ArgumentOutOfRangeException)
            {
                return (false, null, "Groq returned an invalid response envelope.");
            }
        }
    }
}
