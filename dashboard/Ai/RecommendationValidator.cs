namespace Pitstop.ResilienceDashboard;

/// <summary>What the model is allowed to propose. Nothing outside this shape is read.</summary>
/// <remarks>
/// Every field is nullable on purpose. This is deserialized from model output,
/// which may be malformed, truncated, or may omit anything regardless of what
/// the schema asked for. The validator decides what is acceptable; the type
/// system must not assume the model complied.
/// </remarks>
public sealed record AiRecommendation
{
    public string? Experiment { get; init; }
    public string? Target { get; init; }
    public int? DurationSeconds { get; init; }
    public string? Hypothesis { get; init; }
    public string? ExpectedImpact { get; init; }
    public string? Risk { get; init; }
    public string? RiskJustification { get; init; }
    public string[]? ExpectedSignals { get; init; }
    public string? SuccessCriteria { get; init; }
    public string? FailureCriteria { get; init; }
    public string[]? Preconditions { get; init; }
    public string? RecoveryExpectation { get; init; }
    public string? RollbackStrategy { get; init; }
    public string? Reasoning { get; init; }
}

public sealed record ValidationResult(
    bool Allowed,
    IReadOnlyList<string> Rejections,
    AiRecommendation? Recommendation,
    ExperimentSpec? Spec);

/// <summary>
/// Decides whether a model-proposed experiment is permitted to be offered to
/// the operator at all.
/// </summary>
/// <remarks>
/// This is the boundary between "a language model produced some text" and
/// "this system will consider doing something to the sandbox". It is
/// deliberately paranoid and deliberately dumb: it compares against a fixed
/// catalog and rejects anything that does not match exactly.
///
/// Three rules matter most:
///   - The experiment name must already exist in the catalog. The model cannot
///     invent an experiment.
///   - The target is taken FROM THE CATALOG, never from the model. A proposed
///     target that disagrees is a rejection, not a correction - a model trying
///     to retarget is a signal worth surfacing, not smoothing over.
///   - Out-of-range durations are rejected rather than clamped. Clamping would
///     silently execute something other than what was reviewed.
///
/// Passing validation does NOT authorize execution. It only allows the
/// recommendation to be shown to a human, who must then approve it through the
/// existing endpoint, which validates again independently.
/// </remarks>
public static class RecommendationValidator
{
    private static readonly string[] AllowedRisks = ["low", "medium", "high"];

    public static ValidationResult Validate(AiRecommendation? recommendation)
    {
        var rejections = new List<string>();

        if (recommendation is null)
        {
            return new ValidationResult(false, ["The model returned no usable recommendation."], null, null);
        }

        if (!ExperimentCatalog.TryGet(recommendation.Experiment, out var spec))
        {
            var known = string.Join(", ", ExperimentCatalog.All.Keys);
            rejections.Add($"'{recommendation.Experiment ?? "(none)"}' is not an allowlisted experiment. Permitted: {known}.");
            // Without a known experiment there are no bounds to check anything
            // else against, so stop here rather than guess.
            return new ValidationResult(false, rejections, recommendation, null);
        }

        // The model does not get to choose what gets broken.
        if (!string.IsNullOrWhiteSpace(recommendation.Target) &&
            !string.Equals(recommendation.Target, spec.Target, StringComparison.OrdinalIgnoreCase))
        {
            rejections.Add(
                $"Proposed target '{recommendation.Target}' does not match the fixed target for " +
                $"'{spec.Name}' ('{spec.Target}'). The target is not model-selectable.");
        }

        if (recommendation.DurationSeconds is not { } duration)
        {
            rejections.Add("No duration was proposed.");
        }
        else if (duration < spec.MinDuration || duration > spec.MaxDuration)
        {
            rejections.Add(
                $"Proposed duration {duration}s is outside the permitted range for " +
                $"'{spec.Name}' ({spec.MinDuration}-{spec.MaxDuration}s). Out-of-range durations are rejected, not clamped.");
        }

        if (!string.IsNullOrWhiteSpace(recommendation.Risk) &&
            !AllowedRisks.Contains(recommendation.Risk, StringComparer.OrdinalIgnoreCase))
        {
            rejections.Add($"Risk '{recommendation.Risk}' is not one of: {string.Join(", ", AllowedRisks)}.");
        }

        // A recommendation without a hypothesis and an expected impact is not a
        // recommendation, it is a suggestion to break something. The operator
        // needs enough to judge it.
        foreach (var (value, label) in new[]
        {
            (recommendation.Hypothesis, "hypothesis"),
            (recommendation.ExpectedImpact, "expected impact"),
            (recommendation.RollbackStrategy, "rollback strategy"),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                rejections.Add($"The recommendation has no {label}, so it cannot be reviewed.");
            }
        }

        if (recommendation.ExpectedSignals is null || recommendation.ExpectedSignals.Length == 0)
        {
            rejections.Add("The recommendation names no expected monitoring signals, so there is nothing to check it against.");
        }

        return new ValidationResult(rejections.Count == 0, rejections, recommendation, spec);
    }
}
