# Bosch Chaos Hackathon - Pitstop

We use Pitstop, an open-source garage management application, as a local sandbox
for AI-driven chaos engineering. We deliberately interrupt services, measure
the effect on users, and verify recovery.

The application supports customers, vehicles, maintenance scheduling, and job
completion. It includes three APIs, SQL Server, RabbitMQ, and background services.
Its architecture demonstrates microservices, CQRS, event sourcing, Domain Driven
Design, and eventual consistency.

Group repository: https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon

## Current status

- Docker Compose deployment, 13 services, SQL Server data in a Docker-managed
  volume.
- **Five executable failure types**, each registered only after its cleanup was
  proven: `kill-service`, `kill-rabbitmq`, `network-delay`, `packet-loss`,
  `kill-database`.
- **Resilience console** at `:5280` — service discovery, dependency map, live
  telemetry, a baseline/impact/recovery chart, approval-gated injection, kill
  switch, experiment history and ranked findings.
- **AI planner**, advisory only: Groq with a strict JSON schema, falling back to
  local Ollama, falling back to no recommendation. The model cannot invent an
  experiment, choose a target, widen a duration, execute anything, or influence
  any score.
- **Deterministic scoring** — every number comes from published rules in code.
- 29 recorded runs, **193 automated tests**, 56 recovery-contract assertions.

Honest gaps, in more detail in [the evidence matrix](docs/REQUIREMENTS_EVIDENCE.md):

- **Groq is not verified end to end.** Its provider path returns 200, but no
  dashboard recommendation has yet reported `provider: groq`. Ollama answers and
  is what the fallback uses meanwhile.
- **The RabbitMQ write/event divergence is real and open.** A row can commit
  while its event is never published, and restoring the broker does not repair
  it. No outbox exists.
- **No video has been recorded.** The script is in
  [docs/DEMO_SCRIPT.md](docs/DEMO_SCRIPT.md).

## Team

**Team No. 28**

| Member | Name | Proposed Responsibility |
| --- | --- | --- |
| 1 | Saurabh Dwadesh Shreni | Folder structure, README, setup and architecture documentation |
| 2 | Arpit Pal | Workshop API failure experiment and recovery evidence |
| 3 | Tigisha P. Pradhan | RabbitMQ failure experiment and message-delivery observations |
| 4 | Vansh Makhija | Network-delay experiment and latency observations |
| 5 | Omkar Singh | Database failure experiment and AI observability integration |

## Prerequisites

- Git and PowerShell.
- Docker Desktop running with Linux containers and the WSL 2 backend on Windows.
- Internet access for initial image builds and dependency downloads.
- Free host ports: 7005, 1434, 5672, 15672, 4000, 25, and 5341.

The Docker builds install application toolchains inside the images. A local
.NET SDK or Node.js installation is not required for this workflow.

## Run the Application

Clone the group repository. If you already have it, use your existing directory.

```powershell
git clone https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon.git
cd bosch-chaos-hackathon
cd src
```

Build each base image successfully before continuing to the next command:

```powershell
docker build -t pitstop-dotnet-sdk-base:1.0 . -f dotnet-sdk-base-dockerfile
docker build -t pitstop-dotnet-runtime-base:1.0 . -f dotnet-runtime-base-dockerfile
docker build -t pitstop-dotnet-aspnet-base:1.0 . -f dotnet-aspnet-base-dockerfile
```

Build and start the application:

```powershell
docker compose up -d --build
docker compose ps -a
```

