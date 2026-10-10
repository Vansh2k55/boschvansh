# Work log

Chronological. Newest first. Commits are real; anything unverified says so.

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
