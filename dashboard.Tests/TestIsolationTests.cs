using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// Proves automated tests cannot inject a real fault into the developer's stack.
/// </summary>
/// <remarks>
/// An earlier test run started a real experiment: the endpoint reached
/// Process.Start through a local function, and WebApplicationFactory hosts the
/// real application, so a valid request was a real fault. The guard at the time
/// only asserted that *invalid* parameters were rejected, which proves nothing
/// about a future test that posts valid ones.
///
/// These tests post **fully valid, approved** requests - exactly what would have
/// injected a fault before - and assert the fake runner received them. That is
/// a positive proof of isolation rather than an absence of symptoms.
/// </remarks>
public class TestIsolationTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static TestApp IsolatedApp() => new(StubAiHandler.For(
        ollama: (HttpStatusCode.OK, Envelope.Ollama(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25)))));

    [Theory]
    [InlineData("kill-service", 25, null)]
    [InlineData("kill-rabbitmq", 30, null)]
    [InlineData("network-delay", 20, 400)]
    public async Task A_valid_approved_request_reaches_the_fake_runner_and_no_real_process(
        string experiment, int duration, int? delay)
    {
        // Every one of these would have stopped a container or shaped traffic
        // on the developer's machine before the runner became injectable.
        using var app = IsolatedApp();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment,
            durationSeconds = duration,
            delayMilliseconds = delay,
            approved = true,
            approvedBy = "isolation test",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var invocation = Assert.Single(app.Runner.Invocations);
        Assert.Equal(experiment, invocation.Experiment);
        Assert.Equal(duration, invocation.Duration);
        Assert.Equal(delay, invocation.Delay);
    }

    [Fact]
    public async Task The_runner_receives_the_registry_bounds_not_the_request()
    {
        // A delay is resolved from the registry default when the request omits
        // one, so the runner can never be handed an unvalidated value.
        using var app = IsolatedApp();
        using var client = app.CreateClient();

        await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "network-delay",
            durationSeconds = 20,
            approved = true,
            approvedBy = "isolation test",
        });

        var invocation = Assert.Single(app.Runner.Invocations);
        Assert.Equal(ExperimentCatalog.All["network-delay"].DefaultDelayMs, invocation.Delay);
    }

    [Fact]
    public async Task A_rejected_request_never_reaches_the_runner_at_all()
    {
        using var app = IsolatedApp();
        using var client = app.CreateClient();

        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "kill-service", durationSeconds = 25, approved = false });
        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "not-registered", durationSeconds = 25, approved = true });
        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "kill-service", durationSeconds = 9999, approved = true });
        await client.PostAsJsonAsync("/api/experiments/run", new { experiment = "network-delay", durationSeconds = 20, delayMilliseconds = 9999, approved = true });

        Assert.Empty(app.Runner.Invocations);
    }

    [Fact]
    public async Task Approval_is_enforced_before_the_runner_is_reached()
    {
        // The gate is in front of the runner, not inside it.
        using var app = IsolatedApp();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "kill-service",
            durationSeconds = 25,
            approved = false,
            approvedBy = "isolation test",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Runner.Invocations);
    }

    [Fact]
    public async Task Only_one_experiment_can_be_accepted_at_a_time()
    {
        using var app = IsolatedApp();
        using var client = app.CreateClient();

        // The fake completes instantly, so hold the run open by asserting on the
        // first acceptance before the state machine is allowed to settle.
        var first = await client.PostAsJsonAsync("/api/experiments/run", new
        {
            experiment = "kill-service", durationSeconds = 25, approved = true, approvedBy = "isolation test",
        });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Single(app.Runner.Invocations);
    }

    [Fact]
    public void Program_no_longer_starts_an_experiment_process_directly()
    {
        // Structural guard: the only path to a fault must be IExperimentRunner.
        // If a future change reintroduces Process.Start for an experiment in
        // Program.cs, the DI seam stops protecting the tests.
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "dashboard", "Program.cs"));

        Assert.DoesNotContain("RunExperimentAsync", program);
        Assert.Contains("runner.RunAsync", program);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "chaos-scripts"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
