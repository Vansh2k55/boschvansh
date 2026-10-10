# Next-LLM checklist

Written 2026-10-11 at the end of a session that ran out of context. Everything
here was verified by running it unless marked otherwise.

## A. Verified state

| | |
| --- | --- |
| Branch | `main` |
| HEAD | see `git log -1`; last verified commit was the test-isolation fix |
| Working tree | Clean except gitignored `chaos-scripts/results/`, `experiment.lock`, build output |
| Remote | `origin/main` in sync (`git rev-list --left-right --count main...origin/main` → `0 0`) |
| Tags | `baseline-phase0` (`a6bc383`), `pre-groq-migration` (`98ccd16`) |
| Teammate branches | `day2-submission`, `task2-kill-service`, `Jigisha-Pradhan-patch-1` — **never merged, rebased or modified. Leave them alone.** |

Runtime at handover: 13 containers up, health-checked ones healthy, all three
module pages 200 in ~12ms over IPv4, no traffic shaping present, no stray
sidecars, no `STOP` file.

## B. Architecture

```
src/                 Upstream Pitstop (.NET microservices + SQL Server + RabbitMQ)
chaos-scripts/       Experiments, safety module, mapper, analyzer
  ChaosSafety.psm1     Ownership check, cross-process lock, kill-switch assert
  kill-service.ps1     Workshop API crash
  kill-rabbitmq.ps1    Broker outage (needs -Execute)
  network-delay.ps1    Bounded egress latency via tc sidecar
  netem-image/         Dockerfile for pitstop-netem:1.0
  analyze.ps1          Deterministic resilience agent (read-only, no network)
  test-publish-integrity.ps1
  map-dependencies.ps1
dashboard/           ASP.NET Core console + AI layer
  Ai/                  Groq + Ollama providers, router, validators, catalog
  ExperimentRunner.cs  IExperimentRunner — the ONLY path to a real fault
dashboard.Tests/     114 xUnit tests
```

**Safety boundaries.** `ExperimentCatalog` is the single source of truth for what
can run. `RecommendationValidator` rejects anything the model proposes outside
it — target comes from the catalog, never the model; out-of-range durations are
rejected, not clamped. `DiscoveryValidator` admits research notes that have no
execution path. The kill switch is a `STOP` file the scripts poll for and is
independent of the dashboard, the backend and any model.

**Commands.**

```powershell
dotnet build dashboard/Dashboard.csproj
dotnet test dashboard.Tests                     # 114 passing
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
docker build -t pitstop-netem:1.0 chaos-scripts/netem-image   # once, for network-delay
```

Dashboard http://localhost:5280 · App http://localhost:7005 · RabbitMQ 15672 ·
MailDev 4000 · Seq 5341. **Use `127.0.0.1`, not `localhost`, for probes** — a
`wslrelay` process can hold `[::1]:7005` without serving it.

**Stop a running Dashboard before building**, or the build fails on a file lock.

## C. Completed phases

| Phase | Commit | Evidence |
| --- | --- | --- |
| 0 Baseline | `c6ae7a7` | Build, 42 tests, 13 containers, no secrets |
| 1 Inspection + decision | `b60365f` | Adapt PR #2 techniques, do not merge |
| Safety guards | `e25d422` | Each guard fired: foreign container refused, STOP-in-baseline blocked injection, lock refused a second run |
| 2 Provider modes | `a1ad585` | Ollama-only sends nothing to Groq; Groq-only never falls back |
| 3 Evidence rendering | `4dc3341` | Raw JSON paths gone; verified live against Ollama |
| 4 Discovery | `89873ce` | Discovered titles refused at run endpoint and preflight |
| 5 network-delay | `2e633f4` | Workshop 18ms → 5026ms **still HTTP 200**; cleanup proven across 5 paths incl. hard orchestrator kill |
| Test isolation | `5bd3480` | Valid approved requests for all 3 experiments reached a fake runner; stack untouched |

## D. Known bugs, unverified claims, and what would close each

| Item | Status | Evidence needed |
| --- | --- | --- |
| **Watchdog retained when cleanup fails** | **VERIFIED.** PR #7's disposable harness forced `Remove-Shaping` to throw: the watchdog was retained and later cleared the qdisc. Previously: `network-delay.ps1` used to `docker rm -f` the watchdog unconditionally, destroying the safety net exactly when removal failed. Now it is only removed when `$removalVerified` is true. Syntax checked; the behavioural test was blocked by a tool path rule and never ran. | Run both branches on a **disposable** container: force removal to fail, confirm the watchdog survives and clears the qdisc; then a success path, confirm the watchdog is removed. Do not test this on the app stack. |
| **Groq live** | **UNVERIFIED.** No key has ever been configured. All 114 tests use a stubbed transport. | One real request succeeding with `GROQ_API_KEY` set. Never commit the key. |
| **RabbitMQ event loss** | **REAL AND OPEN.** During a genuine broker outage a customer row commits while its event is never published. No transactional outbox exists. The spurious 30% on a *healthy* system was fixed in `649461b`. | An outbox, implemented and tested, as its own scoped change. Until then do not describe the system as resilient. |
| **Watchdog for `kill-service` / `kill-rabbitmq` after hard kill** | **WAS BROKEN, NOW FIXED AND VERIFIED.** PR #7 proved all four scenarios failed. Both now use `Start-RestartWatchdog` (detached container); the same harness reports all four true. See `b037041`. | Closed. |
| **`kill-rabbitmq` threshold rollback** | **VERIFIED.** `tests/recovery_contract_tests.ps1` drives the real fault loop with synthetic telemetry and asserts both threshold triggers and the STOP path, without stopping the live broker. | Closed. |
| Model discovery quality | Known | `qwen3.5:4b` writes weak `failureMode` text while citing accurate evidence. Safety is unaffected. A larger model or Groq improves it. |
| `pitstop-netem:1.0` | Platform note | Local build required; Linux containers; needs `NET_ADMIN` and a local Windows named-pipe Docker context. `--pull=never` means a missing image fails loudly. |

