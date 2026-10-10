# AI resilience platform — implementation plan

Phased plan for turning the working chaos sandbox into a self-analyzing,
AI-assisted resilience platform.

**Status: Phase 0 complete. Phase 1 blocked on a branch-reconciliation decision
— see `docs/AI_IMPLEMENTATION_PROGRESS.md`.**

## The decision that gates everything

Two parallel implementations exist and must be reconciled before further
feature work, or the project will fork permanently. Details, options and
trade-offs are in the progress document. Nothing below Phase 1 should start
until that is settled.

## Phases

### Phase 0 — Baseline and Git safety ✅
Inspect the repository, record a verified build/test baseline, preserve all
user and teammate work, and push a recoverable checkpoint.

**Acceptance:** build succeeds, tests pass, stack runs, no secrets tracked,
checkpoint tag exists on the remote. **Met** — tag `baseline-phase0` at `a6bc383`.

### Phase 1 — Architecture and requirements
Map the dashboard, backend and frontend. Trace the recommendation flow from
evidence collection through validation to approval. Document the provider
implementation and existing safety boundaries.

**Acceptance:** a written map of the current flow, and a reconciliation
decision recorded.

### Phase 2 — Three explicit provider modes
Ollama-only, Groq-only, and Auto (Groq → Ollama). Server-side configuration,
accurate provider attribution, controlled fallback, deterministic-only
degradation.

**Acceptance:** each mode verified by mocked tests; no cross-provider request
leaks (no Groq call in Ollama-only mode and vice versa); the UI reports the
provider that actually answered.

### Phase 3 — Evidence and rendering correctness
Fix raw property-path rendering (`DeterministicAnalysis.Dimensions[1].Earned`)
by correcting the data mapping, not by hiding it with CSS. Resolve evidence
references to real finding titles and values.

**Acceptance:** explanations render readable prose with resolved evidence;
backend mapping covered by tests against real saved reports.

### Phase 4 — Self-analysis and experiment discovery
Evidence-grounded risk analysis over the dependency map, health, history,
coverage gaps and deterministic findings. Structured recommendations that
separate observed fact, inference, hypothesis and unknown.

**Acceptance:** recommendations cite real evidence; hallucinated experiments are
rejected; discovered-but-unimplemented ideas are surfaced as
`DISCOVERED — NOT IMPLEMENTED — NOT EXECUTABLE` and cannot be executed.

### Phase 5 — Experiment registry and extensibility
One registry as the source of truth for the dashboard, validation, preflight
and execution. Documented process for registering future experiments.

**Acceptance:** `kill-service` and `kill-rabbitmq` still work; no conflicting
allowlists remain; adding an experiment requires code and tests, not a prompt
change.

### Phase 6 — Safety and regression hardening
Verify approval, preflight, kill switch, watchdog, rollback and recovery still
hold, and that provider failure cannot bypass any of them.

**Acceptance:** safety tests pass with providers mocked as failing; the RabbitMQ
divergence remains visible and reproducible.

### Phase 7 — Integration and final verification
Full suite, all provider modes under mocks, Ollama live, Groq live only as a
single deliberate call. Security and diff review, then push.

### Phase 8 — Final report
What is implemented, what is verified, what is unresolved, how to continue.

## Non-negotiable constraints

- The kill switch stays deterministic and independent of any LLM or the dashboard.
- No model output may execute. Only registered experiments run, via trusted code.
- All numerical scores stay in the deterministic layer; the model never produces one.
- The RabbitMQ write/event divergence stays visible unless a real, tested fix lands.
- No secrets in Git, logs, responses or browser storage.
- `main` stays stable: build and tests must pass before anything merges.
- The nested `pitstop/` directory is left alone.
