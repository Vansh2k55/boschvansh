# Implementation progress

Last updated: 2026-10-10, end of Phase 0.

## Phase 0 — Baseline and Git safety ✅ COMPLETE

Checkpoint: annotated tag **`baseline-phase0`** → commit **`a6bc383`**, pushed
and confirmed present on the remote.

### Baseline, measured not assumed

| Check | Command | Result |
| --- | --- | --- |
| Build | `dotnet build dashboard/Dashboard.csproj` | **Pass** — 0 warnings, 0 errors |
| Tests | `dotnet test dashboard.Tests` | **Pass** — 42/42, 20s |
| Stack | `docker compose -f src/docker-compose.yml ps` | **13/13 up** |
| Module pages | probes on `127.0.0.1:7005` | **200** on all three |
| Ollama | `GET localhost:11434/api/tags` | **Reachable**, `qwen3.5:4b` |
| Secrets | `git grep` for key patterns in tracked files | **None** |
| Tree | `git status` | **Clean**, `main` == `origin/main` |

**The baseline is genuinely clean** — no pre-existing failures to carry forward.

### Preserved

Nothing was discarded. Working tree was already clean; no uncommitted user work
existed. The nested `pitstop/` clone is untouched and still gitignored. Existing
tags `pre-groq-migration` and now `baseline-phase0` both exist on the remote.

---

## BLOCKER — two parallel implementations have diverged

This must be decided before Phase 1. It is not a merge conflict to resolve
mechanically; it is a question about which architecture the team is submitting.

### What happened

The branches split at **`d30af72`**, before any of the dashboard work on `main`.
Since then both sides built an AI chaos platform independently.

**On `main`** — 10 commits the other branches do not have:

