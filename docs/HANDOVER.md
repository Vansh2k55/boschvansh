# START HERE

Recovery document for whichever coding agent picks this up next (Claude Code,
Antigravity, Codex). Everything below was verified by running it after PR #1
merged, not inferred and not carried over from before the merge.

Last updated: 2026-10-10, post-merge.

## Where things are

| | |
| --- | --- |
| Repository | `C:\Users\Saurabh\pitstop` |
| Remote | https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon |
| Branch | `main`, synchronised with `origin/main` |
| Latest commit | run `git log --oneline -1`; the most recent work is the Day 2 analysis design and scorer |
| Working tree | Clean apart from generated `chaos-scripts/results/` (gitignored) |
| PR #1 | **Merged** as `12a5245`, verified present and working on `main` |
| `feature/kill-rabbitmq` | Tigisha's original broker script. **Superseded** — see below |

There is an untracked nested clone at `pitstop/`. It is a scratch duplicate, it
is gitignored, nothing depends on it. Leave it alone.

## State of the branches

`feature/kill-rabbitmq` holds the original `kill-rabbitmq.ps1` and an empty
`docs/rabbitmq-chaos.md`. That script was reworked and the result is already on
`main`, with the documentation written. **Do not merge that branch** — it would
regress the file. Tell Tigisha the work landed, and that the changes were the
safety contract in `AGENTS.md`: baseline, STOP-file kill switch, watchdog,
threshold abort, endpoint validation and written evidence.

## Start the application

Probably already running: `docker compose -f src/docker-compose.yml ps` — 13 containers.

Cold start, from `src/`, base images first and in order:

```bash
docker build -t pitstop-dotnet-sdk-base:1.0 . -f dotnet-sdk-base-dockerfile
docker build -t pitstop-dotnet-runtime-base:1.0 . -f dotnet-runtime-base-dockerfile
docker build -t pitstop-dotnet-aspnet-base:1.0 . -f dotnet-aspnet-base-dockerfile
docker compose up -d --build
```

Site http://localhost:7005 · RabbitMQ 15672 · MailDev 4000 · Seq 5341.

Stop with `docker compose down`. **Never add `-v`** — it deletes the
`sqlserverdata` volume and every database in it.

## Run everything

```powershell
./chaos-scripts/map-dependencies.ps1                          # map; read-only
./chaos-scripts/kill-service.ps1 -DurationSeconds 30          # Workshop crash
./chaos-scripts/kill-rabbitmq.ps1                             # dry run, changes nothing
./chaos-scripts/kill-rabbitmq.ps1 -DowntimeSeconds 25 -Execute
./chaos-scripts/analyze.ps1                                   # score; read-only, no network
```

Manual kill switch, second terminal at the repo root:

```powershell
New-Item chaos-scripts/STOP -ItemType File -Force
```

Delete it before the next run or the script refuses to start. If PowerShell dies
mid-experiment and the watchdog does not fire:

```bash
docker compose -f src/docker-compose.yml start workshopmanagementapi
docker compose -f src/docker-compose.yml start rabbitmq
```

After a broker run, give the publisher a few seconds before starting another
experiment — the baseline guard will refuse until a write succeeds, which is
correct behaviour, not a bug.

## Verified post-merge

- 13 containers up, health-checked ones healthy.
- Compose valid; single top-level `volumes:` key (the merge conflict fix held).
- Map regenerated: 13 nodes, 40 edges.
- `kill-service`: max-duration rollback, **threshold rollback** and manual kill
  switch all exercised; recovered each time.
- Remote-endpoint guard refused to run with `DOCKER_HOST` set to a working
  endpoint, before touching anything.
- `kill-rabbitmq`: dry run, baseline precondition abort, max-duration rollback,
  manual kill switch; recovery 19.28s.
- `analyze.ps1` scored 7 recorded runs.

## The finding that matters

During a broker outage the module pages stay perfectly healthy — unchanged
latency, 200 throughout. The damage is invisible from the UI.

`POST /api/customers` returns **500**, but the row **is committed**. The customer
exists in Customer Management while `CustomerRegistered` was never published and
never will be, because nothing recorded it to replay. The workshop handler, the
invoice service and the notification service never learn the customer exists, and
the caller's natural retry creates a duplicate rather than repairing anything.

Reproduced three times. Recorded as `DivergenceDetected` in `report.json`. The
root cause hypothesis — no transactional outbox — is in `docs/rabbitmq-chaos.md`.

## Not done

- **No LLM call exists anywhere in the repo.** No key, no network call. The
  design and the deterministic scorer are built; the narrative layer, Groq or
  Gemini integration, and experiment planning are not. This is step 3 onward in
  `docs/AI_ANALYSIS_DESIGN.md`.
- No network-delay or database experiment. No dashboard.
- Watchdog restore untested in both experiments (needs PowerShell killed mid-run).
- Threshold rollback untested in `kill-rabbitmq.ps1` — pages stay healthy during
  a broker outage so there is nothing to trip. Same code is verified in
  `kill-service.ps1`.
- The broker experiment infers non-delivery from the broker being down rather
  than inspecting consumer state.

## Highest-priority next action

**Build the narrative layer on `analyze.ps1`** — step 3 in
`docs/AI_ANALYSIS_DESIGN.md`. The scorer, the findings and the evidence files
already exist, so this is additive: read `$env:GROQ_API_KEY`, send the extracted
facts, require an `evidence` array on every hypothesis, drop anything that cites
nothing. Keep every number deterministic.

If that is blocked, the next most valuable thing is the network-delay experiment
(task 4), which shows graded degradation rather than on/off.

## Risks and safety rules

- Experiments target **only** containers in this Compose project. Both scripts
  enforce a local Windows named-pipe Docker context and refuse a remote
  `DOCKER_HOST`. This is tested.
- The kill switch is file and timer logic. **Keep every model off the rollback
  path, and off every number in the report.**
- `src/*/appsettings.Production.json` carries the upstream sample's hardcoded SQL
  and RabbitMQ passwords. Sandbox-only, inherited from EdwinVW/pitstop. Do not
  imitate the pattern and never put a real secret there.
