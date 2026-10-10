# Audit update, 11 October 2026

Recovery audit: network watchdog retention verified on disposable targets. The
crash Start-Job watchdogs failed parent termination and session exit; both now
use detached containers and all four scenarios pass. See
docs/RECOVERY_SAFETY_AUDIT.md. No Pitstop fault was injected by the audit.

Provider audit: wrong-envelope fallback, body deadlines and error-body privacy
hardened; 132 unit/mocked tests passed. No live provider verification. See
docs/AI_RELIABILITY_AUDIT.md.
Experiment branch review: reject wholesale alternative architecture and shared-database crash execution. See docs/EXPERIMENT_COMPATIBILITY_AUDIT.md. No executable experiment imported.
Reproducibility audit: setup and safe demo guide added in docs/REPRODUCIBLE_DEMO.md. Cold builds, live providers and a full fault demo remain unverified.# Project status

Last updated: 2026-10-10, after PR #1 was merged · `main` (see `docs/HANDOVER.md`
for the exact commit) · working tree clean

**Verified** means it was run after the merge and the output observed.
**Exists** means the code is there but nobody has watched it work.

## Day 1 acceptance checklist — reproduced after the merge

Everything below was re-run from `main` after PR #1 merged. These are not the
pre-merge results carried forward.

| # | Requirement | State | Evidence |
| --- | --- | --- | --- |
| A | Sample app running end to end | **Verified** | 13 containers up; the 4 with health checks healthy; module pages 200 |
| B | Dependencies and data flows mapped | **Verified** | Regenerated post-merge: 13 nodes, 40 edges |
| C | Monitoring baseline under steady load | **Verified** | 3 rounds × 3 modules per run; 11–51ms, 0 errors |
| D | Controlled failure with observable impact | **Verified** | Workshop crash and broker outage, both reproduced post-merge |
| E | Automatic stop/rollback, tested | **Verified** | All three rollback paths exercised, plus the endpoint guard |
| F | Short demo | **Verified** | Script in `docs/HACKATHON_DAY1_PLAN.md` |

**The Day 1 minimum is met on `main`**, and now from a fresh clone rather than
from a branch.

## Post-merge verification log

| Check | Command | Result |
| --- | --- | --- |
| Merge present | `git log --oneline origin/main` | `12a5245 Merge pull request #1` present |
| Compose valid | `docker compose -f src/docker-compose.yml config --quiet` | **Pass** — exit 0 |
| No duplicate YAML key | `grep -c '^volumes:' src/docker-compose.yml` | **Pass** — 1 (the conflict fix survived) |
| Dependency map | `./chaos-scripts/map-dependencies.ps1` | **Pass** — 13 nodes, 40 edges |
| Max-duration rollback | `./chaos-scripts/kill-service.ps1 -DurationSeconds 25` | **Pass** — "Maximum fault duration reached", recovered |
| Threshold rollback | `./chaos-scripts/kill-service.ps1 -DurationSeconds 50 -MaxUnaffectedLatencyMs 1` | **Pass** — aborted after 1 probe, "Safety threshold breached: CustomerManagement" |
| Manual kill switch | create `chaos-scripts/STOP` mid-run | **Pass** — "Manual kill switch", recovered |
| Remote endpoint guard | `DOCKER_HOST` set to a *working* endpoint | **Pass** — refused before injecting; container untouched |
| Broker outage | `./chaos-scripts/kill-rabbitmq.ps1 -DowntimeSeconds 25 -Execute` | **Pass** — recovered in 19.28s, divergence detected |
| Broker kill switch | create `chaos-scripts/STOP` mid-run | **Pass** — "Manual kill switch", recovered |
| Resilience scorer | `./chaos-scripts/analyze.ps1` | **Pass** — 7 runs scored |

The threshold path was previously untested. It is exercised by setting the
unaffected-latency limit to 1ms, so a module's normal ~20ms trips it — a real
test of the rollback logic that damages nothing.

## PR #1 — merged

