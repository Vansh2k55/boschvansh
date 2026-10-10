using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// Stands in for Groq and Ollama so the whole endpoint path can run without a
/// key, a network call, a local model, or a real fault injection.
/// </summary>
/// <remarks>
/// Routes by host so one handler can serve both providers and the fallback
/// chain can be exercised for real rather than simulated.
/// </remarks>
internal sealed class StubAiHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _respond;
    public List<string> CalledHosts { get; } = [];
    public List<string> Bodies { get; } = [];

    public StubAiHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

    /// <summary>Groq answers with `groq`, Ollama with `ollama`; null means that provider fails.</summary>
    public static StubAiHandler For(
        (HttpStatusCode Status, string Body)? groq = null,
        (HttpStatusCode Status, string Body)? ollama = null,
        IEnumerable<string>? installedModels = null)
    {
        var models = installedModels is null
            ? new JsonArray(new JsonObject { ["name"] = "qwen3.5:4b" })
            : new JsonArray(installedModels.Select(m => (JsonNode)new JsonObject { ["name"] = m }).ToArray());

        return new StubAiHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/api/tags"))
            {
                // DeepClone per call: status is queried more than once, and a
                // JsonNode cannot be given a second parent.
                return ollama is null
                    ? (HttpStatusCode.ServiceUnavailable, "down")
                    : (HttpStatusCode.OK, new JsonObject { ["models"] = models.DeepClone() }.ToJsonString());
            }

            if (url.Contains("api.groq.com"))
            {
                return groq ?? (HttpStatusCode.InternalServerError, "{\"error\":\"stubbed groq failure\"}");
            }

            return ollama ?? (HttpStatusCode.InternalServerError, "{\"error\":\"stubbed ollama failure\"}");
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CalledHosts.Add(request.RequestUri!.Host);
        if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));

        var (status, body) = _respond(request);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed class TestApp(StubAiHandler handler) : WebApplicationFactory<Program>
{
    public StubAiHandler Handler { get; } = handler;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<GroqClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddHttpClient<OllamaClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
        });
}

/// <summary>Builds provider-shaped envelopes around a structured payload.</summary>
internal static class Envelope
{
    public static string Groq(string content) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }),
    }.ToJsonString();

    public static string Ollama(string content) => new JsonObject
    {
        ["message"] = new JsonObject { ["content"] = content },
    }.ToJsonString();

    public static string Recommendation(string experiment, string target, int duration) =>
        JsonSerializer.Serialize(new
        {
            experiment,
            target,
            durationSeconds = duration,
            hypothesis = "Stopping the target should not affect unrelated modules.",
            expectedImpact = "Target module degrades; others hold.",
            risk = "medium",
            riskJustification = "Single container with an isolated API.",
            expectedSignals = new[] { "Workshop latency above 5000ms" },
            successCriteria = "Unaffected modules stay healthy.",
            failureCriteria = "An unaffected module degrades.",
            preconditions = new[] { "All modules healthy" },
            recoveryExpectation = "Healthy within 30s.",
            rollbackStrategy = "Duration cap, threshold abort or STOP file.",
            reasoning = "Confirms blast-radius containment cheaply.",
        });
}

