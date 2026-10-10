using System.Text.Json.Nodes;

namespace Pitstop.ResilienceDashboard;

/// <summary>Where the AI layer gets its models from, and how it is configured.</summary>
/// <remarks>
/// Bound from appsettings under "Ai", with environment overrides so a key or a
/// model can be changed for one session without editing a committed file.
/// The Groq API key is the one value that is environment-only - it is never
/// read from configuration files, so it cannot be committed by accident.
/// </remarks>
public sealed class AiOptions
{
    public GroqOptions Groq { get; init; } = new();
    public OllamaOptions Ollama { get; init; } = new();

    public sealed class GroqOptions
    {
        public string Endpoint { get; init; } = "https://api.groq.com/openai/v1/chat/completions";

        /// <summary>
        /// Must be a model that supports response_format json_schema in strict
        /// mode. Per Groq's docs that is the gpt-oss family and qwen3.8-27b -
        /// the Llama models do not, so swapping this for llama-3.3-70b-versatile
        /// would silently lose schema enforcement.
        /// </summary>
        public string Model { get; init; } = "openai/gpt-oss-20b";
        public int TimeoutSeconds { get; init; } = 60;
    }

    public sealed class OllamaOptions
    {
        public string BaseUrl { get; init; } = "http://localhost:11434";

        /// <summary>
        /// Read from configuration rather than assumed. Nothing here downloads a
        /// model; if it is not installed the provider reports that and is skipped.
        /// </summary>
        public string Model { get; init; } = "qwen3.5:4b";

        /// <summary>Local inference on CPU is far slower than a hosted API.</summary>
        public int TimeoutSeconds { get; init; } = 240;
    }

    public static AiOptions Load(IConfiguration configuration)
    {
        var options = configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        return new AiOptions
        {
            Groq = new GroqOptions
            {
                Endpoint = options.Groq.Endpoint,
                Model = Environment.GetEnvironmentVariable("GROQ_MODEL") ?? options.Groq.Model,
                TimeoutSeconds = options.Groq.TimeoutSeconds,
            },
            Ollama = new OllamaOptions
            {
                BaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? options.Ollama.BaseUrl,
                Model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? options.Ollama.Model,
                TimeoutSeconds = options.Ollama.TimeoutSeconds,
            },
        };
    }
}

/// <summary>One attempt against one provider, kept so the UI can show what was tried.</summary>
public sealed record ProviderAttempt(string Provider, bool Ok, string? Error);

public sealed record AiResult<T>(
    bool Ok,
    T? Value,
    string? Error,
    string? Provider,
    IReadOnlyList<ProviderAttempt> Attempts)
{
    public static AiResult<T> Success(T value, string provider, IReadOnlyList<ProviderAttempt> attempts) =>
        new(true, value, null, provider, attempts);

    public static AiResult<T> Fail(string error, IReadOnlyList<ProviderAttempt> attempts) =>
        new(false, default, error, null, attempts);
}

/// <summary>Health of one provider, for the status endpoint and the UI.</summary>
public sealed record ProviderStatus(
    string Provider,
    bool Available,
    string Model,
    string Detail,
    IReadOnlyList<string>? InstalledModels = null);

/// <summary>
/// A source of structured completions. Both implementations take the same
/// JSON schema and return the same shape, so the router and the validator do
/// not care which one answered.
/// </summary>
public interface IAiProvider
{
    string Name { get; }

    Task<ProviderStatus> GetStatusAsync(CancellationToken ct);

    Task<(bool Ok, string? Content, string? Error)> CompleteAsync(
        string systemPrompt, string userPrompt, string schemaName, JsonNode jsonSchema, CancellationToken ct);
}

/// <summary>Which providers the operator has authorised for this installation.</summary>
public enum AiMode
{
    /// <summary>Local only. No request is ever sent to Groq.</summary>
    Ollama,

    /// <summary>Cloud only. Never silently falls back to the local model.</summary>
    Groq,

    /// <summary>Groq first, Ollama if it fails, deterministic if both do.</summary>
    Auto,
}

/// <summary>
/// Persists the selected mode server-side.
/// </summary>
/// <remarks>
/// Server-side on purpose. The mode decides whether a request may leave the
/// machine, so it is not something a browser gets to assert. Only the mode is
/// stored - never the API key, which stays in the environment.
/// </remarks>
public sealed class AiModeStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private AiMode _mode;

    public AiModeStore(string path, AiMode fallback = AiMode.Auto)
    {
        _path = path;
        _mode = fallback;
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path).TrimStart('﻿');
                var stored = JsonNode.Parse(text)?["mode"]?.GetValue<string>();
                if (Enum.TryParse<AiMode>(stored, ignoreCase: true, out var parsed)) _mode = parsed;
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable settings file must not stop the dashboard
            // starting; the fallback mode is a safe default.
        }
    }

    public AiMode Current
    {
        get { lock (_gate) { return _mode; } }
    }

    public void Set(AiMode mode)
    {
        lock (_gate)
        {
            _mode = mode;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, new JsonObject { ["mode"] = mode.ToString() }.ToJsonString());
            }
            catch (IOException)
            {
                // Losing persistence is not worth failing the request over; the
                // mode still applies for this process.
            }
        }
    }
}
