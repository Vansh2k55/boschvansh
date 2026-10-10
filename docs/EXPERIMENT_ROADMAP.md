# Experiment catalog: coverage and roadmap

## Why only three today

Nothing is throttling the catalog: `ExperimentCatalog` holds three entries
because three are all that have met the eight requirements in
`docs/AI_EXPERIMENT_DESIGN.md`. Registration is the last step, not the first —
an experiment becomes executable once its cleanup is proven, not when a script
exists. Two more scripts exist today and are surfaced as
`PREPARED — NOT REGISTERED — NOT EXECUTABLE` for precisely that reason.

## Coverage today

| Experiment | Failure mode | Mechanism | What it proves |
| --- | --- | --- | --- |
| `kill-service` | Process gone, connection refused | `docker kill` | Blast-radius containment behind a single API |
| `kill-rabbitmq` | Shared async dependency outage | `docker kill` + `stop` | Read paths survive; the **write path loses events** |
| `network-delay` | Uniform added latency | `tc netem delay` via sidecar | Graceful degradation — slow but serving |

**The gap.** All three are binary-ish and infrastructure-level: a thing is gone,
or everything is uniformly slower. Three common production failure modes are
untested:

- **Partial, non-deterministic failure.** Some requests fail, most succeed —
  the case retries and idempotency are supposed to handle.
- **A synchronous shared dependency failing.** `sqlserver` has six dependents
  and is the highest inferred topology risk, and has never been injected.
- **A hung process.** Connections accepted and never answered, which exhausts
  caller connection pools differently from a refused connection.

## Candidates assessed

Each against the ten questions in the brief. Only distinct failure *mechanisms*
count — a different delay value is not a new experiment.

### 1. `packet-loss` — recommended first

1. **Failure mode:** a percentage of packets dropped, so some requests fail or
   stall while others succeed.
2. **Target:** `workshopmanagementapi`, same fixed target as `network-delay`.
3. **New coverage:** `network-delay` degrades *every* request uniformly. Loss is
   non-deterministic and partial — the regime where retries matter. Different
   mechanism (`netem loss`), different observable.
4. **Blast radius:** one container's egress. No CPU or memory cost.
5. **Metric:** a mixed sample — some probes at baseline latency, some retried or
   failed — versus `network-delay`'s uniformly raised latency.
6. **Bounded and reversed:** identical qdisc lifecycle to `network-delay`:
   duration cap, threshold abort, STOP file, `tc qdisc del`.
7. **Recovery verified:** probe success rate and latency return to baseline.
   Container status proves nothing — it never stops.
8. **Survives orchestrator crash:** yes, via the same detached watchdog, already
   proven for `network-delay` under a hard parent kill.
9. **Tests needed:** registry bounds, loss-percentage bounds, and the disposable
   apply/verify/remove cycle.
10. **Architecture:** no change. Same sidecar, same `DelayParameter` shape
    generalised to a second numeric parameter.

**Risk: low. Value: high.** Reuses the one cleanup path already proven hardest.

### 2. `kill-database` — recommended second

1. **Failure mode:** synchronous shared dependency outage.
2. **Target:** `sqlserver`.
3. **New coverage:** the highest inferred topology risk in the map — six
   dependents, and every user-visible workflow stops without it. `kill-rabbitmq`
   takes out an *asynchronous* dependency and the pages stay up; this one should
   take the pages down. That contrast is the experiment.
4. **Blast radius:** widest of any experiment. Needs the shortest cap.
5. **Metric:** all three module pages fail, not just one — the opposite of
   `kill-service`.
6. **Bounded and reversed:** `docker stop` + restart, cap **30s** (half of
   `kill-service`), threshold abort, STOP file.
7. **Recovery verified:** module pages healthy **and** a customer write
   accepted, as `kill-rabbitmq` already does. Not container status.
8. **Survives orchestrator crash:** yes — `Start-RestartWatchdog`, proven for
   both crash experiments.
9. **Tests needed:** registry bounds, and confirmation that recovery is judged
   on an accepted write rather than on SQL Server's container being up.
10. **Architecture:** no change. Same mechanism as `kill-service`.

**Risk: medium-high, mitigated by a short cap. Value: highest.**

### 3. `pause-service` — recommended third

1. **Failure mode:** process frozen. Connections are accepted and never
   answered.
2. **Target:** `customermanagementapi` or `vehiclemanagementapi`.
3. **New coverage:** genuinely different from a crash. A killed container
   *refuses* connections, which fails fast; a paused one accepts and hangs,
   which exhausts the caller's connection pool. Different symptom, different fix.
4. **Blast radius:** one API, plus whatever its callers do while blocked.
5. **Metric:** probes hang to timeout rather than erroring immediately.
6. **Bounded and reversed:** `docker pause` / `docker unpause`.
7. **Recovery verified:** the module page serves again within budget.
8. **Survives orchestrator crash:** **not yet.** A paused container stays
   paused. This is the gap that keeps PR #3's scripts non-executable, and it
   needs an unpause variant of `Start-RestartWatchdog`.
9. **Tests needed:** the watchdog variant, disposable pause/unpause
   verification, plus the sandbox-ownership guard PR #3 omits.
10. **Architecture:** small addition — one more watchdog verb.

**Risk: medium. Value: high.** Adapts PR #3's scripts to the current standard.

### 4. `cpu-pressure` — possible, lower priority

`docker update --cpus` is reversible without restarting the container, so the
mechanism is bounded and the original quota can be restored. Distinct from every
current experiment: saturation rather than absence. But the observable is softer
— on an idle sandbox a CPU cap may produce no measurable change, which risks an
experiment that cannot demonstrate its own effect. Needs a load generator to be
meaningful.

### Rejected or deferred

| Candidate | Why not |
| --- | --- |
| **Memory pressure** | Lowering `--memory` on a running container can trigger an OOM kill at an unpredictable moment. The fault would not be bounded by the duration, which breaks the core safety requirement. |
| **Network partition** | Selectively blocking traffic between two named services needs `iptables` rules in the sidecar and careful teardown. Feasible but materially more cleanup surface than `netem`, and `kill-rabbitmq` already covers "service A cannot reach service B" for the highest-value pair. |
| **Dependency timeout** | Not a distinct mechanism. A slow dependency *is* `network-delay`; a dead one is `kill-service`. Registering it would be a parameter variation wearing a new name. |

## Order of work

1. `packet-loss` — lowest risk, reuses the proven qdisc lifecycle.
2. `kill-database` — highest value, proven mechanism, tighter bounds.
3. `pause-service` — needs the unpause watchdog first.
4. `cpu-pressure` — only if a load generator makes the effect measurable.

Reaching five executable types means the first two. Six means the third.

## Status

| Experiment | State |
| --- | --- |
| `kill-service`, `kill-rabbitmq`, `network-delay` | **Executable**, cleanup verified |
| `packet-loss` | **Planned** — next |
| `kill-database` | **Planned** |
| `pause-customer-api`, `pause-vehicle-api` | **Prepared, not executable** — no sandbox guard, no independent recovery |
| `cpu-pressure` | **Candidate**, needs a load generator |
| Memory pressure, network partition, dependency timeout | **Not planned**, reasons above |
