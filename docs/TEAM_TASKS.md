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

**Next:** merge PR #1 (`gh pr merge 1 --merge`), then fold the Day 1 evidence into
the README and drop its now-stale "images may lack curl" troubleshooting note.

Deliverable: `main` carries a complete, reproducible Day 1. Keep `docs/PROJECT_STATUS.md`
and `docs/HANDOVER.md` current — they are how the next agent resumes.

---

## 2 — Arpit Pal · Workshop API crash experiment

**Done and verified.** PR #1: baseline, container kill, watchdog, manual kill
switch, threshold rollback, recovery check, JSON/CSV evidence. Two full runs
observed; both the max-duration and manual-kill-switch rollback paths work.

**Next, if you have time:** exercise the threshold-breach path once so all three
rollback routes have evidence, and record what you did to trigger it.

Files: `chaos-scripts/kill-service.ps1`, `docs/kill-service.md`.

---

## 3 — Tigisha P. Pradhan · RabbitMQ failure experiment

Stop the broker and show what eventual consistency actually costs. This is the
highest-value experiment left: the map puts `rabbitmq` behind four publishers
and four consumers.

The interesting result is not an error page — the APIs keep serving reads. It is
that a customer registered during the outage does not reach the workshop handler,
the invoice service or the notification service. Measure that gap, then measure
how much of it drains once the broker returns.

- Acceptance: baseline captured; broker stopped for a bounded window; at least
  one event published during the outage and tracked; recovery and queue drain
  measured; rollback on duration, threshold and STOP file; evidence written.
- Depends on PR #1 merging first (copy its structure).
- Files: `chaos-scripts/kill-broker.ps1`, `docs/kill-broker.md`.

---

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

**AI analysis.** Read `chaos-scripts/results/dependency-map/graph.json` and an
experiment's `report.json`, produce a ranked experiment plan, an analysis of what
the failure revealed, and remediation recommendations. Groq or Gemini free tier,
whichever is less friction — check current free-tier limits before committing.

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

1. Merge PR #1 — unblocks everyone copying its structure. *(Saurabh, now)*
2. AI analysis — biggest scoring gap, zero progress. *(Omkar)*
3. RabbitMQ experiment — best resilience story. *(Tigisha)*
4. Network delay — shows graded degradation, not just on/off. *(Vansh)*
5. Database experiment and threshold-path evidence. *(Omkar, Arpit)*

Keep P0 intact. If the time runs out, a working demo of what exists beats a
half-finished AI integration.
