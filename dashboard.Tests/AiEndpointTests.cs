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
/// Stands in for the xAI API so the full endpoint path can be exercised without
/// a key, a network call, or a real fault injection.
/// </summary>
internal sealed class StubGrokHandler(string modelJsonContent, HttpStatusCode status = HttpStatusCode.OK)
    : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        if (status != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"stubbed failure\"}") };
        }

        // Shape of a real chat/completions response: the structured payload
        // arrives as a JSON string inside choices[0].message.content.
        var envelope = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["content"] = modelJsonContent },
            }),
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed class TestApp(string modelJson, HttpStatusCode status = HttpStatusCode.OK)
    : WebApplicationFactory<Program>
{
    public StubGrokHandler Handler { get; } = new(modelJson, status);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<GrokClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
        });
    }
}

public class AiEndpointTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("XAI_API_KEY");

    public AiEndpointTests() =>
        // A non-empty placeholder so the client proceeds to the stubbed transport.
        // It never leaves the process and reaches no real endpoint.
        Environment.SetEnvironmentVariable("XAI_API_KEY", "test-placeholder-not-a-real-key");

    public void Dispose() => Environment.SetEnvironmentVariable("XAI_API_KEY", _previousKey);

    private static string Recommendation(string experiment, string target, int duration) =>
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

    [Fact]
    public async Task Valid_recommendation_is_allowed_and_resolved_against_the_catalog()
    {
        using var app = new TestApp(Recommendation("kill-service", "workshopmanagementapi", 25));
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/ai/recommend", null);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();

        Assert.True(body!["allowed"]!.GetValue<bool>());
        Assert.Equal("kill-service", body["resolved"]!["experiment"]!.GetValue<string>());
        Assert.Equal("workshopmanagementapi", body["resolved"]!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_uses_the_documented_xai_contract()
    {
        using var app = new TestApp(Recommendation("kill-service", "workshopmanagementapi", 25));
        using var client = app.CreateClient();

        await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal("https://api.x.ai/v1/chat/completions", app.Handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", app.Handler.LastRequest.Headers.Authorization!.Scheme);

        var sent = JsonNode.Parse(app.Handler.LastBody!)!;
        Assert.Equal("json_schema", sent["response_format"]!["type"]!.GetValue<string>());
        Assert.True(sent["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_model_that_tries_to_retarget_is_rejected_and_nothing_runs()
    {
        // The headline safety claim: the model cannot choose what gets broken.
        using var app = new TestApp(Recommendation("kill-service", "sqlserver", 25));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
        Assert.Contains(body["rejections"]!.AsArray(),
            r => r!.GetValue<string>().Contains("not model-selectable"));
    }

    [Fact]
    public async Task An_invented_experiment_is_rejected()
    {
        using var app = new TestApp(Recommendation("drop-database", "sqlserver", 25));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
        Assert.Null(body["resolved"]);
    }

    [Fact]
    public async Task An_excessive_duration_is_rejected_rather_than_clamped()
    {
        using var app = new TestApp(Recommendation("kill-service", "workshopmanagementapi", 3600));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
        Assert.Contains(body["rejections"]!.AsArray(),
            r => r!.GetValue<string>().Contains("outside the permitted range"));
    }

    [Fact]
    public async Task Malformed_model_output_degrades_to_an_error_not_a_crash()
    {
        using var app = new TestApp("this is not json at all");
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/ai/recommend", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(body!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_api_failure_leaves_the_rest_of_the_dashboard_working()
    {
        using var app = new TestApp("{}", HttpStatusCode.TooManyRequests);
        using var client = app.CreateClient();

        var ai = await client.PostAsync("/api/ai/recommend", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ai.StatusCode);

        // The deterministic surface must be unaffected by a Grok outage.
        var state = await client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
    }

    [Fact]
    public async Task Status_reports_configuration_without_returning_the_key()
    {
        using var app = new TestApp("{}");
        using var client = app.CreateClient();

        var raw = await (await client.GetAsync("/api/ai/status")).Content.ReadAsStringAsync();

        Assert.Contains("\"configured\":true", raw);
        Assert.DoesNotContain("test-placeholder-not-a-real-key", raw);
    }

    [Fact]
    public async Task An_ai_recommendation_alone_cannot_start_an_experiment()
    {
        // The recommend endpoint must never be a way around operator approval.
        using var app = new TestApp(Recommendation("kill-service", "workshopmanagementapi", 25));
        using var client = app.CreateClient();

        await client.PostAsync("/api/ai/recommend", null);

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(state!["run"]!["isRunning"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Approval_is_still_required_when_the_experiment_came_from_the_ai()
    {
        using var app = new TestApp(Recommendation("kill-service", "workshopmanagementapi", 25));
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "kill-service",
            durationSeconds = 25,
            approved = false,
            approvedBy = "AI",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains("approval", body!["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }
}
