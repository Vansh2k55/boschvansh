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

using Pitstop.ResilienceDashboard;

var builder = WebApplication.CreateBuilder(args);

// Resolved before the container is built so registrations can depend on it.
var repoRoot = FindRepoRoot(builder.Environment.ContentRootPath)
    ?? throw new InvalidOperationException("Could not locate the repository root (no chaos-scripts directory found).");
var chaosScripts = Path.Combine(repoRoot, "chaos-scripts");
var composeFile = Path.Combine(repoRoot, "src", "docker-compose.yml");
var stopFile = Path.Combine(chaosScripts, "STOP");
var resultsDir = Path.Combine(chaosScripts, "results");
var auditLog = Path.Combine(resultsDir, "audit.jsonl");

var aiOptions = AiOptions.Load(builder.Configuration);
builder.Services.AddSingleton(aiOptions);
// Per-request timeouts are enforced inside each provider, so the HttpClient
// timeout is only a backstop well above both.
builder.Services.AddHttpClient<GroqClient>(c => c.Timeout = TimeSpan.FromSeconds(300));
builder.Services.AddHttpClient<OllamaClient>(c => c.Timeout = TimeSpan.FromSeconds(300));
// Mode is persisted server-side: it decides whether a request may leave the
// machine, so it is not something a browser asserts.
builder.Services.AddSingleton(_ => new AiModeStore(Path.Combine(resultsDir, "ai-settings.json")));
builder.Services.AddScoped<AiProviderRouter>();
var app = builder.Build();

// Experiments live in ExperimentCatalog so the runner and the AI-recommendation
// validator can never drift apart on what is permitted.
var allowlist = ExperimentCatalog.All;

// IPv4 explicitly. On Windows "localhost" resolves to ::1 first, and a
// wslrelay listener can hold [::1]:7005 without serving it, which makes
// every probe time out while the app is healthy on IPv4.
var siteBaseUrl = Environment.GetEnvironmentVariable("PITSTOP_SITE_URL") ?? "http://127.0.0.1:7005";

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
    if (!ExperimentCatalog.TryGet(name, out var spec))
        return Results.Json(new { ok = false, blockers = new[] { $"'{name}' is not an allowlisted experiment." } }, jsonOptions);

    var blockers = await CollectBlockersAsync(spec, ct);
    return Results.Json(new { ok = blockers.Count == 0, blockers, spec.Risk, spec.ExpectedBlastRadius, spec.RecoveryPlan }, jsonOptions);
});

// ---- AI advisory layer -----------------------------------------------------
// Grok proposes; this server decides. Nothing here can start an experiment:
// a recommendation that passes validation is still only something to show a
// human, who must approve it through /api/experiments/run as before.

app.MapGet("/api/ai/status", async (AiProviderRouter router, CancellationToken ct) =>
{
    var providers = await router.GetStatusAsync(ct);
    return Results.Json(new
    {
        // Whether a key exists, never the key itself.
        providers,
        mode = router.Mode.ToString(),
        modes = Enum.GetNames<AiMode>(),
        activeProvider = await router.ResolveActiveProviderAsync(ct),
        configured = providers.Any(p => p.Available),
        note = "Deterministic scoring and the kill switch do not depend on any of these.",
    }, jsonOptions);
});

app.MapPost("/api/ai/mode", (ModeRequest request, AiModeStore store) =>
{
    if (!Enum.TryParse<AiMode>(request.Mode, ignoreCase: true, out var mode))
    {
        return Results.BadRequest(new { error = $"Unknown mode. Permitted: {string.Join(", ", Enum.GetNames<AiMode>())}." });
    }
    store.Set(mode);
    AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "ai.mode.changed", mode = mode.ToString() });
    return Results.Ok(new { mode = mode.ToString() });
});

