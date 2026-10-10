# Day 1 plan — AI-Driven Chaos Engineering for Cloud-Native Resilience

## The problem

Build a system that deliberately introduces controlled failures into a
cloud-native application, observes how it responds, and uses that to improve
resilience. The intended loop: pick an open-source app, map it, baseline it,
inject a fault, observe, analyse, recommend — ideally with a resilience score
and a readable report.

Rules we work under: the app must be open source, properly licensed,
containerised and run entirely in our own sandbox. Nothing we do may touch
production, other teams, or shared infrastructure.

## The application

[EdwinVW/pitstop](https://github.com/EdwinVW/pitstop) — a garage management
system, Apache 2.0, ten .NET services plus SQL Server, RabbitMQ, MailDev and Seq,
deployed with Docker Compose. It earns its place here: it is genuinely
multi-service, event-driven through a broker, and has a real user-facing web app,
so a fault has somewhere visible to show up.

It did not run as shipped. Four defects had to be fixed first — see
`docs/PROJECT_STATUS.md`.

## Acceptance checklist

**A. Setup and app selection.** Open-source app chosen, licence checked, running
in our sandbox, health verified, start/stop documented.

**B. Dependency mapping.** Services, containers, databases, APIs and data flows
identified. Readable graph, automated where practical, manual correction where
discovery cannot reach. Verified against the real architecture and available as
an artefact.

**C. Monitoring baseline.** Normal health under steady load. Latency and error
rate recorded and comparable against experiment results.

**D. Failure injection.** At least one useful experiment, restricted to our own
sandbox resources, parameters and timings captured, before/after comparison
clear, outcome recorded.

**E. Safety controls.** Emergency stop. Duration and impact limits. Thresholds
that trigger automatic rollback. Tested at least once. Services return to a safe
state. **The kill switch must never depend on an LLM responding** — deterministic
logic only.

**F. Demo.** App starts, map visible, baseline available, one safe failure shown
with visible impact, kill switch works, app returns healthy.

## Priorities

- **P0** — the checklist above. Non-negotiable.
- **P1** — end-to-end reliability: services work together, experiments produce
  before/after results, recovery is safe, documented commands actually work.
- **P2** — intelligence: AI experiment planning, failure analysis, remediation
  advice, explainable resilience score, Groq or Gemini integration.
- **P3** — more failure scenarios, autonomous agents, UI polish.

**Never trade P0 for P2 or P3.** If time runs short, stop adding AI and finish,
test and document the mandatory minimum.

## Agent safety rules

The LLM may *recommend* experiments. It may not execute them.

- Every proposed action is validated against deterministic rules before it runs.
- Operations are restricted to an explicit allowlist of sandbox containers.
- A disruptive experiment needs operator approval to start.
- Duration, blast radius and impact limits are enforced in code.
- The kill switch is independent of the model.
- If monitoring fails or a threshold breaks, stop safely.
- Never execute model-generated shell commands.

API keys come from environment variables and stay out of Git, logs, screenshots
and documentation.

## Definition of done for Day 1

A judge can watch the app start, see the dependency map, watch one failure
injected with visible impact and a working kill switch, and see the app come
back healthy — reproducibly, from the documented commands, from a fresh clone.

## Demo script — about 3 minutes

1. **It's running** (20s). `docker compose -f src/docker-compose.yml ps` — 13
   containers. Open http://localhost:7005 and click through Customer, Vehicle
   and Workshop.
2. **We mapped it** (40s). `./chaos-scripts/map-dependencies.ps1`, then open
   `docs/dependency-map.md`. Point at the Mermaid graph: REST from the web app,
   events through RabbitMQ, databases behind. Note the ranking — logserver has
   the most edges but losing it only costs observability; sqlserver and rabbitmq
   are the real single points of failure.
3. **We broke it, safely** (80s). `./chaos-scripts/kill-service.ps1 -DurationSeconds 30`.
   Narrate the baseline rounds, then the kill. Workshop times out at 5s while
   Customer and Vehicle stay at ~20ms — the blast radius is contained, exactly
   as the map predicts.
4. **We can stop it** (30s). Second terminal: `New-Item chaos-scripts/STOP -ItemType File -Force`.
   The run rolls back early with reason "Manual kill switch". Say plainly that
   the switch is file and timer logic with no model in the path.
5. **It recovered** (20s). Recovery probes go green, reload the Workshop page.
   Show `chaos-scripts/results/<run-id>/report.json`.

Have one completed run's `report.json` open in a tab as a fallback in case the
live run misbehaves.
