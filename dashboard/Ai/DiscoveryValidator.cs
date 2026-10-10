namespace Pitstop.ResilienceDashboard;

/// <summary>
/// A failure mode the model believes is worth testing but which this platform
/// cannot execute.
/// </summary>
/// <remarks>
/// Every field is nullable: this is deserialized from model output, which may
/// omit anything regardless of the schema. Nothing here carries an experiment
/// id, a target or a duration, because a discovery has no execution path and
/// giving it those fields would invite one.
/// </remarks>
public sealed record DiscoveredRisk
{
    public string? Title { get; init; }
    public string? FailureMode { get; init; }
    public string? Rationale { get; init; }
    public string[]? Evidence { get; init; }
    public string? PotentialImpact { get; init; }
    public string? WhyUnmeasured { get; init; }
    public string? SuggestedNextStep { get; init; }
}

/// <summary>A discovery after the server has stamped it. Deliberately not executable.</summary>
public sealed record SurfacedDiscovery(
    string Status,
    bool Executable,
    string Title,
    string FailureMode,
    string? Rationale,
    IReadOnlyList<string> Evidence,
    string? PotentialImpact,
    string? WhyUnmeasured,
    string? SuggestedNextStep);

public sealed record DiscoveryResult(
    IReadOnlyList<SurfacedDiscovery> Surfaced,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Admits research hypotheses to the UI without admitting them to the registry.
/// </summary>
/// <remarks>
/// The point of discovery is that the model can say "nobody has tested what
/// happens when the database is slow" without that sentence becoming a thing
/// the system will do. So a discovery is text and nothing else.
///
/// The status string is written by the server, never copied from the model. If
/// the model could supply it, it could supply a different one.
///
/// A discovery naming a registered experiment is rejected rather than shown.
/// That combination is either a modelling error or an attempt to launder an
/// idea into something executable, and neither should be rendered next to real
/// experiments where an operator might act on it.
/// </remarks>
public static class DiscoveryValidator
{
    public const string NotExecutableStatus = "DISCOVERED — NOT IMPLEMENTED — NOT EXECUTABLE";

    /// <summary>Bounded so a model cannot flood the panel and bury the real recommendation.</summary>
    private const int MaxSurfaced = 5;

    public static DiscoveryResult Validate(IEnumerable<DiscoveredRisk>? discoveries)
    {
        var surfaced = new List<SurfacedDiscovery>();
        var rejected = new List<string>();

        if (discoveries is null) return new DiscoveryResult(surfaced, rejected);

        foreach (var discovery in discoveries)
        {
            if (surfaced.Count >= MaxSurfaced)
            {
                rejected.Add($"Only the first {MaxSurfaced} discoveries are shown; the rest were dropped.");
                break;
            }

            var title = discovery?.Title?.Trim();
            var failureMode = discovery?.FailureMode?.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                rejected.Add("A discovery with no title was dropped.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(failureMode))
            {
                rejected.Add($"'{title}' was dropped: it describes no failure mode.");
                continue;
            }

            // A discovery must not be able to masquerade as something runnable.
            if (ExperimentCatalog.TryGet(title, out _) || ExperimentCatalog.TryGet(failureMode, out _))
            {
                rejected.Add(
                    $"'{title}' was dropped: it names a registered experiment. " +
                    "A registered experiment belongs in the recommendation, not in discovery.");
                continue;
            }

            var evidence = (discovery!.Evidence ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .ToList();

            if (evidence.Count == 0)
            {
                rejected.Add($"'{title}' was dropped: it cites no evidence, so it is speculation rather than a finding.");
                continue;
            }

            surfaced.Add(new SurfacedDiscovery(
                // Written here, never taken from the model.
                Status: NotExecutableStatus,
                Executable: false,
                Title: title,
                FailureMode: failureMode,
                Rationale: Clean(discovery.Rationale),
                Evidence: evidence,
                PotentialImpact: Clean(discovery.PotentialImpact),
                WhyUnmeasured: Clean(discovery.WhyUnmeasured),
                SuggestedNextStep: Clean(discovery.SuggestedNextStep)));
        }

        return new DiscoveryResult(surfaced, rejected);
    }

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
