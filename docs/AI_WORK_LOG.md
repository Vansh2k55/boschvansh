# Work log

Chronological. Newest first. Commits are real; anything unverified says so.

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
