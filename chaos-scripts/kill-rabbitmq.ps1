<#
.SYNOPSIS
    Chaos experiment: stop the RabbitMQ broker and measure what eventual
    consistency actually costs.

.DESCRIPTION
    Original experiment by Tigisha P. Pradhan. Reworked to meet the safety
    contract in AGENTS.md: every experiment needs a bounded duration, a
    threshold that aborts, a STOP-file kill switch, a watchdog that restores the
    service even if this script dies, and written evidence.

    The broker is a wider blast radius than a single API - the dependency map
    puts four publishers and four consumers behind it - so the duration cap is
    tighter here than in kill-service.ps1.

    What this measures, and why it is not just "pages go down":

    The three module pages keep serving during the outage, because each API
    reads from its own database and never touches the broker on the read path.
    The damage is invisible from the UI. So the experiment also writes - it
    registers a customer while the broker is down - and records whether the API
    accepts the write. A 2xx there means the row was committed but
    CustomerRegistered could never be published, so the workshop handler, the
    invoice service and the notification service never learn about a customer
    who now exists. That divergence is the finding, and it is evidence, not
    inference.

.NOTES
    Requires -Execute to inject. Without it the script reports what it would do
    and exits, which is the operator-approval gate.
#>
[CmdletBinding()]
param(
    # Capped well below kill-service.ps1's blast radius budget: this stops the
    # broker every event-driven service depends on.
    [ValidateRange(1, 120)][int]$DowntimeSeconds = 45,
    [ValidateRange(1, 10)][int]$RequestTimeoutSeconds = 5,
    [ValidateRange(1, 10)][int]$BaselineRounds = 3,
    [ValidateRange(1, 30000)][int]$MaxUnaffectedLatencyMs = 3000,
    [ValidateRange(10, 300)][int]$RecoveryTimeoutSeconds = 120,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ChaosSafety.psm1') -Force
$repo = Split-Path $PSScriptRoot -Parent
$composeFile = Join-Path $repo 'src/docker-compose.yml'
$stopFile = Join-Path $PSScriptRoot 'STOP'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$outputDir = Join-Path $PSScriptRoot "results/$runId"
$samples = [System.Collections.Generic.List[object]]::new()
$modules = @('CustomerManagement', 'VehicleManagement', 'WorkshopManagement')
$service = 'rabbitmq'
$container = $null
$lock = $null
$watchdog = $null
$injected = $false
$restored = $false
$recovered = $false
$reason = 'Not injected'
$failure = $null
$faultAt = $null
$restoreAt = $null
$recoveredAt = $null
$writeDuringOutage = $null

function Invoke-Docker {
    param([string[]]$DockerArgs)
    $result = & docker @DockerArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $result" }
    return $result
}

function Measure-Module {
    param([string]$Module, [string]$Phase)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $status = $null; $offline = $false; $healthy = $false; $errorText = $null
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:7005/$Module" -UseBasicParsing -TimeoutSec $RequestTimeoutSeconds
        $status = [int]$response.StatusCode
        $offline = $response.Content -match '(?i)module is offline|management\s*-\s*offline'
        $healthy = $status -eq 200 -and -not $offline -and $response.Content -match ($Module -replace 'Management', ' Management')
    }
    catch { $errorText = $_.Exception.Message }
    $timer.Stop()
    $sample = [pscustomobject]@{
        TimestampUtc = [DateTime]::UtcNow.ToString('o'); Phase = $Phase
        Module = $Module; StatusCode = $status; OfflinePage = $offline
        Healthy = $healthy; LatencyMs = [math]::Round($timer.Elapsed.TotalMilliseconds, 1)
        Error = $errorText
    }
    $samples.Add($sample)
    Write-Host "$Phase $Module healthy=$healthy status=$status latency=$($sample.LatencyMs)ms"
    return $sample
}

function Test-BrokerReady {
    # check_running only says the Erlang node is up. It returns true while the
    # broker is still refusing AMQP connections, and a publisher that reconnects
    # on demand will still fail against it - an earlier run of this experiment
    # reported a false recovery for exactly that reason. check_port_connectivity
    # is the gate that matches what a publisher actually needs.
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker compose -f $composeFile exec -T $service rabbitmq-diagnostics -q check_running 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { return $false }
        & docker compose -f $composeFile exec -T $service rabbitmq-diagnostics -q check_port_connectivity 2>&1 | Out-Null
        return ($LASTEXITCODE -eq 0)
    }
    finally { $ErrorActionPreference = $previous }
}

function Test-CustomerExists {
    param([string]$CustomerId)
    try {
        $response = Invoke-WebRequest -Uri "$script:customerApi/api/customers/$CustomerId" -UseBasicParsing -TimeoutSec $RequestTimeoutSeconds
        return ([int]$response.StatusCode -eq 200)
    }
    catch { return $false }
}

