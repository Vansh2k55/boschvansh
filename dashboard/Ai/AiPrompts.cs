using System.Text.Json.Nodes;

namespace Pitstop.ResilienceDashboard;

/// <summary>
/// Prompts and response schemas for the two things Grok is used for: proposing
/// an experiment, and explaining one that already ran.
/// </summary>
/// <remarks>
/// The model is given extracted facts, never raw logs and never shell access.
/// Both schemas are strict, so the response shape is enforced at generation
/// time rather than hoped for.
/// </remarks>
public static class AiPrompts
{
    public static string RecommendSystemPrompt =>
        """
        You are a chaos engineering advisor for a local sandbox running Pitstop, a
        .NET microservice application.

        You may ONLY recommend one of these experiments, by exact name:

          kill-service   - stops the Workshop API container.
                           target: workshopmanagementapi. duration 5-60 seconds.
          kill-rabbitmq  - stops the RabbitMQ broker.
                           target: rabbitmq. duration 5-120 seconds.
          network-delay  - adds egress latency to the Workshop API, degrading it
                           rather than stopping it.
                           target: workshopmanagementapi. duration 5-60 seconds.

        Rules you must follow:
        - Recommend exactly one experiment, chosen from that list.
        - Use the fixed target for that experiment. You do not choose targets.
        - Keep the duration inside the stated range. Out-of-range values are
          rejected outright, not clamped.
        - Base your choice on the evidence provided. Prefer the experiment that
          would tell the operator something they do not already know: an
          untested dependency, an unmeasured dimension, or a weakness the
          existing findings only partly explain.
        - If the evidence shows a service is already unhealthy, say so in the
          risk justification.

        Separately from the recommendation, you may list failure modes this
        platform CANNOT currently test - slow dependencies, resource exhaustion,
        partial network partitions, timeouts, anything the two experiments above
        do not cover. Put those in `discoveries`. They are research notes: they
        will be shown to the operator as not implemented and not executable, and
        nothing will run them. This is how gaps in coverage get surfaced.

        Rules for discoveries:
        - Each one must cite evidence from what you were given. A discovery with
          no evidence is speculation and will be dropped.
        - Do NOT name kill-service, kill-rabbitmq or network-delay in a discovery.
          Those are
          already executable; if one of them is the right move, recommend it.
        - Return an empty list if the evidence does not support any.

        You are advisory only. A human reviews your recommendation and decides
        whether it runs. Never claim authority to execute anything.
        """;

    public static string ExplainSystemPrompt =>
        """
        You are explaining the result of a chaos experiment on a local sandbox to
        an engineer.

        You are given measurements and deterministic findings that have already
        been computed. Your job is to explain them, not to re-score them.

        Rules:
        - Every claim must trace to a value in the evidence you were given.
        - Cite evidence as a readable statement that quotes the actual value,
          for example "Workshop latency reached 5034ms during the fault" or
          "DivergenceDetected was true". Never answer with a JSON path such as
          DeterministicAnalysis.Dimensions[1].Earned - a path is not an explanation.
        - When you cite evidence, write it as a readable statement that quotes the
          actual value, for example "Workshop latency reached 5034ms during the
          fault" or "DivergenceDetected was true". Do NOT answer with a JSON path
          such as DeterministicAnalysis.Dimensions[1].Earned - a path is not an
          explanation.
        - If the evidence does not support a conclusion, say so plainly and mark
          it as unsupported rather than presenting a guess as a fact.
        - Do not invent metrics, numbers, percentages or timings. If you want to
          cite a number, it must appear in the evidence.
        - Do not contradict the deterministic score or findings. If something
          looks inconsistent, point at it instead of overriding it.
        - Be concrete and short. An engineer is reading this during a demo.
        """;

