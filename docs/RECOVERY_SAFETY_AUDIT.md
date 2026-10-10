# Recovery safety audit, 11 October 2026

Base: `0e9b6147e5e1f01c9fd67e672325568311252904` (verified remote main).
No running Pitstop fault was injected. User scripts/dashboard work and PR #3
are excluded. This contribution adds opt-in audit harnesses, not new experiments.

## Findings and execution decision

**High: suspend service-kill and RabbitMQ-kill demonstrations until independent
rollback is implemented and proven.** Both current `Start-Job` bodies failed to
restart disposable targets when their PowerShell parent was forcibly terminated
and when its session exited. Each target remained stopped beyond the five-second
job deadline (observed after eight additional seconds). Comments calling these
jobs independent watchdogs are incorrect. The audit harness explicitly restored
and removed its targets afterward. No runtime safety fix is claimed here.

**Network watchdog retention passed the tested paths.** Actual production
`Invoke-Docker`, `Invoke-Tc`, `Get-Qdisc`, `Remove-Shaping` functions and the
`finally` block were extracted and executed against a newly created container.
Primary removal failure was forced by replacing only `Remove-Shaping` with a
throwing function; the watchdog retained real NET_ADMIN and real tc operations.
This proves retention under that failure, not arbitrary kernel/Docker failure.

| Observation | Successful cleanup | Forced primary cleanup exception |
| --- | --- | --- |
| Applied state | netem handle 28:, delay 50ms | netem handle 28:, delay 50ms |
| Immediately after finally | noqueue; watchdog removed | netem still present; watchdog running |
| Operator output | removal verified True | warning plus full watchdog ID and 75s deadline |
| Eventual network state | noqueue | noqueue after watchdog |
| Synthetic HTTP after removal | 21.0ms | 24.9ms |

Baseline synthetic HTTP was 215.5ms; fault requests were 142.3ms and 119.5ms.
Those individual samples do **not** demonstrate latency degradation because
startup noise dominates the baseline. Injection/removal evidence is the actual
qdisc state; HTTP verifies the synthetic endpoint still responds afterward.
No Pitstop application recovery is claimed by these measurements.

The first disposable run used a 20s watchdog and failed the immediate liveness
assertion. Cleanup completed. Docker/process overhead consumed the observation
window. The repeat used the supported 60s duration plus 15s grace and passed.
This failed attempt is not counted as a pass.

**High residual limitation:** network watchdog is one-shot (`tc ... || true`),
with no retry or durable failure report. Docker unavailability, a hung primary
Docker call, or a failed watchdog tc call is not proven safe by retention alone.
Keep execution disabled wherever these conditions cannot be controlled. A retained
watchdog is a fallback, not a universal recovery guarantee.

## Invariants inspected

| Invariant | Evidence and remaining gap |
| --- | --- |
| Fixed targets and bounded inputs | Catalog, validators and PowerShell ValidateRange; CLI crash minima are 1s while dashboard minima are 5s |
| Approval | Run endpoint requires approval; RabbitMQ CLI requires Execute. Workshop/network CLI invocation injects without a separate approval flag |
| Independent STOP | File checked before baseline, before injection and between fault probes; no LLM participates. Blocking Docker calls have no script-level timeout, so STOP is not instantaneous or guaranteed to interrupt them |
| Concurrency | RunState plus exclusive script lock; unit check rejected competing lock. Lock is checkout-scoped, not machine-wide, and scripts release it before recovery measurement; failed rollback can leave a fault active after lock release |
| Cleanup paths | finally attempts primary cleanup on ordinary errors/STOP/threshold/duration. Hard process death skips finally; crash jobs failed the independent recovery test |
| Visible failed cleanup | Removal exception remains Error; shaping unverified prevents successful exit. If watchdog later succeeds the failed run is still failed, appropriately |
| Recovery evidence | Workshop HTTP/content; network measured latency budget; broker diagnostics, HTTP and a successful customer write. None proves downstream delivery of prior outage events |
| Unavailable Docker | No hard timeout/retry around native Docker commands; wait-job timeout cannot interrupt a blocked primary call. Not dynamically tested |

RabbitMQ customer write/event divergence remains open: HTTP 500 can follow a
committed row with no published event. Restarting the broker does not repair it.
No transactional outbox or business-data change is included.

## Reproduction and test classification

Run from this checkout with .NET 10 SDK, PowerShell 7, Python and local Docker
Desktop Linux containers on PATH. Sidecar must already exist; tests never pull it.

```powershell
dotnet test dashboard.Tests --logger 'console;verbosity=minimal'
pwsh -NoProfile -File tests/recovery_contract_tests.ps1
python tests/disposable_recovery_audit.py --execute-disposable --pwsh pwsh
python tests/disposable_broker_audit.py --execute-disposable
```

- **Unit/mocked:** existing 114 .NET tests passed, zero skipped/failed, 2m59s.
  Fake runner prevents real injection; stub transports do not verify Groq live.
- **Unit/mocked:** 14 recovery contract assertions passed. STOP, exclusive lock,
  verified/unverified/exception cleanup, and extracted RabbitMQ threshold/STOP
  logic. No Docker or HTTP calls.
- **Disposable-container integration:** network success/failure branches passed;
  four parent-job lifecycle observations reproduced missing restart. Zero labelled
  containers remained. All 13 pre-existing running containers retained their exact
  StartedAt values. Dedicated Compose projects were taken down by the harness.
- **Disposable-container integration:** separate broker harness passed real
  diagnostics and synthetic publish/get before and after kill/start. Initial
  readiness took 40.03s; restarted readiness took 39.5s. Zero disposable broker
  containers remained. Data used tmpfs, no host ports or persistent volumes;
  this proves fresh broker functionality after restart, not message durability
  or Pitstop recovery. Three earlier attempts failed readiness; logs identified
  cookie-file EACCES. The passing run explicitly used the image's inspected
  UID 100 / GID 101 with matching tmpfs ownership and bounded Erlang schedulers.

Actual tools in this Windows workspace were `.runtime/dotnet/dotnet.exe`,
`.runtime/pwsh/pwsh.exe`, `.runtime/agentic-venv/Scripts/python.exe` and Docker
Desktop's installed docker.exe. These local paths are not fresh-checkout prerequisites.

Not tested: entire production scripts against a fully isolated Pitstop stack;
real terminal UI closure (session exit was tested); real RabbitMQ threshold-driven
rollback with Pitstop HTTP telemetry; delayed/unavailable Docker; watchdog tc
failure; application/customer/downstream consistency; Windows/Docker host restart;
live Groq/Ollama. No database fault was run. Recovery is not universally proven.

Changed files: this report, `docs/HANDOVER.md`, `docs/PROJECT_STATUS.md`, and the
three files under `tests/` named above. Python syntax compilation passed.
Follow-up priority: durable bounded crash watchdog, fail-closed execution gating,
retain fault ownership/lock through verified recovery, Docker deadlines and retries,
then isolated full-stack consumer/event verification. PR metadata records head hash.
