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
/// bearer auth, OpenAI-compatible body, and structured output through
/// response_format json_schema with strict true.
///
/// Strict schema mode is only available on the gpt-oss family and qwen3.8-27b,
/// which is why the default model is openai/gpt-oss-20b rather than a Llama
/// model - those would accept the request and quietly ignore the schema.
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
            var message = JsonNode.Parse(raw)?["error"]?["message"]?.GetValue<string>();
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

        var body = new JsonObject
        {
            ["model"] = options.Groq.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = schemaName,
                    ["strict"] = true,
                    ["schema"] = jsonSchema,
                },
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
