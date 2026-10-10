# Pull request review log

Decisions taken 11 October 2026 against `main`. No teammate branch was modified,
rebased or deleted.

## Accepted

### PR #7 — recovery safety audit and disposable harnesses
**Merged.** Documentation plus opt-in audit harnesses; no runtime code.

It closed the open item from the previous session by verifying network-delay
watchdog retention against a disposable container with `Remove-Shaping` forced
to throw, and it **proved a defect I had predicted but never demonstrated**: the
`Start-Job` watchdogs in `kill-service` and `kill-rabbitmq` did not survive
their parent. The harness is disposable-only — it writes its own Compose project
under a separate `COMPOSE_PROJECT_NAME` and never touches the running stack.

Acted on: both crash experiments now use detached containers. See `b037041`.

### PR #5 — provider response robustness
**Merged, with one adaptation.** Three real fixes: the body read was bounded by
the caller's token rather than the provider timeout; JSON navigation could throw
`InvalidOperationException` on a malformed envelope where only `JsonException`
was caught; response headers are now read before the body.

Adapted afterwards in `27384c6`. PR #5 removed all upstream error detail, which
left `"Groq returned 400."` with no reason — unhelpful when a mistyped model id
is the likeliest first failure. The explanation is now read from the structured
`error.message` field, bounded to 200 characters, and nothing else is passed
through. Raw bodies are still never echoed.

### PR #4 — experiment compatibility review
**Merged.** Documentation only. Independently reaches the same conclusion
recorded in Phase 1: do not import another dashboard, agent framework or
executable experiment. Its branch inventory is useful given how many now exist.

### PR #6 — reproducible setup and demo checklist
**Merged.** Documentation only. Fresh-checkout Windows path and a bounded demo
sequence. Does not contradict `docs/AI_SETUP.md`.

## Changes requested — not merged

### PR #8 — combined scripts and dashboard integration
**Rejected as-is. One blocking defect.**

It changes `AiProviderRouter.Mode` to return `AiMode.Groq` whenever
`options.Ollama.Enabled` is false, and ships `"Enabled": false` in
`appsettings.json`:

```csharp
public AiMode Mode => options.Ollama.Enabled ? modeStore.Current : AiMode.Groq;
```

An operator who selects **Ollama-only would have their requests sent to Groq**.
That is the precise inverse of the selection, and it breaks a guarantee that is
tested in `ProviderModeTests`: picking a mode is a statement about where
evidence is allowed to travel. On this machine, where no Groq key is
configured, it would also disable the only working provider by default.

Secondary: it keeps both `EvidenceFormatter` and a new
`EvidenceReferenceResolver` and calls them on adjacent lines, leaving two
implementations of one job.

**Reusable and welcome once that is fixed:** `PreparedExperiments` is good
design — display-only metadata, `canExecute = false`, explicitly never consumed
by the validators or `IExperimentRunner`. That is the same
discovered-but-not-executable pattern used for AI discoveries, and it is the
right way to surface a script that is written but unproven.

To accept: drop the `Mode` override (gate availability, never the operator's
selection), and either fold `EvidenceReferenceResolver` into `EvidenceFormatter`
or remove one.

### PR #3 — Customer and Vehicle API pause experiments
**Changes requested.** Superseded in scope by PR #8, which contains the same
scripts, so the two cannot both be merged.

The scripts use the shared `ChaosSafety` module for the lock and the kill-switch
checks, which is right, and avoid `Start-Job`. Two gaps against the standard in
`docs/AI_EXPERIMENT_DESIGN.md`:

- No `Assert-SandboxContainer`, so the Compose-ownership guard is skipped.
- **No independent recovery.** A paused container stays paused if the
  orchestrator dies. This is exactly the defect PR #7 proved for the crash
  experiments and that `b037041` fixed with `Start-RestartWatchdog`; a pause
  experiment needs the same treatment with `docker unpause`.

The `analyze.ps1` change — adding Customer and Vehicle to `$targetModules` — is
sound and additive, and can be taken on its own.

### PR #2 — AI agent on `task2-kill-service`
**Still not merged**, unchanged from the Phase 1 decision. It is a PowerShell
agent plus a Node dashboard that diverged before the current architecture
existed. Its techniques were adapted instead: Compose-ownership verification,
the cross-process lock, the kill-switch re-check, and the `tc` sidecar.

## Rule applied throughout

A contribution is merged when it is compatible with the C# architecture, does
not weaken a tested guarantee, and can be verified. Where it cannot, the
specific defect is named so it can be fixed, rather than the branch being
merged and patched afterwards.
