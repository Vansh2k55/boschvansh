# START HERE

Recovery document for whichever coding agent picks this up next (Claude Code,
Antigravity, Codex). Everything below was verified by running it, not inferred.

Last updated: 2026-10-10T09:13Z

## Where things are

| | |
| --- | --- |
| Repository | `C:\Users\Saurabh\pitstop` |
| Remote | https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon |
| Branch | `main`, at `ae492ca`, pushed |
| Working tree | Clean except for generated `chaos-scripts/results/` (gitignored) |
| Open PR | [#1](https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon/pull/1), branch `task2-kill-service` at `d30af72` — **reviewed, verified, conflict-free, NOT merged** |

There is also an untracked nested clone at `pitstop/` inside the repo. It is a
scratch duplicate, it is gitignored, and nothing depends on it. Ignore or delete it.

## Read these next

1. `docs/HACKATHON_DAY1_PLAN.md` — requirements and the acceptance checklist.
2. `docs/PROJECT_STATUS.md` — what is verified, what is not.
3. `docs/dependency-map.md` — the generated service map.
4. `AGENTS.md` — conventions and safety rules for this repo.

## The one thing to know

**PR #1 holds the chaos experiment and is not merged yet.** `main` currently has
the dependency map and the infrastructure fixes but *not* `chaos-scripts/kill-service.ps1`.
Until the PR merges, the experiment only exists on `task2-kill-service`.

Merging it needs a human click (an agent was blocked from merging without review):

```bash
gh pr merge 1 --merge
```

The branch is already merged up to `main` and its compose conflict is resolved, so
the merge is clean. **This is the highest-priority next action.**

## Start the application

Already running? `docker compose -f src/docker-compose.yml ps` — 13 containers.

From a cold start, in `src/`, build the three base images first, in order:

```bash
docker build -t pitstop-dotnet-sdk-base:1.0 . -f dotnet-sdk-base-dockerfile
docker build -t pitstop-dotnet-runtime-base:1.0 . -f dotnet-runtime-base-dockerfile
docker build -t pitstop-dotnet-aspnet-base:1.0 . -f dotnet-aspnet-base-dockerfile
docker compose up -d --build
```

Site at http://localhost:7005. RabbitMQ 15672, MailDev 4000, Seq 5341.

Stop with `docker compose down`. **Do not add `-v`** — that deletes the
`sqlserverdata` volume and every database in it.

## Run the two Day 1 artefacts

```powershell
# Dependency map -> docs/dependency-map.md + chaos-scripts/results/dependency-map/graph.json
./chaos-scripts/map-dependencies.ps1

# Chaos experiment (only after PR #1 merges)
./chaos-scripts/kill-service.ps1 -DurationSeconds 30
```

Manual kill switch, from a second terminal at the repo root:

```powershell
New-Item chaos-scripts/STOP -ItemType File -Force
```

Delete that file before the next run or the script refuses to start. If PowerShell
is killed mid-experiment, restore by hand:

```bash
docker compose -f src/docker-compose.yml start workshopmanagementapi
```

## Verified by running it

- All 13 containers up; the four with health checks report healthy.
- Dependency map: 13 nodes, 40 edges, generated from Compose + appsettings + runtime.
- Chaos experiment, two full runs. Baseline 15–40ms. Under fault the Workshop
  module times out at ~5s while Customer and Vehicle stay at ~20ms — blast radius
  contained, and that empirically confirms the map's `webapp -> workshopmanagementapi` edge.
- Rollback on max duration: verified, recovered.
- Rollback on manual kill switch: verified, recovered.

## Not done

- No AI/LLM integration at all. No Groq, no Gemini, no key, no fallback path. (P2)
- Only one failure type. No network delay, no broker kill, no database fault. (P2/P3)
- No resilience score, no report generation, no dashboard. (P2/P3)
- Threshold-breach rollback is implemented but never exercised — the other two
  rollback paths were. Testing it means making Customer or Vehicle unhealthy.
- Six services have no Docker health check, so the map shows `no healthcheck`
  for them. Only the three APIs and the web app are probe-able.

## Risks and safety rules

- Experiments may target **only** containers in this Compose project. The script
  enforces a local Windows named-pipe Docker context and refuses a remote `DOCKER_HOST`.
- The kill switch is plain file and timer logic. Keep it that way — no LLM on the
  rollback path.
- `src/*/appsettings.Production.json` carries the upstream sample's hardcoded SQL
  and RabbitMQ passwords. They came from EdwinVW/pitstop and are sandbox-only.
  Do not copy the pattern into anything new, and do not put real secrets there.
