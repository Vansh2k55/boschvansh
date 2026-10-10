# AI reliability and evidence audit, 11 October 2026

Base: `0e9b6147e5e1f01c9fd67e672325568311252904`, fetched remote main.
Scope: provider clients and mocked tests only; no experiment authority, dashboard
redesign, business semantics, teammate branch or user's pending work changed.
Existing `.runtime/audit-ai` edits were inspected and copied into this separate
checkout; that worktree was not edited. Files coordinated by isolation:
`dashboard/Ai/GroqClient.cs`, `dashboard/Ai/OllamaClient.cs`,
`dashboard.Tests/ProviderResponseSafetyTests.cs`, and this report.

## Compatible improvement

Both clients previously caught JSON syntax errors but could throw on valid JSON
with the wrong envelope types: Groq `{"choices":[]}`, `{"choices":{}}`, numeric
content, and Ollama `{"message":[]}`. An exception could interrupt Auto fallback.
The clients now return provider failure for these shapes, allowing Auto to try
the next provider. Caller cancellation still propagates.

Response-body reading now uses the provider deadline, with headers-first HTTP
completion. This also bounds bodies supplied by custom handlers, rather than
relying on HttpClient's default eager buffering. Read errors become failures.
Provider error bodies and transport exception text are no longer exposed to the
dashboard. An upstream body can echo credentials/evidence, so even a 300-character
excerpt is unsuitable. HTTP status-specific diagnostics remain.

Reproducible mocked examples are in `ProviderResponseSafetyTests`: wrong-shape
envelopes, body that never completes, caller cancellation, an error body containing
a synthetic credential, and Auto fallback after an empty Groq choices array.

## Current pipeline

PowerShell writes run report/samples. `analyze.ps1` deterministically scores
recorded evidence and writes assessment. The explain endpoint combines report
fields, matching deterministic analysis and the first 40 samples, then calls
`AiProviderRouter.CompleteAsync<AiExplanation>`. `EvidenceFormatter` resolves
citations for display. The browser displays explanation separately from the
deterministic assessment.

Recommendation flow supplies graph/health/history evidence, parses structured
output, applies `RecommendationValidator` and `DiscoveryValidator`, and offers
only catalog experiments for human review. The run endpoint separately enforces
approval, registered experiment, integer bounds, STOP, preflight and concurrency.
`IExperimentRunner` receives catalog data and validated integers, never shell text.
Discovery has no executable path. Model/provider failures do not grant approval.

| Mode | Actual router policy |
| --- | --- |
| Ollama | Only Ollama completion; Groq skipped |
| Groq | Groq only when key is present; no Ollama fallback |
| Auto | Configured Groq first, then Ollama on failure/invalid JSON, then deterministic-only |

`GROQ_API_KEY` is environment-only, read at call time. `GROQ_MODEL`, `OLLAMA_MODEL`
and `OLLAMA_BASE_URL` override configured defaults. Status reporting treats a
configured Groq key as available without a real API request; that is configuration
status, not proof that credentials, model access or quota work. No key was requested,
read from local secret files, printed or committed for this audit.

## Evidence quality findings

**High residual risk:** deserialization is not full JSON Schema validation.
`{}` can deserialize to a nullable explanation record. Recommendation validation
blocks unsafe execution, but explanation prose is not checked claim-by-claim.
`EvidenceFormatter` leaves unknown paths/prose unchanged, so it does not prove
citations exist or values match. Prompts ask for grounded facts; that alone cannot
prevent fabricated measurements. Treat prose as unverified advice, never a score,
recovery verdict or safety decision. Add a separate evidence-claim validator before
making stronger trust claims.

**Medium:** first-40 sample truncation can omit recovery observations; the model
receives summary recovery fields but not necessarily the relevant raw samples.
Prefer phase-aware sampling and explicit counts/missing-evidence flags next.
Observed facts, calculated metrics, hypotheses, absent evidence and follow-up checks
are not enforced as separate server-validated claim categories today.

Numerical scores/outcomes remain outside the LLM; missing evidence must not be
interpreted as passing. A running container is insufficient recovery evidence.
RabbitMQ HTTP 500 / committed-row / missing-event divergence remains unresolved;
broker restoration does not repair it and no outbox was added.

## Verification

```powershell
dotnet test dashboard.Tests --logger 'console;verbosity=minimal'
git diff --check
```

**Unit/mocked:** 132 passed, 0 failed/skipped, duration 2m6s. Includes 18 new mocked
provider tests plus the existing 114. Test hosting uses fake experiment runners;
no test launches a fault. Synthetic credentials are not live keys.

**Live provider:** neither Groq nor Ollama tested in this audit. Prior documentation
reports Ollama verification and explicitly unverified Groq. Stub results here do
not change that status. No live evidence was transmitted to either provider.

**Not tested:** live provider contract/model/schema support, quota/authentication,
real streaming failures, real-provider explanation grounding, sustained load,
full semantic validation or missing-evidence rejection. `docs/GROK_SETUP.md` requested
in the task does not exist on this main; use `docs/AI_SETUP.md`. The implementation
plan contains stale phase status; NEXT_LLM_CHECKLIST is more current.

No arbitrary shell, unrestricted targets or bypass of server-side execution guards
was added. This fix improves failure handling; it does not certify AI prose as true.
Head commit and PR link are in the PR metadata.
