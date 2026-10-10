using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// Resilience dashboard for the Pitstop chaos sandbox.
//
// Safety boundary, which is the point of this file:
//   - It cannot run an arbitrary command. Experiments come from a fixed
//     allowlist below; nothing from a request reaches a shell.
//   - Parameters are integers validated against per-experiment bounds.
//   - Fault injection requires an explicit approval flag in the request body.
//   - Only one experiment may run at a time.
//   - The kill switch is a file on disk that the PowerShell scripts check. It
//     works whether or not this process is alive, which is why the dashboard
//     writes the file rather than signalling the script directly.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var repoRoot = FindRepoRoot(app.Environment.ContentRootPath)
    ?? throw new InvalidOperationException("Could not locate the repository root (no chaos-scripts directory found).");
var chaosScripts = Path.Combine(repoRoot, "chaos-scripts");
var composeFile = Path.Combine(repoRoot, "src", "docker-compose.yml");
var stopFile = Path.Combine(chaosScripts, "STOP");
var resultsDir = Path.Combine(chaosScripts, "results");
var auditLog = Path.Combine(resultsDir, "audit.jsonl");

// The complete set of things this dashboard is allowed to do to the system.
var allowlist = new Dictionary<string, ExperimentSpec>(StringComparer.OrdinalIgnoreCase)
{
    ["kill-service"] = new(
        Script: "kill-service.ps1",
        DurationParameter: "-DurationSeconds",
        MinDuration: 5, MaxDuration: 60, DefaultDuration: 25,
        RequiresExecuteFlag: false,
        Target: "workshopmanagementapi",
        ExpectedBlastRadius: "The Workshop module page only. Customer and Vehicle are served by separate APIs and are expected to stay healthy.",
        Risk: "medium",
        RecoveryPlan: "The container is restarted when the duration cap, a safety threshold or the STOP file ends the run. An independent watchdog job restarts it even if the script dies."),
    ["kill-rabbitmq"] = new(
        Script: "kill-rabbitmq.ps1",
        DurationParameter: "-DowntimeSeconds",
        MinDuration: 5, MaxDuration: 120, DefaultDuration: 25,
        RequiresExecuteFlag: true,
        Target: "rabbitmq",
        ExpectedBlastRadius: "No visible impact on any module page - all three read from their own database. The damage is on the write path: publishes fail while the broker is down.",
        Risk: "high",
        RecoveryPlan: "The broker is restarted on duration cap, threshold or STOP file, with a watchdog as backup. Recovery requires the broker to accept connections AND a customer write to succeed."),
};

var runState = new RunState();
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- read-only state ------------------------------------------------------

app.MapGet("/api/state", async (CancellationToken ct) => Results.Json(new
{
    generatedUtc = DateTime.UtcNow,
    services = await ProbeServicesAsync(ct),
    modules = await ProbeModulesAsync(ct),
    graph = ReadJsonFile(Path.Combine(resultsDir, "dependency-map", "graph.json")),
    assessment = ReadJsonFile(Path.Combine(resultsDir, "analysis", "analysis.json")),
    experiments = allowlist.Select(e => new
    {
        name = e.Key,
        target = e.Value.Target,
        risk = e.Value.Risk,
        expectedBlastRadius = e.Value.ExpectedBlastRadius,
        recoveryPlan = e.Value.RecoveryPlan,
        minDuration = e.Value.MinDuration,
        maxDuration = e.Value.MaxDuration,
        defaultDuration = e.Value.DefaultDuration,
    }),
    run = runState.Snapshot(),
    killSwitchEngaged = File.Exists(stopFile),
    runs = ListRuns(),
}, jsonOptions));

app.MapGet("/api/runs/{runId}", (string runId) =>
{
    // Path traversal guard: run ids are timestamps this tool generates.
    if (!runId.All(c => char.IsLetterOrDigit(c))) return Results.BadRequest(new { error = "Invalid run id." });
    var path = Path.Combine(resultsDir, runId, "report.json");
    var report = ReadJsonFile(path);
    return report is null ? Results.NotFound() : Results.Json(report, jsonOptions);
});

// Preflight is a real check, not a label. It is what the UI shows before the
// operator is offered an approval button.
app.MapGet("/api/preflight/{name}", async (string name, CancellationToken ct) =>
{
    if (!allowlist.TryGetValue(name, out var spec))
        return Results.Json(new { ok = false, blockers = new[] { $"'{name}' is not an allowlisted experiment." } }, jsonOptions);

    var blockers = new List<string>();
    if (runState.IsRunning) blockers.Add($"'{runState.Experiment}' is already running. Overlapping experiments are not permitted.");
    if (File.Exists(stopFile)) blockers.Add("The STOP file is present. Remove it before starting a new experiment.");
    if (!File.Exists(composeFile)) blockers.Add("docker-compose.yml not found.");

    var services = await ProbeServicesAsync(ct);
    var target = services.FirstOrDefault(s => string.Equals(s.Name, spec.Target, StringComparison.OrdinalIgnoreCase));
    if (target is null) blockers.Add($"Target container '{spec.Target}' was not found.");
    else if (!target.Running) blockers.Add($"Target container '{spec.Target}' is not running.");

    var modules = await ProbeModulesAsync(ct);
    foreach (var unhealthy in modules.Where(m => !m.Healthy))
        blockers.Add($"{unhealthy.Name} is not healthy, so there is no clean baseline to compare against.");

    return Results.Json(new { ok = blockers.Count == 0, blockers, spec.Risk, spec.ExpectedBlastRadius, spec.RecoveryPlan }, jsonOptions);
});

