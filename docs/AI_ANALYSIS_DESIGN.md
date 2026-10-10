# Day 2 design: AI-assisted resilience analysis

A minimal design that consumes the evidence we already produce. Written against
the actual fields in `chaos-scripts/results/<run-id>/report.json` and
`results/dependency-map/graph.json`, not an idealised pipeline.

## The one rule that shapes everything

**The LLM explains. It does not decide, and it does not act.**

The resilience score is computed by deterministic rules in code. The LLM never
produces a number, never picks a target, never triggers an injection, and is
never on the rollback path. If the model is unavailable, rate-limited, slow or
wrong, the analysis still runs and still produces a score — it just loses the
prose.

This is not only a safety position. A score an LLM invents is not reproducible
and not defensible to a judge; a score from published rules is both.

## Pipeline

```
map-dependencies.ps1 ──> graph.json ─┐
                                     ├──> analyze.ps1 ──> analysis.json
kill-service.ps1    ──> report.json ─┤                    analysis.md
kill-rabbitmq.ps1   ──> report.json ─┘
                                          │
                                          ├─ deterministic scorer   (always)
                                          └─ narrative layer (LLM)  (optional)
```

`analyze.ps1` reads evidence files only. It does not run Docker, does not touch
the running stack, and takes no action. It is safe to run at any time.

## Resilience scoring — the rules

Five dimensions, each computed from fields that exist in the evidence today.
Published here so the number can be checked by hand.

| Dimension | Weight | Rule | Source field |
| --- | --- | --- | --- |
| Blast radius containment | 25 | Share of non-target modules healthy on every `fault` sample | `Samples[].Phase/Module/Healthy` |
| Graceful degradation | 20 | Target returned a handled response (`OfflinePage`) rather than a hard timeout | `Samples[].OfflinePage`, `StatusCode` |
| Recovery | 25 | `Recovered` true scores 25, scaled down as `RecoverySeconds` approaches the 60s target; not recovered scores 0 | `Recovered`, `RecoverySeconds` |
| Data integrity | 20 | `DivergenceDetected` true scores 0; false scores 20 | `DivergenceDetected` |
| Safety controls | 10 | `Restored` true **and** `RollbackReason` is one of the three known controlled reasons | `Restored`, `RollbackReason` |

**Dimensions that were not measured are excluded and the remainder renormalised
to 100.** `kill-service.ps1` does not probe a write path, so it has no
`DivergenceDetected` field and is scored over the other four. The report states
which dimensions were measured, so a 90 from four dimensions is never silently
presented as the same thing as a 90 from five. Unmeasured is not the same as
passing, and the report must not let it look that way.

Applied to the evidence we already hold, by hand:

- **kill-service** — blast radius contained (Customer and Vehicle healthy on
  every fault sample), target hard-timed-out at ~5s rather than degrading
  gracefully, recovered, rollback controlled. Scores well on containment and
  safety, poorly on graceful degradation.
- **kill-rabbitmq** — containment perfect on the read path, recovery 19.28s,
  rollback controlled, but `DivergenceDetected: true` zeroes data integrity.
  The score should come out materially worse than kill-service, and that is the
  correct answer: a silent inconsistency is worse than a visibly broken page.

## Root-cause hypotheses

The model may only produce a hypothesis that cites specific evidence fields. The
prompt supplies extracted facts, never raw logs, and the response schema
requires an `evidence` array naming the fields relied on. A hypothesis that
cites nothing is dropped before it reaches the report.

Worked example from real evidence — this is the shape required, and it is the
finding `kill-rabbitmq` actually produced:

> **Hypothesis.** The write and the publish are not atomic, so there is no
> transactional outbox.
> **Evidence.** `WriteDuringOutage.StatusCode = 500`,
> `OutageWriteRowCommitted = true`, `DivergenceDetected = true`.
> **Implication.** A customer exists that no downstream service will ever hear
> about, and nothing replays when the broker returns.

## Remediation

Recommendations are advisory text attached to a finding. Each must name the
service and the evidence behind it. They are never applied automatically, and
no recommendation is turned into a command.

## Experiment planning

The model may rank *existing* experiments and suggest new targets, constrained
hard:

- Candidate targets come from `graph.json`, filtered to containers in this
  Compose project. Anything else is discarded in code before a human sees it.
- Fault types come from a fixed allowlist (`stop-container`, `network-delay`).
  A free-text fault suggestion is discarded.
- The output is a ranked list with reasons. **A human runs it, with `-Execute`.**
  The planner cannot inject, and nothing in the pipeline calls a chaos script.

## Model choice

Both Groq and Google Gemini have usable free tiers and either would work.
**Recommend Groq**, for three reasons specific to this codebase: the API is
OpenAI-compatible so it is a plain `Invoke-RestMethod` POST with no SDK and no
new dependency in a repo that is .NET and PowerShell; it supports JSON-mode
structured output, which the schema constraint above depends on; and it is fast
enough that analysis does not become a demo risk.

Check the current free-tier model list and rate limits before committing to a
model id — do not take a model name from this document on trust.

Gemini is the fallback choice if Groq's free tier does not fit; the design does
not depend on which is used, since the provider sits behind one function.

## Secrets

The key comes from `$env:GROQ_API_KEY`, read at call time. Never a parameter
default, never a file in the repo, never echoed to the console or into
`analysis.json`. If the variable is absent the script takes the deterministic
path without complaint.

Add nothing to `.gitignore` for this — there is no key file to ignore, which is
the point.

## Deterministic fallback

Not a degraded mode. It is the default path, and the narrative layer is the
addition.

Without a key, `analyze.ps1` produces the score, the per-dimension breakdown,
the measured/unmeasured list, and findings from fixed rules (`DivergenceDetected`
→ the outbox finding; `Recovered = false` → a recovery finding). What is lost is
the prose and the ranked planning — not a single number and not a single
finding.

## Build order

1. `analyze.ps1` with the deterministic scorer, reading both report shapes. No
   LLM at all. This is the whole backbone and it is testable offline.
2. Markdown report rendering.
3. Narrative layer behind `if ($env:GROQ_API_KEY)`, with the schema check and
   the evidence-citation requirement.
4. Experiment planning, allowlist-constrained.

Stop after step 1 if time is short. A reproducible score with no prose is worth
more than prose over a number nobody can check.