[#1 Add Workshop chaos experiment with monitoring and rollback](https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon/pull/1),
by AP16112, merged as `12a5245`. Verified present and working on `main`.

During integration Git auto-merged `src/docker-compose.yml` cleanly but produced
**two top-level `volumes:` keys** — valid-looking text, broken YAML. Resolved on
the branch before the merge; confirmed still correct on `main`.

PR #1's README still lists "images may lack curl" as a troubleshooting item.
That was fixed in `763b52a`; the note is stale and should be dropped.

## Measurements

**Workshop API crash** — the failure is loud and contained.

| Phase | Customer | Vehicle | Workshop |
| --- | --- | --- | --- |
| Baseline | 15–40ms, 200 | 13–30ms, 200 | 22–172ms, 200 |
| Under fault | 16–30ms, 200 | 14–22ms, 200 | **~5000ms, timeout** |
| Recovered | 14–21ms, 200 | 14–33ms, 200 | 390–490ms, 200 |

**Broker outage** — the failure is silent, and that is the finding.

| | Baseline | Broker down | Recovered |
| --- | --- | --- | --- |
| Module pages | 200, 13–51ms | **200, 9–29ms (unchanged)** | 200, 11–24ms |
| `POST /api/customers` | 201 | **500** | 201 |

All three pages stayed healthy for the whole broker outage, because each API
reads from its own database and never touches the broker. Monitoring page health
alone would have reported this outage as a non-event.

The write path tells the truth. During the outage the API returned 500 **but the
row was committed** (`OutageWriteRowCommitted: true`). A customer exists whom no
downstream service will ever hear about, and nothing replays, because the event
was never recorded anywhere. Reproduced in three runs.

## Resilience scores

From `chaos-scripts/analyze.ps1`, by the published rules in
`docs/AI_ANALYSIS_DESIGN.md`. No model produced any of these numbers.

| Experiment | Score | Note |
| --- | --- | --- |
| kill-service (full run) | 80, 73 | Loses points on graceful degradation — a hard 5s timeout, not a fast fail |
| kill-service (threshold abort) | 98 | **Low confidence, 1 fault probe.** Scored high because little was measured |
| kill-rabbitmq (full run) | 65, 62 | Data integrity scores 0 — the divergence |
| kill-rabbitmq (early abort) | 36 | Baseline guard refused to inject; no fault |

The broker outage scoring below the crash is the intended behaviour: a silent
inconsistency is worse than a visibly broken page.

## Since the merge: agent, dashboard and the integrity finding

**Housekeeping.** PR #1 was already merged (`12a5245`), so nothing was closed -
there was no open PR. `feature/kill-rabbitmq` had one unique behaviour not on
`main` (dumping broker status and logs when recovery fails); it was ported in
`d4685e5` and the branch was then deleted from the remote. `task2-kill-service`
remains and is safe to delete whenever you want.

**Resilience agent.** `analyze.ps1` now aggregates across both experiments
instead of rating runs one at a time: per-experiment scores that exclude
low-confidence runs, a cross-experiment comparison, findings consolidated with
reproduction-driven confidence, topology risk read from the dependency map and
labelled inferred rather than observed, and coverage gaps. Current result:
kill-rabbitmq 70, kill-service 78.

**Dashboard.** ASP.NET Core minimal API at http://localhost:5280 with no package
references. Live Docker status, live HTTP probes and the evidence files; nothing
sampled or seeded, and missing values render as "not measured". Allowlisted
experiments only, integer parameters with bounds, explicit approval, real
preflight, no overlapping runs, kill switch writes the STOP file the scripts
poll for, and approvals and outcomes append to `results/audit.jsonl`.

**The integrity finding changed shape.** It is not outage-only. On a healthy
system 6 of 20 customer registrations (30%) return 500 with the row committed
and no event published. Root cause is `AddTransient` on the publisher combined
with an accidentally `async void` `Connect()`, so every request races a
half-built connection.

**It is not fixed, on purpose.** `src/Infrastructure.Messaging/` is consumed as
a published NuGet package, so editing it does not change the running system -
verified by rebuilding and force-recreating the containers and seeing the same
exception. This also means the earlier `RabbitMQMessageHandler` correction was
inert at runtime. Three options with trade-offs are in
`docs/publish-integrity.md`; the decision is Saurabh's.

## Open work

**P2 — designed, partially built.** `docs/AI_ANALYSIS_DESIGN.md` sets out the
pipeline, the scoring rules and the safety boundary. Step 1, the deterministic
scorer, is **built and working**. The narrative LLM layer, Groq/Gemini
integration and experiment planning are **not built** — no key, no API call
anywhere in the repo yet.

**P3 — not started.** Network-delay and database experiments. No dashboard.

**Known gaps.**
- The watchdog restore path is implemented in both experiments but never tested
  by killing PowerShell mid-run.
- Threshold rollback is verified in `kill-service.ps1`, not in `kill-rabbitmq.ps1`
  — module pages stay healthy during a broker outage, so there is nothing to trip.
- The broker experiment infers non-delivery from the broker being down; it does
  not inspect consumer state.
- Six of ten services have no Docker health check.
- Each broker run leaves `chaos-*` probe customers in the database.
- `chaos-scripts/results/` is gitignored, so evidence for submission must be
  copied out of band.
