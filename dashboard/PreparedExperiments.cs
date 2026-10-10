namespace Pitstop.ResilienceDashboard;

/// <summary>
/// Scripts that exist on disk but are not registered, and cannot run.
/// </summary>
/// <remarks>
/// Adapted from PR #8 (feat/combined-scripts-dashboard), whose design here was
/// right: display metadata only, `canExecute = false`, and never consumed by
/// `RecommendationValidator`, `ExperimentCatalog` or `IExperimentRunner`. The
/// rest of that PR was declined because it let a config flag override the
/// operator's provider mode; this type carries none of that.
///
/// It is the same shape as an AI discovery: something worth showing an operator
/// that has no path to execution. The difference is provenance - a discovery
/// comes from the model, these come from scripts a teammate wrote.
///
/// A script only graduates out of here by being registered in
/// `ExperimentCatalog`, which per `docs/AI_EXPERIMENT_DESIGN.md` requires
/// bounded parameters, a sandbox-ownership check, independent recovery that
/// survives the orchestrator dying, verified cleanup, and tests.
/// </remarks>
public static class PreparedExperiments
{
    public const string NotRegisteredStatus = "PREPARED — NOT REGISTERED — NOT EXECUTABLE";

    public static object[] List(string scriptsDirectory) =>
    [
        Describe("pause-customer-api", "customermanagementapi", scriptsDirectory),
        Describe("pause-vehicle-api", "vehiclemanagementapi", scriptsDirectory),
    ];

    private static object Describe(string name, string target, string directory) => new
    {
        name,
        target,
        status = NotRegisteredStatus,
        // Always false. Nothing reads this to decide whether to run something -
        // the run endpoint consults ExperimentCatalog, which does not contain
        // these - but stating it keeps the UI honest.
        canExecute = false,
        scriptAvailable = File.Exists(Path.Combine(directory, name + ".ps1"))
            && File.Exists(Path.Combine(directory, "PauseExperiment.ps1")),
        fault = "Pauses the target container's processes, so requests hang rather than being refused.",
        blockedBy = "No sandbox-ownership check, and no recovery that survives the orchestrator being killed: a paused container stays paused. See docs/PR_REVIEW_LOG.md.",
    };
}

/// <summary>Validates a run identifier before it is used to build a path.</summary>
/// <remarks>
/// Adapted from PR #8. Run ids are timestamps this tool generates, so anything
/// outside that shape is rejected rather than sanitised - this guards the
/// evidence endpoints against traversal.
/// </remarks>
public static class ReportIds
{
    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= 100
        && System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*\z");
}
