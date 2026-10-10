using System.Diagnostics;

namespace Pitstop.ResilienceDashboard;

/// <summary>
/// The one component that can actually start a fault.
/// </summary>
/// <remarks>
/// This exists as an injectable seam, not for elegance. The endpoint used to
/// call a local function that reached Process.Start directly, which meant an
/// integration test hosting the app with WebApplicationFactory would spawn a
/// real PowerShell experiment against the developer's running stack the moment
/// it posted a valid request. That happened once.
///
/// Rejecting today's invalid parameter sets is not isolation - it only holds
/// until someone writes a test with valid ones. Tests now substitute a fake
/// implementation, so no automated test can reach a Docker command regardless
/// of what it posts.
/// </remarks>
public interface IExperimentRunner
{
    Task RunAsync(ExperimentSpec spec, int durationSeconds, int? delayMilliseconds);
}

/// <summary>Runs a registered experiment's PowerShell script as a child process.</summary>
/// <remarks>
/// Arguments are built from the registry and validated integers only. Nothing
/// from a request is interpolated into a command string, and ArgumentList keeps
/// each value a separate argv entry rather than something a shell re-parses.
/// </remarks>
public sealed class PowerShellExperimentRunner(
    string repoRoot,
    string chaosScripts,
    RunState runState,
    Action<object> audit) : IExperimentRunner
{
    public async Task RunAsync(ExperimentSpec spec, int durationSeconds, int? delayMilliseconds)
    {
        var scriptPath = Path.Combine(chaosScripts, spec.Script);
        var args = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-File", scriptPath,
            spec.DurationParameter, durationSeconds.ToString(),
        };
        if (spec.SupportsDelay && delayMilliseconds is { } ms)
        {
            args.Add(spec.DelayParameter!);
            args.Add(ms.ToString());
        }
        if (spec.RequiresExecuteFlag) args.Add("-Execute");

        audit(new
        {
            timestampUtc = DateTime.UtcNow,
            action = "experiment.started",
            experiment = spec.Script,
            durationSeconds,
            delayMilliseconds,
        });

        try
        {
            var psi = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repoRoot,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)!;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) runState.AppendLog(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) runState.AppendLog(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            runState.Finish(process.ExitCode);
            audit(new
            {
                timestampUtc = DateTime.UtcNow,
                action = "experiment.finished",
                experiment = spec.Script,
                exitCode = process.ExitCode,
            });
        }
        catch (Exception ex)
        {
            // The script owns rollback; a failure to even launch it still has to
            // clear the running flag or the dashboard would refuse every later run.
            runState.AppendLog($"Dashboard failed to run the experiment: {ex.Message}");
            runState.Finish(-1);
            audit(new
            {
                timestampUtc = DateTime.UtcNow,
                action = "experiment.failed",
                experiment = spec.Script,
                error = ex.Message,
            });
        }

        await RefreshAssessmentAsync();
    }

    /// <summary>Re-scores the evidence so the dashboard reflects the run that just ended.</summary>
    private async Task RefreshAssessmentAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repoRoot,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(chaosScripts, "analyze.ps1") })
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process is null) return;

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            // A stale assessment is a cosmetic problem. It must not mask the
            // outcome of the experiment that just ran.
            runState.AppendLog($"Assessment refresh failed: {ex.Message}");
        }
    }
}
