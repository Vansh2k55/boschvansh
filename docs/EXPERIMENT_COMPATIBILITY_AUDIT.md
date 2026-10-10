# Experiment compatibility review, 11 October 2026

Base: `0e9b6147e5e1f01c9fd67e672325568311252904`, fetched from GitHub for this review.
Decision: do not import another dashboard, agent framework, or executable experiment.

## Remote inventory

Read using `git fetch origin --prune`, `git log -1 origin/<branch>`, `git show`,
`git diff --stat origin/main origin/day2-submission -- chaos-scripts agent dashboard`,
and GitHub's repository pulls API (`state=all&per_page=100`).

| Branch / PR | Observed head / state | Decision |
| --- | --- | --- |
| main | 0e9b6147e5e1f01c9fd67e672325568311252904 | C# architecture is the base |
| day2-submission | 9c0d8cae6dc9b4eb87250bc056310c57d08be0b5 | Node dashboard and independent agent are incompatible wholesale |
| task2-kill-service / PR #2 | 2c08c0286b6ac64e631ace2c157e897ea2d71aaf / open | Review techniques individually |
| Jigisha-Pradhan-patch-1 | 5ca3298b0bbacfc46845c212a2bf05a8aff49d15 | Older broker implementation lacks current shared guards |
| feat/api-pause-scripts / PR #3 | 75ef90dae6331cac1f2ac9b05f934788fa23d8f3 / open | User's separate contribution; excluded |
| PR #1 | closed | Historical Workshop contribution |

No teammate branch was changed. Branch heads and PR states are observations,
not promises about later GitHub activity.

## Experiment comparison

| Implementation | Fault, target and bounds | Validation, impact and rollback | Recovery / surviving orchestrator death |
| --- | --- | --- | --- |
| main kill-service | Workshop container kill; script 1–60s, dashboard 5–60s | Compose config labels, local named-pipe endpoint, STOP and lock; Workshop page fails; `docker start` in finally | Module HTTP/content probes; Start-Job lifetime is tied to parent, so crash recovery is unsafe to assume |
| main kill-rabbitmq | Broker kill then stop; script 1–120s, dashboard 5–120s; `-Execute` | Same ownership guards; read pages can remain healthy while writes diverge; Compose start in finally | Broker running AND port-connectivity diagnostics, module probes, successful customer write; does not repair prior missing events; parent-owned job |
| main network-delay | Workshop egress netem 50–2000ms, 5–60s | Refuses existing qdisc; fixed target, ownership/STOP/lock; detached sidecar; deletes handle 28: and re-reads qdisc | HTTP latency budget from baseline; detached watchdog retained if removal unverified; one-shot watchdog remains limited by Docker/kernel availability |
| task2 / day2 unified kill-service | Crash targets Workshop, SQL Server, RabbitMQ; 1–60s. NetworkDelay Workshop only, 100–1000ms | ValidateSet plus Compose-label/local endpoint checks; broader database blast radius; finally restores target | Start-Job backup can die with parent. Queue snapshots are useful evidence but do not establish end-to-end event delivery |
| day2 kill-database | Wrapper around unified runner targeting sqlserver, 1–60s | Retains persistent volume, but targets the shared development DB; all three modules affected | Inherits parent-job weakness. No disposable database or isolated data contract demonstrated |
| day2 network-delay | Thin wrapper, Workshop delay 100–1000ms, 1–60s | Delegates to unified runner | Inherits Start-Job recovery risk; current main's detached approach is preferable |
| Jigisha broker script | Broker outage 1–120s; Execute gate | Older version has endpoint guard but no ChaosSafety ownership/lock module | Broker diagnostics and customer write checks; same Start-Job limitation |

## Prioritized findings

1. **High:** reject executable database-crash contribution. Retaining a volume
   does not isolate the normal dataset or prove SQL crash recovery. Require a
   separately named Compose project, fresh disposable volume, isolated test data,
   independent rollback, database query/integrity checks and downstream workflow
   recovery before registration. Do not run the existing proposal against Pitstop.
2. **High:** hard-kill/session-close recovery must be demonstrated before trusting
   either crash experiment. Do not copy the misleading independent-job comments.
3. **High:** RabbitMQ outage can produce HTTP 500 after the customer row commits
   without publishing its event. Broker restoration does not replay or repair it.
   No outbox is implemented here and business semantics are unchanged.
4. **Medium:** queue depth, consumer counts and time-series request load are useful
   ideas for a later C# contribution. Snapshot equality alone does not prove event
   delivery, especially while the time service produces unrelated events.

## C# integration contract for any future candidate

Use `ExperimentSpec` / `ExperimentCatalog`, `RecommendationValidator`, existing
preflight, `IExperimentRunner`, `RunState` and the existing report/scorer contract.
Add fixed targets, parameter bounds, approval, shared STOP/locking and evidence
fields to those interfaces. Discovery remains research-only. Never route model
text to shell, permit arbitrary Docker targets, or add the Node dashboard.

## Verification and limits

This is a static review, not fault verification. No experiment, SQL operation,
business write or provider request was executed for this branch. Main's existing
114 unit/mocked dashboard tests passed in the separate safety checkout; that is
not evidence that these branch implementations recover. Changed file: this review
only. No imported code warranted. Follow the separate recovery audit for disposable
execution evidence. Head commit and PR URL are recorded in the PR metadata.
