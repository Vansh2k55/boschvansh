# Windows setup and safe demonstration

Audited on 11 October 2026 against remote main
`0e9b6147e5e1f01c9fd67e672325568311252904`. This is a documentation-only
contribution. It keeps the existing ASP.NET Core dashboard and PowerShell scripts.

## Prerequisites

- Windows with Docker Desktop running Linux containers and Compose v2.
  Experiments require a local Windows named-pipe Docker context, not a remote
  DOCKER_HOST. Network sidecars need NET_ADMIN.
- .NET 10 SDK and PowerShell 7 (`dotnet` and `pwsh` on PATH). Windows PowerShell
  alone does not satisfy the dashboard runner, which starts `pwsh` by name.
- Git and access to the repository, NuGet, container registries and image build
  package repositories. Docker must have enough memory for SQL Server and services.
- Optional Ollama plus an installed model. Optional Groq key; neither provider is
  required for deterministic analysis or the dashboard.

Check these before setup:

```powershell
git log -1 --format='%H %s'
git status --short
dotnet --info
pwsh --version
docker version
docker compose version
docker context show
docker context inspect --format '{{.Endpoints.docker.Host}}'
```

Do not copy another developer's `.env` or cached runtime folder. The setup below
does not depend on `.runtime`, Node, Python, or the user's separate dashboard work.

## Build and start the application

From repository root, build the three base images in the documented source
context, then Compose. The Dockerfiles use .NET 10 and the ASP.NET base installs
curl for health checks.

```powershell
Push-Location src
docker build -t pitstop-dotnet-sdk-base:1.0 -f dotnet-sdk-base-dockerfile .
docker build -t pitstop-dotnet-runtime-base:1.0 -f dotnet-runtime-base-dockerfile .
docker build -t pitstop-dotnet-aspnet-base:1.0 -f dotnet-aspnet-base-dockerfile .
docker compose config --quiet
docker compose up -d --build
docker compose ps
Pop-Location
docker build -t pitstop-netem:1.0 chaos-scripts/netem-image
docker image inspect pitstop-netem:1.0 --format '{{.Id}}'
```

These build commands match checked-in Dockerfiles and Compose. **A complete
fresh image build/start was not repeated in this audit.** Compose validation
passed and the sidecar image was already installed. Registry downloads, proxy/DNS,
Docker memory and cold-start time remain environment-dependent.

Compose uses explicit container names for several services and fixed host ports.
Changing the Compose project name alone does not isolate a second full stack.
Do not start another checkout over an existing stack. Ownership checks require
containers created from this checkout's Compose path.

| Service | Host port / lookup |
| --- | --- |
| WebApp | 7005 |
| RabbitMQ AMQP / management | 5672 / 15672 |
| SQL Server | 1434 |
| MailDev SMTP / UI | 25 / 4000 |
| Seq UI | 5341 |
| Customer / Vehicle / Workshop APIs | dynamically published; use Compose port for 5100 / 5000 / 5200 |
| C# dashboard | 5280 (explicit launch URL below) |
| Ollama default | 11434 |

SQL and RabbitMQ use named persistent volumes. Never add `-v` to the normal
Compose shutdown command. Upstream sample credentials are for a disposable local
sandbox, not deployment credentials. Do not print configuration containing them.

## Dashboard, tests and read-only verification

```powershell
dotnet build dashboard/Dashboard.csproj
dotnet test dashboard.Tests --logger 'console;verbosity=minimal'
dotnet run --project dashboard/Dashboard.csproj --urls http://127.0.0.1:5280
```

Run the server in its own terminal; stop it with Ctrl+C before rebuilding if files
are locked. In a second terminal:

```powershell
docker compose -f src/docker-compose.yml ps
Invoke-RestMethod http://127.0.0.1:5280/api/state
Invoke-RestMethod http://127.0.0.1:5280/api/ai/status
Invoke-RestMethod http://127.0.0.1:5280/api/preflight/network-delay
foreach ($module in @('CustomerManagement','VehicleManagement','WorkshopManagement')) {
    Invoke-WebRequest "http://127.0.0.1:7005/$module" -UseBasicParsing -TimeoutSec 8 |
        Select-Object StatusCode
}
pwsh -NoProfile -File chaos-scripts/map-dependencies.ps1
pwsh -NoProfile -File chaos-scripts/analyze.ps1
```

The mapper and analyzer produce ignored evidence. They do not inject faults.
They were inspected, but not executed in this audit. `/api/state`, `/api/ai/status`
and `/api/preflight/network-delay` exist in Program.cs and returned HTTP 200 in a
temporary dashboard smoke test. A 200 preflight response does **not** mean its
blocker list is empty or that execution is authorized. Check the response content.
Use IPv4 `127.0.0.1` for probes; prior handover reports a problematic IPv6 relay.

## Optional AI setup

The dashboard reads Groq keys from the launching process environment, not an
automatically loaded `.env`. Keep keys out of commands saved to shell history,
Git, logs and screenshots. Set `GROQ_API_KEY` privately through your normal secret
mechanism before launch; no key is needed for these instructions.

