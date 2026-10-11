namespace Pitstop.ResilienceDashboard;

/// <summary>
/// The complete set of experiments this system is permitted to run, and the
/// only place their targets and bounds are defined.
/// </summary>
/// <remarks>
/// The runner and the AI-recommendation validator both read from here. If they
/// had separate copies, a model could eventually be validated against one set
/// of rules and executed against another.
/// </remarks>
public sealed record ExperimentSpec(
    string Name,
    string Script,
    string DurationParameter,
    int MinDuration,
    int MaxDuration,
    int DefaultDuration,
    bool RequiresExecuteFlag,
    string Target,
    string ExpectedBlastRadius,
    string Risk,
    string RecoveryPlan,

    // A second, experiment-specific knob. network-delay measures it in
    // milliseconds and packet-loss in percent, so it carries its own label and
    // unit rather than each fault type growing its own parameter mechanism.
    // Null means the experiment takes none, and the runner passes none.
    string? IntensityParameter = null,
    string? IntensityLabel = null,
    string? IntensityUnit = null,
    int MinIntensity = 0,
    int MaxIntensity = 0,
    int DefaultIntensity = 0)
{
    public bool SupportsIntensity => IntensityParameter is not null;
}

public static class ExperimentCatalog
{
    public static readonly IReadOnlyDictionary<string, ExperimentSpec> All =
        new Dictionary<string, ExperimentSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["kill-service"] = new(
                Name: "kill-service",
                Script: "kill-service.ps1",
                DurationParameter: "-DurationSeconds",
                MinDuration: 5, MaxDuration: 60, DefaultDuration: 25,
                RequiresExecuteFlag: false,
                Target: "workshopmanagementapi",
                ExpectedBlastRadius: "The Workshop module page only. Customer and Vehicle are served by separate APIs and are expected to stay healthy.",
                Risk: "medium",
                RecoveryPlan: "The container is restarted when the duration cap, a safety threshold or the STOP file ends the run. An independent watchdog job restarts it even if the script dies."),

            ["kill-rabbitmq"] = new(
                Name: "kill-rabbitmq",
                Script: "kill-rabbitmq.ps1",
                DurationParameter: "-DowntimeSeconds",
                MinDuration: 5, MaxDuration: 120, DefaultDuration: 25,
                RequiresExecuteFlag: true,
                Target: "rabbitmq",
                ExpectedBlastRadius: "No visible impact on any module page - all three read from their own database. The damage is on the write path: publishes fail while the broker is down.",
                Risk: "high",
                RecoveryPlan: "The broker is restarted on duration cap, threshold or STOP file, with a watchdog as backup. Recovery requires the broker to accept connections AND a customer write to succeed."),

            // Registered only after cleanup was demonstrated in this environment
            // across normal completion, kill switch, orchestrator crash, error
            // and concurrent rejection. See docs/AI_EXPERIMENT_DESIGN.md.
            ["network-delay"] = new(
                Name: "network-delay",
                Script: "network-delay.ps1",
                DurationParameter: "-DurationSeconds",
                MinDuration: 5, MaxDuration: 60, DefaultDuration: 20,
                RequiresExecuteFlag: false,
                Target: "workshopmanagementapi",
                ExpectedBlastRadius: "The Workshop module stays up but slows markedly - roughly 3s per page at 400ms of added latency. Customer and Vehicle are unaffected. This degrades rather than removes the service.",
                Risk: "medium",
                RecoveryPlan: "The qdisc is deleted and its removal verified. A detached watchdog container owned by Docker removes it even if the orchestrator is killed, and restarting the container clears it as a last resort. Recovery is judged on latency returning to baseline, not on container status - the container never stops.",
                IntensityParameter: "-DelayMilliseconds",
                IntensityLabel: "Delay", IntensityUnit: "ms",
                MinIntensity: 50, MaxIntensity: 2000, DefaultIntensity: 400),

            // Registered after the same cleanup proof as network-delay: the
            // qdisc lifecycle, the detached watchdog, and a verified removal.
            // Distinct from network-delay because loss is partial and erratic
            // rather than uniform - the regime retries exist for.
            ["packet-loss"] = new(
                Name: "packet-loss",
                Script: "packet-loss.ps1",
                DurationParameter: "-DurationSeconds",
                MinDuration: 5, MaxDuration: 60, DefaultDuration: 20,
                RequiresExecuteFlag: false,
                Target: "workshopmanagementapi",
                ExpectedBlastRadius: "The Workshop module stays up but its response times become erratic - most requests succeed, some take seconds while TCP retransmits. Customer and Vehicle are unaffected.",
                Risk: "medium",
                RecoveryPlan: "The qdisc is deleted and its removal verified. A detached watchdog container owned by Docker removes it even if the orchestrator is killed, and restarting the container clears it as a last resort. Recovery is judged on probe success and latency returning to baseline, not on container status - the container never stops.",
                IntensityParameter: "-LossPercent",
                IntensityLabel: "Loss", IntensityUnit: "%",
                MinIntensity: 1, MaxIntensity: 40, DefaultIntensity: 20),

            // The highest inferred topology risk in the dependency map - six
            // dependents - and the only experiment where every module page is
            // expected to fail. Registered after a disposable probe proved the
            // stack self-heals from a stop/start with no dependent restarts.
            ["kill-database"] = new(
                Name: "kill-database",
                Script: "kill-database.ps1",
                DurationParameter: "-DowntimeSeconds",
                // Tighter than every other experiment: the blast radius is total,
                // so there is no unaffected module left to abort on.
                MinDuration: 5, MaxDuration: 30, DefaultDuration: 20,
                RequiresExecuteFlag: true,
                Target: "sqlserver",
                ExpectedBlastRadius: "Every module page fails and writes are refused - measured, not predicted: 3 of 3 modules down and 0 of 2 writes accepted during a 15s run. This is the widest blast radius in the catalog, because reads and writes both end at this database. Unlike kill-rabbitmq, nothing is silently lost; requests are refused outright.",
                Risk: "high",
                RecoveryPlan: "docker stop then start - never rm, so the container and the sqlserverdata volume survive and no data is discarded. A detached watchdog owned by Docker starts the container again even if the orchestrator is killed. Recovery is judged on every module page serving AND a customer write being accepted, not on container status: SQL Server runs well before it answers queries. Measured recovery was 23.5-58.6s after restart, which is why the budget is minutes rather than seconds."),
        };

    public static bool TryGet(string? name, out ExperimentSpec spec)
    {
        spec = null!;
        return !string.IsNullOrWhiteSpace(name) && All.TryGetValue(name, out spec!);
    }
}