public class AiEndpointTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    private static void WithCloudKey() =>
        // A placeholder so the router attempts Groq. It never leaves the process
        // and reaches only the stubbed transport.
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-placeholder-not-a-real-key");

    private static void WithoutCloudKey() =>
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static readonly (HttpStatusCode, string) GoodGroq =
        (HttpStatusCode.OK, Envelope.Groq(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25)));

    private static readonly (HttpStatusCode, string) GoodOllama =
        (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-rabbitmq", "rabbitmq", 30)));

    // ---- provider selection -------------------------------------------------

    [Fact]
    public async Task Groq_answers_and_is_named_as_the_source()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(groq: GoodGroq, ollama: GoodOllama));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.True(body!["allowed"]!.GetValue<bool>());
        Assert.Equal("groq", body["provider"]!.GetValue<string>());
        Assert.Equal("kill-service", body["resolved"]!["experiment"]!.GetValue<string>());
    }

    [Fact]
    public async Task Groq_request_uses_the_documented_contract()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(groq: GoodGroq));
        using var client = app.CreateClient();

        await client.PostAsync("/api/ai/recommend", null);

        var groqBody = app.Handler.Bodies.First(b => b.Contains("response_format"));
        var sent = JsonNode.Parse(groqBody)!;
        Assert.Equal("json_schema", sent["response_format"]!["type"]!.GetValue<string>());
        Assert.True(sent["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Contains("api.groq.com", app.Handler.CalledHosts);
    }

    [Fact]
    public async Task No_request_reaches_groq_when_no_cloud_key_is_configured()
    {
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(ollama: GoodOllama));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.DoesNotContain("api.groq.com", app.Handler.CalledHosts);
        Assert.Equal("ollama", body!["provider"]!.GetValue<string>());
    }

    // ---- fallback -----------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]   // rate limited / out of quota
    [InlineData(HttpStatusCode.Unauthorized)]      // bad key
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Falls_back_to_ollama_when_groq_fails(HttpStatusCode failure)
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (failure, "{\"error\":\"groq unavailable\"}"),
            ollama: GoodOllama));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.True(body!["allowed"]!.GetValue<bool>());
        Assert.Equal("ollama", body["provider"]!.GetValue<string>());

        // The failed cloud attempt is recorded rather than hidden.
        var attempts = body["attempts"]!.AsArray();
        Assert.Contains(attempts, a => a!["provider"]!.GetValue<string>() == "groq" && !a["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Falls_back_when_groq_returns_unparseable_content()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.OK, Envelope.Groq("not json at all")),
            ollama: GoodOllama));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Equal("ollama", body!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reports_failure_when_ollama_is_unreachable_and_there_is_no_cloud_key()
    {
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(ollama: null));
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(body!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Skips_ollama_when_the_configured_model_is_not_installed()
    {
        // Reachable, but without the model. Nothing here downloads one.
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            ollama: GoodOllama,
            installedModels: ["llama3:8b", "mistral:7b"]));
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains("not installed", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Both_providers_failing_leaves_the_deterministic_dashboard_working()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.TooManyRequests, "quota"),
            ollama: null));
        using var client = app.CreateClient();

        var ai = await client.PostAsync("/api/ai/recommend", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ai.StatusCode);

        var state = await client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
    }

    [Fact]
    public async Task Invalid_structured_output_from_both_providers_is_a_clean_failure()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.OK, Envelope.Groq("{ broken")),
            ollama: (HttpStatusCode.OK, Envelope.Ollama("also not json"))));
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(2, body!["attempts"]!.AsArray().Count);
    }

    // ---- safety, unchanged by the provider swap -----------------------------

    [Fact]
    public async Task A_model_that_tries_to_retarget_is_rejected_and_nothing_runs()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.OK, Envelope.Groq(Envelope.Recommendation("kill-service", "sqlserver", 25)))));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
        Assert.Contains(body["rejections"]!.AsArray(), r => r!.GetValue<string>().Contains("not model-selectable"));
    }

    [Fact]
    public async Task An_invented_experiment_is_rejected()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.OK, Envelope.Groq(Envelope.Recommendation("drop-database", "sqlserver", 25)))));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
        Assert.Null(body["resolved"]);
    }

    [Fact]
    public async Task An_excessive_duration_is_rejected_rather_than_clamped()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.OK, Envelope.Groq(Envelope.Recommendation("kill-service", "workshopmanagementapi", 3600)))));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
        Assert.Contains(body["rejections"]!.AsArray(),
            r => r!.GetValue<string>().Contains("outside the permitted range"));
    }

    [Fact]
    public async Task A_rejected_recommendation_from_the_local_model_is_still_rejected()
    {
        // The safety layer does not care which provider answered.
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            ollama: (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-rabbitmq", "sqlserver", 30)))));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Equal("ollama", body!["provider"]!.GetValue<string>());
        Assert.False(body["allowed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_ai_recommendation_alone_cannot_start_an_experiment()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(groq: GoodGroq));
        using var client = app.CreateClient();

        await client.PostAsync("/api/ai/recommend", null);

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(state!["run"]!["isRunning"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Approval_is_still_required_when_the_experiment_came_from_the_ai()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(groq: GoodGroq));
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "kill-service",
            durationSeconds = 25,
            approved = false,
            approvedBy = "AI",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- status and secrets -------------------------------------------------

    [Fact]
    public async Task Status_reports_both_providers_without_returning_the_key()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(groq: GoodGroq, ollama: GoodOllama));
        using var client = app.CreateClient();

        var raw = await (await client.GetAsync("/api/ai/status")).Content.ReadAsStringAsync();

        Assert.Contains("groq", raw);
        Assert.Contains("ollama", raw);
        Assert.DoesNotContain("test-placeholder-not-a-real-key", raw);
    }

    [Fact]
    public async Task Status_names_ollama_as_active_when_there_is_no_cloud_key()
    {
        WithoutCloudKey();
        using var app = new TestApp(StubAiHandler.For(ollama: GoodOllama));
        using var client = app.CreateClient();

        var body = await (await client.GetAsync("/api/ai/status")).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Equal("ollama", body!["activeProvider"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_error_response_leaks_the_api_key()
    {
        WithCloudKey();
        using var app = new TestApp(StubAiHandler.For(
            groq: (HttpStatusCode.Unauthorized, "{\"error\":\"invalid api key\"}"),
            ollama: null));
        using var client = app.CreateClient();

        var raw = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("test-placeholder-not-a-real-key", raw);
        Assert.Contains("rejected the key", raw);
    }
}
