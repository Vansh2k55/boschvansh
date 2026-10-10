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

## Next action

Settle the reconciliation question, then begin Phase 1.

Nothing on the remote has been altered: no branch was merged, rebased, deleted
or force-pushed. `main` only gained documentation.

## Resume commands

```powershell
git fetch origin --prune
git log --oneline -5
git status --short
dotnet build dashboard/Dashboard.csproj
dotnet test dashboard.Tests
docker compose -f src/docker-compose.yml ps
```
