using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// Discovery lets the model name a risk the platform cannot test. These tests
/// are about the second half of that sentence.
/// </summary>
public class DiscoveryValidatorTests
{
    private static DiscoveredRisk Risk(
        string title = "Slow database responses",
        string failureMode = "SQL Server responds but with multi-second latency",
        string[]? evidence = null) => new()
        {
            Title = title,
            FailureMode = failureMode,
            Rationale = "Six services depend on sqlserver and none has been tested under latency.",
            Evidence = evidence ?? ["sqlserver has 6 dependents in the dependency map"],
            PotentialImpact = "Every user-facing workflow would slow or time out.",
            WhyUnmeasured = "Both current experiments stop a container outright.",
            SuggestedNextStep = "Register a latency experiment with bounded delay and verified cleanup.",
        };

    [Fact]
    public void Surfaces_a_well_formed_discovery_as_not_executable()
    {
        var result = DiscoveryValidator.Validate([Risk()]);

        var discovery = Assert.Single(result.Surfaced);
        Assert.Equal("DISCOVERED — NOT IMPLEMENTED — NOT EXECUTABLE", discovery.Status);
        Assert.False(discovery.Executable);
    }

    [Fact]
    public void Status_comes_from_the_server_not_the_model()
    {
        // DiscoveredRisk has no status field at all, so a model cannot supply one.
        Assert.DoesNotContain(
            typeof(DiscoveredRisk).GetProperties(),
            p => p.Name.Contains("Status", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Executable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_discovery_carries_no_executable_parameters()
    {
        // No id, target or duration means nothing to hand to the runner.
        var names = typeof(DiscoveredRisk).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("Experiment", names);
        Assert.DoesNotContain("Target", names);
        Assert.DoesNotContain("DurationSeconds", names);
    }

    [Theory]
    [InlineData("kill-service")]
    [InlineData("KILL-RABBITMQ")]
    public void Rejects_a_discovery_that_names_a_registered_experiment(string title)
    {
        // Either a modelling error or an attempt to launder an idea into
        // something runnable. Neither belongs beside real experiments.
        var result = DiscoveryValidator.Validate([Risk(title: title)]);

        Assert.Empty(result.Surfaced);
        Assert.Contains(result.Rejected, r => r.Contains("names a registered experiment"));
    }

    [Fact]
    public void Rejects_a_registered_experiment_hidden_in_the_failure_mode()
    {
        var result = DiscoveryValidator.Validate([Risk(failureMode: "kill-rabbitmq")]);

        Assert.Empty(result.Surfaced);
    }

    [Fact]
    public void Rejects_a_discovery_with_no_evidence()
    {
        var result = DiscoveryValidator.Validate([Risk(evidence: [])]);

        Assert.Empty(result.Surfaced);
        Assert.Contains(result.Rejected, r => r.Contains("speculation"));
    }

    [Fact]
    public void Rejects_a_discovery_whose_evidence_is_only_whitespace()
    {
        var result = DiscoveryValidator.Validate([Risk(evidence: ["", "   "])]);

        Assert.Empty(result.Surfaced);
    }

    [Fact]
    public void Rejects_a_discovery_with_no_title_or_no_failure_mode()
    {
        var result = DiscoveryValidator.Validate([Risk(title: " "), Risk(failureMode: " ")]);

        Assert.Empty(result.Surfaced);
        Assert.Equal(2, result.Rejected.Count);
    }

    [Fact]
    public void Caps_how_many_discoveries_can_be_surfaced()
    {
        // A model must not be able to bury the real recommendation.
        var many = Enumerable.Range(0, 20).Select(i => Risk(title: $"Risk {i}")).ToArray();

        var result = DiscoveryValidator.Validate(many);

        Assert.Equal(5, result.Surfaced.Count);
        Assert.Contains(result.Rejected, r => r.Contains("dropped"));
    }

    [Fact]
    public void Handles_no_discoveries_at_all()
    {
        Assert.Empty(DiscoveryValidator.Validate(null).Surfaced);
        Assert.Empty(DiscoveryValidator.Validate([]).Surfaced);
    }
}

/// <summary>
/// The endpoint-level half: a discovery must have no route to execution, and
/// calling the API directly must not be a way around the UI.
/// </summary>
public class DiscoveryExecutionBoundaryTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public DiscoveryExecutionBoundaryTests() =>
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-placeholder-not-a-real-key");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private const string DiscoveredTitle = "Slow database responses";

    private static string RecommendationWithDiscovery() => JsonSerializer.Serialize(new
    {
        experiment = "kill-service",
        target = "workshopmanagementapi",
        durationSeconds = 25,
        hypothesis = "Workshop failure stays contained.",
        expectedImpact = "Workshop degrades; others hold.",
        risk = "medium",
        riskJustification = "Isolated API.",
        expectedSignals = new[] { "Workshop latency above 5000ms" },
        successCriteria = "Others stay healthy.",
        failureCriteria = "An unaffected module degrades.",
        preconditions = new[] { "All modules healthy" },
        recoveryExpectation = "Healthy within 30s.",
        rollbackStrategy = "Duration cap or STOP file.",
        reasoning = "Cheapest confirmation of containment.",
        discoveries = new[]
        {
            new
            {
                title = DiscoveredTitle,
                failureMode = "SQL Server responds but with multi-second latency",
                rationale = "Six services depend on it and none has been tested under latency.",
                evidence = new[] { "sqlserver has 6 dependents in the dependency map" },
                potentialImpact = "Every workflow slows or times out.",
                whyUnmeasured = "Both experiments stop a container outright.",
                suggestedNextStep = "Register a bounded latency experiment.",
            },
        },
    });

    private static TestApp AppWithDiscovery() => new(StubAiHandler.For(
        groq: (HttpStatusCode.OK, Envelope.Groq(RecommendationWithDiscovery()))));

    [Fact]
    public async Task Discoveries_are_returned_marked_not_executable()
    {
        using var app = AppWithDiscovery();
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        var discovery = Assert.Single(body!["discoveries"]!.AsArray());
        Assert.Equal("DISCOVERED — NOT IMPLEMENTED — NOT EXECUTABLE", discovery!["status"]!.GetValue<string>());
        Assert.False(discovery["executable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_discovery_never_appears_in_the_executable_registry()
    {
        using var app = AppWithDiscovery();
        using var client = app.CreateClient();
        await client.PostAsync("/api/ai/recommend", null);

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();

        Assert.DoesNotContain(state!["experiments"]!.AsArray(),
            e => e!["name"]!.GetValue<string>().Contains("database", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, state["experiments"]!.AsArray().Count);
    }

    [Fact]
    public async Task Running_a_discovered_experiment_by_title_is_refused()
    {
        // The UI offers no button for this. Someone calling the API directly
        // must hit the same wall.
        using var app = AppWithDiscovery();
        using var client = app.CreateClient();
        await client.PostAsync("/api/ai/recommend", null);

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = DiscoveredTitle,
            durationSeconds = 25,
            approved = true,
            approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains("allowlisted", body!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("resource-exhaustion")]
    [InlineData("dependency-timeout")]
    [InlineData("kill-database")]
    public async Task Plausible_future_experiment_names_are_all_refused(string name)
    {
        // Exactly the names a model proposes. None is registered, so none runs,
        // and preflight refuses them too. network-delay was on this list until
        // it was implemented and proven - which is the only way off it.
        using var app = AppWithDiscovery();
        using var client = app.CreateClient();

        var run = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = name,
            durationSeconds = 20,
            approved = true,
            approvedBy = "operator",
        });
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);

        var preflight = await (await client.GetAsync($"/api/preflight/{name}")).Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(preflight!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Surfacing_a_discovery_starts_nothing()
    {
        using var app = AppWithDiscovery();
        using var client = app.CreateClient();

        await client.PostAsync("/api/ai/recommend", null);

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(state!["run"]!["isRunning"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_discovery_colliding_with_a_registered_experiment_is_not_surfaced()
    {
        var payload = RecommendationWithDiscovery().Replace(DiscoveredTitle, "kill-rabbitmq");
        using var app = new TestApp(StubAiHandler.For(groq: (HttpStatusCode.OK, Envelope.Groq(payload))));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Empty(body!["discoveries"]!.AsArray());
        Assert.NotEmpty(body["discoveriesRejected"]!.AsArray());
    }

    [Fact]
    public async Task A_rejected_recommendation_still_surfaces_its_discoveries()
    {
        // The two are independent: a bad experiment proposal does not discard a
        // sound observation about coverage.
        var payload = RecommendationWithDiscovery().Replace("\"target\":\"workshopmanagementapi\"", "\"target\":\"sqlserver\"");
        using var app = new TestApp(StubAiHandler.For(groq: (HttpStatusCode.OK, Envelope.Groq(payload))));
        using var client = app.CreateClient();

        var body = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();

        Assert.False(body!["allowed"]!.GetValue<bool>());
        Assert.Single(body["discoveries"]!.AsArray());
    }
}