Allow SQL Server and the databases to initialize. Open
[Pitstop](http://localhost:7005) and check all three modules. If dependent services
exited during initialization, confirm SQL Server is ready, then restart them:

```powershell
docker compose logs --tail 50 sqlserver
docker compose restart customermanagementapi vehiclemanagementapi workshopmanagementapi workshopmanagementeventhandler invoiceservice notificationservice
```

For a basic workflow, create a customer, register their vehicle, schedule
maintenance, and finish the job. License numbers require three hyphen-separated
groups of 1-3 letters or 1-3 digits, such as `ABC-12-34`.

## Local Addresses

| Component | Address |
| --- | --- |
| Website | http://localhost:7005 |
| Customer Management | http://localhost:7005/CustomerManagement |
| Vehicle Management | http://localhost:7005/VehicleManagement |
| Workshop Management | http://localhost:7005/WorkshopManagement |
| Website health endpoint | http://localhost:7005/hc |
| RabbitMQ management | http://localhost:15672 |
| Captured emails (MailDev) | http://localhost:4000 |
| Centralized logs (Seq) | http://localhost:5341 |

## The resilience console

```powershell
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

Then open `http://localhost:5280`. This is the intended way to run an
experiment: it shows the baseline, requires explicit approval, and records
evidence.

For the AI planner, set the key **in the window you start the dashboard from** —
a key set in a different window is not inherited by the dashboard's process:

```powershell
$env:GROQ_API_KEY = "<your key>"
```

Without a key it falls back to local Ollama. Without either, the deterministic
assessment and the kill switch work exactly as before. Setup detail is in
[docs/AI_SETUP.md](docs/AI_SETUP.md).

## The five experiments

Each is bounded, approval-gated, independently recoverable, and registered only
after its cleanup was proven. A script exists for each, but **the catalog is the
allowlist** — nothing runs that is not registered.

| Experiment | Target | Cap | What it demonstrates |
| --- | --- | --- | --- |
| `kill-service` | Workshop API | 60s | Blast-radius containment: 2 of 3 modules keep serving |
| `kill-rabbitmq` | RabbitMQ | 120s | All pages stay up while events are silently lost |
| `network-delay` | Workshop API | 60s | Graceful degradation under uniform latency |
| `packet-loss` | Workshop API | 60s | Partial, non-deterministic failure |
| `kill-database` | SQL Server | 30s | Total failure: 0 of 3 pages serve, writes refused |

Run one directly, from the repository root:

```powershell
./chaos-scripts/kill-service.ps1 -DurationSeconds 30
./chaos-scripts/network-delay.ps1 -DurationSeconds 20 -DelayMilliseconds 400
./chaos-scripts/packet-loss.ps1 -DurationSeconds 20 -LossPercent 20
./chaos-scripts/kill-rabbitmq.ps1                    # dry run, changes nothing
./chaos-scripts/kill-database.ps1                    # dry run, changes nothing
```

The two widest-blast-radius experiments require `-Execute` to inject; without
it they print what they would do and change nothing.

The traffic-shaping experiments need a sidecar image, built once:

```powershell
docker build -t pitstop-netem:1.0 chaos-scripts/netem-image
docker pull docker:cli
```

Evidence lands in `chaos-scripts/results/<run-id>/samples.csv` and
`report.json`, which Git ignores. Score the archive with:

```powershell
./chaos-scripts/analyze.ps1
```

## Safety boundaries

These are the point of the project, not a feature list.

- **Sandbox only.** Every experiment verifies the target belongs to this
  Compose project and that Docker is a local named-pipe context, before
  touching anything.
- **Bounded before anything is injected.** A duration cap, a threshold that
  aborts when an unaffected module degrades, and a STOP-file check.
- **The kill switch is deterministic.** Plain file and timer logic polled by the
  running script. It does not depend on the dashboard, the backend, or any model
  staying up. **No LLM on the rollback path, ever.**
- **Rollback survives the script dying.** A detached watchdog container owned by
  Docker restores the target even if the orchestrator is killed.
- **Restoration is verified, not assumed.** A zero exit code from `docker start`
  does not mean the container stayed up, so its state is checked before the
  watchdog is released.
- **Recovery is judged on application behaviour** — pages serving, and where a
  write path exists, a write accepted — never on container status.
- **The model recommends; a human approves.** Out-of-range values are rejected,
  not clamped.
- **Secrets come from environment variables** and are never committed, printed
  or logged.

### Manual Kill Switch

In another terminal, from the repository root:

```powershell
New-Item chaos-scripts/STOP -ItemType File -Force
```

The script checks this file between probes and restores the API. Remove the
file before the next experiment:

```powershell
Remove-Item chaos-scripts/STOP
```

If PowerShell is forcibly terminated, the detached watchdog container restores
the target on its own — that is what it exists for, and it is verified by the
disposable recovery audit. To restore by hand anyway, from the repository root:

```powershell
docker compose -f src/docker-compose.yml start workshopmanagementapi
```

## Stop and Restart

From `src`:

```powershell
# Stop containers, retaining persistent data.
docker compose down

# Restart with the existing images.
docker compose up -d
```

SQL Server uses the Compose-managed `sqlserverdata` volume. Adding `-v` to
`docker compose down` deletes that volume and its databases.

## Troubleshooting

- **Module offline:** from `src`, check `docker compose ps -a` and
  `docker compose logs --tail 100 workshopmanagementapi sqlserver`.
  The home page can work while a module is unavailable.
- **SQL Server startup failure on Windows:** retain the Docker-managed database
  volume in `src/docker-compose.yml`; Windows/OneDrive bind mounts caused the
  startup failure observed during initial setup.
- **Unhealthy container but working pages:** existing API/web images may lack
  `curl`, which their Docker health checks call. Inspect with
  `docker inspect --format '{{json .State.Health}}' <container-name>`.
  Fix health-check tooling before relying on that status for monitoring.
- **License number rejected:** use the required format, such as `ABC-12-34`.
- **Port conflict:** stop the conflicting local application or change its host
  port mapping. The chaos script expects the website at `http://localhost:7005`.

## Folder Structure

```text
chaos-scripts/
  ChaosSafety.psm1          Shared guards: sandbox check, lock, watchdog
  kill-service.ps1          Workshop API crash
  kill-rabbitmq.ps1         Broker outage (needs -Execute)
  kill-database.ps1         SQL Server outage (needs -Execute)
  network-delay.ps1         Added latency via netem sidecar
  packet-loss.ps1           Partial packet loss via netem sidecar
  map-dependencies.ps1      Dependency and data-flow mapper
  analyze.ps1               Deterministic scoring and findings
  results/                  Generated local evidence (ignored by Git)
dashboard/                  Resilience console (ASP.NET Core)
  Ai/                       Providers, router, validator, experiment catalog
  wwwroot/                  UI, including the baseline chart
dashboard.Tests/            193 xUnit tests, including the model boundary
tests/                      Recovery-contract and disposable audits
docs/
  ARCHITECTURE_OVERVIEW.md  Submission architecture page
  FINDINGS_REPORT.md        Submission findings
  DEMO_SCRIPT.md            Submission demo script
  TEAM_FORM.md              Submission team details
  arc42/                    Original upstream architecture documentation
  ADRs/                     Original upstream decision records
src/
  docker-compose.yml       Local deployment
  WebApp/                  Garage management website
  CustomerManagementAPI/   Customer service
  VehicleManagementAPI/    Vehicle service
  WorkshopManagementAPI/   Maintenance planning service
  ...                      Background services, tests, and deployment tooling
LICENSE
README.md
```

## Submission documents

| Document | What it is |
| --- | --- |
| [Architecture overview](docs/ARCHITECTURE_OVERVIEW.md) | One page: services, telemetry, AI, safety controller, recovery |
| [Findings report](docs/FINDINGS_REPORT.md) | Ranked weaknesses with severity, evidence and fixes, from 29 real runs |
| [Demo script](docs/DEMO_SCRIPT.md) | Timed 3-minute script and shot list. The video is not yet recorded |
| [Team form](docs/TEAM_FORM.md) | Ready-to-copy submission details |
| [Requirements to evidence](docs/REQUIREMENTS_EVIDENCE.md) | Every requirement, what satisfies it, and how it was checked |
| [AI setup](docs/AI_SETUP.md) | Provider configuration, and why the local model was chosen by measurement |
| [Experiment roadmap](docs/EXPERIMENT_ROADMAP.md) | Why each experiment exists, and why some are still not executable |

## Known limitations

- The RabbitMQ write/event divergence is **open**: a row can commit while its
  event is never published. Restoring the broker does not repair it.
- `pause-service` is **not executable**. A paused container stays paused, and no
  unpause watchdog exists yet, so it is not registered.
- All measurements are from a near-idle single-host sandbox with no load
  generator, so they are a floor rather than a production figure.
- A configured packet-loss percentage is **not** a request-failure rate; TCP
  retransmits absorb most of it.

## Attribution and License

Based on [EdwinVW/pitstop](https://github.com/EdwinVW/pitstop), distributed under
the [Apache License 2.0](LICENSE). Retain upstream attribution and license notices.

See the [original wiki](https://github.com/EdwinVW/pitstop/wiki) for Docker,
Kubernetes, service-mesh, and testing documentation, and the
[Arc42 documentation](docs/arc42/arc42.md) for the original architecture.

![Pitstop garage](pitstop-garage.png)

The upstream project credits Thomas Moerkerken for this photo of a garage in
Dresden, Germany.
