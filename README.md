# Bosch Chaos Hackathon - Pitstop

We use Pitstop, an open-source garage management application, as a local sandbox
for AI-driven chaos engineering. We deliberately interrupt services, measure
the effect on users, and verify recovery.

The application supports customers, vehicles, maintenance scheduling, and job
completion. It includes three APIs, SQL Server, RabbitMQ, and background services.
Its architecture demonstrates microservices, CQRS, event sourcing, Domain Driven
Design, and eventual consistency.

Group repository: https://github.com/saurabhshreni-cmyk/bosch-chaos-hackathon

## Current Status

- Docker Compose deployment with SQL Server data in a Docker-managed volume.
- Workshop API crash experiment with baseline, fault, and recovery measurements.
- Duration watchdog, early rollback on unrelated module failure or excessive
  latency, and a manual kill switch.
- JSON/CSV evidence and documented results from verified experiments.

AI planning and analysis, additional failure types, and a dashboard are planned
work, not implemented features in this checkout.

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

## Workshop Failure Experiment

From the repository root, with all three module pages working:

```powershell
./chaos-scripts/kill-service.ps1
```

If your terminal is in `src`, first run `cd ..`.

The script captures three baseline rounds, kills the single Workshop API
container, monitors the module pages, and starts the API again after about
30 seconds. It restores earlier if Customer or Vehicle fails, their latency
exceeds the configured threshold, or the manual kill switch is activated.
It checks offline-page content as well as HTTP status, then verifies recovery.

Evidence is saved in `chaos-scripts/results/<run-id>/samples.csv` and
`report.json`. Results are ignored by Git; retain selected evidence separately
for submission. See [experiment documentation](docs/kill-service.md) for
observed outcomes, options, and limitations.

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

If PowerShell is forcibly terminated, restore manually from the repository root:

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
  .gitkeep
  kill-service.ps1          Workshop API crash experiment
  results/                 Generated local evidence (ignored by Git)
docs/
  .gitkeep
  kill-service.md          Experiment instructions and findings
  arc42/                   Original architecture documentation
  ADRs/                    Original architecture decision records
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

## Hackathon Roadmap

Day 1: demonstrate a running app, map dependencies and data flows, capture a
steady-load baseline, and show one failure with tested rollback controls.

Day 2: use AI to rank experiments, run at least three failure types, and generate
ranked findings with evidence and recommended fixes. Prepare the source code,
demo video (three minutes or less), architecture overview, findings report, and
team form.

## Attribution and License

Based on [EdwinVW/pitstop](https://github.com/EdwinVW/pitstop), distributed under
the [Apache License 2.0](LICENSE). Retain upstream attribution and license notices.

See the [original wiki](https://github.com/EdwinVW/pitstop/wiki) for Docker,
Kubernetes, service-mesh, and testing documentation, and the
[Arc42 documentation](docs/arc42/arc42.md) for the original architecture.

![Pitstop garage](pitstop-garage.png)

The upstream project credits Thomas Moerkerken for this photo of a garage in
Dresden, Germany.
