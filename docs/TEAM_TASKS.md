# Team tasks — Team 28

Assignments follow the split the team already agreed in the README, adjusted for
what is actually finished. Each task owns its own files so two people never edit
the same script.

Shared ground rules:

- Branch per task, PR into `main`. Do not push to `main` directly.
- New experiments go in their own `chaos-scripts/<name>.ps1`. Copy the structure
  of `kill-service.ps1` — baseline, fault, rollback, recovery, JSON + CSV evidence.
- **Every experiment needs its own duration cap, threshold check and STOP-file
  check before it is allowed to inject anything.** No exceptions, no "I'll add
  safety after it works".
- Target only containers in this Compose project. Never a remote Docker endpoint.
- Run `./chaos-scripts/map-dependencies.ps1` after any compose or handler change.

---

## 1 — Saurabh Dwadesh Shreni · docs, structure, integration

**Done:** repo structure, infrastructure fixes, dependency map, PR review.

**Done since:** PR #1 merged, Day 1 reproduced from `main`, broker experiment
integrated, Day 2 analysis design and deterministic scorer built.

**Next:** fold the Day 1 evidence into the README and drop its now-stale "images
may lack curl" troubleshooting note — that was fixed in `763b52a`.

Deliverable: `main` carries a complete, reproducible Day 1. Keep `docs/PROJECT_STATUS.md`
and `docs/HANDOVER.md` current — they are how the next agent resumes.

---

## 2 — Arpit Pal · Workshop API crash experiment

**Done and verified.** PR #1: baseline, container kill, watchdog, manual kill
switch, threshold rollback, recovery check, JSON/CSV evidence. Two full runs
observed; both the max-duration and manual-kill-switch rollback paths work.

**Threshold path now covered.** Running with `-MaxUnaffectedLatencyMs 1` makes a
module's normal ~20ms trip the limit, which exercises the rollback for real and
damages nothing. All three rollback routes now have evidence.

**Next, if you have time:** test the watchdog by killing PowerShell mid-run — it
is the one safety path still unproven in both experiments.

Files: `chaos-scripts/kill-service.ps1`, `docs/kill-service.md`.

---

## 3 — Tigisha P. Pradhan · RabbitMQ failure experiment

**Landed on `main`.** Your script was the basis; it was reworked to meet the
safety contract in `AGENTS.md` and extended to measure the write path, then
verified over four runs. `feature/kill-rabbitmq` is superseded — do not merge it.

What was added and why: baseline rounds, STOP-file kill switch, watchdog
restore, threshold abort, local-endpoint validation and written evidence. The
dry-run `-Execute` gate you wrote was kept, as the operator-approval step.

It also had to measure something different from what we expected. The module
pages stay completely healthy during a broker outage, so the experiment writes a
customer during the fault — which exposed that the row commits while the event is
never published. That is the project's strongest finding so far. See
`docs/rabbitmq-chaos.md`.

**Next, if you want it:** confirm non-delivery directly by inspecting consumer
state or the audit log, instead of inferring it from the broker being down. That
would turn a strong inference into proof.

## 4 — Vansh Makhija · network delay experiment

Degrade rather than kill. Inject bounded latency between the web app and one API
and find where it stops being slow and starts being broken — timeouts, retries,
a cascading stall.

Use a sidecar or `tc` inside a container; do not change host networking. Removing
the delay must be the rollback, and it must work even if the script dies.

- Acceptance: baseline; measured delay injected for a bounded window; latency
  shift shown at the web app; rollback on duration, threshold and STOP file;
  delay provably removed afterwards; evidence written.
- Files: `chaos-scripts/network-delay.ps1`, `docs/network-delay.md`.

---

## 5 — Omkar Singh · database failure + AI analysis

Two pieces. **Do the AI piece first** — it is a P2 gap with nothing behind it,
and Day 2 scoring leans on it.

**AI analysis — design done, step 1 built.** Read `docs/AI_ANALYSIS_DESIGN.md`
first. The deterministic scorer (`chaos-scripts/analyze.ps1`) already reads
`graph.json` and every `report.json`, scores five documented dimensions and emits
findings. Your job is the narrative layer on top: `$env:GROQ_API_KEY`, send the
extracted facts, require an `evidence` array on every hypothesis, drop anything
citing nothing. Then the allowlist-constrained experiment planner.

Groq is recommended over Gemini in the design — OpenAI-compatible REST means no
new dependency in a PowerShell repo. Check current free-tier models and limits
before committing to a model id.

Hard constraints, not suggestions:
- API key from an environment variable. Never in Git, logs or screenshots.
- The model **recommends only**. It never executes, and it is never on the
  rollback path.
- Validate every model suggestion against an allowlist of sandbox containers
  before a human is even offered it.
- Ship a deterministic fallback so the project still works with no API key.

**Database failure.** Stop SQL Server for a bounded window. The map shows six
services depending on it, so expect the widest blast radius — set tighter limits
and a shorter window than the Workshop experiment.

- Files: `chaos-scripts/analyze.ps1` (or a small script under `ai/`),
  `chaos-scripts/kill-database.ps1`, `docs/ai-analysis.md`, `docs/kill-database.md`.

---

## Suggested order for the remaining time

1. **Narrative layer on `analyze.ps1`** — the only P2 gap left, and everything it
   needs already exists. *(Omkar)*
2. **Network delay** — shows graded degradation rather than on/off. *(Vansh)*
3. **Database experiment** — widest blast radius, so tighter limits and a shorter
   window than the others. *(Omkar)*
4. **Prove non-delivery** in the broker experiment instead of inferring it. *(Tigisha)*
5. **Watchdog test** — kill PowerShell mid-run; the last unproven safety path. *(Arpit)*

Keep P0 intact. A working demo of what exists beats a half-finished AI
integration, and no number in a report may come from a model.
