using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// network-delay as a registered experiment: its bounds, and the fact that
/// registering it did not loosen anything.
/// </summary>
public class NetworkDelayRegistrationTests
{
    [Fact]
    public void Is_registered_with_a_fixed_target_and_bounded_parameters()
    {
        Assert.True(ExperimentCatalog.TryGet("network-delay", out var spec));

        Assert.Equal("workshopmanagementapi", spec.Target);
        Assert.Equal(5, spec.MinDuration);
        Assert.Equal(60, spec.MaxDuration);
        Assert.True(spec.SupportsDelay);
        Assert.Equal(50, spec.MinDelayMs);
        Assert.Equal(2000, spec.MaxDelayMs);
        Assert.InRange(spec.DefaultDelayMs, spec.MinDelayMs, spec.MaxDelayMs);
    }

    [Fact]
    public void Its_recovery_plan_does_not_rest_on_container_status()
    {
        // The container never stops during this experiment, so "it is running"
        // would be evidence of nothing.
        var spec = ExperimentCatalog.All["network-delay"];

        Assert.Contains("latency", spec.RecoveryPlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not on container status", spec.RecoveryPlan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_the_traffic_shaping_experiment_takes_a_delay()
    {
        Assert.False(ExperimentCatalog.All["kill-service"].SupportsDelay);
        Assert.False(ExperimentCatalog.All["kill-rabbitmq"].SupportsDelay);
        Assert.True(ExperimentCatalog.All["network-delay"].SupportsDelay);
    }

    [Fact]
    public void Registering_it_did_not_widen_the_blast_radius_budget()
    {
        foreach (var spec in ExperimentCatalog.All.Values)
        {
            Assert.True(spec.MaxDuration <= 120, $"{spec.Name} may run for {spec.MaxDuration}s.");
            Assert.True(spec.MinDuration > 0, $"{spec.Name} has no lower bound.");
        }
        Assert.Equal(3, ExperimentCatalog.All.Count);
    }

    [Fact]
    public void The_recommendation_validator_accepts_it_like_any_other_experiment()
    {
        var recommendation = new AiRecommendation
        {
            Experiment = "network-delay",
            Target = "workshopmanagementapi",
            DurationSeconds = 20,
            Hypothesis = "Workshop degrades but stays up; other modules are unaffected.",
            ExpectedImpact = "Workshop page slows markedly, others hold.",
            Risk = "medium",
            RiskJustification = "Shaping is bounded and removal is verified.",
            ExpectedSignals = ["WorkshopManagement latency rises well above baseline while staying 200"],
            SuccessCriteria = "Customer and Vehicle stay at baseline latency.",
            FailureCriteria = "An unaffected module degrades.",
            Preconditions = ["All modules healthy", "No existing traffic-control rule"],
            RecoveryExpectation = "Latency returns to baseline once the qdisc is removed.",
            RollbackStrategy = "Delete the qdisc, verified; detached watchdog as backup.",
            Reasoning = "Graceful degradation is the one failure mode never measured.",
        };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.True(result.Allowed);
        Assert.Equal("workshopmanagementapi", result.Spec!.Target);
    }

    [Fact]
    public void A_model_cannot_retarget_the_delay_experiment()
    {
        var recommendation = new AiRecommendation
        {
            Experiment = "network-delay",
            Target = "sqlserver",
            DurationSeconds = 20,
            Hypothesis = "h", ExpectedImpact = "i", Risk = "medium",
            RiskJustification = "r", ExpectedSignals = ["s"],
            SuccessCriteria = "s", FailureCriteria = "f",
            Preconditions = ["p"], RecoveryExpectation = "r", RollbackStrategy = "r",
        };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("not model-selectable"));
    }
}

public class NetworkDelayEndpointTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static TestApp App() => new(StubAiHandler.For(
        ollama: (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25)))));

    [Fact]
    public async Task Is_offered_to_the_operator_with_its_delay_bounds()
    {
        using var app = App();
        using var client = app.CreateClient();

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();

        var delay = state!["experiments"]!.AsArray()
            .First(e => e!["name"]!.GetValue<string>() == "network-delay");
        Assert.True(delay!["supportsDelay"]!.GetValue<bool>());
        Assert.Equal(2000, delay["maxDelayMs"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(2001)]
    [InlineData(60000)]
    public async Task Rejects_a_delay_outside_the_registered_bounds(int delay)
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "network-delay",
            durationSeconds = 20,
            delayMilliseconds = delay,
            approved = true,
            approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains("Delay must be between", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rejects_a_delay_on_an_experiment_that_takes_none()
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "kill-service",
            durationSeconds = 20,
            delayMilliseconds = 400,
            approved = true,
            approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains("takes no delay parameter", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Still_requires_explicit_approval()
    {
        // Registering a third experiment must not create a third way in.
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "network-delay",
            durationSeconds = 20,
            delayMilliseconds = 400,
            approved = false,
            approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Contains("approval", body!["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_rejected_request_leaves_an_experiment_running()
    {
        // These tests post approved:true deliberately, relying on invalid
        // parameters to be refused. If a future change made one of them valid,
        // the suite would start injecting real faults against the live stack.
        // This catches that.
        using var app = App();
        using var client = app.CreateClient();

        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "network-delay", durationSeconds = 20, delayMilliseconds = 9999, approved = true });
        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "network-delay", durationSeconds = 999, approved = true });
        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "kill-service", durationSeconds = 20, delayMilliseconds = 400, approved = true });
        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "not-an-experiment", durationSeconds = 20, approved = true });

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(state!["run"]!["isRunning"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(61)]
    public async Task Still_enforces_the_duration_bounds(int duration)
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "network-delay",
            durationSeconds = duration,
            approved = true,
            approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
