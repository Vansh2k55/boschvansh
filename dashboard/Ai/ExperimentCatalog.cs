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

    // Only experiments that shape traffic use these. Null means the experiment
    // takes no delay parameter, and the runner will not pass one.
    string? DelayParameter = null,
    int MinDelayMs = 0,
    int MaxDelayMs = 0,
    int DefaultDelayMs = 0)
{
    public bool SupportsDelay => DelayParameter is not null;
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
                DelayParameter: "-DelayMilliseconds",
                MinDelayMs: 50, MaxDelayMs: 2000, DefaultDelayMs: 400),
        };

    public static bool TryGet(string? name, out ExperimentSpec spec)
    {
        spec = null!;
        return !string.IsNullOrWhiteSpace(name) && All.TryGetValue(name, out spec!);
    }
}
