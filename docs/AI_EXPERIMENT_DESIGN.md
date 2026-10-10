# Experiment catalog, validation and extension

The registry is the single source of truth for what can execute. The dashboard,
the AI-recommendation validator, preflight and the execution layer all read from
it, so no component can be convinced that something is runnable when it is not.

## Executable experiments

| ID | Target | Fault | Duration | Risk | Status |
| --- | --- | --- | --- | --- | --- |
| `kill-service` | `workshopmanagementapi` | container stop | 5–60s (default 25) | medium | **Implemented, tested** |
| `kill-rabbitmq` | `rabbitmq` | container stop | 5–120s (default 25) | high | **Implemented, tested** |
| `network-delay` | `workshopmanagementapi` | egress latency, 50–2000ms (default 400) | 5–60s (default 20) | medium | **Implemented, tested** |

Each registered experiment defines: a stable ID, its fixed target, bounded
parameters, risk level, expected blast radius, a recovery plan, preconditions
checked by preflight, and a rollback path that does not depend on any model.

## What the model may and may not do

The model **proposes**. It never executes.

- It may only name an experiment that already exists in the registry. Both the
  response schema (enum) and the validator enforce this.
- The target is taken **from the registry**, never from the model. A proposal
  naming a different target is rejected, not corrected.
- Durations outside the registered range are **rejected, not clamped** —
  clamping would run something other than what the operator reviewed.
- A proposal with no hypothesis, expected impact, rollback or expected signals
  is rejected as unreviewable.
- The model gets no shell, no Docker, no filesystem and no arbitrary endpoint.

Passing validation authorizes nothing. It only allows a recommendation to be
shown to a human, who approves it through the run endpoint, which validates
again independently.

## Discovered but not executable

The model may identify a risk the platform cannot yet test — network latency,
resource exhaustion, a dependency timeout. That is useful: it is how coverage
gaps surface.

Such a proposal is surfaced as **`DISCOVERED — NOT IMPLEMENTED — NOT EXECUTABLE`**
and has no path to execution. Turning a discovered idea into a runnable
experiment requires a registered implementation and tests, never a prompt change.

## Adding an experiment

An experiment is only registered once all of this exists:

1. A fixed target, or a list of permitted targets. Never operator- or
   model-supplied.
2. Bounded parameters with a maximum duration justified by blast radius.
3. Preconditions preflight can actually check.
4. An injection path that touches only containers in this Compose project.
5. **A rollback that works when the script dies** — a watchdog, not just a
   `finally` block.
6. **Verified cleanup**: proof the fault is gone afterwards, not an assumption.
7. Evidence written to `results/<run-id>/` as `samples.csv` and `report.json`.
8. Automated tests for validation and the safety boundary.

Until all eight hold, the experiment stays disabled.

## Candidates under evaluation

Adapted from teammate work on `origin/task2-kill-service` (PR #2), which is
referenced for technique and not merged. See the integration decision in
`docs/AI_IMPLEMENTATION_PROGRESS.md`.

### `network-delay` — registered

Adds bounded egress latency to the Workshop API, so the service **degrades**
instead of disappearing. The two crash experiments only ever fail one way:
completely. This is the first measurement of the more common production case.

`tc` runs in a throwaway sidecar sharing the target's network namespace, so the
application image needs no `tc` and the application container never holds
`NET_ADMIN`. The sidecar is `pitstop-netem:1.0`, built locally from
`chaos-scripts/netem-image` — about 16MB against several hundred for a
general-purpose netshoot image — and run with `--pull=never`, so a missing
image fails loudly instead of downloading mid-fault.

**Cleanup was the hard part**, because the qdisc lives in the *target's*
namespace and outlives whatever applied it. Three independent layers, each
verified in this environment before registration:

1. **Normal path** — the script deletes the qdisc and confirms it is gone.
   `ShapingRemovalVerified` in the report records that check, not an assumption.
2. **Orchestrator dies** — a *detached* watchdog container, owned by Docker
   rather than by the script, deletes the qdisc after the duration elapses. It
   survives PowerShell being killed outright, which an in-process `Start-Job`
   does not. Verified by hard-killing the orchestrator mid-fault.
3. **Everything fails** — restarting the container discards the namespace and
   the shaping with it. The script prints that command when it exits unhappy.

It refuses to start if a traffic-control rule is already present, so an
unrelated configuration is never clobbered, and it verifies the fault actually
applied — an experiment that silently failed to inject would otherwise report
a clean run and prove nothing.

**Recovery is judged on observed latency returning to within a budget derived
from the measured baseline.** Container status is meaningless here: the
container never stops.

### `kill-database` — deferred

`sqlserver` has the widest blast radius in the dependency map: six dependents,
and every user-visible workflow stops without it. It is the most informative
untested experiment and the most dangerous.

Deferred until the network-delay path has proven the registry's extension
process end to end, and with a shorter maximum duration than any existing
experiment.

## Safety invariants

These hold for every experiment, present and future:

- Only containers in this Compose project. A remote `DOCKER_HOST` is refused
  before anything is touched.
- A bounded duration, a threshold that aborts, and a STOP-file kill switch
  checked between probes.
- The kill switch is file and timer logic. **No model is on the rollback path**,
  and a provider outage can never prevent stopping an experiment.
- One experiment at a time.
- Explicit operator approval before injection, every time.
- Approvals, parameters, validation decisions, execution and recovery are
  appended to `results/audit.jsonl`.
