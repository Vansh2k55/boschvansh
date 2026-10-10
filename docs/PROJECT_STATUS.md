# Project status

Last updated: 2026-10-10T09:13Z · `main` @ `ae492ca` (pushed, tree clean)

The distinction that matters below: **verified** means it was run and the output
observed in this session. **Exists** means the code is there but nobody has
watched it work.

## Day 1 acceptance checklist

| # | Requirement | State | Evidence |
| --- | --- | --- | --- |
| A | Sample app running end to end in the sandbox | **Verified** | 13 containers up; APIs and web app healthy; module pages return 200 |
| B | Services, dependencies and data flows mapped | **Verified** | `docs/dependency-map.md`, 13 nodes / 40 edges, regenerated three times |
| C | Monitoring baseline under steady load | **Verified, on PR #1** | 3 baseline rounds × 3 modules per run; 15–40ms, 0 errors |
| D | One controlled failure injected and observed | **Verified, on PR #1** | 2 runs; Workshop times out ~5s, Customer/Vehicle unaffected |
| E | Automatic stop/rollback, tested once | **Verified, on PR #1** | Max-duration rollback and manual kill switch both exercised; recovery confirmed both times |
| F | Short demo: app running, one safe failure, impact visible | **Verified** | Demo script in `docs/HACKATHON_DAY1_PLAN.md` |

**The Day 1 minimum is met** — running app, dependency map, one safe failure
injection with a working kill switch — but C, D and E live on PR #1 and are not
on `main` yet. Merging PR #1 is what makes the checklist true for a fresh clone.

## What landed this session

| Commit | Change |
| --- | --- |
| `763b52a` | Four fixes that make the stack boot healthy end to end |
| `ae492ca` | Dependency and data-flow mapper, overlay, generated map |

`763b52a` in detail — the stack did not reach a usable steady state before it:

- `RabbitMQMessageHandler` passed an async lambda to Polly's `Policy.Execute`,
  binding the `Action` overload and compiling to `async void`. The retry policy
  returned before the connection existed and swallowed failures, so event-driven
  services ran without ever subscribing to their queue.
- `WorkshopManagementAPI` resolves its event store from a transient repository,
  so the store and its health check only appeared on the first request.
- The aspnet base image has no `curl`, which every service's `HEALTHCHECK` calls,
  so containers were permanently unhealthy.
- SQL Server and RabbitMQ used host bind mounts that fail on Windows/OneDrive
  paths, and services used bare `depends_on`, so APIs started before their
  dependencies accepted connections.

## Pull requests

| PR | Branch | Author | Review | Merge |
| --- | --- | --- | --- | --- |
| [#1 Add Workshop chaos experiment with monitoring and rollback](https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon/pull/1) | `task2-kill-service` @ `d30af72` | AP16112 | **Approved — ran it twice, both rollback paths work** | **Pending.** Agent was blocked from merging without review; needs `gh pr merge 1 --merge` |

Review notes: good work — the watchdog job restores the container even if the
main loop stalls, probes assert on page content as well as status code, and the
context check refuses a non-local Docker endpoint.

One real defect was found and fixed during integration. Git auto-merged
`src/docker-compose.yml` cleanly but the result had **two top-level `volumes:`
keys** — the PR added one at the top of the file, `main` already had one at the
bottom. Textually clean, semantically broken YAML. Resolved on the PR branch by
keeping `main`'s block (a superset: it also declares `rabbitmqdata`), then
validated with `docker compose config --quiet`.

PR #1's README lists "unhealthy container but working pages, images may lack
curl" as an open troubleshooting item. `763b52a` fixes that; the note can be
dropped from the README after the merge.

## Measurements

| Phase | Customer | Vehicle | Workshop |
| --- | --- | --- | --- |
| Baseline | 15–40ms, 200 | 15–45ms, 200 | 26–42ms, 200 |
| Under fault | 16–30ms, 200 | 16–41ms, 200 | ~5000ms, timeout |
| Recovered | 13–29ms, 200 | 14–42ms, 200 | 390–530ms, 200 |

Recovery took one to two probe rounds after the container restarted — the first
post-restart request pays .NET and EF warm-up, which is why Workshop sits near
500ms while the others are back at 20ms.

## Open work

**P2 — not started.** No AI or LLM integration of any kind: no Groq, no Gemini,
no API key configured, no deterministic fallback. No resilience score, no
generated report. Day 2 depends on this.

**P2/P3 — not started.** Only one failure type exists. Network delay, broker
kill and database fault are assigned but unwritten. No dashboard.

**Known gaps.** The threshold-breach rollback path is implemented but never
exercised. Six of ten services have no Docker health check. `chaos-scripts/results/`
is gitignored, so evidence for submission must be copied out of band.
