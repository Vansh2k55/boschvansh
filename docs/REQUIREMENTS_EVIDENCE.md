# Requirements to evidence

Every hackathon requirement, what satisfies it, and how that was checked. The
status words are used strictly:

| Word | Meaning |
| --- | --- |
| **Verified** | Run, and the output was read. Evidence named below. |
| **Implemented** | Code exists and tests cover it, but it has not been exercised end to end by hand. |
| **Partial** | Some of the requirement is met; the gap is stated. |
| **Unverified** | Code exists; the thing it talks to has never confirmed success. |
| **Not started** | Nothing exists yet. |

Audited at commit `d6a3eb5`, branch `main`, working tree clean, in sync with
`origin/main`. Prior agent summaries were treated as claims and re-checked.

## Application capabilities

### 1. Sample cloud-native application and reproducible environment

**Verified.** 13 Compose services running: `auditlogservice`,
`customermanagementapi`, `invoiceservice`, `logserver`, `mailserver`,
`notificationservice`, `rabbitmq`, `sqlserver`, `timeservice`,
`vehiclemanagementapi`, `webapp`, `workshopmanagementapi`,
`workshopmanagementeventhandler`. All three module pages return HTTP 200 at
11–21ms. SQL Server data is on the named volume `sqlserverdata`, so
stop/start preserves it.

Gap: the README understates the project — it still says the dashboard, AI
planning and additional failure types are "planned work, not implemented
features in this checkout". All three exist. Fixed in the README phase.

Sandbox-only credentials: `src/*/appsettings.Production.json` and the
`sqlserver` service in `src/docker-compose.yml` carry hardcoded passwords.
That is upstream sample-app code for a throwaway local sandbox, documented as
such in `AGENTS.md`. No real credential is present, and no actual credential
value is reproduced in any report. Changing them means touching connection
strings across nine services, so the limitation is documented rather than
half-changed.

### 2. Dependency and data-flow mapping

**Verified.** `chaos-scripts/results/dependency-map/graph.json` (14KB) carries
`nodes`, `edges`, `mostDependedOn` and `overlayNotes`. Inferred Compose
relationships and the hand-curated RabbitMQ event topology
(`dependency-overlay.json`) are distinguished in the data. The dashboard
renders it as an SVG with a blast-radius note.

`sqlserver` has six dependents and is the highest inferred topology risk — the
finding that motivated `kill-database`.

### 3. Monitoring baseline and experiment observability

**Partial.** What exists: the dashboard probes all three modules every 2s with
status, latency, offline-page detection and error text; every experiment writes
`samples.csv` plus `report.json` with per-probe `Phase` (`baseline`, `fault`,
`recovery`), latency and health; `/api/runs/{runId}` serves those samples;
`/api/state` lists 28 recorded runs.

Gap: no time-series or comparative graph. The only chart in the UI is the
dependency map. Addressed in the monitoring-baseline phase, drawn from real
recorded samples, never invented values.

### 4. AI-assisted planning, safe experiments, and recovery

**Implemented and partly verified.** Five executable experiments, each
registered only after its cleanup was proven:

| Experiment | Target | Cap | Distinct failure mode |
| --- | --- | --- | --- |
| `kill-service` | `workshopmanagementapi` | 60s | Process gone, connection refused |
| `kill-rabbitmq` | `rabbitmq` | 120s | Async dependency outage; pages stay up |
| `network-delay` | `workshopmanagementapi` | 60s | Uniform added latency |
| `packet-loss` | `workshopmanagementapi` | 60s | Partial, non-deterministic loss |
| `kill-database` | `sqlserver` | 30s | Synchronous shared dependency; total failure |

Safety, each checked rather than asserted: `ExperimentCatalog` is the only
allowlist; targets come from the catalog and never from the model;
out-of-range values are rejected, not clamped; `IExperimentRunner` is the only
path to a real fault and tests substitute a fake; operator approval is
required; the STOP file refuses a start; detached watchdog containers owned by
Docker survive the orchestrator being killed; recovery is judged on
application behaviour, never container status.

**Evidence:** 193 automated tests passing, 32 recovery-contract assertions
passing, 28 recorded runs across all five experiment types.

