# START HERE

Recovery document for whichever coding agent picks this up next (Claude Code,
Antigravity, Codex). Everything below was verified by running it.

Last updated: 2026-10-10, after the dashboard landed.

## Where things are

| | |
| --- | --- |
| Repository | `C:\Users\Saurabh\pitstop` |
| Remote | https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon |
| Branch | `main`, synchronised with `origin/main` |
| Latest commit | `git log --oneline -1` — most recent work is the publish-integrity finding |
| Working tree | Clean apart from gitignored `chaos-scripts/results/` and `dashboard/bin|obj` |
| PR #1 | **Merged** as `12a5245`. No open PRs. |
| `feature/kill-rabbitmq` | **Deleted** — its one unique behaviour was ported first (`d4685e5`) |
| `task2-kill-service` | PR #1's merged branch, still present. Safe to delete; nobody asked. |

An untracked nested clone sits at `pitstop/`. Scratch duplicate, gitignored,
nothing depends on it. Leave it.

## Start everything

Stack (13 containers): `docker compose -f src/docker-compose.yml ps`

Cold start, from `src/`, base images first and in order:

```bash
docker build -t pitstop-dotnet-sdk-base:1.0 . -f dotnet-sdk-base-dockerfile
docker build -t pitstop-dotnet-runtime-base:1.0 . -f dotnet-runtime-base-dockerfile
docker build -t pitstop-dotnet-aspnet-base:1.0 . -f dotnet-aspnet-base-dockerfile
docker compose up -d --build
```

Dashboard:

```bash
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

**Dashboard: http://localhost:5280** · Site 7005 · RabbitMQ 15672 · MailDev 4000 · Seq 5341.

`docker compose down` to stop. **Never `-v`** — it deletes `sqlserverdata` and
every database in it.

## Run things from the command line

```powershell
./chaos-scripts/map-dependencies.ps1                          # map; read-only
./chaos-scripts/kill-service.ps1 -DurationSeconds 30          # Workshop crash
./chaos-scripts/kill-rabbitmq.ps1                             # dry run, changes nothing
./chaos-scripts/kill-rabbitmq.ps1 -DowntimeSeconds 25 -Execute
./chaos-scripts/analyze.ps1                                   # agent; read-only, no network
./chaos-scripts/test-publish-integrity.ps1 -Writes 20         # regression test
```

Kill switch, from a second terminal at the repo root (or the dashboard button):

```powershell
New-Item chaos-scripts/STOP -ItemType File -Force
```

Delete it before the next run or every experiment refuses to start.

## The open decision — read this before building anything

**The write/event divergence is confirmed, far worse than first thought, and
deliberately not fixed.** Full write-up in `docs/publish-integrity.md`.

Against a *healthy* system, 30% of customer registrations return 500 with the
row committed and no event ever published. Cause: the publisher is registered
`AddTransient`, so a new publisher and connection is built per request, and
`Connect()` is accidentally `async void`, so the constructor returns before
`_channel` is assigned. The publish then races the connection and often throws
`NullReferenceException` — after `SaveChangesAsync` has committed.

**Why it is not fixed:** every service consumes `Infrastructure.Messaging` as a
NuGet `PackageReference` pinned to the published `5.5.0`. There is no
`nuget.config`, and each Dockerfile's build context never copies the messaging
source. **Editing `src/Infrastructure.Messaging/` does not change the running
system.** This was verified by making the fix, rebuilding the images,
force-recreating the containers and seeing the same exception. The same applies
to the earlier `RabbitMQMessageHandler` correction — it is inert at runtime.

Three options are written up with trade-offs in `docs/publish-integrity.md`:
make the local source authoritative via project references (A), decorate the
publisher per API (B), or add a transactional outbox (C). **Saurabh needs to
pick before anyone implements.** `chaos-scripts/test-publish-integrity.ps1` is
the before/after measurement for whichever is chosen.

## Verified by running it

**Day 1, reproduced from `main` after the merge**
- 13 containers up; the health-checked ones healthy.
- Compose valid, single top-level `volumes:` key.
- Map: 13 nodes, 40 edges.
- `kill-service`: max-duration, **threshold** and manual kill switch rollbacks.
- Remote-endpoint guard refused to run with `DOCKER_HOST` set to a working endpoint.
- `kill-rabbitmq`: dry run, baseline abort, max-duration, manual kill switch; recovery 19.28s.

**Dashboard, end to end through the UI**
- Approved and injected `kill-service`; watched Workshop time out at ~6s while
  Customer and Vehicle held ~15ms; saw the container pass through `starting`;
  confirmed recovery; run landed in history; assessment refreshed itself.
- Kill switch from the dashboard aborted a 55s run at 21.3s, recovered, audited.
- Rejected: missing approval, non-allowlisted experiment, out-of-bounds duration,
  path traversal on `/api/runs`, and starting while STOP is present.

**Integrity**
- 6 of 20 writes divergent on a healthy system; `CustomerId` is the primary key,
  so a same-id retry 500s and a new-id retry duplicates the person.

## Not done

- **No LLM call exists anywhere in the repo.** No key, no network call. The
  design and the deterministic agent are built; the narrative layer is step 3 in
  `docs/AI_ANALYSIS_DESIGN.md`.
- The publish-integrity fix — blocked on the scope decision above.
- No network-delay or database experiment.
- Watchdog restore untested in both experiments (needs PowerShell killed mid-run).
- Threshold rollback untested in `kill-rabbitmq.ps1` — pages stay healthy during a
  broker outage so nothing trips it. Verified in `kill-service.ps1`.
- The broker experiment infers non-delivery from the broker being down rather
  than inspecting consumer state.
- The dashboard polls every 2–2.5s rather than streaming. Live, but not push.

## Highest-priority next action

Get the scope decision on `docs/publish-integrity.md`, then implement it and
re-run `test-publish-integrity.ps1` to record the after.

If that is blocked, build the narrative layer on `analyze.ps1` (step 3 in
`docs/AI_ANALYSIS_DESIGN.md`) — additive, everything it needs exists.

## Safety rules

- Experiments target **only** containers in this Compose project. Both scripts
  enforce a local Windows named-pipe context and refuse a remote `DOCKER_HOST`.
  Tested.
- The dashboard cannot run an arbitrary command: fixed allowlist, integer
  parameters with bounds, explicit approval flag, no overlapping runs.
- The kill switch is a file the scripts poll for. **Keep every model off the
  rollback path, and off every number in the report.**
- `src/*/appsettings.Production.json` carries the upstream sample's hardcoded SQL
  and RabbitMQ passwords. Sandbox-only, inherited from EdwinVW/pitstop. Do not
  imitate and never put a real secret there.
