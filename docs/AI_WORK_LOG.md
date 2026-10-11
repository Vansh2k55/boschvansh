# Work log

Chronological. Newest first. Commits are real; anything unverified says so.

## 2026-10-11 — Phases 0–6, final delivery

**State at start:** `main` @ `d6a3eb5`, clean, synced. Ended at `285557b`.

**Phase 0 — audit, not trust.** Prior summaries were re-checked. Two claims in
my own last report were stale: the dashboard had no "Day 1"/"Day 2" labels to
remove, and 193 tests was accurate. `docs/REQUIREMENTS_EVIDENCE.md` records
every requirement against what actually satisfies it.

**A real safety defect, found by closing a test gap.** `kill-service` was the
one experiment whose cleanup block the contract harness never executed. It and
`kill-rabbitmq` declared restoration from the exit code of `docker start`
alone. A zero exit code means the start command was accepted, not that the
container stayed up - and that value decides whether `Stop-RestartWatchdog`
deletes the watchdog. A container that started and immediately died would have
lost its only independent route back, at the moment it was needed. Both now
verify `State.Status`; `kill-database` verified it but only printed the reason,
so it now records it. Harness: 32 → 56 assertions, all three crash experiments'
real `finally` blocks executed against a healthy restart and a stuck container.
Confirmed with a live `kill-service` run: restored, recovered in 6.04s.

**The AI planner was broken, and only a live call showed it.** One
recommendation through the real dashboard returned **HTTP 503**: Groq 400, and
`ollama: Ollama timed out after 240s`. No recommendation at all. Both models
were then timed against the real schema and the real 8.2KB evidence payload,
using OllamaClient's exact request shape:

    qwen2.5:3b   18.4s cold, 9.1-9.5s warm, conforming, deterministic
    qwen3.5:4b   exceeded 240s, produced nothing

`qwen2.5:3b` is now the default in appsettings **and** in the C# default, so a
missing config cannot restore a model that does not finish.

**Groq: still UNVERIFIED, and now precisely diagnosed.** The schema was
eliminated offline - it walks clean against strict structured-output rules at
all three object levels, no unsupported keywords. And the running dashboard
turned out to be a **stale build**: its `/api/state` offers 3 executable
experiments, not 5, so it predates `59874e1` and therefore `ad4f118`, which
added `max_completion_tokens`. Its error also lacked the `failed_generation`
tail that commit appends. The 400 came from a build without the fix. The fix is
committed and has never run. No further Groq calls were spent.

**A fabricated finding, removed.** The analyzer raised "Service did not recover
within the timeout" at High severity whenever `Recovered` was false - including
runs that aborted before injecting anything. Both such runs carry
`FaultStartedUtc=null` and "No fault was injected". The safety controls working
were being scored as a resilience weakness, and it would have shipped in the
findings report. Across 29 runs there are now **zero genuine recovery
failures**.

**Dashboard.** Nine milestone-oriented areas, each with a status pill computed
from live state - nothing hardcoded to a happy value. A baseline/impact/recovery
chart drawn from recorded samples, with two honesty defects fixed while
verifying it in a browser: "not probed" was being reported for a module that was
probed and failed every time, and failed probes' timeout values were setting the
y-axis, flattening every real 10-22ms reading under a 5168ms ceiling.

**Deliverables.** All five written from real evidence and verified through a new
`/api/submission` endpoint that reads the filesystem: README (which had claimed
the dashboard and AI planning were "planned work"), architecture overview,
findings report, demo script, team form. **No video is recorded**, and the
endpoint reports that rather than inferring otherwise.

**Final:** 193 tests, 56 contract assertions, 13/13 containers, no leftover
watchdogs, no STOP file, `qdisc noqueue` on the shaping target, all three pages
200 at 14-20ms.

## 2026-10-11 — Phase 4, `kill-database` reaches five executable experiments

**State at start:** `main` @ `59874e1`, clean, synced. No Groq API calls in this
phase either.

**The gate was not the mechanism.** `docker stop`/`start` and the restart
watchdog were already proven by `kill-service`. The question that decided
whether this experiment could exist at all was whether the *application*
recovers: if a dependent API cached a dead connection, restoring `sqlserver`
would not restore the system, and the fault would not be reversible however
carefully the script was written. So that was tested first, with a throwaway
probe, before any experiment code was written.