// ---- the only two state-changing endpoints --------------------------------

app.MapPost("/api/experiments/run", async (RunRequest request, CancellationToken ct) =>
{
    if (!allowlist.TryGetValue(request.Experiment ?? "", out var spec))
        return Results.BadRequest(new { error = "Not an allowlisted experiment." });

    // Operator approval is a hard gate. An AI recommendation never reaches here.
    if (!request.Approved)
        return Results.BadRequest(new { error = "Explicit operator approval is required before any disruptive action." });

    var duration = request.DurationSeconds ?? spec.DefaultDuration;
    if (duration < spec.MinDuration || duration > spec.MaxDuration)
        return Results.BadRequest(new { error = $"Duration must be between {spec.MinDuration} and {spec.MaxDuration} seconds." });

    if (File.Exists(stopFile))
        return Results.BadRequest(new { error = "The STOP file is present. Remove it before starting an experiment." });

    if (!runState.TryStart(request.Experiment!, duration, out var reason))
        return Results.BadRequest(new { error = reason });

    AppendAudit(new
    {
        timestampUtc = DateTime.UtcNow,
        action = "experiment.approved",
        experiment = request.Experiment,
        durationSeconds = duration,
        approvedBy = string.IsNullOrWhiteSpace(request.ApprovedBy) ? "dashboard operator" : request.ApprovedBy,
    });

    _ = Task.Run(() => RunExperimentAsync(spec, duration), CancellationToken.None);
    return Results.Accepted($"/api/state", new { started = true, experiment = request.Experiment, durationSeconds = duration });
});

app.MapPost("/api/experiments/stop", () =>
{
    // Writing the file IS the kill switch. The running script polls for it, so
    // this works even if this process dies immediately afterwards.
    File.WriteAllText(stopFile, $"Engaged from the dashboard at {DateTime.UtcNow:o}\n");
    runState.NoteKillSwitch();
    AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "killswitch.engaged", experiment = runState.Experiment });
    return Results.Ok(new { engaged = true });
});

app.MapPost("/api/experiments/clear-stop", () =>
{
    if (File.Exists(stopFile)) File.Delete(stopFile);
    AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "killswitch.cleared" });
    return Results.Ok(new { engaged = false });
});

app.Run();

// ---- helpers ---------------------------------------------------------------

string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "chaos-scripts"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

JsonNode? ReadJsonFile(string path)
{
    if (!File.Exists(path)) return null;
    try
    {
        // These files are written by PowerShell with a UTF-8 BOM in some hosts.
        var text = File.ReadAllText(path).TrimStart('﻿');
        return JsonNode.Parse(text);
    }
    catch (JsonException) { return null; }
    catch (IOException) { return null; }
}

async Task<List<ServiceStatus>> ProbeServicesAsync(CancellationToken ct)
{
    var statuses = new List<ServiceStatus>();
    // Fixed argument list. Nothing from a request is interpolated here.
    var output = await RunProcessAsync("docker", new[] { "compose", "-f", composeFile, "ps", "--format", "json" }, TimeSpan.FromSeconds(20), ct);
    if (output is null) return statuses;

    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
        if (node is null) continue;
        var name = node["Service"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) continue;
        var state = node["State"]?.GetValue<string>() ?? "unknown";
        var health = node["Health"]?.GetValue<string>();
        statuses.Add(new ServiceStatus(
            Name: name,
            Running: string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
            State: state,
            // An empty Health means the image declares no healthcheck. That is
            // "not measured", not "healthy", and the UI must show it that way.
            Health: string.IsNullOrWhiteSpace(health) ? "no healthcheck" : health));
    }
    return statuses;
}

async Task<List<ModuleProbe>> ProbeModulesAsync(CancellationToken ct)
{
    var modules = new[] { "CustomerManagement", "VehicleManagement", "WorkshopManagement" };
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    var probes = modules.Select(async module =>
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await http.GetAsync($"http://localhost:7005/{module}", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            var offline = body.Contains("module is offline", StringComparison.OrdinalIgnoreCase);
            return new ModuleProbe(module, (int)response.StatusCode, !offline && response.IsSuccessStatusCode,
                Math.Round(sw.Elapsed.TotalMilliseconds, 1), offline, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ModuleProbe(module, null, false, Math.Round(sw.Elapsed.TotalMilliseconds, 1), false, ex.GetType().Name);
        }
    });
    return (await Task.WhenAll(probes)).ToList();
}