app.MapPost("/api/ai/recommend", async (AiProviderRouter router, CancellationToken ct) =>
{
    var services = await ProbeServicesAsync(ct);
    var modules = await ProbeModulesAsync(ct);
    var assessment = ReadJsonFile(Path.Combine(resultsDir, "analysis", "analysis.json"));
    var graph = ReadJsonFile(Path.Combine(resultsDir, "dependency-map", "graph.json"));

    // Only the extracted facts go to the model.
    var evidence = new JsonObject
    {
        ["liveServices"] = new JsonArray(services.Select(s =>
            (JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["running"] = s.Running,
                ["health"] = s.Health,
            }).ToArray()),
        ["liveModuleProbes"] = new JsonArray(modules.Select(m =>
            (JsonNode)new JsonObject
            {
                ["module"] = m.Name,
                ["statusCode"] = m.StatusCode,
                ["healthy"] = m.Healthy,
                ["latencyMs"] = m.LatencyMs,
            }).ToArray()),
        ["experimentsRunSoFar"] = new JsonArray(ListRuns()
            .Select(r => (JsonNode)JsonSerializer.SerializeToNode(r, jsonOptions)!).ToArray()),
        ["deterministicAssessment"] = assessment?.DeepClone(),
        ["mostDependedOn"] = graph?["mostDependedOn"]?.DeepClone(),
    };

    var result = await router.CompleteAsync<AiRecommendation>(
        AiPrompts.RecommendSystemPrompt,
        "Recommend the single most informative next chaos experiment for this system.\n\n" +
        "Evidence:\n" + evidence.ToJsonString(),
        "chaos_experiment_recommendation",
        AiPrompts.RecommendationSchema(),
        ct);

    if (!result.Ok)
    {
        AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "ai.recommend.failed", error = result.Error, attempts = result.Attempts });
        return Results.Json(new { ok = false, error = result.Error, attempts = result.Attempts }, jsonOptions, statusCode: 503);
    }

    var validation = RecommendationValidator.Validate(result.Value);

    // Discoveries travel beside the recommendation but never into it. They are
    // text: no id, no target, no duration, and no route to /api/experiments/run.
    var discoveries = DiscoveryValidator.Validate(result.Value?.Discoveries);
    AppendAudit(new
    {
        timestampUtc = DateTime.UtcNow,
        action = "ai.recommend",
        provider = result.Provider,
        experiment = result.Value?.Experiment,
        proposedTarget = result.Value?.Target,
        proposedDuration = result.Value?.DurationSeconds,
        allowed = validation.Allowed,
        rejections = validation.Rejections,
        discoveries = discoveries.Surfaced.Count,
    });

    // Preflight is reported alongside, so the operator sees both "is this
    // permitted" and "is the system in a state where it is safe right now".
    object? preflight = null;
    if (validation.Allowed && validation.Spec is { } spec)
    {
        var blockers = await CollectBlockersAsync(spec, ct);
        preflight = new { ok = blockers.Count == 0, blockers };
    }

    return Results.Json(new
    {
        ok = true,
        provider = result.Provider,
        attempts = result.Attempts,
        allowed = validation.Allowed,
        rejections = validation.Rejections,
        recommendation = validation.Recommendation,
        discoveries = discoveries.Surfaced,
        discoveriesRejected = discoveries.Rejected,
        // Always the catalog's target and bounds, never the model's.
        resolved = validation.Spec is null ? null : new
        {
            experiment = validation.Spec.Name,
            target = validation.Spec.Target,
            minDuration = validation.Spec.MinDuration,
            maxDuration = validation.Spec.MaxDuration,
            catalogRisk = validation.Spec.Risk,
            expectedBlastRadius = validation.Spec.ExpectedBlastRadius,
            recoveryPlan = validation.Spec.RecoveryPlan,
        },
        preflight,
    }, jsonOptions);
});

app.MapPost("/api/ai/explain/{runId}", async (string runId, AiProviderRouter router, CancellationToken ct) =>
{
    if (!runId.All(char.IsLetterOrDigit)) return Results.BadRequest(new { error = "Invalid run id." });
    var report = ReadJsonFile(Path.Combine(resultsDir, runId, "report.json"));
    if (report is null) return Results.NotFound(new { error = "No evidence found for that run." });

    var assessment = ReadJsonFile(Path.Combine(resultsDir, "analysis", "analysis.json"));
    var runAnalysis = assessment?["Analyses"]?.AsArray()
        .FirstOrDefault(a => a?["RunId"]?.GetValue<string>() == runId);

    // Samples are trimmed: the model needs the shape of the run, not 84 rows.
    var samples = report["Samples"]?.AsArray();
    var trimmed = new JsonArray((samples ?? []).Take(40)
        .Select(x => x!.DeepClone()).ToArray());

    var evidence = new JsonObject
    {
        ["runId"] = runId,
        ["experiment"] = report["Experiment"]?.DeepClone() ?? "kill-service",
        ["targetService"] = report["TargetService"]?.DeepClone(),
        ["rollbackReason"] = report["RollbackReason"]?.DeepClone(),
        ["recovered"] = report["Recovered"]?.DeepClone(),
        ["recoverySeconds"] = report["RecoverySeconds"]?.DeepClone(),
        ["divergenceDetected"] = report["DivergenceDetected"]?.DeepClone(),
        ["outageWriteRowCommitted"] = report["OutageWriteRowCommitted"]?.DeepClone(),
        ["writeDuringOutage"] = report["WriteDuringOutage"]?.DeepClone(),
        ["deterministicAnalysis"] = runAnalysis?.DeepClone(),
        ["sampleExcerpt"] = trimmed,
    };

    var result = await router.CompleteAsync<AiExplanation>(
        AiPrompts.ExplainSystemPrompt,
        "Explain what this chaos experiment revealed.\n\nEvidence:\n" + evidence.ToJsonString(),
        "chaos_experiment_explanation",
        AiPrompts.ExplanationSchema(),
        ct);

    if (!result.Ok)
    {
        return Results.Json(new { ok = false, error = result.Error, attempts = result.Attempts }, jsonOptions, statusCode: 503);
    }

    AppendAudit(new { timestampUtc = DateTime.UtcNow, action = "ai.explain", runId, provider = result.Provider });

    // Models cite the JSON path they read. Resolve those against the evidence so
    // the operator sees the value, not the path, and drop duplicates.
    var explanation = result.Value! with
    {
        EvidenceCited = [.. EvidenceFormatter.ResolveAll(result.Value.EvidenceCited, evidence)],
    };
    return Results.Json(new { ok = true, provider = result.Provider, attempts = result.Attempts, explanation }, jsonOptions);
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

// Preflight is a real gate, not a label: it is what both the manual and the
// AI-assisted paths check before an operator is offered an approval button.
async Task<List<string>> CollectBlockersAsync(ExperimentSpec spec, CancellationToken ct)
{
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

    return blockers;
}

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
            var response = await http.GetAsync($"{siteBaseUrl}/{module}", ct);
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

record ServiceStatus(string Name, bool Running, string State, string Health);
record ModuleProbe(string Name, int? StatusCode, bool Healthy, double LatencyMs, bool OfflinePage, string? Error);
record RunRequest(string? Experiment, int? DurationSeconds, bool Approved, string? ApprovedBy);
record ModeRequest(string? Mode);

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

// Exposed so integration tests can host the app with a stubbed Grok client.
public partial class Program { }