- `dashboard/` — ASP.NET Core minimal API + live console (C#)
- Groq + Ollama provider abstraction with a router
- `RecommendationValidator`, `ExperimentCatalog`
- 42 xUnit tests
- The publish-integrity fix: `AddTransient` → `AddSingleton` and the
  `async void` `Connect()` correction, which took write divergence on a healthy
  system from **30% to 0%**
- `test-publish-integrity.ps1`, `analyze.ps1`, `map-dependencies.ps1`

**On `origin/task2-kill-service`** (open **PR #2 "Added an AI Agent"**, 44 files):

- `agent/` — a PowerShell AI agent (`run-agent.ps1`, `AgentTools.psm1`)
- `agent/experiments.json` — **four** experiments vs the two on `main`:
  `workshop-crash`, `database-crash`, `rabbitmq-crash`, **`workshop-delay`**
  (a real `NetworkDelay` implementation)
- Its own tests (`plan-validation.tests.ps1`, `planner-response.tests.ps1`)
- A `-RuleBased` switch for running with no AI at all
- `docs/evidence/` — captured baseline and live evidence

**On `origin/day2-submission`** (119 files, includes the above plus):

- A **different** `dashboard/` — Node-based (`package-lock.json`,
  `package-submission.mjs`, `launch-agent.ps1`), which collides directly with
  the C# `dashboard/` on `main`
- `submission/` — `demo.webm`, `findings.md`, `team-form.json`, `manifest.json`,
  `verification.json`, `dependency-map.png`

### Why this is a decision, not a cleanup

Each side has something the other lacks:

| | `main` | `day2-submission` |
| --- | --- | --- |
| Experiment types | 2 | **4**, incl. network delay |
| Dashboard | C# live console, provider modes | Node, submission packaging |
| Automated tests | 42 xUnit | PowerShell test files |
| Integrity fix (30% → 0%) | **Yes** | No |
| Submission package | No | **Yes** (demo video, team form) |

`day2-submission` carries what looks like an actual hackathon submission. A
careless merge could damage it, and `dashboard/` cannot hold both
implementations as-is.

### Options

1. **Merge their experiments onto `main`.** Port `database-crash` and
   `workshop-delay` into the registry, keep the C# dashboard and the integrity
   fix. Most work; best end state.
2. **Rebase their branch onto `main`.** Keeps their agent and submission, picks
   up the integrity fix. Conflicts concentrate in `dashboard/`.
3. **Keep both, submit `day2-submission`.** Treat `main` as the engineering
   track. Zero risk to the submission; the 30% integrity fix does not ship.
4. **Leave untouched, build only on `main`.** Safe but the fork becomes
   permanent.

**Not decided unilaterally** — it affects what the team submits.

---

## Phase 1 — Inspection and integration decision ✅

Their branches were **read only**. Nothing was merged, rebased, modified or
deleted, and the submission package was not touched.

### What was inspected

`agent/AgentTools.psm1`, `agent/run-agent.ps1`, `agent/experiments.json`,
`agent/dependencies.json`, their `agent/tests/`, their evolved
`chaos-scripts/kill-service.ps1`, `chaos-scripts/network-delay.ps1`, and the
file list of `origin/day2-submission`.

### Decision: adopt techniques, not the framework

Their platform is a PowerShell agent plus a Node dashboard. Ours is a C#
dashboard with a provider abstraction and 42 xUnit tests. Importing their
framework would mean two dashboards and two agents; porting our work onto
theirs would discard the integrity fix and the test suite. Neither is a good
trade, so specific techniques are adapted into our architecture instead.

**Adopt — safety, and genuinely better than what we have**

| Technique | Why |
| --- | --- |
| Compose-ownership verification | They inspect container labels (`com.docker.compose.project`, `.service`, `.project.config_files`) and refuse a container not owned by this checkout's compose file. Stronger than our service-name lookup, and cheap to add. |
| `experiment.lock` file lock | An OS-level exclusive lock prevents two *script invocations* overlapping. Our dashboard lock is in-process only, so two terminals could still collide. |
| Kill-switch re-check after baseline | They re-test the STOP file immediately before injecting. Closes a real window: engaging the switch during a 3-round baseline currently still injects. |

**Adopt — capability**

| Technique | Why |
| --- | --- |
| `tc` via a sidecar sharing the network namespace | `docker run --rm --network container:<id> --cap-add NET_ADMIN ... tc`. The app image needs no `tc` and the app container never holds `NET_ADMIN`. This is the right way to do it. |
| Pre-injection qdisc guard and verified removal | Refuses to clobber an existing traffic rule, and checks the `netem` qdisc is actually gone on restore rather than assuming it. |
| `--pull=never` on the sidecar | A missing image fails loudly instead of silently downloading one mid-experiment. |

**Reject**

| Component | Why |
| --- | --- |
| Node `dashboard/` | Collides directly with the C# `dashboard/`. Two dashboards is not one coherent platform. |
| PowerShell `agent/` framework | Duplicates the provider abstraction, router, validator and registry we already have under test. |
| Their `kill-service.ps1` wholesale | It is a fork of the same file. Taking it would drop our IPv4 probe fix and the write/event divergence instrumentation. The *logic* is adapted instead. |
| `kill-database` (`sqlserver`) | Deferred, not rejected. Widest blast radius in the map; it waits until the registry extension path is proven. |

Their plan validator rejects unexpected fields outright ("commands and target
overrides are forbidden"). Ours achieves the same structurally by
deserializing into a fixed record, so unknown fields are dropped before
validation. No change needed, but their explicitness is worth noting.

Attribution for adapted techniques is recorded in the code comments and commit
messages.

### Sequencing note

The adopted safety items belong to Phase 6 and the network-delay experiment to
Phase 5. The three safety adaptations are small, low-risk and protect every
experiment, so they are pulled forward ahead of Phase 2 rather than left until
late. The reason is recorded here as required.

---
## Phase 2 — Three provider modes ✅  (`a1ad585`)

Ollama-only, Groq-only, Auto. `PlanFor()` in `AiProviderRouter` is the single
place provider order is decided, so a mode cannot be bypassed from elsewhere.
Mode is held server-side and persisted to `results/ai-settings.json`; the
browser requests a change but does not assert one.

| Guarantee | How it is enforced |
| --- | --- |
| Ollama-only sends nothing to Groq | The Groq provider is never added to the plan |
| Groq-only never falls back | Ollama is recorded as an explicit "no fallback" skip |
| Auto skips Groq with no key | Avoids calling it to collect a 401 |
| No unbounded retries | Each provider is attempted once |

**13 tests added (55 total).** The two that matter most assert the *absence* of
a request. Verified live: Auto and Ollama resolve to `ollama`; Groq-only with
no key reports no active provider rather than quietly answering locally.

## Phase 3 — Evidence rendering ✅  (`4dc3341`)

The reported defect: the UI showed `DeterministicAnalysis.Dimensions[1].Earned`.
Fixed at the source, not with CSS.

- Prompt and schema now require readable statements quoting the actual value,
  and state that a JSON path is not an explanation.
- `EvidenceFormatter` repairs path-shaped citations that still arrive: it walks
  the evidence, substitutes real values, labels array elements by name
  (`Dimensions[1]` → "Graceful degradation"), summarises cited objects, matches
  property names case-insensitively and drops duplicates.
- An unresolvable path is returned **unchanged**. It never invents a value.

**17 tests added (70 total)**, including the exact screenshot string and a run
against a real saved report. Verified live against Ollama: citations came back
as prose quoting real values, no property paths.

Two bugs were caught by those tests rather than by inspection - bare field
names were never resolved, and named array elements duplicated their container
label.

## Safety guards adapted from PR #2 ✅  (`e25d422`)

Pulled forward from Phase 6 because they are small and protect every
experiment. Compose-ownership verification, an OS-level experiment lock, and a
kill-switch re-check after baseline. Each was verified by firing it, not by
reading it: a foreign container is refused, STOP engaged during baseline
prevents injection entirely, and a second experiment is refused while the first
holds the lock.

**Record correction:** commit `e25d422` claimed a passing build and test run
that had not executed - a stray `Dashboard` process held a file lock. Both were
run immediately after and did pass, but the claim preceded the evidence.

## Phase 4 — Safe AI experiment discovery ✅

The model can now name a failure mode this platform cannot test, without that
sentence becoming something the system will do.

A discovery is text. `DiscoveredRisk` has no experiment id, no target and no
duration, so there is nothing to hand a runner even if something tried. The
`DISCOVERED — NOT IMPLEMENTED — NOT EXECUTABLE` status is written by the server,
never copied from the model - if the model could supply it, it could supply a
different one.

`DiscoveryValidator` drops a discovery that:

- names a registered experiment, in its title or its failure mode. That is
  either a modelling error or an attempt to launder an idea into something
  runnable, and neither belongs beside real experiments.
- cites no evidence, which makes it speculation rather than a finding.
- has no title or no failure mode.

Surfacing is capped at five so a model cannot bury the real recommendation.
Discoveries and the recommendation are independent: a rejected experiment
proposal does not discard a sound observation about coverage.

The UI renders them as text with **no controls** - no button exists to add,
because there is nothing to run.

**21 tests added (91 total).** The ones that matter prove absence: running a
discovered title through `/api/experiments/run` with `approved: true` is
refused, and so are `network-delay`, `resource-exhaustion`,
`dependency-timeout` and `kill-database` - the exact names a model proposes -
at both the run endpoint and preflight. Calling the API directly is not a way
around the UI, because the UI was never the gate.

Verified live against Ollama: three discoveries surfaced, each marked not
executable and each citing real evidence from the saved reports.

*Model-quality note, not a safety issue:* `qwen3.5:4b` produced weak
`failureMode` text ("failure", "success") while citing accurate evidence. A
larger local model or Groq would do better; the safety boundary is unaffected.

---

## Remaining

| Phase | State |
| --- | --- |
| 4 — Self-analysis and experiment discovery | **Complete.** |
| 5 — Experiment registry and `network-delay` | **Not started.** `ExperimentCatalog` is already the single source of truth; the adapted `tc` sidecar experiment is specified in `docs/AI_EXPERIMENT_DESIGN.md` and not implemented. |
| 6 — Safety hardening | **Partly done** via the guards above. Watchdog-under-kill remains untested. |
| 7 — Integration and live verification | **Not started.** Groq has never run live - no key. |
| 8 — Final report | Pending. |

## Known gaps, unchanged

- **Groq is unverified live.** 70 tests use a stubbed transport. Ollama is
  verified end to end.
- **The RabbitMQ write/event divergence is still real and still reported.** The
  spurious 30% on a healthy system was fixed; a genuine broker outage still
  loses the event. No outbox exists and nothing claims one does.
- Watchdog restore is implemented in both experiments but never tested by
  killing PowerShell mid-run.
- `kill-rabbitmq` threshold rollback is untested - module pages stay healthy
  during a broker outage, so nothing trips it.

## Next action

Phase 5: implement `network-delay` as a registered experiment using the
tc-sidecar technique in `docs/AI_EXPERIMENT_DESIGN.md`, with the qdisc guard and
verified removal. Enable it only if cleanup can be proven after normal
completion, failure, timeout, cancellation and kill-switch activation.

