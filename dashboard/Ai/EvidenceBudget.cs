using System.Text.Json.Nodes;

namespace Pitstop.ResilienceDashboard;

/// <summary>
/// Keeps the evidence sent to a provider bounded as history grows.
/// </summary>
/// <remarks>
/// Found by the first live Groq call, which failed with
/// `413 Request too large for model openai/gpt-oss-20b`. The payload was about
/// 54KB, 45KB of it the deterministic analysis's per-run detail - one entry per
/// experiment ever run, so it grew without limit and Groq would have failed
/// permanently while quietly falling back to Ollama.
///
/// The model does not need every run. It needs the aggregates the analyzer
/// already computes - per-experiment scores, cross-experiment comparison,
/// consolidated findings, topology risk and coverage gaps - plus a window of
/// recent runs. The full per-run detail stays on disk and is what the
/// deterministic score is actually computed from; nothing is discarded, only
/// left out of the prompt.
/// </remarks>
public static class EvidenceBudget
{
    /// <summary>Enough recent runs to show a trend without paying for the archive.</summary>
    public const int MaxRuns = 8;

    /// <summary>
    /// Drops the per-run array from the assessment, keeping the aggregates and
    /// recording how many runs they were computed from.
    /// </summary>
    public static JsonNode? SummariseAssessment(JsonNode? assessment)
    {
        if (assessment is not JsonObject source) return assessment;

        var summary = new JsonObject();
        var analysedRuns = 0;

        foreach (var pair in source)
        {
            if (string.Equals(pair.Key, "Analyses", StringComparison.OrdinalIgnoreCase))
            {
                // Counted, not copied. This is the part that grew unbounded.
                analysedRuns = (pair.Value as JsonArray)?.Count ?? 0;
                continue;
            }
            summary[pair.Key] = pair.Value?.DeepClone();
        }

        summary["AnalysedRunCount"] = analysedRuns;
        summary["PerRunDetailOmitted"] =
            "Per-run detail is on disk in chaos-scripts/results and is what the scores were computed from. " +
            "It is left out of this prompt to keep the request within provider limits.";
        return summary;
    }

    /// <summary>Most recent runs first, capped.</summary>
    public static JsonArray RecentRuns(IEnumerable<JsonNode?> runs, int max = MaxRuns)
    {
        var recent = new JsonArray();
        foreach (var run in runs.Take(max))
        {
            recent.Add(run?.DeepClone());
        }
        return recent;
    }
}
