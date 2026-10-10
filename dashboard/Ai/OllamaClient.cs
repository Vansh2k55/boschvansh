using System.Text;
using System.Text.Json.Nodes;

namespace Pitstop.ResilienceDashboard;

/// <summary>
/// Local Ollama provider, used when Groq is unconfigured, rate-limited or down.
/// </summary>
/// <remarks>
/// Verified against a local Ollama 0.40.2: POST {BaseUrl}/api/chat with
/// stream false and `format` set to a RAW JSON schema. That differs from Groq,
/// which wraps the schema in response_format.json_schema - hence the provider
/// abstraction rather than one client with a flag.
///
/// `think: false` is sent because reasoning models (qwen3.5, deepseek-r1) will
/// otherwise emit a thinking block and make the response slower without making
/// it better for a fixed-schema extraction.
///
/// This never downloads a model. If the configured model is not installed the
/// provider reports that and the router skips it, because pulling several GB
/// is the operator's decision, not a fallback path's.
/// </remarks>
public sealed class OllamaClient(HttpClient http, AiOptions options) : IAiProvider
{
    public string Name => "ollama";

    public async Task<ProviderStatus> GetStatusAsync(CancellationToken ct)
    {
        var installed = await ListModelsAsync(ct);
        if (installed is null)
        {
            return new ProviderStatus(Name, false, options.Ollama.Model,
                $"Ollama is not reachable at {options.Ollama.BaseUrl}.");
        }

        // Ollama reports "qwen3.5:4b"; accept a bare "qwen3.5" as a match for it.
        var model = options.Ollama.Model;
        var present = installed.Any(m =>
            string.Equals(m, model, StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));

        return new ProviderStatus(Name, present, model,
            present
                ? "Local fallback ready."
                : $"Ollama is running but '{model}' is not installed. Install it with: ollama pull {model}",
            installed);
    }

    private async Task<List<string>?> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var raw = await http.GetStringAsync($"{options.Ollama.BaseUrl}/api/tags", cts.Token);
            var models = JsonNode.Parse(raw)?["models"]?.AsArray();
            return models is null
                ? []
                : [.. models.Select(m => m?["name"]?.GetValue<string>()).Where(n => n is not null).Select(n => n!)];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unreachable, refused, or not installed. All mean "no local fallback".
            return null;
        }
    }

    public async Task<(bool Ok, string? Content, string? Error)> CompleteAsync(
        string systemPrompt, string userPrompt, string schemaName, JsonNode jsonSchema, CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        if (!status.Available)
        {
            return (false, null, status.Detail);
        }

        var body = new JsonObject
        {
            ["model"] = options.Ollama.Model,
            ["stream"] = false,
            ["think"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }),
            // Ollama takes the schema directly, not wrapped the way Groq does.
            ["format"] = jsonSchema,
            ["options"] = new JsonObject { ["temperature"] = 0 },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Ollama.TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Ollama.BaseUrl}/api/chat")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, $"Ollama timed out after {options.Ollama.TimeoutSeconds}s. Local inference can be slow on CPU.");
        }
        catch (HttpRequestException)
        {
            return (false, null, "Could not reach Ollama.");
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
                return (false, null, $"Ollama timed out after {options.Ollama.TimeoutSeconds}s while reading its response.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return (false, null, "Could not read Ollama's response.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"Ollama returned {(int)response.StatusCode}.");
            }

            try
            {
                var content = JsonNode.Parse(raw)?["message"]?["content"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(content)
                    ? (false, null, "Ollama returned no content.")
                    : (true, content, null);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException
                                      or InvalidOperationException or ArgumentOutOfRangeException)
            {
                return (false, null, "Ollama returned an invalid response envelope.");
            }
        }
    }
}
