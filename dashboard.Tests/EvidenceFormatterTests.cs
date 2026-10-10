using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// Turning a model's JSON-path citation into something an operator can read.
/// </summary>
/// <remarks>
/// The shapes here mirror the real evidence sent to the model: a deterministic
/// analysis with scored dimensions and findings, plus top-level run fields.
/// </remarks>
public class EvidenceFormatterTests
{
    private static JsonNode Evidence() => JsonNode.Parse("""
    {
      "runId": "20261010T181440501Z",
      "experiment": "kill-rabbitmq",
      "recovered": true,
      "recoverySeconds": 23.67,
      "divergenceDetected": true,
      "writeDuringOutage": { "Accepted": false, "StatusCode": 500 },
      "deterministicAnalysis": {
        "ResilienceScore": 63,
        "Confidence": "high",
        "Dimensions": [
          { "Name": "Blast radius containment", "Weight": 25, "Earned": 25 },
          { "Name": "Graceful degradation", "Weight": 20, "Earned": 0 }
        ],
        "Findings": [
          {
            "Title": "Database and event stream diverge during a broker outage",
            "Severity": "High",
            "Evidence": [ "WriteDuringOutage.StatusCode=500", "DivergenceDetected=true" ]
          }
        ]
      }
    }
    """)!;

    [Fact]
    public void Resolves_the_path_from_the_reported_screenshot()
    {
        // This exact string is what the dashboard was showing a user.
        var result = EvidenceFormatter.Resolve("DeterministicAnalysis.Dimensions[1].Earned", Evidence());

        Assert.Equal("Deterministic analysis → Graceful degradation → Earned: 0", result);
        Assert.DoesNotContain("[1]", result);
    }

    [Fact]
    public void Labels_an_array_element_by_its_title_rather_than_its_index()
    {
        var result = EvidenceFormatter.Resolve("DeterministicAnalysis.Findings[0].Evidence[1]", Evidence());

        Assert.Equal(
            "Deterministic analysis → Database and event stream diverge during a broker outage → Evidence → #2: DivergenceDetected=true",
            result);
    }

    [Fact]
    public void Resolves_a_top_level_field_to_its_value()
    {
        Assert.Equal("Recovery seconds: 23.67", EvidenceFormatter.Resolve("recoverySeconds", Evidence()));
    }

    [Fact]
    public void Matches_properties_regardless_of_casing()
    {
        // Models rarely reproduce our casing exactly.
        Assert.Equal("Divergence detected: true", EvidenceFormatter.Resolve("DIVERGENCEDETECTED", Evidence()));
    }

    [Fact]
    public void Summarises_a_cited_object_instead_of_dumping_it()
    {
        var result = EvidenceFormatter.Resolve("writeDuringOutage", Evidence());

        Assert.Equal("Write during outage: Accepted: false, Status code: 500", result);
    }

    [Fact]
    public void Leaves_a_readable_statement_untouched()
    {
        const string statement = "Workshop latency reached 5034ms during the fault";

        Assert.Equal(statement, EvidenceFormatter.Resolve(statement, Evidence()));
    }

    [Fact]
    public void Returns_an_unresolvable_path_unchanged_rather_than_inventing_a_value()
    {
        // A wrong-but-readable citation is worse than an ugly one.
        const string path = "DeterministicAnalysis.Dimensions[99].Earned";

        Assert.Equal(path, EvidenceFormatter.Resolve(path, Evidence()));
    }

    [Fact]
    public void Returns_the_citation_unchanged_when_there_is_no_evidence()
    {
        Assert.Equal("Some.Path", EvidenceFormatter.Resolve("Some.Path", null));
    }

    [Fact]
    public void Drops_duplicate_citations()
    {
        // The model cites the same field across several claims.
        string[] citations =
        [
            "recoverySeconds",
            "recoverySeconds",
            "RECOVERYSECONDS",
            "divergenceDetected",
        ];

        var resolved = EvidenceFormatter.ResolveAll(citations, Evidence());

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void Skips_blank_citations()
    {
        var resolved = EvidenceFormatter.ResolveAll(["", "   ", "recoverySeconds"], Evidence());

        Assert.Single(resolved);
    }

    [Fact]
    public void Handles_a_null_citation_list()
    {
        Assert.Empty(EvidenceFormatter.ResolveAll(null, Evidence()));
    }

    [Theory]
    [InlineData("RecoverySeconds", "Recovery seconds")]
    [InlineData("runId", "Run id")]
    [InlineData("ResilienceScore", "Resilience score")]
    public void Humanises_identifiers(string input, string expected)
    {
        Assert.Equal(expected, EvidenceFormatter.Humanise(input));
    }

    [Fact]
    public void Resolves_against_a_real_saved_report_when_one_exists()
    {
        // Guards against the formatter only working on the shape above.
        var results = Path.Combine(FindRepoRoot(), "chaos-scripts", "results");
        if (!Directory.Exists(results)) return;

        var report = Directory.EnumerateDirectories(results)
            .Select(d => Path.Combine(d, "report.json"))
            .FirstOrDefault(File.Exists);
        if (report is null) return;

        var evidence = JsonNode.Parse(File.ReadAllText(report).TrimStart('﻿'));

        var resolved = EvidenceFormatter.Resolve("RollbackReason", evidence);

        Assert.StartsWith("Rollback reason: ", resolved);
        Assert.DoesNotContain("RollbackReason", resolved);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "chaos-scripts"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