function Invoke-CustomerWrite {
    param([string]$Phase)
    # Exercises the publish path: the API commits a row and then publishes
    # CustomerRegistered. With the broker down, the two halves disagree.
    $id = "chaos-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $body = @{
        CustomerId = $id; Name = "Chaos Probe $id"; Address = '1 Test Street'
        PostalCode = '1234AB'; City = 'Sandbox'; TelephoneNumber = '0600000000'
        EmailAddress = "$id@example.invalid"
    } | ConvertTo-Json
    $result = [ordered]@{ Phase = $Phase; CustomerId = $id; StatusCode = $null; Accepted = $false; Error = $null }
    try {
        $response = Invoke-WebRequest -Uri "$script:customerApi/api/customers" -Method Post -Body $body `
            -ContentType 'application/json' -UseBasicParsing -TimeoutSec $RequestTimeoutSeconds
        $result.StatusCode = [int]$response.StatusCode
        $result.Accepted = $result.StatusCode -ge 200 -and $result.StatusCode -lt 300
    }
    catch {
        $result.Error = $_.Exception.Message
        if ($_.Exception.Response) { $result.StatusCode = [int]$_.Exception.Response.StatusCode }
    }
    Write-Host "$Phase write customerId=$id accepted=$($result.Accepted) status=$($result.StatusCode)"
    return $result
}

try {
    Assert-KillSwitchClear -StopFile $stopFile -Context 'before starting'

    # Blast radius: this must never reach a Docker endpoint that is not the
    # local sandbox. Checked before anything is touched.
    $ids = @(Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'ps', '-q', $service))
    if ($ids.Count -ne 1 -or -not $ids[0]) { throw "Expected exactly one running $service container." }
    $container = "$($ids[0])".Trim()
    $context = (Invoke-Docker -DockerArgs @('context', 'show') | Out-String).Trim()
    $endpoint = (Invoke-Docker -DockerArgs @('context', 'inspect', $context, '--format', '{{.Endpoints.docker.Host}}') | Out-String).Trim()
    if ($env:DOCKER_HOST -or $endpoint -notmatch '^npipe://') { throw 'This experiment requires a local Windows Docker named-pipe context.' }

    # Blast radius: the container must belong to this checkout, and no other
    # experiment may be running anywhere on the machine.
    $project = Assert-SandboxContainer -ComposeFile $composeFile -Service 'rabbitmq' -ContainerId $container
    $lock = Enter-ExperimentLock -Path (Join-Path $PSScriptRoot 'experiment.lock')
    Write-Host "Target: rabbitmq (Compose project: $project)"

    $portLine = (Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'port', 'customermanagementapi', '5100') | Out-String).Trim()
    if (-not $portLine) { throw 'Could not resolve the Customer API published port.' }
    $script:customerApi = "http://127.0.0.1:$(($portLine -split ':')[-1])"

    if (-not $Execute) {
        Write-Host ''
        Write-Host "DRY RUN. Would stop '$service' (container $container) for $DowntimeSeconds seconds."
        Write-Host "Customer API probe target: $script:customerApi"
        Write-Host 'Re-run with -Execute to inject. Nothing has been changed.'
        exit 0
    }

    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

    if (-not (Test-BrokerReady)) { throw 'Broker is not ready before the experiment. Aborting.' }
    for ($round = 0; $round -lt $BaselineRounds; $round++) {
        foreach ($module in $modules) {
            if (-not (Measure-Module $module 'baseline').Healthy) {
                throw "Baseline failed for $module. No fault was injected."
            }
        }
        Start-Sleep -Seconds 1
    }
    $baselineWrite = Invoke-CustomerWrite 'baseline'
    if (-not $baselineWrite.Accepted) { throw 'Baseline customer write failed. No fault was injected.' }

    Assert-KillSwitchClear -StopFile $stopFile -Context 'during baseline'

    # The watchdog restores the broker even if this script is killed outright.
    $watchdog = Start-Job -ArgumentList $composeFile, $service, $DowntimeSeconds -ScriptBlock {
        param($file, $target, $duration)
        Start-Sleep -Seconds $duration
        & docker compose -f $file start $target
        if ($LASTEXITCODE -ne 0) { throw 'Watchdog could not restore the broker.' }
    }

    $injected = $true
    $faultAt = [DateTime]::UtcNow
    Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'kill', '-s', 'SIGKILL', $service) | Out-Null
    # kill alone leaves the container eligible for restart; stop keeps it down
    # for the whole measured window.
    Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'stop', $service) | Out-Null
    $reason = 'Maximum fault duration reached'

    $faultTimer = [Diagnostics.Stopwatch]::StartNew()
    $wroteDuringOutage = $false
    :FaultLoop while ($faultTimer.Elapsed.TotalSeconds -lt $DowntimeSeconds) {
        foreach ($module in $modules) {
            if (Test-Path $stopFile) { $reason = 'Manual kill switch'; break FaultLoop }
            if ($faultTimer.Elapsed.TotalSeconds -ge $DowntimeSeconds) { break FaultLoop }
            $sample = Measure-Module $module 'fault'
            # Every module is "unaffected" on the read path here, so all three
            # are held to the threshold.
            if (-not $sample.Healthy -or $sample.LatencyMs -gt $MaxUnaffectedLatencyMs) {
                $reason = "Safety threshold breached: $module"
                break FaultLoop
            }
        }
        if (-not $wroteDuringOutage) {
            $writeDuringOutage = Invoke-CustomerWrite 'fault'
            $wroteDuringOutage = $true
        }
        Start-Sleep -Seconds 1
    }
}
catch { $failure = $_.Exception.Message; $reason = "Experiment error: $failure" }
finally {
    if ($injected) {
        try {
            Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'start', $service) | Out-Null
            $restored = $true
            $restoreAt = [DateTime]::UtcNow
        }
        catch { $failure = "Restore failed: $($_.Exception.Message)" }
    }
    if ($lock) { $lock.Dispose(); $lock = $null }
    if ($watchdog -and $restored) {
        Stop-Job $watchdog
        Remove-Job $watchdog -Force
    }
    elseif ($watchdog) {
        Wait-Job $watchdog -Timeout ($DowntimeSeconds + 10) | Out-Null
        Receive-Job $watchdog -ErrorAction Continue | Out-Host
    }
}

# Recovery means the publish path works again, not merely that the pages render.
# The read path never broke, so module health alone would declare recovery while
# the system was still unable to emit a single event.
$postRecoveryWrite = $null
if ($restored) {
    $recoveryTimer = [Diagnostics.Stopwatch]::StartNew()
    while ($recoveryTimer.Elapsed.TotalSeconds -lt $RecoveryTimeoutSeconds) {
        if (Test-BrokerReady) {
            $roundHealthy = $true
            foreach ($module in $modules) {
                if (-not (Measure-Module $module 'recovery').Healthy) { $roundHealthy = $false }
            }
            if ($roundHealthy) {
                $postRecoveryWrite = Invoke-CustomerWrite 'recovery'
                if ($postRecoveryWrite.Accepted) {
                    $recovered = $true
                    $recoveredAt = [DateTime]::UtcNow
                    break
                }
                Write-Host 'recovery publish path not yet restored, retrying'
            }
        }
        else { Write-Host 'recovery broker still initializing' }
        Start-Sleep -Seconds 2
    }
}

# Divergence check. If the outage write was rejected but the row is present, the
# database and the event stream disagree: the customer exists, but no
# CustomerRegistered was ever published, so no downstream service knows about
# them. Recorded as evidence; the analysis layer decides what it means.
$outageWriteCommitted = $null
if ($writeDuringOutage -and $writeDuringOutage.CustomerId) {
    $outageWriteCommitted = Test-CustomerExists $writeDuringOutage.CustomerId
    Write-Host "divergence check: outage write accepted=$($writeDuringOutage.Accepted) rowCommitted=$outageWriteCommitted"
}

if (Test-Path $outputDir) {
    $samples | Export-Csv -Path (Join-Path $outputDir 'samples.csv') -NoTypeInformation
    $summary = [ordered]@{
        RunId = $runId; Experiment = 'kill-rabbitmq'; TargetService = $service; ContainerId = $container
        RequestedDurationSeconds = $DowntimeSeconds; RollbackReason = $reason
        FaultStartedUtc = $faultAt; RestoreCommandCompletedUtc = $restoreAt; RecoveredUtc = $recoveredAt
        Restored = $restored; Recovered = $recovered; Error = $failure
        RecoverySeconds = $(if ($recovered -and $restoreAt) { [math]::Round(($recoveredAt - $restoreAt).TotalSeconds, 2) } else { $null })
        BaselineWrite = $baselineWrite; WriteDuringOutage = $writeDuringOutage; PostRecoveryWrite = $postRecoveryWrite
        OutageWriteRowCommitted = $outageWriteCommitted
        DivergenceDetected = ($null -ne $writeDuringOutage -and -not $writeDuringOutage.Accepted -and $outageWriteCommitted -eq $true)
        # Recorded as an observation, not a verdict - a human reads the write
        # results and decides what it means.
        ReadPathObservation = 'Module pages are served from each API''s own database and do not touch the broker, so they are expected to stay healthy during the outage.'
        Samples = @($samples.ToArray())
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outputDir 'report.json') -Encoding UTF8
    Write-Host "Results: $outputDir"
}

if ($failure -or -not $recovered) {
    # Ported from Tigisha's original: when recovery fails the broker's own logs
    # are the first thing anyone will want, and by then the container may be in
    # a state that is awkward to inspect by hand.
    Write-Host ''
    Write-Host '=== broker status and last 30 log lines ==='
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker compose -f $composeFile ps -a $service 2>&1 | Out-Host
        & docker compose -f $composeFile logs --tail=30 $service 2>&1 | Out-Host
    }
    finally { $ErrorActionPreference = $previous }
    throw "Experiment did not complete successfully. $failure Rollback: $reason"
}
Write-Host "Broker recovered. Rollback: $reason"
