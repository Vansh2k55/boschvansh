# Demo video script — 3 minutes

**No video has been recorded.** This is the script, shot list and exact
commands. Recording is a manual step that still has to be done.

Target: **2:50**, leaving margin under the 3-minute limit.

## Before you hit record

```powershell
# 1. Stack up, all 13 containers healthy
docker compose -f src/docker-compose.yml up -d
docker compose -f src/docker-compose.yml ps

# 2. Sidecar image for the traffic-shaping experiments (once)
docker build -t pitstop-netem:1.0 chaos-scripts/netem-image
docker pull docker:cli

# 3. Fresh assessment so the findings panel is populated
./chaos-scripts/analyze.ps1

# 4. Dashboard - IN A WINDOW THAT HAS GROQ_API_KEY SET, if you want Groq
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

Checklist: no STOP file; `chaos-scripts/experiment.lock` not held; browser at
`http://localhost:5280` zoomed so a panel fills the frame; a second tab on
`http://127.0.0.1:7005`.

A key set in a *different* window is not inherited by the dashboard's process.
If the planner says `provider: ollama`, that is Ollama answering — say so rather
than implying otherwise.

## Shot list

| Time | Shot | What you say |
| --- | --- | --- |
| **0:00–0:20** | Pitstop at `127.0.0.1:7005`, click through Customer → Vehicle → Workshop | "Pitstop — a ten-service .NET garage system. Three user-facing modules over SQL Server and RabbitMQ. This is our sandbox, running locally." |
| **0:20–0:40** | Console: Environment and Service Discovery, then Dependency and Data-Flow Map | "Our console discovers all 13 services and maps 40 dependencies. Solid lines are calls and database edges, dotted are events through the broker. It ranks what carries the most risk — SQL Server, six dependents, and it had never been tested." |
| **0:40–1:05** | Monitoring Baseline: live table, then the chart | "Baseline first, because a fault with no baseline proves nothing. Pages answer in 10 to 20 milliseconds. This chart is real recorded probes — baseline, impact, recovery — and a failed probe is marked as a failure, not drawn as a latency, because a client timeout isn't something the service did." |
| **1:05–1:30** | AI Experiment Planner → **AI Recommend Experiment**. Show the rationale and the provider badge | "The AI reads the live evidence and proposes one experiment with its reasoning and expected blast radius. It recommends — it cannot execute. The server checks every suggestion against a fixed allowlist: it can't invent an experiment, pick a different target, or widen a duration." |
| **1:30–1:55** | Approved Failure Injection → preflight → confirm dialog → approve `kill-database` (15s) | "A human approves. Preflight runs first. We're stopping SQL Server — the widest blast radius we have, so it's capped at 30 seconds and needs an explicit execute flag." |
| **1:55–2:20** | Split: Pitstop tab failing, console timeline advancing | "Every module page fails — 3 of 3, and writes are refused. That's the finding: this system has no graceful degradation path for its shared database. Compare RabbitMQ, where all three pages stay up and the damage is invisible." |
| **2:20–2:40** | Recovery and Safety Verification; pages return; chart redraws | "Automatic restoration, then recovery is verified on application behaviour — pages serving and a write accepted — never on container status. Back in 23 seconds. If this script had been killed mid-run, a detached watchdog container owned by Docker would still have restored it." |
| **2:40–2:50** | Experiment History and Findings + Resilience Assessment | "Ranked findings with severity and evidence. Our top one is real and still open: during a broker outage a row commits while its event is never published — restoring RabbitMQ doesn't repair it. Every number here is deterministic. No model produced any score." |

## The line that matters

If you only land one point, make it this:

> "The model recommends. A human approves. The kill switch is plain file and
> timer logic that doesn't depend on the dashboard, the backend, or any model
> staying up."

## Fallbacks if something misbehaves

- **Planner returns 503** — both providers failed. Say so, and show the
  deterministic assessment still working. That is the designed behaviour: no
  recommendation means nothing to approve. Do not re-record to hide it.
- **Ollama slow on the day** — it is measured at 9–18s with `qwen2.5:3b`. If it
  stalls, switch Provider mode to Groq only, or narrate the fallback.
- **Experiment refuses to start** — check for a STOP file and a stale
  `experiment.lock`. Both refusals are correct behaviour and are worth showing.
- **Short on time** — cut the 0:00–0:20 app tour, not the safety narration.

## What not to claim

- Do not call Groq verified unless the badge on screen says `provider: groq`.
- Do not describe the RabbitMQ divergence as fixed. It is open.
- Do not present the configured packet-loss percentage as a request-failure
  rate; TCP retransmits mean they are not the same number.