**Never equate a running container with recovery or with event integrity.**
`network-delay` never stops its container, so container status proves nothing
there — recovery is judged on measured latency.

## D2. Pull requests still open

Full reasoning in `docs/PR_REVIEW_LOG.md`. Merged: #4, #5, #6, #7.

| PR | State | Why |
| --- | --- | --- |
| #8 combined scripts + dashboard | **Partly adapted; rest still declined** | `PreparedExperiments` and `ReportIds` were taken without the provider-mode override. The remainder stands rejected: | Forces `AiMode.Groq` whenever `Ollama.Enabled` is false, and ships it false. An operator selecting Ollama-only would have requests sent to Groq - the inverse of their choice, and a break of a tested guarantee. Also keeps two evidence resolvers. `PreparedExperiments` in it is good and worth taking once fixed. |
| #3 API pause experiments | **Changes requested** | Superseded in scope by #8 (same scripts, cannot merge both). Missing `Assert-SandboxContainer`, and no independent recovery - a paused container stays paused if the orchestrator dies, the same defect just fixed for the crash experiments. Its `analyze.ps1` change is sound and can be taken alone. |
| #2 AI agent | **Not merged** | Unchanged from the Phase 1 decision; techniques already adapted. |

Do not merge #8 or #3 without the named fixes. Do not modify those branches.

## D3. Experiment catalog state

Roadmap and per-candidate assessment: `docs/EXPERIMENT_ROADMAP.md`.

| Experiment | State |
| --- | --- |
| `kill-service`, `kill-rabbitmq`, `network-delay` | **Executable**, cleanup verified |
| `packet-loss` | **Planned, next** - reuses the proven qdisc lifecycle |
| `kill-database` | **Planned** - highest value, needs a 30s cap |
| `pause-customer-api`, `pause-vehicle-api` | **Prepared, not executable** - need a sandbox guard and an unpause watchdog |
| `cpu-pressure` | Candidate; needs a load generator to be measurable |
| Memory pressure, network partition, dependency timeout | Not planned - reasons in the roadmap |

## D4. Groq is configured now

The first live call returned **413 Request too large**, which found a real
defect: the evidence payload grew with every run. Fixed in `9c36885`
(`EvidenceBudget`). **The running dashboard still has the old code** - it must
be restarted to pick the fix up, and the live call re-tried to confirm Groq
answers rather than falling back.

## E. Prioritised plan for the next session

Reconcile with `docs/AI_IMPLEMENTATION_PLAN.md`. Phases 0–5 are done; **Phase 6
is next** and has not started. Do not renumber the phases.

1. **Close the watchdog fix** (above). Checkpoint.
2. **Phase 6 — safety hardening.** Verify the crash-experiment watchdogs under
   hard kill; if they fail, adopt the detached-container pattern from
   `network-delay.ps1`. Verify `kill-rabbitmq` threshold rollback. Checkpoint.
3. **Provider validation.** Ollama mode, Groq-only, Auto fallback. A single
   deliberate live Groq request only if a key is configured. Checkpoint.
4. **Data integrity.** Characterise the committed-write / missing-event case,
   then design an outbox as a separate explicitly scoped change. Do not fold it
   into unrelated work. Checkpoint.
5. **Phase 7 — integration**, then **Phase 8 — final report**.

Acceptance for each: build succeeds, full suite passes, diff reviewed for
secrets and new execution paths, docs updated, commit pushed.

## F. Git safety rules

- Inspect `git status`, branch and remote before changing anything.
- Preserve unexpected changes; report rather than discard.
- **Never merge, rebase, modify or delete a teammate branch.**
- Never force-push; never reset or clean to get a tidy state.
- Build and test before committing. Never claim a result whose command did not
  complete — one commit in this project's history did, and it is corrected in
  `docs/AI_IMPLEMENTATION_PROGRESS.md`.
- Never commit secrets, build output, or generated evidence.

## G. Continuation prompt

```
Continue the Bosch chaos resilience platform on branch main.

Read docs/NEXT_LLM_CHECKLIST.md first, then docs/AI_IMPLEMENTATION_PLAN.md,
docs/AI_IMPLEMENTATION_PROGRESS.md and docs/AI_EXPERIMENT_DESIGN.md.
Independently verify branch, HEAD, working tree and remote sync before changing
anything.

Phases 0-5 are complete. Start with the one open item in section D: the
watchdog-retention fix in chaos-scripts/network-delay.ps1 is written and syntax
checked but was never verified by execution. Verify both branches on a
DISPOSABLE container, never the running app stack. Then begin Phase 6.

Rules: do not merge or modify teammate branches (day2-submission,
task2-kill-service, Jigisha-Pradhan-patch-1). Keep the RabbitMQ event-loss
finding visible - no outbox exists. Do not claim Groq live verification without
a real successful request. Stop any running Dashboard process before building or
the build fails on a file lock. Run `dotnet test dashboard.Tests` (114 passing)
and verify it completed before committing.
```
