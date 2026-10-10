# Workshop API failure experiment

Run from the repository root, with the local Docker Desktop app already running:

```powershell
./chaos-scripts/kill-service.ps1
```

The script resolves the single running `workshopmanagementapi` container through
Compose, checks three rounds of healthy baseline requests, kills that container,
and observes the application for up to 30 seconds. It then starts the same
container and checks all three modules for recovery. It does not delete data.

## Measurements

Each request records UTC time, phase, module, HTTP status, latency, offline-page
detection, and exceptions. HTTP 200 alone is insufficient: the web app's offline
fallback also returns 200. A timed-out request is recorded as a failed probe;
it does not prove that the server never eventually returns a response.

Generated `chaos-scripts/results/<run-id>/samples.csv` and `report.json` contain
the evidence for baseline, fault, and recovery. The report records the rollback
reason and elapsed time from completion of the start command to a healthy round.
The requests are sequential and provide a low-rate baseline, not a load test.

## Safety and kill switch

Only one local Workshop API container is targeted. The script refuses a remote
Docker context and an unhealthy baseline. Customer or Vehicle failure, or latency
above 3000 ms in either unaffected module, causes early rollback. Workshop failure
is the intended experiment and does not itself trigger early rollback.

A background watchdog starts the container at the duration limit. A `finally`
block also restores it on script errors. Request timeout defaults to five seconds.
The script verifies recovery for up to 90 seconds. Docker itself must remain
available for restoration; a host shutdown or forcibly terminating the PowerShell
process cannot be handled by a script job. If interrupted that way, restore with
`docker compose -f src/docker-compose.yml start workshopmanagementapi`.

To stop the fault early, run in a second terminal from the repository root:

```powershell
New-Item chaos-scripts/STOP -ItemType File -Force
```

The switch is checked between probes, so response can take the duration of one
probe. Remove the file before another run:

```powershell
Remove-Item chaos-scripts/STOP
```

To exercise automatic early rollback, use a deliberately low fault-phase latency
threshold (baseline must still be healthy):

```powershell
./chaos-scripts/kill-service.ps1 -MaxUnaffectedLatencyMs 1
```

Choose a value below measured normal latency; if necessary raise steady test load
on the local sandbox. Confirm the report shows `Safety threshold breached` and
the service recovers before the duration limit. Test the manual switch during a
fault and confirm `RollbackReason` is `Manual kill switch`.

## Expected impact (verify against recorded results)

- Workshop planning, job details, scheduling, and finishing depend on the killed
  API and may time out or display an offline fallback.
- Customer and Vehicle pages use separate APIs and are expected to remain usable.
- The web process can remain running even when a module fails.
- RabbitMQ, SQL Server, and the Workshop event handler are not stopped. This
  experiment does not establish message loss or successful background processing.
- Recovery should restore Workshop reads without restarting the web app. Only
  list-page reads are probed; write workflows require a separate manual check.

## Observed local results

Verified on October 10, 2026 against the local Docker Desktop deployment:

| Run | Observation | Fault-to-start completion | Start completion to healthy round |
| --- | --- | --- | --- |
| `20261010T074500332Z` | Duration limit; Workshop probes timed out at about 5 seconds; Customer and Vehicle stayed healthy | 30.96 s | 1.86 s |
| `20261010T074554618Z` | Deliberate 1 ms threshold triggered early rollback on a 20.4 ms Customer response | 0.82 s | 2.91 s |
| `20261010T074610286Z` | STOP file triggered manual rollback | 18.97 s | 2.38 s |

All three runs recovered without restarting the web app or other services.
The threshold run demonstrates the guard working; its deliberately tiny threshold
does not indicate an actual Customer Management performance problem. In that run,
the first Workshop recovery request returned HTTP 200 but failed the content
health check; a later round succeeded. Samples are local generated artifacts and
are ignored by Git; retain or export them separately for submission evidence.

Docker reports the Workshop container as `unhealthy` because its existing image
health-check command requires `curl`, which is absent (`/bin/sh: 1: curl: not
found`). This occurred before injection too. The final Workshop page probe
returned 200 with no offline fallback. This script reports measured page health
separately from the container's broken health-check command.
