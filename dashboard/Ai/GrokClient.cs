using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pitstop.ResilienceDashboard;

public sealed record GrokResult<T>(bool Ok, T? Value, string? Error)
{
    public static GrokResult<T> Fail(string error) => new(false, default, error);
    public static GrokResult<T> Success(T value) => new(true, value, null);
}

/// <summary>
/// Minimal client for the xAI Grok API.
/// </summary>
/// <remarks>
/// Verified against docs.x.ai: POST https://api.x.ai/v1/chat/completions, bearer
/// auth, and structured output through response_format with a json_schema and
/// strict: true. Default model grok-4.7.
///
/// The key is read from XAI_API_KEY at call time, never cached in a field and
/// never written to a response, a log line or an audit entry. Every failure
/// path returns a GrokResult rather than throwing, because the dashboard and
/// the deterministic analyzer must keep working when this is unavailable.
/// </remarks>
public sealed class GrokClient(HttpClient http)
{
    public const string DefaultModel = "grok-4.7";
    private const string Endpoint = "https://api.x.ai/v1/chat/completions";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"));

    public async Task<GrokResult<T>> CompleteAsync<T>(
        string systemPrompt,
        string userPrompt,
        string schemaName,
        JsonNode jsonSchema,
        CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("XAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return GrokResult<T>.Fail("XAI_API_KEY is not set. Deterministic analysis is unaffected.");
        }

        var body = new JsonObject
        {
            ["model"] = Environment.GetEnvironmentVariable("XAI_MODEL") ?? DefaultModel,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = schemaName,
                    ["schema"] = jsonSchema,
                    ["strict"] = true,
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException)
        {
            return GrokResult<T>.Fail("The Grok request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return GrokResult<T>.Fail($"Could not reach the Grok API: {ex.Message}");
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Surface the status and a short excerpt. The request carried the
                // key, the response does not, but the excerpt stays bounded so a
                // verbose error body cannot flood a log.
                var excerpt = raw.Length > 300 ? raw[..300] + "…" : raw;
                return GrokResult<T>.Fail($"Grok API returned {(int)response.StatusCode}: {excerpt}");
            }

            try
            {
                var content = JsonNode.Parse(raw)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(content))
                {
                    return GrokResult<T>.Fail("Grok returned no content.");
                }

                var value = JsonSerializer.Deserialize<T>(content, ReadOptions);
                return value is null
                    ? GrokResult<T>.Fail("Grok returned content that did not match the expected shape.")
                    : GrokResult<T>.Success(value);
            }
            catch (JsonException ex)
            {
                // strict json_schema should prevent this, but a malformed
                // response must degrade to an error the caller can show, never
                // to an exception that takes a request down.
                return GrokResult<T>.Fail($"Could not parse Grok's response: {ex.Message}");
            }
        }
    }
}
