# Findings report

AI-driven chaos engineering on a Pitstop microservices sandbox.
Team 28 · generated from 29 recorded runs · 2026-10-11 · commit `ba815a5`.

Every number here comes from `chaos-scripts/results/`, written by the
experiments themselves and scored by `chaos-scripts/analyze.ps1` against the
published rules in `docs/AI_ANALYSIS_DESIGN.md`. **No language model produced
any number in this report.** A model is used only to narrate results, and its
narration cannot change a score.

## Environment

| | |
| --- | --- |
| Application | [EdwinVW/pitstop](https://github.com/EdwinVW/pitstop), Apache 2.0, unmodified except where noted |
| Services | 13 Docker Compose containers |
| Host | Windows 11, Docker Desktop, WSL 2 backend |
| Entry point | `http://127.0.0.1:7005` |
| Runs analysed | 29, across 5 distinct failure types |
| Recovery target | 60s |

## What was injected

| Experiment | Target | Mechanism | Runs | Score |
| --- | --- | --- | --- | --- |
| `network-delay` | `workshopmanagementapi` | `tc netem delay` via sidecar | 6 | **100** |
| `packet-loss` | `workshopmanagementapi` | `tc netem loss` via sidecar | 1 | **100** |
| `kill-service` | `workshopmanagementapi` | `docker kill` + restart | 13 | **76** |
| `kill-rabbitmq` | `rabbitmq` | `docker kill`/`stop` + restart | 6 | **67** |
| `kill-database` | `sqlserver` | `docker stop` + `start` | 2 | **25** |

Scores are comparable only where the same dimensions were measured. Each
experiment's `NeverMeasuredDimensions` is recorded in `analysis.json`;
an unmeasured dimension is never counted as a pass.

## Ranked weaknesses

### 1. High — Database and event stream diverge during a broker outage

**Reproduced in 4 runs. Confidence: high.**

A customer write returns HTTP 500 while the row is committed, and the
`CustomerRegistered` event is never published. Restoring RabbitMQ does not
repair the gap: the event is simply gone, and every downstream consumer — the
workshop event handler, invoicing, notifications, the audit log — is
permanently missing it.

*Evidence:* `WriteDuringOutage.StatusCode=500`, `OutageWriteRowCommitted=True`,
`DivergenceDetected=true`. Independently reproducible with
`chaos-scripts/test-publish-integrity.ps1`.

*Root cause — proven mechanism, not a hypothesis:* the database write and the
event publish are not in one transaction. A fix to the publisher's error
handling (an `async void` Polly retry that swallowed failures) reduced the
divergence rate from roughly 30% of writes to 0% under normal conditions, which
confirms the publish path. But under a genuine broker outage the divergence
still occurs, because no amount of retry logic makes two separate systems
atomic.

*Recommended fix:* write the event to an outbox table in the same transaction
as the row, drain it with a relay, and make the write idempotent on
`CustomerId` so a retry cannot duplicate the record.

**Status: open.** No outbox exists. Nothing in this project should be read as
claiming this is solved.

### 2. High — No graceful degradation path for the shared database

**Observed in both `kill-database` runs. Confidence: high.**

With `sqlserver` stopped, **3 of 3 module pages failed and 0 of 2 writes were
accepted**. This is the widest blast radius measured, and the contrast with the
other outage experiments is the finding:

| Experiment | Module pages still serving |
| --- | --- |
| `kill-rabbitmq` | 3 of 3 — damage is invisible from the UI |
| `kill-service` | 2 of 3 — failure is contained to one module |
| `kill-database` | **0 of 3** |

The resulting score of **25/100** is not a scoring artefact. Blast-radius
containment genuinely earns nothing because nothing is contained. `sqlserver`
carries six dependents and is the highest inferred topology risk in the
dependency map.

*One thing it does well:* writes were **refused** (HTTP 500), not accepted and
lost. That is the opposite of finding 1 — refusal loses the request, divergence
loses the truth.

*Recommended fix:* a read-through cache or a read replica so browse-only pages
survive, and a circuit breaker so callers fail fast instead of hanging.

### 3. Medium — Injected service hard-times-out instead of failing fast

**Reproduced in 12 runs. Confidence: high.**

When the Workshop API is killed, its page does not render the offline fallback
— the caller blocks until the client timeout. Measured: 0 of 5 target probes
degraded gracefully rather than timing out, with probe latencies pinned at the
5s and 10s client timeouts rather than failing immediately.

*Hypothesis, not proven:* the caller has no timeout or circuit breaker on this
dependency, so a dead service costs a full request timeout per call.

*Recommended fix:* a short client timeout and a circuit breaker, so the module
renders its offline page immediately instead of blocking.

## What recovered, and how fast

All 29 runs that injected a fault recovered. Measured recovery times:

| Experiment | Recovery after restore |
| --- | --- |
| `kill-service` | 2.9–6.0s |
| `packet-loss` | immediate on qdisc removal |
| `kill-database` | 23.5s and 58.6s |

Recovery is judged on application behaviour — module pages serving, and for
`kill-rabbitmq` and `kill-database` a customer write being accepted — never on
container status. A container is running well before it answers queries.

Two runs appear as "not recovered" in the raw data. Both aborted **before
injecting anything** — one on a failed baseline write, one on the kill switch
during baseline — so nothing was broken and nothing had to recover. An earlier
version of the analyzer counted these as High-severity recovery failures. That
was a defect in the report, not in the system, and it was fixed: the finding
now requires that a fault actually reached the system. See "Corrections" below.

## Resilience assessment methodology

Five weighted dimensions, scored only when the run measured them:

| Dimension | Weight | Measured by |
| --- | --- | --- |
| Blast radius containment | 25 | Non-target probes staying healthy during the fault |
| Graceful degradation | 20 | Target probes rendering an offline page rather than timing out |
| Recovery | 25 | Observed recovery time against a 60s target |
| Data integrity | 20 | A write exercised during the fault, and whether its event survived |
| Safety controls | 10 | Restoration verified and a recorded rollback reason |

Confidence is tied to probe count: a high score from few probes says less than
the same score from many. Rules are in `docs/AI_ANALYSIS_DESIGN.md`.

## Limitations and unmeasured properties

Stated plainly, because an unmeasured property is not a passing one.

- **Single-host sandbox.** 13 containers on one machine. Nothing here measures
  multi-node, network-partition or cloud-provider failure modes.
- **No load generator.** All measurements are against a near-idle system, so
  these numbers are a floor. Under real traffic, timeouts and pool exhaustion
  would appear earlier and worse.
- **`packet-loss` has one recorded run.** Its 100 score rests on a single
  observation and should not be read as settled.
- **The configured packet-loss percentage is not a request-failure rate.** At
  20% configured loss, 0 of 9 probes failed — TCP retransmitted. The report
  records `LossMeasurementCaveat` for exactly this reason. What was measured is
  the latency spread, not a failure percentage.
- **Data integrity is unmeasured for three experiments.** Only `kill-rabbitmq`
  and `kill-database` exercise a write path.
- **A hung process has never been injected.** `pause-service` remains
  unregistered because its unpause watchdog does not exist. A paused container
  accepts connections and never answers, which exhausts caller pools differently
  from a refused connection. That failure mode is untested.
- **No before/after comparison after a fix.** The publisher fix predates this
  measurement series, so the 30%-to-0% figure comes from that earlier work
  rather than from a paired rerun in this archive.
- **Sandbox credentials.** `src/*/appsettings.Production.json` and the
  `sqlserver` Compose service carry hardcoded passwords. That is upstream
  sample-app code for a throwaway local sandbox. No real credential is involved
  and no credential value is reproduced here.

## Corrections made to this report's own tooling

Both found by reading output against the raw data rather than trusting it.

1. **A fabricated High-severity finding.** "Service did not recover within the
   timeout" was raised whenever `Recovered` was false, including for runs that
   aborted before injecting a fault. Both such runs carry `FaultStartedUtc=null`
   and "No fault was injected". The analyzer now requires that a fault actually
   started. Without this, the report would have claimed a recovery weakness the
   evidence does not support.
2. **"Not probed" versus "probed and failed".** The dashboard chart reported a
   module as "not probed" during a fault when it had been probed and every probe
   had failed — collapsing the absence of a measurement into the measurement
   itself. It now distinguishes the two.