Gap: the operator journey has not been walked end to end through the running
dashboard in one pass. Addressed in the integration phase.

### 5. AI findings and resilience assessment

**Verified.** `chaos-scripts/analyze.ps1` regenerated over all 28 runs
produces, per run: a resilience score with its weighted dimensions, a
confidence rating tied to probe count, ranked findings with severity, evidence
and a recommendation, explicitly separated hypotheses, and
`UnmeasuredDimensions` so an unmeasured dimension is never read as a pass. It
also emits cross-experiment `Comparison`, `ObservedFindings`,
`InferredTopologyRisks` and `CoverageGaps`.

No number on that page comes from a model, by design
(`docs/AI_ANALYSIS_DESIGN.md`).

Representative result worth stating plainly: `kill-database` scores **25/100**.
That is not a scoring defect — blast-radius containment genuinely earns nothing
because every module page fails. The system has no graceful degradation path
for the loss of its shared database.

Gap: the generated report lives under `chaos-scripts/results/`, which is
gitignored, and is organised per run. A committed, aggregated findings document
is produced in the deliverables phase.

## Provider status

**Groq: UNVERIFIED end-to-end.** Honest breakdown, and no live call was spent
to produce it:

- *Provider path:* `tests/groq_live_check.ps1` returned HTTP 200 with usable
  output for `openai/gpt-oss-20b` and `openai/gpt-oss-120b`. That test posts to
  Groq directly and bypasses the application, so it cannot stand in for the
  real workflow.
- *Application path:* the last observed dashboard recommendation reported
  `provider: ollama` after Groq returned HTTP 400 with a JSON-validation error.
  That observation predates commit `ad4f118`, which added
  `max_completion_tokens` (default 4000) — confirmed present in
  `dashboard/Ai/GroqClient.cs`. Nothing has retested through the application
  since, so the fix is **unproven**.
- *Schema eliminated as the cause, offline.* The live schema from
  `/api/ai/schema` was walked against strict structured-output rules: 3 objects,
  all `additionalProperties: false`, every property in its object's `required`,
  and no unsupported keyword (`minimum`, `maxItems`, `pattern`, `format`,
  `allOf`, `default`, …). Zero findings. So the 400 was **not** a schema-shape
  rejection; the remaining explanation is the model's own output failing to
  validate, which is consistent with truncation and is exactly what
  `max_completion_tokens` addresses.

**Ollama fallback: working.** It answered and passed safety validation when
Groq failed. A fallback is not a Groq pass, and the UI reports which provider
actually answered.

To close this, one real recommendation must be requested from a dashboard
started in a process that has `GROQ_API_KEY` in its own environment, and the
response must report `provider: groq`. A key set in a different window is not
inherited by the dashboard's process. The key is never committed, printed or
logged.

## Deliverables

| # | Deliverable | Status |
| --- | --- | --- |
| 1 | Source code and README | README stale — understates the project |
| 2 | Demo video (≤3 min) | Script material exists in `docs/REPRODUCIBLE_DEMO.md`; no timed 3-minute script, **no video recorded** |
| 3 | Architecture overview | Not started. `docs/arc42/` is upstream Pitstop's, not ours |
| 4 | Findings report | Source evidence exists and is current; no committed aggregated report |
| 5 | Team form information | Team data exists in the README; no ready-to-copy document |

## Known limitation, preserved

**RabbitMQ write/event divergence is real and open.** A customer row can commit
while its `CustomerRegistered` event is never published. Restoring the broker
does not repair the gap, because no transactional outbox exists. This has been
reproduced by `chaos-scripts/test-publish-integrity.ps1`. It is not solved, and
nothing here should be read as claiming it is.

## Gaps going in

1. Monitoring baseline graph — the only outstanding application capability.
2. Milestone-oriented dashboard labels with truthful per-area status.
3. End-to-end operator journey walked in one pass.
4. All five deliverables.
5. `kill-service` is the one experiment whose cleanup block the contract
   harness never executes, although it uses the same watchdog pattern as
   `kill-rabbitmq` and `kill-database`, which are both covered.
6. Groq end-to-end, which needs the operator's keyed process and must not block
   anything else.
