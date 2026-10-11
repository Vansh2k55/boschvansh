# Experiment catalog: coverage and roadmap

## Why five today

Nothing is throttling the catalog: `ExperimentCatalog` holds five entries
because five are all that have met the eight requirements in
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
| `packet-loss` | Partial, non-deterministic loss | `tc netem loss` via sidecar | Erratic latency under retransmit — most requests still served |
| `kill-database` | Synchronous shared dependency outage | `docker stop` + `start` | Total failure — no graceful degradation path exists |

**The gap.** Two of the three original gaps closed on 2026-10-11:
`packet-loss` covered partial failure, and `kill-database` covered the
synchronous shared dependency. One remains:

- **A hung process.** Connections accepted and never answered, which exhausts
  caller connection pools differently from a refused connection. This is
  `pause-service`, and it stays unregistered until an unpause watchdog exists.

## Candidates assessed

Each against the ten questions in the brief. Only distinct failure *mechanisms*
count — a different delay value is not a new experiment.

### 1. `packet-loss` — DONE, registered and executable

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
10. **Architecture:** no change. Same sidecar. The delay-only second parameter
    was generalised into a labelled, unit-carrying `IntensityParameter`, so loss
    reuses it rather than adding a parallel mechanism.

**Risk: low. Value: high.** Reuses the one cleanup path already proven hardest.

**Evidence, 2026-10-11.** Registered after the proof, not before:

- *Disposable cycle:* `qdisc noqueue` → `netem 28: ... loss 20%` → `noqueue`.
  The apply step's verification matched `loss 20%`, so a silently ineffective
  qdisc cannot pass as injected.
- *Live bounded run* (15s, 20% loss): the Workshop module produced a genuinely
  distinct signature — `862.9, 437.7, 1952.5, 649.3, 239.3, 440.3, 13.5, 444,
  649.9ms`, erratic rather than `network-delay`'s uniform ~3200–5000ms.
  Customer and Vehicle stayed at ~8–18ms, so the blast radius held.
- *Cleanup:* `Shaping removed and verified: True`, watchdog container removed,
  target back to `qdisc noqueue` — re-confirmed after the run.
- *Honesty:* `0/9` probes failed at 20% configured loss. The report therefore
  carries `LossMeasurementCaveat`: the configured drop percentage is **not** an
  observed request-failure rate, because TCP retransmits absorb most of it. The
  latency spread is what was actually measured.
- *Tests:* 170 passing, 12 specific to `packet-loss` — registry bounds, the 40%
  cap, out-of-range loss rejected rather than clamped, approval still required,
  a model unable to retarget it, and the runner reached only through the fake.

### 2. `kill-database` — DONE, registered and executable

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

**Evidence, 2026-10-11.** The gating question was not the mechanism —
`docker stop`/`start` was already proven — but whether the *application*
recovers. If a dependent API cached a dead connection, restoring the target
would not restore the system, and no amount of careful scripting would make
this experiment reversible. So that was tested first, disposably, before a line
of the experiment was written:

- *Self-healing probe:* with `sqlserver` stopped for 20s and **no dependent
  service restarted**, the stack returned to all-pages-healthy plus an accepted
  write **58.6s** after `docker start`. The fault is reversible by restoring the
  target alone.
- *Live run* (15s): **3 of 3** module pages failed and **0 of 2** writes were
  accepted — the predicted contrast with `kill-service` (two modules stay up)
  and `kill-rabbitmq` (all three stay up) confirmed by measurement. Recovered
  in 23.5s.
- *Data safety:* `docker stop`/`start` only, never `rm`. The container and the
  `sqlserverdata` volume survive, so nothing is discarded. Two tests assert the
  recovery plan says so, and the contract harness asserts the cleanup block
  contains no `rm`.
- *Kill switch:* with a STOP file present the experiment refused to start and
  reported "No fault was injected"; `StartedAt` on the container confirmed it
  was untouched.
- *Cleanup under failure:* the real `finally` block was executed against a
  synthetic container that reports `exited` after `docker start`. It declined to
  declare recovery and kept the watchdog alive, which is the only remaining
  route back in that case.
- *Graceful stop, not SIGKILL.* A database is the one target where SIGKILL
  manufactures a corruption risk the experiment was not designed to study, so
  this one uses `docker stop`. The outage is just as total.

**Two measurement defects were found and fixed by running it.** The first live
run probed only 2 of 3 modules and attempted no write, because each failing
probe blocked for its full 10s timeout and overran the window — yet the report
still carried a fixed sentence claiming a total blast radius. The fault-phase
timeout is now 3s, the first round always completes in full, and both notes are
built from the run's own samples. The report also states the observed fault
duration (20.7s for a 15s request) rather than implying the request was exact.

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

1. ~~`packet-loss`~~ — **done**, evidence above.
2. ~~`kill-database`~~ — **done**, evidence above.
3. `pause-service` — needs the unpause watchdog first.
4. `cpu-pressure` — only if a load generator makes the effect measurable.

**Five are executable, which was the target.** A sixth would be
`pause-service`, and it stays blocked until an unpause watchdog exists and has
been tested. It is not made executable to reach a number.

## Status

| Experiment | State |
| --- | --- |
| `kill-service`, `kill-rabbitmq`, `network-delay` | **Executable**, cleanup verified |
| `packet-loss` | **Executable**, cleanup verified 2026-10-11 |
| `kill-database` | **Executable**, recovery verified 2026-10-11 |
| `pause-customer-api`, `pause-vehicle-api` | **Prepared, not executable** — no sandbox guard, no independent recovery |
| `cpu-pressure` | **Candidate**, needs a load generator |
| Memory pressure, network partition, dependency timeout | **Not planned**, reasons above |
