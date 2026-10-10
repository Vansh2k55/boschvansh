# Agent instructions — Bosch Chaos Hackathon (Pitstop sandbox)

Shared instructions for any coding agent working in this repository.

**Resuming? Read `docs/HANDOVER.md` first.** It carries the live branch, commit,
and the single highest-priority next action.

## What this repository is

A fork of [EdwinVW/pitstop](https://github.com/EdwinVW/pitstop) (Apache 2.0), a
ten-service .NET garage management system, used as the local sandbox for an
AI-driven chaos engineering project. We break it on purpose, measure what breaks,
and prove we can stop and recover.

The application is upstream code. Our work lives in `chaos-scripts/` and `docs/`.

## Layout

```
chaos-scripts/   Experiments, the dependency mapper and the scorer. Our code.
  map-dependencies.ps1     Builds the service map (read-only, safe to run)
  dependency-overlay.json  Hand-curated RabbitMQ event topology
  kill-service.ps1         Workshop API crash experiment
  kill-rabbitmq.ps1        Broker outage experiment (needs -Execute to inject)
  analyze.ps1              Deterministic resilience agent (read-only, no network)
  test-publish-integrity.ps1  Regression test for the write/event divergence
  results/                 Generated evidence and audit.jsonl, gitignored
dashboard/       Live resilience console + AI advisory layer (ASP.NET Core)
  Ai/                      Groq + Ollama providers, router, validator, catalog
dashboard.Tests/ xUnit safety tests (no real API calls, no real faults)
docs/            Plan, status, handover, team tasks, generated map, experiment write-ups
src/             Upstream Pitstop. Change only to fix something genuinely broken.
```

## Commands

```bash
docker compose -f src/docker-compose.yml ps          # what is running
docker compose -f src/docker-compose.yml up -d       # start
docker compose -f src/docker-compose.yml down        # stop, keep data
```

```powershell
./chaos-scripts/map-dependencies.ps1                        # regenerate the map
./chaos-scripts/kill-service.ps1 -DurationSeconds 30        # Workshop API crash
./chaos-scripts/kill-rabbitmq.ps1                           # dry run, changes nothing
./chaos-scripts/kill-rabbitmq.ps1 -DowntimeSeconds 25 -Execute
./chaos-scripts/analyze.ps1                                 # assess every recorded run
./chaos-scripts/test-publish-integrity.ps1 -Writes 20       # regression test
```

```bash
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

`docker compose down -v` destroys the `sqlserverdata` volume and every database
in it. Do not use it casually.

Site http://localhost:7005 · RabbitMQ 15672 · MailDev 4000 · Seq 5341.

## Safety rules

These are the point of the project. Treat them as binding.

1. **Blast radius.** Experiments touch only containers in this Compose project.
   Never a remote Docker endpoint, never anything outside this sandbox, never
   another team's environment.
2. **The kill switch is deterministic.** File checks, timers and threshold
   comparisons. **No LLM on the rollback path, ever.** If the model is slow,
   wrong, rate-limited or offline, rollback still happens.
3. **Every experiment is bounded before it injects anything** — a duration cap, a
   threshold that aborts, and a STOP-file check. Safety is not a follow-up commit.
4. **Rollback survives the script dying.** `kill-service.ps1` uses an independent
   watchdog job for exactly this. Copy that pattern.
5. **The model recommends, it never executes.** The experiment allowlist lives
   in `ExperimentCatalog`; `RecommendationValidator` rejects anything outside
   it. The model cannot invent an experiment, choose a target or widen a
   duration, and out-of-range values are rejected rather than clamped. Validate every suggestion against
   an allowlist before a human is offered it. Never run model-generated shell
   commands. **No score, no number and no ranking may come from a model** — they
   come from published rules in code, so they are reproducible and checkable.
   See `docs/AI_ANALYSIS_DESIGN.md` and `docs/AI_SETUP.md`.
   Providers degrade Groq -> Ollama -> deterministic. A provider error is never
   permission to act: no recommendation means nothing to approve.
6. **Secrets come from environment variables.** Never commit a key, never print
   one in logs, docs or screenshots.

**`src/Infrastructure.Messaging/` is not compiled into the services.** They
consume it as a NuGet PackageReference pinned to the published 5.5.0, and no
Dockerfile copies that source. Editing it changes nothing at runtime - verify
any change there by rebuilding and observing the container, never by reading
the diff. See `docs/publish-integrity.md`.

Note: `src/*/appsettings.Production.json` contains hardcoded SQL Server and
RabbitMQ passwords. That is upstream sample-app code for a throwaway local
sandbox. Leave it alone, do not imitate it, and never put a real credential there.

## Conventions

- One experiment per script, named for what it breaks.
- Every experiment writes `results/<run-id>/samples.csv` and `report.json`.
  Evidence beats description.
- Measure a baseline before the fault, and verify recovery after it. A fault with
  no baseline proves nothing.
- Branch per task, PR into `main`. Conventional commits (`feat:`, `fix:`, `docs:`).
- After changing `src/docker-compose.yml` or an event handler, regenerate the map.
- `docker compose config --quiet` after any compose edit. A clean Git merge of
  that file has already produced invalid YAML once — duplicate top-level keys
  merge without conflict.

## Working rules

- Verify before claiming. Run it, read the output, then write it down. Never
  record a test as passing that was not run, or a PR as merged that was reviewed.
- Keep `docs/PROJECT_STATUS.md` and `docs/HANDOVER.md` current — they are the
  project's memory across context limits and across agents. Checkpoint to Git at
  each real milestone.
- Finish P0 before anything optional. The priority ladder is in
  `docs/HACKATHON_DAY1_PLAN.md`.