List<object> ListRuns()
{
    if (!Directory.Exists(resultsDir)) return new List<object>();
    var rows = new List<(DateTime Written, object Row)>();
    foreach (var dir in Directory.EnumerateDirectories(resultsDir))
    {
        var reportPath = Path.Combine(dir, "report.json");
        if (!File.Exists(reportPath)) continue;
        var report = ReadJsonFile(reportPath);
        if (report is null) continue;
        var written = File.GetLastWriteTimeUtc(reportPath);
        rows.Add((written, new
        {
            runId = report["RunId"]?.GetValue<string>() ?? Path.GetFileName(dir),
            experiment = report["Experiment"]?.GetValue<string>() ?? "kill-service",
            target = report["TargetService"]?.GetValue<string>(),
            rollbackReason = report["RollbackReason"]?.GetValue<string>(),
            recovered = report["Recovered"]?.GetValue<bool>() ?? false,
            recoverySeconds = report["RecoverySeconds"]?.GetValue<double?>(),
            divergenceDetected = report["DivergenceDetected"]?.GetValue<bool?>(),
            requestedDurationSeconds = report["RequestedDurationSeconds"]?.GetValue<int?>(),
            faultStartedUtc = report["FaultStartedUtc"]?.GetValue<string>(),
            lastWriteUtc = written,
        }));
    }
    return rows.OrderByDescending(r => r.Written).Select(r => r.Row).ToList();
}

async Task RunExperimentAsync(ExperimentSpec spec, int duration)
{
    var scriptPath = Path.Combine(chaosScripts, spec.Script);
    var args = new List<string> { "-NoProfile", "-NonInteractive", "-File", scriptPath, spec.DurationParameter, duration.ToString() };
    if (spec.RequiresExecuteFlag) args.Add("-Execute");

    AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "experiment.started", experiment = spec.Script, durationSeconds = duration });

    try
    {
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repoRoot };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) runState.AppendLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) runState.AppendLog(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        runState.Finish(process.ExitCode);
        AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "experiment.finished", experiment = spec.Script, exitCode = process.ExitCode });
    }
    catch (Exception ex)
    {
        runState.AppendLog($"Dashboard failed to run the experiment: {ex.Message}");
        runState.Finish(-1);
        AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "experiment.failed", experiment = spec.Script, error = ex.Message });
    }

    // Refresh the assessment so the dashboard reflects the run that just ended.
    try
    {
        await RunProcessAsync("pwsh", new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(chaosScripts, "analyze.ps1") }, TimeSpan.FromMinutes(2), CancellationToken.None);
    }
    catch (Exception ex) { runState.AppendLog($"Assessment refresh failed: {ex.Message}"); }
}

async Task<string?> RunProcessAsync(string file, string[] args, TimeSpan timeout, CancellationToken ct)
{
    try
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repoRoot };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi);
        if (process is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        return stdout;
    }
    catch (Exception) { return null; }
}

void AppendAudit(object entry)
{
    try
    {
        Directory.CreateDirectory(resultsDir);
        File.AppendAllText(auditLog, JsonSerializer.Serialize(entry, jsonOptions) + Environment.NewLine);
    }
    catch (IOException) { /* an audit write must never take the dashboard down */ }
}

// ---- types -----------------------------------------------------------------

record ExperimentSpec(
    string Script, string DurationParameter, int MinDuration, int MaxDuration, int DefaultDuration,
    bool RequiresExecuteFlag, string Target, string ExpectedBlastRadius, string Risk, string RecoveryPlan);

record ServiceStatus(string Name, bool Running, string State, string Health);
record ModuleProbe(string Name, int? StatusCode, bool Healthy, double LatencyMs, bool OfflinePage, string? Error);
record RunRequest(string? Experiment, int? DurationSeconds, bool Approved, string? ApprovedBy);

sealed class RunState
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _log = new();
    public string? Experiment { get; private set; }
    public bool IsRunning { get; private set; }
    private int _duration;
    private DateTime _startedUtc;
    private DateTime? _finishedUtc;
    private int? _exitCode;
    private bool _killSwitchUsed;

    public bool TryStart(string experiment, int duration, out string reason)
    {
        lock (_gate)
        {
            if (IsRunning) { reason = $"'{Experiment}' is already running."; return false; }
            Experiment = experiment; IsRunning = true; _duration = duration;
            _startedUtc = DateTime.UtcNow; _finishedUtc = null; _exitCode = null; _killSwitchUsed = false;
            _log.Clear();
            reason = ""; return true;
        }
    }

    public void AppendLog(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > 400) _log.TryDequeue(out _);
    }

    public void NoteKillSwitch() { lock (_gate) { _killSwitchUsed = true; } }

    public void Finish(int exitCode)
    {
        lock (_gate) { IsRunning = false; _finishedUtc = DateTime.UtcNow; _exitCode = exitCode; }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                experiment = Experiment,
                isRunning = IsRunning,
                durationSeconds = _duration,
                startedUtc = Experiment is null ? (DateTime?)null : _startedUtc,
                finishedUtc = _finishedUtc,
                exitCode = _exitCode,
                killSwitchUsed = _killSwitchUsed,
                log = _log.ToArray(),
            };
        }
    }
}