**Probe result:** `sqlserver` stopped for 20s, **no dependent service
restarted**, and the stack returned to all-pages-healthy plus an accepted write
**58.6s** after `docker start`. Reversible by restoring the target alone. That
58.6s also sized the recovery budget, which is why it is minutes rather than
seconds.

**Live run** (15s, `-Execute`): **3/3** module pages failed, **0/2** writes
accepted, recovered in 23.5s. The contrast the dependency map predicted is now
measured: `kill-service` leaves two modules up, `kill-rabbitmq` leaves all
three up, `kill-database` leaves none. A synchronous shared dependency has no
graceful degradation path here.

**Two measurement defects, found by running it rather than by reading it.**

1. The first live run probed **2 of 3** modules and attempted **no write**,
   because each failing probe blocked for its full 10s timeout and overran the
   15s window. The report nonetheless printed a fixed sentence claiming every
   module failed, and a second claiming writes were refused — with
   `FaultWrites: 0` sitting next to it. Hardcoded conclusions surviving a run
   that did not support them.
2. The downtime cap was soft: 24.8s observed for a 15s request.

Fixed: a 3s fault-phase probe timeout (a healthy page answers in under 150ms,
so 10s buys nothing), a first round that always completes in full so the
headline claim always has full data, and both notes now built from the run's own
samples — including branches that say "incomplete" or "no write was attempted"
when that is what happened. The report states requested *and* observed duration
(20.7s for 15s) instead of conflating them. Re-run confirmed: 3/3 modules
probed and failed, 2/2 writes refused.

**Safety, each verified rather than asserted:**

- `docker stop`, not `docker kill`. A database is the one target where SIGKILL
  manufactures a corruption risk the experiment was not designed to study.
- Never `rm`. The container and the `sqlserverdata` volume survive, so no data
  is discarded. Two tests and a contract assertion enforce it.
- Tightest cap in the catalog (30s) and `-Execute` required, because with every
  module expected to fail there is no unaffected-module threshold to abort on.
  A test asserts no other experiment has a shorter cap.
- STOP file: refused to start, reported "No fault was injected", and the
  container's `StartedAt` confirmed nothing was touched.
- The real `finally` block executed against a container reporting `exited`
  after `docker start`: declined to declare recovery, kept the watchdog.

**Tests:** 193 passing (was 170), 32 contract assertions (was 23). Six failures
along the way were expectation updates for the fifth entry. Two of them had
used `kill-database` as the example of a name a model might invent that must be
refused; those now use `pause-service`, `cpu-pressure` and `network-partition`,
which are still only candidates. One assertion was *tightened* rather than
relaxed: a discovery check matching any name containing "database" now matches
the discovery's actual title, since the discovery is about database latency and
`kill-database` is an outage.

**Five executable experiments, which was the target.** `pause-service` stays
unregistered: its unpause watchdog does not exist, and it is not promoted to
make the count look better.

**Groq status unchanged: provider path passed, end-to-end UNVERIFIED.**

## 2026-10-11 — Phase 3, `packet-loss` registered as the fourth experiment

**State at start:** `main` @ `ad4f118`, clean, synced. No Groq API calls were
made in this phase, by instruction.

**Why this ran before Groq was closed out.** A documented exception, granted
explicitly by the operator. `packet-loss` shares no machinery with the provider
layer: it reuses the qdisc lifecycle already proven for `network-delay`, and the
AI layer only gains one more allowlisted name it may recommend. Recorded so the
order of work is not mistaken for the Groq gate having been dropped.

**Evidence, in the order produced — registration was the last step:**

1. *Disposable cycle* on the real container: `qdisc noqueue` →
   `netem 28: ... loss 20%` → `noqueue`. The apply step's verification matched
   `loss 20%`, so an ineffective qdisc cannot be reported as injected.
2. *Live bounded run*, 15s at 20% loss. Workshop latencies: `862.9, 437.7,
   1952.5, 649.3, 239.3, 440.3, 13.5, 444, 649.9ms` — erratic, and clearly a
   different fault from `network-delay`'s uniform ~3200–5000ms. Customer and
   Vehicle stayed at ~8–18ms.
