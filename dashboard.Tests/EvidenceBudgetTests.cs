using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// Keeping the prompt bounded as history grows.
/// </summary>
/// <remarks>
/// The first live Groq call failed with 413 because the evidence carried one
/// entry per experiment ever run. Without a bound Groq fails permanently and
/// the router quietly falls back to Ollama, so the cloud provider looks
/// configured and never actually answers.
/// </remarks>
public class EvidenceBudgetTests
{
    private static JsonNode Assessment(int runs)
    {
        var analyses = new JsonArray();
        for (var i = 0; i < runs; i++)
        {
            analyses.Add(new JsonObject
            {
                ["RunId"] = $"run-{i}",
                // Roughly the size of a real per-run entry.
                ["Padding"] = new string('x', 1500),
            });
        }

        return new JsonObject
        {
            ["Experiments"] = new JsonArray(new JsonObject { ["Experiment"] = "kill-service" }),
            ["Comparison"] = new JsonObject { ["WeakestExperiment"] = "kill-rabbitmq" },
            ["ObservedFindings"] = new JsonArray(new JsonObject { ["Title"] = "divergence" }),
            ["InferredTopologyRisks"] = new JsonArray(new JsonObject { ["Service"] = "sqlserver" }),
            ["CoverageGaps"] = new JsonArray(),
            ["Analyses"] = analyses,
        };
    }

    [Fact]
    public void Keeps_the_aggregates_the_model_actually_needs()
    {
        var summary = EvidenceBudget.SummariseAssessment(Assessment(25))!.AsObject();

        Assert.True(summary.ContainsKey("Experiments"));
        Assert.True(summary.ContainsKey("Comparison"));
        Assert.True(summary.ContainsKey("ObservedFindings"));
        Assert.True(summary.ContainsKey("InferredTopologyRisks"));
        Assert.True(summary.ContainsKey("CoverageGaps"));
    }

    [Fact]
    public void Drops_the_per_run_detail_that_grew_without_limit()
    {
        var summary = EvidenceBudget.SummariseAssessment(Assessment(25))!.AsObject();

        Assert.False(summary.ContainsKey("Analyses"));
        Assert.Equal(25, summary["AnalysedRunCount"]!.GetValue<int>());
        Assert.Contains("on disk", summary["PerRunDetailOmitted"]!.GetValue<string>());
    }

    [Fact]
    public void Payload_stops_growing_with_history()
    {
        // The property that matters: adding runs must not enlarge the prompt.
        var ten = EvidenceBudget.SummariseAssessment(Assessment(10))!.ToJsonString().Length;
        var thousand = EvidenceBudget.SummariseAssessment(Assessment(1000))!.ToJsonString().Length;

        Assert.True(thousand - ten < 100,
            $"grew by {thousand - ten} chars between 10 and 1000 runs");
    }

    [Fact]
    public void Summarised_assessment_is_a_fraction_of_the_original()
    {
        var original = Assessment(25).ToJsonString().Length;
        var summarised = EvidenceBudget.SummariseAssessment(Assessment(25))!.ToJsonString().Length;

        Assert.True(summarised < original / 10,
            $"original {original}, summarised {summarised}");
    }

    [Fact]
    public void Caps_the_run_window()
    {
        var runs = Enumerable.Range(0, 50)
            .Select(i => (JsonNode?)new JsonObject { ["runId"] = $"run-{i}" });

        var recent = EvidenceBudget.RecentRuns(runs);

        Assert.Equal(EvidenceBudget.MaxRuns, recent.Count);
        // ListRuns returns newest first, so the window is the newest ones.
        Assert.Equal("run-0", recent[0]!["runId"]!.GetValue<string>());
    }

    [Fact]
    public void Handles_fewer_runs_than_the_cap_and_no_runs_at_all()
    {
        Assert.Equal(2, EvidenceBudget.RecentRuns(
            [new JsonObject { ["runId"] = "a" }, new JsonObject { ["runId"] = "b" }]).Count);
        Assert.Empty(EvidenceBudget.RecentRuns([]));
    }

    [Fact]
    public void Leaves_a_missing_or_unexpected_assessment_alone()
    {
        Assert.Null(EvidenceBudget.SummariseAssessment(null));

        // An array is not the shape we summarise; pass it through untouched
        // rather than guessing.
        var array = new JsonArray(new JsonObject());
        Assert.Equal(array.ToJsonString(), EvidenceBudget.SummariseAssessment(array)!.ToJsonString());
    }
}
