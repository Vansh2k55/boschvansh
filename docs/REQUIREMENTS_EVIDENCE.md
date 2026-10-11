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

Audited at commit `d6a3eb5` and updated through `285557b`, branch `main`,
working tree clean, in sync with `origin/main`. Prior agent summaries were
treated as claims and re-checked.

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

**Verified.** The dashboard probes all three modules every 2s with status,
latency, offline-page detection and error text; every experiment writes
`samples.csv` plus `report.json` with per-probe `Phase` (`baseline`, `fault`,
`recovery`), latency and health; `/api/runs/{runId}` serves those samples;
`/api/state` lists 29 recorded runs.

A baseline/impact/recovery chart now draws those recorded samples, with phase
bands making the impact a visible region. Verified in the browser against run
`20261011T003459044Z`.

Two honesty defects were found while verifying it, and fixed:

- it labelled a module "not probed" during a fault when the module had been
  probed and every probe failed. It now reads "all 2 probes failed", and
  discloses partial failures as "955ms (2 of 3 failed)".
- a failed probe's latency is a client timeout constant, not something the
  service did. Using it for the y-axis squashed every real 10-22ms reading
  under a 5168ms ceiling. Failures now sit on their own row and the axis is
  scaled to real measurements.

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

Gap, now measured rather than assumed: a live recommendation through the real
dashboard returned **HTTP 503** - Groq 400, and `ollama: Ollama timed out after
240s`. The planner produced nothing. That is correct designed behaviour (no
recommendation means nothing to approve) but it is not a usable demo, so the
local model was replaced on measurement: `qwen2.5:3b` returns a conforming
recommendation in 18.4s cold and 9.1-9.5s warm, where `qwen3.5:4b` exceeded
240s and produced nothing.

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

A committed, aggregated findings document now exists at
`docs/FINDINGS_REPORT.md`.

**A fabricated finding was found and removed.** The analyzer raised "Service
did not recover within the timeout" at High severity whenever `Recovered` was
false, including for runs that aborted before injecting anything. Both such
runs carry `FaultStartedUtc=null` and "No fault was injected" - one a failed
baseline write, one the kill switch during baseline. The safety controls
working were being scored as a resilience weakness. The finding now requires
that a fault actually started, and across 29 runs there are **zero genuine
recovery failures**.

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
| 1 | Source code and README | **Complete** — `README.md`, rewritten to match what exists |
| 2 | Demo video (≤3 min) | **Script complete** — `docs/DEMO_SCRIPT.md`, timed with a shot list. **No video recorded** |
| 3 | Architecture overview | **Complete** — `docs/ARCHITECTURE_OVERVIEW.md` |
| 4 | Findings report | **Complete** — `docs/FINDINGS_REPORT.md`, from 29 real runs |
| 5 | Team form information | **Complete** — `docs/TEAM_FORM.md`, unknowns left as placeholders |

Verified through `/api/submission`, which reads the filesystem: all five report
`Complete`, and `videoRecorded` is `false` rather than inferred.

## Known limitation, preserved

**RabbitMQ write/event divergence is real and open.** A customer row can commit
while its `CustomerRegistered` event is never published. Restoring the broker
does not repair the gap, because no transactional outbox exists. This has been
reproduced by `chaos-scripts/test-publish-integrity.ps1`. It is not solved, and
nothing here should be read as claiming it is.

## Gaps going in, and what happened to each

| # | Gap | Outcome |
| --- | --- | --- |
| 1 | Monitoring baseline graph | **Done** — real recorded samples, two honesty defects fixed |
| 2 | Milestone labels and truthful status | **Done** — nine areas, every pill computed from live state |
| 3 | End-to-end operator journey | **Measured** — found both providers failing; local model fixed by measurement |
| 4 | Five deliverables | **Done** — all five committed and verified through `/api/submission` |
| 5 | `kill-service` cleanup never executed by the harness | **Done** — and it exposed a real safety defect, below |
| 6 | Groq end-to-end | **Still UNVERIFIED** — see below. It did not block anything else |

### The defect gap 5 exposed

`kill-service` and `kill-rabbitmq` declared restoration from the exit code of
`docker start` alone. A zero exit code means the start command was accepted,
not that the container stayed up - and that value decides whether
`Stop-RestartWatchdog` deletes the watchdog. A container that started and
immediately died would have had its only independent route back removed on the
strength of an exit code, at the moment it was needed. Both now verify
`State.Status` and record why a restart did not take. The harness grew from 32
to 56 assertions and executes all three crash experiments' real cleanup blocks
against both a healthy restart and a container that reports `exited`.

### Why Groq is still unverified, precisely

A live recommendation through the running dashboard returned Groq 400 again.
But the running dashboard is a **stale build**: its own `/api/state` offers
**3** executable experiments, not 5, so it predates `59874e1` and therefore
also `ad4f118`, the commit that added `max_completion_tokens`. Its error also
lacked the `failed_generation` tail that `ad4f118` appends, while Groq's own
message says that field is present - consistent with the same conclusion.

So the 400 observed came from a build without the fix. The fix is committed and
has still never run. The schema has been eliminated as a cause offline. What
remains is to restart the dashboard from current `main` in a process that has
`GROQ_API_KEY` in its own environment, and request one recommendation.
