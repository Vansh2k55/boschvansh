# Team form — ready to copy

Bosch Chaos Hackathon submission details. Anything not verifiable from this
repository is left as an explicit `[PLACEHOLDER]` rather than guessed.

## Team

**Team number:** 28
**Project:** AI-Driven Chaos Engineering for Cloud-Native Resilience

| # | Name | Responsibility |
| --- | --- | --- |
| 1 | Saurabh Dwadesh Shreni | Folder structure, README, setup and architecture documentation |
| 2 | Arpit Pal | Workshop API failure experiment and recovery evidence |
| 3 | Tigisha P. Pradhan | RabbitMQ failure experiment and message-delivery observations |
| 4 | Vansh Makhija | Network-delay experiment and latency observations |
| 5 | Omkar Singh | Database failure experiment and AI observability integration |

Contact email, college and enrolment details: `[PLACEHOLDER — fill in]`

## Repository and deliverables

| Item | Location |
| --- | --- |
| Repository | https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon |
| README / setup | `README.md` |
| Architecture overview | `docs/ARCHITECTURE_OVERVIEW.md` |
| Findings report | `docs/FINDINGS_REPORT.md` |
| Demo video script | `docs/DEMO_SCRIPT.md` |
| Demo video file | `[PLACEHOLDER — not yet recorded]` |
| Requirements-to-evidence matrix | `docs/REQUIREMENTS_EVIDENCE.md` |

## Tools and technologies

- .NET 10 / ASP.NET Core minimal API — resilience console and AI layer
- C# — dashboard, provider clients, validators, experiment registry
- xUnit — 193 automated tests, including the model-boundary safety tests
- PowerShell 7 — chaos experiments, dependency mapper, deterministic analyzer
- Python 3 — disposable recovery audits
- Docker and Docker Compose — 13-container sandbox
- `tc` / `netem` via a throwaway sidecar container — latency and packet loss
- Mermaid — architecture diagram
- Git / GitHub — version control

## Sample application

[EdwinVW/pitstop](https://github.com/EdwinVW/pitstop), Apache 2.0. A .NET
microservices garage-management reference application: three user-facing
modules, SQL Server, RabbitMQ, and four background workers. Used unmodified
except for one fix to its RabbitMQ publisher error handling.

## AI models and provider usage

| Role | Provider | Model | Status |
| --- | --- | --- | --- |
| Primary | Groq Cloud | `openai/gpt-oss-20b` (strict JSON schema) | Provider path returns 200; **not yet verified end to end through the application** |
| Fallback | Ollama, local | `qwen2.5:3b` | Working. 18.4s cold, 9.1–9.5s warm, conforming output |
| Final fallback | none | — | No recommendation, by design: nothing to approve |

**How AI is used, stated precisely.** The model proposes one experiment with a
hypothesis, expected blast radius and rollback plan, and narrates completed
runs. It is advisory only.

**What the model cannot do.** Invent an experiment, choose a target, widen a
duration, execute anything, or influence any score. Every number in the
assessment and every ranking comes from published rules in code. There is no
model on the rollback path.

Development assistance: Claude (Anthropic) was used as a coding assistant while
building this project.

## Third-party libraries

No NuGet package references beyond the shared framework are used by the
dashboard. The sample application carries its own upstream dependencies
(Entity Framework Core, RabbitMQ.Client, Polly, Serilog, AutoMapper), unchanged
from upstream Pitstop except as noted.

## Data sources and test data

- No real, personal or production data is used anywhere.
- Test customers are generated per run with synthetic identifiers
  (`chaos-<random>`), `example.invalid` email addresses and placeholder
  addresses.
- All telemetry is collected by this project from its own local sandbox.
- Credentials in `src/*/appsettings.Production.json` and the `sqlserver` Compose
  service are upstream sample-app values for a throwaway local sandbox. No real
  credential exists in this repository. The Groq API key is read from an
  environment variable at call time and is never committed, logged or printed.

## Results summary

- **5** executable failure types, each registered only after its cleanup was
  proven: `kill-service`, `kill-rabbitmq`, `network-delay`, `packet-loss`,
  `kill-database`.
- **29** recorded runs with baseline, impact and recovery measurements.
- **193** automated tests and **56** recovery-contract assertions passing.
- **1** open High-severity finding: database and event stream diverge during a
  broker outage. Not fixed; honestly reported.
