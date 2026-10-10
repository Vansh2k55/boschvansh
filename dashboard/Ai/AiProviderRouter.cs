using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pitstop.ResilienceDashboard;

/// <summary>
/// Picks a provider and degrades through them: Groq first when a key is
/// configured, then local Ollama, then nothing.
/// </summary>
/// <remarks>
/// "Then nothing" is a real outcome, not a bug. If no provider answers, the
/// caller gets a failure and the dashboard keeps running on its deterministic
/// assessment. A provider error is never treated as permission to do anything:
/// no recommendation means no recommendation to approve.
///
/// Every attempt is recorded so the UI can show what was tried and why it moved
/// on, and so the provider that actually answered is always attributable.
/// </remarks>
public sealed class AiProviderRouter(GroqClient groq, OllamaClient ollama, AiModeStore modeStore)
{
    public AiMode Mode => modeStore.Current;

    /// <summary>
    /// The providers this mode permits, in order. This is the only place that
    /// decides whether a request may leave the machine.
    /// </summary>
    /// <remarks>
    /// Ollama-only must never produce a Groq request and Groq-only must never
    /// silently fall back, because an operator who picked a mode is making a
    /// statement about where their evidence is allowed to go.
    /// </remarks>
    private (List<IAiProvider> Providers, List<ProviderAttempt> Skipped) PlanFor(AiMode mode)
    {
        var providers = new List<IAiProvider>();
        var skipped = new List<ProviderAttempt>();

        switch (mode)
        {
            case AiMode.Ollama:
                providers.Add(ollama);
                skipped.Add(new ProviderAttempt(groq.Name, false, "Skipped: mode is Ollama-only."));
                break;

            case AiMode.Groq:
                if (GroqClient.IsConfigured) providers.Add(groq);
                else skipped.Add(new ProviderAttempt(groq.Name, false, "GROQ_API_KEY is not set."));
                skipped.Add(new ProviderAttempt(ollama.Name, false, "Skipped: mode is Groq-only, no fallback."));
                break;

            default:
                if (GroqClient.IsConfigured) providers.Add(groq);
                else skipped.Add(new ProviderAttempt(groq.Name, false, "Skipped: GROQ_API_KEY is not set."));
                providers.Add(ollama);
                break;
        }

        return (providers, skipped);
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<IReadOnlyList<ProviderStatus>> GetStatusAsync(CancellationToken ct) =>
        [await groq.GetStatusAsync(ct), await ollama.GetStatusAsync(ct)];

    /// <summary>Which provider would answer right now under the current mode, or null.</summary>
    public async Task<string?> ResolveActiveProviderAsync(CancellationToken ct)
    {
        var (providers, _) = PlanFor(modeStore.Current);
        foreach (var provider in providers)
        {
            if (provider is GroqClient) return groq.Name;
            var status = await provider.GetStatusAsync(ct);
            if (status.Available) return provider.Name;
        }
        return null;
    }

    public async Task<AiResult<T>> CompleteAsync<T>(
        string systemPrompt, string userPrompt, string schemaName, JsonNode jsonSchema, CancellationToken ct)
    {
        var (ordered, attempts) = PlanFor(modeStore.Current);

        foreach (var provider in ordered)
        {
            // Each provider gets its own copy: Groq nests the schema inside
            // response_format and Ollama takes it raw, and a JsonNode cannot be
            // parented twice.
            var schemaForProvider = jsonSchema.DeepClone();
            var (ok, content, error) = await provider.CompleteAsync(
                systemPrompt, userPrompt, schemaName, schemaForProvider, ct);

            if (!ok)
            {
                attempts.Add(new ProviderAttempt(provider.Name, false, error));
                continue;
            }

            // A provider answering is not the same as it answering usefully.
            // Parse before declaring success so malformed output falls through
            // to the next provider rather than reaching the validator as null.
            try
            {
                var value = JsonSerializer.Deserialize<T>(content!, ReadOptions);
                if (value is null)
                {
                    attempts.Add(new ProviderAttempt(provider.Name, false, "Response did not match the expected shape."));
                    continue;
                }
                attempts.Add(new ProviderAttempt(provider.Name, true, null));
                return AiResult<T>.Success(value, provider.Name, attempts);
            }
            catch (JsonException ex)
            {
                attempts.Add(new ProviderAttempt(provider.Name, false, $"Invalid JSON: {ex.Message}"));
            }
        }

        var summary = string.Join(" | ", attempts.Select(a => $"{a.Provider}: {a.Error}"));
        return AiResult<T>.Fail(
            $"No AI provider could answer in {modeStore.Current} mode. {summary}. " +
            "The deterministic assessment is unaffected.",
            attempts);
    }
}
