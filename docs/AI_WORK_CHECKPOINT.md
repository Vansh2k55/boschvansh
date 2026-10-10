# Current checkpoint

Authoritative state. If this disagrees with a conversation summary, trust this
file and `git log`.

**Updated:** 2026-10-11 · **Branch:** `main` · **Commit:** see `git log -1`
**Working tree:** clean · **Remote:** `origin/main` in sync

## Where the work is

| Phase | State |
| --- | --- |
| 0 Recover and checkpoint | **Done** |
| 1 Verify Groq live | **Provider path passed. End-to-end UNVERIFIED** — see below |
| 2 Audit workflow against milestones | Not started |
| 3 Implement `packet-loss` | **Done** — registered and executable, evidence below |
| 4 Reach five executable experiments | In progress — four executable; `kill-database` is next |
| 5–8 | Not started |

## Phase 1 — Groq: provider path passed, application path not yet

Two things were being conflated. They are now separated, because only one of
them has evidence.

**Passed — the provider path.** `tests/groq_live_check.ps1` returned status 200
with usable output for both `openai/gpt-oss-20b` and `openai/gpt-oss-120b`,
against the real schema fetched from `/api/ai/schema`. Both earlier defects are
fixed: the 413 (`EvidenceBudget`, `9c36885`) and the 400 validation failure,
which was a missing `max_completion_tokens` — the model was being cut off
mid-object, so its truncated JSON could never validate (`ad4f118`).

**UNVERIFIED — the application path.** No dashboard recommendation has yet
reported `provider: groq`. The live check posts to Groq directly and bypasses
the application, so it cannot stand in for the real workflow. Until a real
recommendation comes back with `provider: groq`, Groq is **not** verified
end-to-end. A fallback to Ollama is not a pass.

The check itself overstated its result and was corrected in this phase: it
reported "schema-valid JSON" when it had only parsed JSON. It now checks the
returned object against the schema's own top-level `required` list, so a model
that returns well-formed JSON with properties missing is reported as a failure
rather than a pass. Its verdict also now states in plain words that it covers
the provider path only.

**To close this**, in a window that has `GROQ_API_KEY` set: start the dashboard,
request a recommendation from the UI, and confirm the response says
`provider: groq`. No further standalone provider tests are needed — that
question is already answered.

## Experiment catalog

Four executable, cleanup verified: `kill-service`, `kill-rabbitmq`,
`network-delay`, `packet-loss`. Target is five. Order and per-candidate
reasoning are in `docs/EXPERIMENT_ROADMAP.md`:

1. ~~`packet-loss`~~ — **done**, evidence below
2. `kill-database` — next
3. `pause-service` — blocked on an unpause watchdog; stays non-executable

### `packet-loss` — registered 2026-10-11

Built under a **documented exception** to the standing rule that catalog work
waits on Groq verification. The operator granted it explicitly, on the grounds
that `packet-loss` shares none of Groq's machinery: it reuses the qdisc
lifecycle already proven for `network-delay`, and the AI layer only gains one
more allowlisted name it may recommend. The exception is recorded here so the
sequence is not mistaken for the gate having been quietly dropped.

Evidence, in the order it was produced:

1. **Disposable cycle** on the real container: `qdisc noqueue` →
   `netem 28: ... loss 20%` → `noqueue`. The apply step's verification matched
   `loss 20%`, so an ineffective qdisc cannot be reported as injected.
2. **Live bounded run**, 15s at 20% loss. Workshop latencies came back erratic
   — `862.9, 437.7, 1952.5, 649.3, 239.3, 440.3, 13.5, 444, 649.9ms` — clearly
   distinct from `network-delay`'s uniform ~3200–5000ms. Customer and Vehicle
   held at ~8–18ms, so the blast radius was contained.
3. **Cleanup**: `Shaping removed and verified: True`; watchdog container
   removed; target re-checked afterwards and back to `qdisc noqueue`, with no
   leftover sidecar containers and no STOP file.
4. **Honesty constraint**: `0/9` probes failed at 20% configured loss, because
   TCP retransmits absorbed it. The report therefore carries
   `LossMeasurementCaveat` stating that the configured drop percentage is not an
   observed request-failure rate. The measured signal is the latency spread.
5. **Tests**: 170 passing, 12 specific to `packet-loss` — registry bounds, the
   40% ceiling, out-of-range values rejected rather than clamped, approval still
   required, a model unable to retarget it, and the runner reachable only
   through the fake.

The delay-only second parameter was generalised into a labelled,
unit-carrying `IntensityParameter`, so `network-delay` reports "Delay ms" and
`packet-loss` "Loss %" through one mechanism instead of two.

Two scripts are surfaced as `PREPARED — NOT REGISTERED — NOT EXECUTABLE`.

## Instructions for the next LLM

**Startup**

```powershell
docker compose -f src/docker-compose.yml up -d            # 13 containers
docker build -t pitstop-netem:1.0 chaos-scripts/netem-image   # once
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

Dashboard `http://localhost:5280`, app `http://127.0.0.1:7005` — use the IPv4
form for probes, a `wslrelay` process can hold `[::1]:7005` without serving it.

**Architecture**

- `src/` upstream Pitstop; `chaos-scripts/` experiments, `ChaosSafety.psm1`
  guards, `analyze.ps1` deterministic agent; `dashboard/` ASP.NET Core console
  and `Ai/` provider layer; `dashboard.Tests/` 170 tests; `tests/` disposable
  and contract harnesses.
- `ExperimentCatalog` is the only allowlist. `RecommendationValidator` and
  `DiscoveryValidator` are the model boundary. `IExperimentRunner` is the only
  path to a real fault, and tests substitute a fake.

**Tests**

```powershell
dotnet test dashboard.Tests                                   # 170 passing
./tests/recovery_contract_tests.ps1                           # 23 assertions
python tests/disposable_recovery_audit.py --execute-disposable
./tests/groq_live_check.ps1                                   # live, needs the key
```

Stop any running Dashboard before building, or the build fails on a file lock.
To avoid killing the operator's keyed session, build with
`-p:BaseOutputPath=<temp>`.

**Safety restrictions — do not weaken**

- Only `ExperimentCatalog` entries run; targets come from the catalog, never the
  model; out-of-range values are rejected, not clamped.
- STOP file, duration caps, preflight, approval, detached watchdogs, verified
  cleanup. Recovery is judged on application behaviour, never container status.
- No secrets in Git, logs or responses. Never print `GROQ_API_KEY`.
- **Do not change the dependency map UI.**
- Teammate branches are never merged, rebased or modified.

**Not verified**

- Groq live completion (above).
- `pause-service` recovery.
- RabbitMQ write/event divergence is **real and open** — a row commits while its
  event is never published, and restoring the broker does not repair it. No
  outbox exists.
