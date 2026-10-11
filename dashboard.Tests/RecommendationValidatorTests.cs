using Microsoft.Extensions.Configuration;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// The validator is the boundary between model output and anything happening to
/// the sandbox, so these tests are the safety guarantees written down.
/// None of them touch the real API or inject a real fault.
/// </summary>
public class RecommendationValidatorTests
{
    private static AiRecommendation Valid(
        string experiment = "kill-service",
        string? target = "workshopmanagementapi",
        int? duration = 25) => new()
        {
            Experiment = experiment,
            Target = target,
            DurationSeconds = duration,
            Hypothesis = "Killing the Workshop API should not affect Customer or Vehicle.",
            ExpectedImpact = "Workshop module times out; the other two stay healthy.",
            Risk = "medium",
            RiskJustification = "Single container, separate API per module.",
            ExpectedSignals = ["WorkshopManagement latency above 5000ms"],
            SuccessCriteria = "Customer and Vehicle stay healthy throughout.",
            FailureCriteria = "Any unaffected module degrades.",
            Preconditions = ["All three modules healthy"],
            RecoveryExpectation = "Healthy within ~30s of restart.",
            RollbackStrategy = "Duration cap, threshold abort or STOP file; watchdog as backup.",
            Reasoning = "Cheapest experiment that confirms blast-radius containment.",
        };

    [Fact]
    public void Accepts_a_well_formed_recommendation_for_an_allowlisted_experiment()
    {
        var result = RecommendationValidator.Validate(Valid());

        Assert.True(result.Allowed);
        Assert.Empty(result.Rejections);
        Assert.Equal("workshopmanagementapi", result.Spec!.Target);
    }

    [Fact]
    public void Rejects_an_experiment_that_is_not_in_the_catalog()
    {
        var result = RecommendationValidator.Validate(Valid(experiment: "rm -rf /"));

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("not an allowlisted experiment"));
        Assert.Null(result.Spec);
    }

    [Fact]
    public void Rejects_a_target_the_model_tried_to_choose_for_itself()
    {
        // A model proposing a different target is a signal worth surfacing,
        // not something to quietly correct.
        var result = RecommendationValidator.Validate(Valid(target: "sqlserver"));

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("not model-selectable"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(61)]
    [InlineData(9999)]
    public void Rejects_durations_outside_the_catalog_bounds(int duration)
    {
        var result = RecommendationValidator.Validate(Valid(duration: duration));

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("outside the permitted range"));
    }

    [Fact]
    public void Does_not_clamp_an_excessive_duration_into_range()
    {
        // Clamping would execute something other than what the operator read.
        var result = RecommendationValidator.Validate(Valid(duration: 600));

        Assert.False(result.Allowed);
        Assert.Equal(600, result.Recommendation!.DurationSeconds);
    }

    [Fact]
    public void Allows_the_upper_bound_of_the_broker_experiment()
    {
        var recommendation = Valid(experiment: "kill-rabbitmq", target: "rabbitmq", duration: 120);

        var result = RecommendationValidator.Validate(recommendation);

        Assert.True(result.Allowed);
        Assert.Equal("rabbitmq", result.Spec!.Target);
    }

    [Fact]
    public void Rejects_null_output_from_a_failed_or_empty_completion()
    {
        var result = RecommendationValidator.Validate(null);

        Assert.False(result.Allowed);
        Assert.Single(result.Rejections);
    }

    [Fact]
    public void Rejects_a_malformed_response_missing_every_narrative_field()
    {
        // What a truncated or schema-ignoring completion looks like.
        var malformed = new AiRecommendation { Experiment = "kill-service", DurationSeconds = 25 };

        var result = RecommendationValidator.Validate(malformed);

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("hypothesis"));
        Assert.Contains(result.Rejections, r => r.Contains("expected impact"));
        Assert.Contains(result.Rejections, r => r.Contains("rollback strategy"));
        Assert.Contains(result.Rejections, r => r.Contains("monitoring signals"));
    }

    [Fact]
    public void Rejects_a_risk_level_outside_the_permitted_set()
    {
        var result = RecommendationValidator.Validate(Valid() with { Risk = "trivial" });

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("is not one of"));
    }

    [Fact]
    public void Rejects_a_missing_duration_rather_than_substituting_the_default()
    {
        var result = RecommendationValidator.Validate(Valid(duration: null));

        Assert.False(result.Allowed);
        Assert.Contains(result.Rejections, r => r.Contains("No duration was proposed"));
    }

    [Fact]
    public void Reports_every_problem_at_once_rather_than_stopping_at_the_first()
    {
        var recommendation = Valid(target: "sqlserver", duration: 500) with { Risk = "catastrophic" };

        var result = RecommendationValidator.Validate(recommendation);

        Assert.False(result.Allowed);
        Assert.True(result.Rejections.Count >= 3, $"Expected several rejections, got: {string.Join(" | ", result.Rejections)}");
    }
}