3. *Cleanup:* `Shaping removed and verified: True`, watchdog container removed.
   Re-checked after the run: target back to `qdisc noqueue`, no leftover sidecar
   containers, no STOP file, 13/13 compose services running.
4. *Tests:* **170 passing, 0 failing.** 12 are specific to `packet-loss`.

**The measurement I refused to claim.** At 20% configured loss, `0/9` probes
failed — TCP retransmitted and the requests still completed, just erratically.
Reporting "20% packet loss" as a failure rate would have been inventing a
measurement, so `report.json` carries `LossMeasurementCaveat` saying the
configured drop is not an observed request-failure rate, and the latency spread
is what was actually measured. The run's own closing line — `failed probes 0/9`
— is consistent with it.

**Refactor, not a new mechanism.** The delay-only second parameter became a
labelled, unit-carrying `IntensityParameter`. `network-delay` renders "Delay ms"
and `packet-loss` "Loss %" through one path, rather than each fault type growing
its own parameter. Six call sites changed; `DelayMilliseconds` is gone from the
API surface, replaced by `Intensity`.

**Corrected an overstated claim of my own.** `tests/groq_live_check.ps1`
reported "schema-valid JSON" when it had only parsed JSON — a model could have
returned well-formed JSON missing half the required properties and been called a
pass. It now checks the returned object against the schema's own top-level
`required` list and reports exactly which fields are missing. While fixing it I
first reached one level too deep (`.schema.required`), which would have hit the
nested discoveries item — the same trap that produced a false positive in an
earlier phase. Verified against the source and corrected. The logic was
rehearsed offline against conforming and non-conforming objects; **no Groq calls
were made.** Its verdict now also states that it covers the provider path only.

**Groq status unchanged: provider path passed, end-to-end UNVERIFIED.** No
dashboard recommendation has reported `provider: groq` yet. Not retested here.

**Unchanged by instruction:** the dependency map UI, and `pause-service`, which
stays non-executable until an unpause watchdog exists and has been tested.
Four experiments are executable; the fifth is `kill-database`.

## 2026-10-11 — Phase 1, Groq live verification

**State at start:** `main` @ `9451fde`, clean, synced. `EvidenceBudget` present
in source and wired into the recommend endpoint.

**Live call 1** (before the evidence fix was running):
`413 Request too large for model openai/gpt-oss-20b`. Fell back to Ollama.
Measured payload: ~54KB, 45KB of it 25 per-run analyses. Fixed in `9c36885`.

**Live call 2** (175s, HTTP 200 from our endpoint):
`Groq returned 400. Upstream said: Failed to validate JSON. Please adjust your
prompt.` Fell back to Ollama, which answered and passed safety validation
(`allowed: true`, kill-rabbitmq, 2 discoveries).

**The 413 is resolved.** The request now gets past size and fails later, on
schema validation.

**Schema checked against Groq strict rules — compliant.** All 15 top-level
properties in `required`, `additionalProperties: false` on both objects, no
unsupported keywords. First automated check gave a false positive because it
matched the nested discoveries `required`; re-checked by reading the source.

**Hypothesis, not yet proven:** `openai/gpt-oss-20b` cannot reliably produce
conforming output for a 15-property schema containing a nested array of
7-property objects. A larger model may.

**Added to settle it with evidence:**
- `GET /api/ai/schema` — read-only, returns the exact schemas sent to providers.
  No evidence, no secrets. Lets a live test use the real schema without
  duplicating it.
- `tests/groq_live_check.ps1` — a genuine live-provider test. Tries 20b and
  120b against the real schema, reports status, upstream message and whether the
  output parsed. Reads `GROQ_API_KEY` from the environment and never prints it.
  Verified it exits cleanly with guidance when no key is set.

**Groq status: UNVERIFIED.** Every call so far fell back to Ollama. A status
endpoint showing Groq available is not a successful completion.

**Blocked on the operator:** the dashboard must restart to serve
`/api/ai/schema`, then the live check runs. Commands are in
`docs/AI_WORK_CHECKPOINT.md`.

**Builds:** compiled with `-p:BaseOutputPath=<temp>` to avoid killing the
operator's keyed dashboard session. Build succeeded.

**Not run this phase:** the full test suite, because the running dashboard holds
the normal output path. Last known result was 158/158 at `9451fde`.