    /// <summary>Strict schema for a recommendation. additionalProperties defaults to false.</summary>
    public static JsonNode RecommendationSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            // Constrained at the schema level, not just asked for in prose. A
            // small local model will otherwise invent an experiment name, and a
            // structural constraint stops that before it reaches the validator.
            // The validator still checks both - this makes the common case work,
            // it does not replace the safety boundary.
            ["experiment"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Which allowlisted experiment to run.",
                ["enum"] = new JsonArray("kill-service", "kill-rabbitmq", "network-delay"),
            },
            ["target"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The fixed target container for that experiment.",
                ["enum"] = new JsonArray("workshopmanagementapi", "rabbitmq"),
            },
            ["durationSeconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Within the permitted range for the chosen experiment." },
            ["hypothesis"] = Str("What you expect this experiment to reveal."),
            ["expectedImpact"] = Str("What the operator should expect to see happen."),
            ["risk"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("low", "medium", "high") },
            ["riskJustification"] = Str("Why that risk level, given the evidence."),
            ["expectedSignals"] = StrArray("Specific monitoring signals to watch, e.g. 'WorkshopManagement latency rises above 5000ms'."),
            ["successCriteria"] = Str("What would make this experiment a success."),
            ["failureCriteria"] = Str("What would mean the system failed the test."),
            ["preconditions"] = StrArray("What must be true before running this."),
            ["recoveryExpectation"] = Str("What recovery should look like, and roughly how long."),
            ["rollbackStrategy"] = Str("How the experiment is stopped and the system restored."),
            ["reasoning"] = Str("Why this experiment, over the alternative, given the evidence."),
            ["discoveries"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Failure modes worth testing that this platform cannot execute. Research notes, not instructions.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = Str("Short name for the failure mode. Never a registered experiment id."),
                        ["failureMode"] = Str("What would go wrong, concretely."),
                        ["rationale"] = Str("Why the evidence suggests this is worth testing."),
                        ["evidence"] = StrArray("Readable statements quoting the values this rests on."),
                        ["potentialImpact"] = Str("What it would cost if it happened in practice."),
                        ["whyUnmeasured"] = Str("Why current experiments do not cover it."),
                        ["suggestedNextStep"] = Str("What a human would need to build to test it."),
                    },
                    ["required"] = new JsonArray("title", "failureMode", "rationale", "evidence", "potentialImpact", "whyUnmeasured", "suggestedNextStep"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray(
            "experiment", "target", "durationSeconds", "hypothesis", "expectedImpact",
            "risk", "riskJustification", "expectedSignals", "successCriteria",
            "failureCriteria", "preconditions", "recoveryExpectation", "rollbackStrategy", "reasoning", "discoveries"),
        ["additionalProperties"] = false,
    };

    public static JsonNode ExplanationSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["summary"] = Str("Two or three sentences on what this experiment showed."),
            ["rootCause"] = Str("The most likely cause of the observed behaviour, grounded in the evidence."),
            ["failurePropagation"] = Str("How the fault did or did not spread through dependencies."),
            ["expectedVsActual"] = Str("Where observed behaviour matched expectations and where it did not."),
            ["weaknesses"] = StrArray("Resilience weaknesses this run exposed."),
            ["recommendations"] = StrArray("Prioritised, concrete remediation steps."),
            ["evidenceCited"] = StrArray("Readable statements quoting the actual values your claims rest on. Not JSON paths."),
            ["unsupported"] = StrArray("Anything you suspect but the evidence does not establish. Empty if none."),
        },
        ["required"] = new JsonArray(
            "summary", "rootCause", "failurePropagation", "expectedVsActual",
            "weaknesses", "recommendations", "evidenceCited", "unsupported"),
        ["additionalProperties"] = false,
    };

    private static JsonObject Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject StrArray(string description) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = new JsonObject { ["type"] = "string" } };
}

/// <summary>Grok's explanation of a completed run. Nullable for the same reason as AiRecommendation.</summary>
public sealed record AiExplanation
{
    public string? Summary { get; init; }
    public string? RootCause { get; init; }
    public string? FailurePropagation { get; init; }
    public string? ExpectedVsActual { get; init; }
    public string[]? Weaknesses { get; init; }
    public string[]? Recommendations { get; init; }
    public string[]? EvidenceCited { get; init; }
    public string[]? Unsupported { get; init; }
}