public class ExperimentCatalogTests
{
    [Fact]
    public void Contains_only_the_experiments_that_have_been_proven_safe()
    {
        // An entry appearing here that is not in this list means something
        // widened the blast radius without the review that implies.
        Assert.Equal(5, ExperimentCatalog.All.Count);
        Assert.Contains("kill-service", ExperimentCatalog.All.Keys);
        Assert.Contains("kill-rabbitmq", ExperimentCatalog.All.Keys);
        Assert.Contains("network-delay", ExperimentCatalog.All.Keys);
        Assert.Contains("packet-loss", ExperimentCatalog.All.Keys);
        Assert.Contains("kill-database", ExperimentCatalog.All.Keys);
    }

    [Fact]
    public void Every_experiment_has_a_bounded_duration()
    {
        foreach (var spec in ExperimentCatalog.All.Values)
        {
            Assert.True(spec.MinDuration > 0, $"{spec.Name} has no lower bound.");
            Assert.True(spec.MaxDuration >= spec.MinDuration, $"{spec.Name} has an inverted range.");
            Assert.True(spec.MaxDuration <= 120, $"{spec.Name} may run for {spec.MaxDuration}s, which is beyond the agreed blast-radius budget.");
            Assert.InRange(spec.DefaultDuration, spec.MinDuration, spec.MaxDuration);
        }
    }

    [Fact]
    public void Lookup_is_case_insensitive_but_still_rejects_unknown_names()
    {
        Assert.True(ExperimentCatalog.TryGet("KILL-SERVICE", out _));
        Assert.False(ExperimentCatalog.TryGet("kill-sqlserver", out _));
        Assert.False(ExperimentCatalog.TryGet(null, out _));
        Assert.False(ExperimentCatalog.TryGet("  ", out _));
    }
}

public class ProviderConfigurationTests
{
    [Fact]
    public void Groq_reports_configuration_state_without_exposing_the_key()
    {
        var previous = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-value-not-a-real-key");
            Assert.True(GroqClient.IsConfigured);

            Environment.SetEnvironmentVariable("GROQ_API_KEY", "");
            Assert.False(GroqClient.IsConfigured);
        }
        finally { Environment.SetEnvironmentVariable("GROQ_API_KEY", previous); }
    }

    [Fact]
    public void Defaults_to_a_groq_model_that_supports_strict_schema_output()
    {
        // Groq only honours response_format json_schema strict on the gpt-oss
        // family and qwen3.8-27b. A Llama default would accept the request and
        // silently ignore the schema.
        var options = new AiOptions();

        Assert.Contains(options.Groq.Model, new[] { "openai/gpt-oss-20b", "openai/gpt-oss-120b", "qwen/qwen3.8-27b" });
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", options.Groq.Endpoint);
    }

    [Fact]
    public void Ollama_defaults_to_the_documented_local_endpoint()
    {
        var options = new AiOptions();

        Assert.Equal("http://localhost:11434", options.Ollama.BaseUrl);
        Assert.False(string.IsNullOrWhiteSpace(options.Ollama.Model));
    }

    [Fact]
    public void Environment_overrides_the_configured_models()
    {
        var previousModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("OLLAMA_MODEL", "deepseek-r1:8b");
            var options = AiOptions.Load(new ConfigurationBuilder().Build());

            Assert.Equal("deepseek-r1:8b", options.Ollama.Model);
        }
        finally { Environment.SetEnvironmentVariable("OLLAMA_MODEL", previousModel); }
    }
}
