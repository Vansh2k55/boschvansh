using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

public class PacketLossRegistrationTests
{
    [Fact]
    public void Is_registered_with_a_fixed_target_and_bounded_parameters()
    {
        Assert.True(ExperimentCatalog.TryGet("packet-loss", out var spec));

        Assert.Equal("workshopmanagementapi", spec.Target);
        Assert.Equal(5, spec.MinDuration);
        Assert.Equal(60, spec.MaxDuration);
        Assert.True(spec.SupportsIntensity);
        Assert.Equal("%", spec.IntensityUnit);
        Assert.InRange(spec.DefaultIntensity, spec.MinIntensity, spec.MaxIntensity);
    }

    [Fact]
    public void Loss_is_capped_below_a_total_outage()
    {
        // Above roughly half, this stops being degradation and becomes an
        // outage that kill-service already covers.
        var spec = ExperimentCatalog.All["packet-loss"];

        Assert.True(spec.MaxIntensity <= 40, $"max loss is {spec.MaxIntensity}%");
        Assert.True(spec.MinIntensity >= 1);
    }

    [Fact]
    public void Its_recovery_plan_does_not_rest_on_container_status()
    {
        var spec = ExperimentCatalog.All["packet-loss"];

        Assert.Contains("not on container status", spec.RecoveryPlan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_is_a_distinct_failure_mode_from_network_delay()
    {
        // Same target and mechanism family, different fault: if these ever
        // became the same parameter, one of them stopped earning its place.
        var loss = ExperimentCatalog.All["packet-loss"];
        var delay = ExperimentCatalog.All["network-delay"];

        Assert.Equal(delay.Target, loss.Target);
        Assert.NotEqual(delay.IntensityParameter, loss.IntensityParameter);
        Assert.NotEqual(delay.IntensityUnit, loss.IntensityUnit);
    }

    [Fact]
    public void The_validator_accepts_it_like_any_other_registered_experiment()
    {
        var recommendation = new AiRecommendation
        {
            Experiment = "packet-loss",
            Target = "workshopmanagementapi",
            DurationSeconds = 20,
            Hypothesis = "Partial loss makes Workshop erratic without taking it down.",
            ExpectedImpact = "Most requests succeed; some stall on retransmit.",
            Risk = "medium",
            RiskJustification = "Bounded loss on one container's egress, removal verified.",
            ExpectedSignals = ["WorkshopManagement latency spread widens while staying 200"],
            SuccessCriteria = "Customer and Vehicle stay at baseline.",
            FailureCriteria = "An unaffected module degrades.",
            Preconditions = ["All modules healthy", "No existing traffic-control rule"],
            RecoveryExpectation = "Latency returns to baseline once the qdisc is removed.",
            RollbackStrategy = "Delete the qdisc, verified; detached watchdog as backup.",
            Reasoning = "Partial failure is the one regime never measured.",
        };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void A_model_cannot_retarget_it()
    {
        var recommendation = new AiRecommendation
        {
            Experiment = "packet-loss", Target = "sqlserver", DurationSeconds = 20,
            Hypothesis = "h", ExpectedImpact = "i", Risk = "medium", RiskJustification = "r",
            ExpectedSignals = ["s"], SuccessCriteria = "s", FailureCriteria = "f",
            Preconditions = ["p"], RecoveryExpectation = "r", RollbackStrategy = "r",
        };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("not model-selectable"));
    }
}

public class PacketLossEndpointTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static TestApp App() => new(StubAiHandler.For(
        ollama: (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25)))));

    [Fact]
    public async Task Is_offered_with_its_own_label_and_unit()
    {
        using var app = App();
        using var client = app.CreateClient();

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();

        var loss = state!["experiments"]!.AsArray().First(e => e!["name"]!.GetValue<string>() == "packet-loss");
        Assert.True(loss!["supportsIntensity"]!.GetValue<bool>());
        Assert.Equal("Loss", loss["intensityLabel"]!.GetValue<string>());
        Assert.Equal("%", loss["intensityUnit"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    [InlineData(100)]
    public async Task Rejects_a_loss_percentage_outside_the_registered_bounds(int loss)
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "packet-loss",
            durationSeconds = 20,
            intensity = loss,
            approved = true,
            approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Runner.Invocations);
    }

    [Fact]
    public async Task Still_requires_explicit_approval()
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "packet-loss", durationSeconds = 20, intensity = 20, approved = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Runner.Invocations);
    }

    [Fact]
    public async Task A_valid_approved_request_reaches_only_the_fake_runner()
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "packet-loss", durationSeconds = 20, intensity = 25,
            approved = true, approvedBy = "isolation test",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var invocation = Assert.Single(app.Runner.Invocations);
        Assert.Equal("packet-loss", invocation.Experiment);
        Assert.Equal(25, invocation.Intensity);
    }
}
