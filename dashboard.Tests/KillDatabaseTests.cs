using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

public class KillDatabaseRegistrationTests
{
    private static ExperimentSpec Spec => ExperimentCatalog.All["kill-database"];

    [Fact]
    public void Is_registered_against_the_database_with_bounded_duration()
    {
        Assert.True(ExperimentCatalog.TryGet("kill-database", out var spec));

        Assert.Equal("sqlserver", spec.Target);
        Assert.Equal("kill-database.ps1", spec.Script);
        Assert.Equal("-DowntimeSeconds", spec.DurationParameter);
        Assert.InRange(spec.DefaultDuration, spec.MinDuration, spec.MaxDuration);
    }

    [Fact]
    public void Its_cap_is_the_tightest_in_the_catalog()
    {
        // The blast radius is total, so there is no unaffected module left to
        // abort on. The duration cap is doing that job instead, and must stay
        // no longer than the experiments that have a threshold to fall back on.
        var killDatabase = Spec;
        var others = ExperimentCatalog.All.Values.Where(e => e.Name != "kill-database");

        Assert.All(others, other =>
            Assert.True(killDatabase.MaxDuration <= other.MaxDuration,
                $"kill-database caps at {killDatabase.MaxDuration}s but {other.Name} at {other.MaxDuration}s"));
        Assert.True(killDatabase.MaxDuration <= 30);
    }

    [Fact]
    public void It_is_gated_behind_the_execute_flag()
    {
        // The widest blast radius in the catalog does not run by accident.
        Assert.True(Spec.RequiresExecuteFlag);
    }

    [Fact]
    public void It_is_declared_high_risk()
    {
        Assert.Equal("high", Spec.Risk);
    }

    [Fact]
    public void It_takes_no_intensity_parameter()
    {
        Assert.False(Spec.SupportsIntensity);
        Assert.Null(Spec.IntensityParameter);
    }

    [Fact]
    public void Its_recovery_plan_rules_out_removing_the_container_or_volume()
    {
        // docker rm, or removing sqlserverdata, would discard the database. The
        // plan has to say so, because a future edit reaching for rm is the one
        // change that turns this from a bounded fault into data loss.
        var plan = Spec.RecoveryPlan;

        Assert.Contains("never rm", plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sqlserverdata", plan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Its_recovery_plan_rests_on_a_write_not_on_container_status()
    {
        var plan = Spec.RecoveryPlan;

        Assert.Contains("write", plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not on container status", plan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Five_experiments_are_executable()
    {
        Assert.Equal(5, ExperimentCatalog.All.Count);
        Assert.Equal(
            new[] { "kill-database", "kill-rabbitmq", "kill-service", "network-delay", "packet-loss" },
            ExperimentCatalog.All.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Every_registered_experiment_has_a_fixed_target()
    {
        // The model never chooses a target. If an entry ever shipped without
        // one, a recommendation could aim a fault wherever it liked.
        Assert.All(ExperimentCatalog.All.Values,
            spec => Assert.False(string.IsNullOrWhiteSpace(spec.Target)));
    }

    [Fact]
    public void The_validator_accepts_it_at_its_registered_target()
    {
        var recommendation = new AiRecommendation
        {
            Experiment = "kill-database",
            Target = "sqlserver",
            DurationSeconds = 20,
            Hypothesis = "Losing the shared database stops every workflow, not just one module.",
            ExpectedImpact = "All three module pages fail and writes are refused.",
            Risk = "high",
            RiskJustification = "Widest blast radius, so the cap is 30s and recovery is measured on a write.",
            ExpectedSignals = ["All three module pages fail", "Customer writes refused with 500"],
            SuccessCriteria = "Pages and writes recover after the database restarts.",
            FailureCriteria = "A dependent service stays broken after the database returns.",
            Preconditions = ["All modules healthy", "A baseline write accepted"],
            RecoveryExpectation = "Pages serve and a write is accepted within the recovery budget.",
            RollbackStrategy = "docker start, plus a detached watchdog; the volume is never removed.",
            Reasoning = "Six dependents and never injected - the highest inferred topology risk.",
        };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.True(result.Allowed, string.Join("; ", result.Rejections));
    }

    [Fact]
    public void A_model_cannot_point_it_at_a_different_container()
    {
        var recommendation = new AiRecommendation
        {
            Experiment = "kill-database", Target = "rabbitmq", DurationSeconds = 20,
            Hypothesis = "h", ExpectedImpact = "i", Risk = "high", RiskJustification = "r",
            ExpectedSignals = ["s"], SuccessCriteria = "s", FailureCriteria = "f",
            Preconditions = ["p"], RecoveryExpectation = "r", RollbackStrategy = "r",
        };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("not model-selectable"));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(60)]
    [InlineData(120)]
    public void The_validator_rejects_a_duration_past_the_tight_cap(int seconds)
    {
        // 60s is legal for kill-service. It must not be legal here.
        var recommendation = new AiRecommendation
        {
            Experiment = "kill-database", Target = "sqlserver", DurationSeconds = seconds,
            Hypothesis = "h", ExpectedImpact = "i", Risk = "high", RiskJustification = "r",
            ExpectedSignals = ["s"], SuccessCriteria = "s", FailureCriteria = "f",
            Preconditions = ["p"], RecoveryExpectation = "r", RollbackStrategy = "r",
        };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.False(result.Allowed);
    }
}

public class KillDatabaseEndpointTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static TestApp App() => new(StubAiHandler.For(
        ollama: (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25)))));

    [Fact]
    public async Task Is_offered_with_its_tight_cap_and_no_intensity_control()
    {
        using var app = App();
        using var client = app.CreateClient();

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();

        var db = state!["experiments"]!.AsArray().First(e => e!["name"]!.GetValue<string>() == "kill-database");
        Assert.Equal("sqlserver", db!["target"]!.GetValue<string>());
        Assert.Equal(30, db["maxDuration"]!.GetValue<int>());
        Assert.False(db["supportsIntensity"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(31)]
    [InlineData(60)]
    public async Task Rejects_a_duration_outside_its_bounds_without_running_anything(int seconds)
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "kill-database", durationSeconds = seconds,
            approved = true, approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Runner.Invocations);
    }

    [Fact]
    public async Task Rejects_an_intensity_it_does_not_take()
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "kill-database", durationSeconds = 20, intensity = 20,
            approved = true, approvedBy = "operator",
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
            experiment = "kill-database", durationSeconds = 20, approved = false,
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
            experiment = "kill-database", durationSeconds = 15,
            approved = true, approvedBy = "isolation test",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var invocation = Assert.Single(app.Runner.Invocations);
        Assert.Equal("kill-database", invocation.Experiment);
        Assert.Equal(15, invocation.Duration);
        Assert.Null(invocation.Intensity);
    }
}
