using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// The three provider modes, and the guarantee that each keeps requests where
/// the operator said they may go.
/// </summary>
/// <remarks>
/// Picking a mode is a statement about where evidence is allowed to travel, so
/// "no Groq request in Ollama-only" and "no silent local fallback in Groq-only"
/// are correctness properties, not preferences.
/// </remarks>
public class ProviderModeTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static void WithCloudKey() =>
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-placeholder-not-a-real-key");

    private static void WithoutCloudKey() =>
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);

    private static readonly (HttpStatusCode, string) GoodGroq =
        (HttpStatusCode.OK, Envelope.Groq(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25)));

    private static readonly (HttpStatusCode, string) GoodOllama =
        (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-rabbitmq", "rabbitmq", 30)));

    private static async Task SetModeAsync(HttpClient client, string mode) =>
        (await client.PostAsJsonAsync("/api/ai/mode", new { mode })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Ollama_only_never_sends_a_request_to_groq()
    {
        // Even with a key present and Groq healthy, the cloud must not be called.
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(groq: GoodGroq, ollama: GoodOllama));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Ollama");

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.DoesNotContain("api.groq.com", app.Handler.CalledHosts);
        Assert.Equal("ollama", body!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Groq_only_never_falls_back_to_ollama()
    {
        // Groq fails and Ollama is perfectly healthy. The request must still fail.
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.TooManyRequests, "quota exhausted"),
            ollama: GoodOllama));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Groq");

        var response = await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains(body!["attempts"]!.AsArray(),
            a => a!["provider"]!.GetValue<string>() == "ollama"
                 && a["error"]!.GetValue<string>().Contains("no fallback"));
    }

    [Fact]
    public async Task Groq_only_without_a_key_fails_rather_than_using_the_local_model()
    {
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(ollama: GoodOllama));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Groq");

        var response = await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains("GROQ_API_KEY", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Auto_prefers_groq_then_falls_back()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.InternalServerError, "boom"),
            ollama: GoodOllama));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Auto");

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Contains("api.groq.com", app.Handler.CalledHosts);
        Assert.Equal("ollama", body!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Auto_without_a_key_skips_groq_and_uses_ollama()
    {
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(ollama: GoodOllama));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Auto");

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.DoesNotContain("api.groq.com", app.Handler.CalledHosts);
        Assert.Equal("ollama", body!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failing_provider_is_tried_once_not_repeatedly()
    {
        // Retries against a paid API multiply cost for no new information.
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.InternalServerError, "boom"),
            ollama: (HttpStatusCode.InternalServerError, "boom")));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Auto");

        await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal(1, app.Handler.CalledHosts.Count(h => h == "api.groq.com"));
    }

    [Fact]
    public async Task Status_reports_the_selected_mode_and_the_available_modes()
    {
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(ollama: GoodOllama));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Ollama");

        var body = await (await client.GetAsync("/api/ai/status")).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Equal("Ollama", body!["mode"]!.GetValue<string>());
        Assert.Equal(3, body["modes"]!.AsArray().Count);
    }

    [Fact]
    public async Task An_unknown_mode_is_rejected()
    {
        using var app = new TestApp(StubAiHandler.For(ollama: GoodOllama));
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ai/mode", new { mode = "skynet" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mode_does_not_weaken_recommendation_validation()
    {
        // Ollama-only is not a loophole: the same validator applies.
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            ollama: (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-service", "sqlserver", 25)))));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Ollama");

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Deterministic_state_survives_every_provider_failing()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(groq: null, ollama: null));
        using var client = app.CreateClient();
        await SetModeAsync(client, "Auto");

        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsync("/api/ai/recommend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/state")).StatusCode);
    }
}

public class AiModeStoreTests
{
    [Fact]
    public void Persists_and_reloads_the_selected_mode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AiModeStore(path).Set(AiMode.Groq);

            Assert.Equal(AiMode.Groq, new AiModeStore(path).Current);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Falls_back_to_auto_when_the_settings_file_is_corrupt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");

            Assert.Equal(AiMode.Auto, new AiModeStore(path).Current);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Defaults_to_auto_when_no_settings_exist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");

        Assert.Equal(AiMode.Auto, new AiModeStore(path).Current);
    }
}