For local inference, install Ollama separately and install the model you choose.
The checked-in default is `qwen3.5:4b`; its installation was not attempted here.
The client never downloads a model automatically.

```powershell
$env:OLLAMA_BASE_URL = 'http://localhost:11434'
$env:OLLAMA_MODEL = 'qwen3.5:4b'
# Optional model override, only if supported by your Groq account:
$env:GROQ_MODEL = 'openai/gpt-oss-20b'
```

Select a mode in the existing dashboard: Ollama uses only local completion;
Groq uses only Groq with no fallback; Auto tries configured Groq then Ollama,
otherwise retains deterministic analysis. Status that says Groq is configured
does not verify a live key/model/quota. **No live provider call was made here.**
See `AI_SETUP.md`; `GROK_SETUP.md` does not exist on this main.

## Demo checklist and evidence plan

Safety gate: the recovery audit reproduced failed parent-job rollback for
kill-service and kill-rabbitmq. Do not use those crash experiments in a judged
demonstration until independently owned rollback is fixed and verified. Network
watchdog retention was demonstrated on disposable containers, but delayed Docker
and watchdog-command failure remain unverified. A baseline-only demo is appropriate
when these gates are not met. Do not remove an existing STOP file casually.

1. Record checkout commit, Docker context, image IDs, Compose ownership and date.
   Ensure no experiment runs and preserve an operator-controlled STOP terminal.
2. Record HTTP status, content health and several latency samples for all three
   modules. Capture dependency map and deterministic assessment. Empty history
   is missing evidence, not a successful experiment.
3. Only after recovery gates pass, a human reviews fixed target, duration, delay,
   blast radius and preflight blockers. Use the dashboard's approval flow. For a
   future approved network demo, stay within 5–60s and 50–2000ms; do not widen
   bounds. Never use model-generated commands.
4. Capture actual shaping via a tc sidecar in the exact target's network
   namespace, fault-phase HTTP latency and unaffected-module health. Container
   `running` proves nothing about shaping or application recovery.
5. Engage STOP using the dashboard or `New-Item chaos-scripts/STOP -ItemType File
   -Force` in the owning checkout. Record activation time and observed rollback.
   Do not confuse a requested stop with a completed stop.
6. Re-read qdisc and verify netem is absent; record watchdog ID/disposition. Verify
   application health and latency return within the run's measured recovery budget.
   If cleanup fails, keep the watchdog and mark the run failed. Escalate manual
   recovery to the operator; do not label a pending watchdog successful recovery.
7. Preserve report.json, samples.csv and audit.jsonl privately as run evidence.
   AI explanation must cite measured facts, distinguish hypotheses and missing
   evidence, and leave score/outcome to deterministic code. Label synthetic data.
8. Stop the dashboard with Ctrl+C. When the application is no longer needed,
   `docker compose -f src/docker-compose.yml down` preserves named volumes.

No fault demo was conducted against Pitstop in this audit. Do not present this
checklist as a recording or claim it produced application fault/recovery results.

## Verified observations and limits

- **Unit/mocked:** unchanged main dashboard suite, 114 passed, 0 failed/skipped,
  duration 2m59s. Fake runner isolates Docker, stub transports isolate providers.
- **Static/configuration:** Compose config validation passed; Docker server 29.8.2;
  sidecar image present; image definitions, ports and endpoints read from source.
- **Live-system read-only:** dashboard `/`, state, AI status and network preflight
  returned 200 on temporary port 5281. Existing module pages returned 200 and did
  not match offline text: Customer 369ms, Vehicle 112ms, Workshop 142ms. Single
  samples are smoke checks, not a baseline distribution or resilience proof.
- **Disposable-container integration:** separate safety PR contains the harness
  and detailed network retention and failed crash-watchdog results. It never
  targets the normal stack. This documentation PR changes no experiment behavior.

Not verified: clean-machine installation, cold image builds, new stack start/stop,
model installation, live Groq/Ollama, full application fault/kill-switch/recovery
demo, database durability, downstream event delivery or consumer reconciliation.

## Known limitation and troubleshooting

When RabbitMQ is unavailable, `POST /api/customers` may return HTTP 500 after the
row commits and its event is not published. Restarting RabbitMQ does not repair
the inconsistency. No transactional outbox is included. Do not retry blindly or
call the system resilient because module read pages still work.

Missing `pwsh`: add PowerShell 7 to PATH in the dashboard's launching terminal.
Missing netem image: build it before injection, because scripts use `--pull=never`.
Unhealthy services: inspect Compose status and relevant logs, then HTTP and broker
diagnostics; do not rely on running state. Port collisions: find the existing owner
before starting a stack; do not stop another checkout. Ownership refusal: use the
checkout that created the stack. Provider 429 falls back only in Auto mode, not
Groq-only. STOP means admission is blocked, not that a blocked Docker call was
interrupted. Persistent shaping requires operator recovery; prior handover gives
Workshop restart as a last resort, which is disruptive and should be deliberate.

Changed file: this guide. Base and head commits, PR URL and validation commands
are included in the documentation PR metadata.
