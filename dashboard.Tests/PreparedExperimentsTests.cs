using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// Prepared scripts are shown, never run. Same boundary as AI discoveries,
/// different provenance.
/// </summary>
public class PreparedExperimentsTests
{
    [Fact]
    public void Nothing_prepared_is_in_the_executable_registry()
    {
        foreach (var prepared in new[] { "pause-customer-api", "pause-vehicle-api" })
        {
            Assert.False(ExperimentCatalog.TryGet(prepared, out _), prepared);
        }
        Assert.Equal(5, ExperimentCatalog.All.Count);
    }

    [Fact]
    public void Every_entry_is_marked_not_executable()
    {
        // Parsed rather than substring-matched: the em dash serialises escaped.
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            PreparedExperiments.List("nonexistent")))!.AsArray();

        Assert.NotEmpty(json);
        foreach (var entry in json)
        {
            Assert.Equal(PreparedExperiments.NotRegisteredStatus, entry!["status"]!.GetValue<string>());
            Assert.False(entry["canExecute"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void Script_availability_reflects_the_filesystem_not_a_hardcoded_value()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(PreparedExperiments.List("definitely-not-a-directory"));

        Assert.Contains("\"scriptAvailable\":false", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("20261010T185113682Z", true)]
    [InlineData("run-123", true)]
    [InlineData("../../../etc/passwd", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Report_ids_reject_anything_that_is_not_a_generated_id(string? id, bool valid)
    {
        Assert.Equal(valid, ReportIds.IsValid(id));
    }
}

public class PreparedExperimentExecutionBoundaryTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static TestApp App() => new(StubAiHandler.For(
        ollama: (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25)))));

    [Fact]
    public async Task Prepared_experiments_are_listed_separately_from_executable_ones()
    {
        using var app = App();
        using var client = app.CreateClient();

        var state = await (await client.GetAsync("/api/state")).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Equal(5, state!["experiments"]!.AsArray().Count);
        Assert.Equal(2, state["preparedExperiments"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("pause-customer-api")]
    [InlineData("pause-vehicle-api")]
    public async Task Running_a_prepared_experiment_is_refused(string name)
    {
        // Being visible in the UI must not make it reachable.
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = name,
            durationSeconds = 15,
            approved = true,
            approvedBy = "operator",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Runner.Invocations);
    }

    [Fact]
    public async Task Listing_them_does_not_change_the_provider_mode()
    {
        // PR #8 was declined because a config flag overrode the operator's mode.
        // Taking this type must not drag that behaviour in with it.
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
        using var app = App();
        using var client = app.CreateClient();

        await client.PostAsJsonAsync("/api/ai/mode", new { mode = "Ollama" });
        await client.GetAsync("/api/state");

        var status = await (await client.GetAsync("/api/ai/status")).Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("Ollama", status!["mode"]!.GetValue<string>());
    }
}
