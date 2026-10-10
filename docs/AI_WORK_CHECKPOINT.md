# Current checkpoint

Authoritative state. If this disagrees with a conversation summary, trust this
file and `git log`.

**Updated:** 2026-10-11 · **Branch:** `main` · **Commit:** see `git log -1`
**Working tree:** clean · **Remote:** `origin/main` in sync

## Where the work is

| Phase | State |
| --- | --- |
| 0 Recover and checkpoint | **Done** |
| 1 Verify Groq live | **BLOCKED — see below.** Needs one command from the operator |
| 2 Audit workflow against milestones | Not started |
| 3 Implement `packet-loss` | Not started — gated on Phase 1 |
| 4 Reach five executable experiments | Not started |
| 5–8 | Not started |

## Phase 1 — Groq: two defects found, one fixed, one open

Groq is configured and reachable. **It has still never returned a usable
completion**, so live verification is **UNVERIFIED**, not passed. Every request
so far fell back to Ollama, and a fallback is not a pass.

### Fixed: payload too large (`9c36885`)

First live call: `413 Request too large for model openai/gpt-oss-20b`. The
evidence carried one entry per experiment ever run — about 54KB, 45KB of it
per-run detail — so it grew without limit. `EvidenceBudget` now sends the
aggregates plus the eight most recent runs; full detail stays on disk. A test
asserts the payload grows by under 100 characters between 10 and 1000 runs.

**Verified:** the 413 is gone. The next call got past request size.

### Open: model cannot satisfy the schema

Second live call:

```
Groq returned 400. Upstream said: Failed to validate JSON. Please adjust your prompt.
```

Our schema was checked against Groq's strict rules and **is compliant**: all 15
top-level properties are in `required`, `additionalProperties` is false on both
objects, and no unsupported keywords are used. That points at
`openai/gpt-oss-20b` failing to *produce* conforming output for a 15-property
schema with a nested array of 7-property objects, rather than at the schema.

That is a hypothesis, not a conclusion. `tests/groq_live_check.ps1` settles it
with evidence.

## Next action — needs the operator

The dashboard must be restarted to pick up `/api/ai/schema`, then the live check
run. **In the window that has `GROQ_API_KEY` set:**

```powershell
# 1. restart the dashboard (Ctrl+C the running one first)
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280

# 2. in a second window that also has GROQ_API_KEY set
cd C:\Users\Saurabh\pitstop
./tests/groq_live_check.ps1
```

It tries `openai/gpt-oss-20b` and `openai/gpt-oss-120b` against the real schema
fetched from `/api/ai/schema`, so the test cannot drift from what the dashboard
sends. The key is read from the environment and never printed or stored.

**Acceptance:** at least one model returns status 200 with output that parses
against the schema. Then set that model as the default (`GROQ_MODEL`, or
`dashboard/appsettings.json`) and re-run a real recommendation to confirm
`provider: groq`.

**If every model fails**, Groq stays UNVERIFIED and the honest options are to
simplify the schema for the Groq path or to accept Ollama as the working
provider and disclose the limitation.

## Experiment catalog

Three executable, cleanup verified: `kill-service`, `kill-rabbitmq`,
`network-delay`. Target is five. Order and per-candidate reasoning are in
`docs/EXPERIMENT_ROADMAP.md`:

1. `packet-loss` — next, reuses the proven qdisc lifecycle
2. `kill-database` — only after `packet-loss` is registered and verified
3. `pause-service` — blocked on an unpause watchdog; stays non-executable

Two scripts are surfaced as `PREPARED — NOT REGISTERED — NOT EXECUTABLE`.

## Instructions for the next LLM

**Startup**

```powershell
docker compose -f src/docker-compose.yml up -d            # 13 containers
docker build -t pitstop-netem:1.0 chaos-scripts/netem-image   # once
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

Dashboard `http://localhost:5280`, app `http://127.0.0.1:7005` — use the IPv4
form for probes, a `wslrelay` process can hold `[::1]:7005` without serving it.

**Architecture**

- `src/` upstream Pitstop; `chaos-scripts/` experiments, `ChaosSafety.psm1`
  guards, `analyze.ps1` deterministic agent; `dashboard/` ASP.NET Core console
  and `Ai/` provider layer; `dashboard.Tests/` 158 tests; `tests/` disposable
  and contract harnesses.
- `ExperimentCatalog` is the only allowlist. `RecommendationValidator` and
  `DiscoveryValidator` are the model boundary. `IExperimentRunner` is the only
  path to a real fault, and tests substitute a fake.

**Tests**

```powershell
dotnet test dashboard.Tests                                   # 158 passing
./tests/recovery_contract_tests.ps1                           # 14 assertions
python tests/disposable_recovery_audit.py --execute-disposable
./tests/groq_live_check.ps1                                   # live, needs the key
```

Stop any running Dashboard before building, or the build fails on a file lock.
To avoid killing the operator's keyed session, build with
`-p:BaseOutputPath=<temp>`.

**Safety restrictions — do not weaken**

- Only `ExperimentCatalog` entries run; targets come from the catalog, never the
  model; out-of-range values are rejected, not clamped.
- STOP file, duration caps, preflight, approval, detached watchdogs, verified
  cleanup. Recovery is judged on application behaviour, never container status.
- No secrets in Git, logs or responses. Never print `GROQ_API_KEY`.
- **Do not change the dependency map UI.**
- Teammate branches are never merged, rebased or modified.

**Not verified**

- Groq live completion (above).
- `pause-service` recovery.
- RabbitMQ write/event divergence is **real and open** — a row commits while its
  event is never published, and restoring the broker does not repair it. No
  outbox exists.
