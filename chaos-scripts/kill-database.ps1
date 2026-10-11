<#
.SYNOPSIS
    Chaos experiment: stop SQL Server, the one dependency every workflow needs.

.DESCRIPTION
    The dependency map puts six dependents behind `sqlserver`, the highest
    inferred topology risk in the system, and it had never been injected. This
    is the experiment that pays for the map.

    The contrast with the existing two crash experiments is the point:

      kill-service    one module degrades, the other two serve.
      kill-rabbitmq   every page keeps serving. The damage is invisible from
                      the UI - a row commits and its event is never published.
      kill-database   every page fails. Nothing is invisible and nothing is
                      salvageable, because reads and writes both end here.

    Measured, not assumed. The disposable probe behind this script recorded
    3/3 module pages failing and 0 writes accepted during the outage.

    Two deliberate departures from the other experiments:

    1. `docker stop`, not `docker kill`. SIGKILL on a database leaves recovery
       work for startup, and a chaos experiment should not manufacture a
       corruption risk it was not designed to study. SIGTERM lets SQL Server
       shut down cleanly. The outage is just as total.

    2. No unaffected-module safety threshold. Every other experiment aborts when
       a module that should be fine is not; here every module is expected to
       fail, so there is nothing to compare against. The in-flight guards reduce
       to the duration cap and the STOP file. That is exactly why the cap is 30s
       rather than kill-service's 60s, and why -Execute is required.

    The container and its named volume (`sqlserverdata`) are never removed, so
    the data survives the stop. Nothing here calls `rm`.

    Recovery is dominated by SQL Server's own startup, not by the fault: the
    probe measured 58.6s from `docker start` to a healthy page and an accepted
    write. A 20s fault therefore means roughly 80s of disruption, and the
    recovery timeout is sized for that rather than for the fault duration.

    The downtime bound is a floor plus one round, not an exact figure. The
    deadline is checked between rounds, so a round already under way carries the
    fault past it - at most 3 module probes and one write at the fault timeout,
    about 12s. A 15s request measured 20.7s. The report states both the
    requested and the observed duration rather than implying they are equal.
#>
[CmdletBinding()]
param(
    # Tighter than every other experiment because the blast radius is total and
    # there is no unaffected module to abort on.
    [ValidateRange(5, 30)][int]$DowntimeSeconds = 20,

    [ValidateRange(1, 20)][int]$RequestTimeoutSeconds = 10,

    # Shorter during the fault, for two reasons found by a live run. A healthy
    # page answers in under 150ms, so waiting 10s to conclude "not healthy"
    # buys nothing; and three modules timing out at 10s each overran a 15s cap
    # by nine seconds, which made the duration bound soft. A failed probe is
    # established quickly, so the cap stays real.
    [ValidateRange(1, 10)][int]$FaultProbeTimeoutSeconds = 3,

    [ValidateRange(1, 10)][int]$BaselineRounds = 2,

    # Sized from the measured 58.6s, not guessed. Too short a budget would
    # report a false failure to recover while SQL Server was still starting.
    [ValidateRange(60, 300)][int]$RecoveryTimeoutSeconds = 180,

    # The widest blast radius in the catalog does not run by accident.
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
$writes = [System.Collections.Generic.List[object]]::new()
$modules = @('CustomerManagement', 'VehicleManagement', 'WorkshopManagement')
$service = 'sqlserver'

$container = $null
$lock = $null
$watchdog = $null
$stopped = $false
$restored = $false
$recovered = $false
$reason = 'Not injected'
$failure = $null
$faultAt = $null
$restoreAt = $null
$recoveredAt = $null
$observedFaultSeconds = $null

function Invoke-Docker {
    param([string[]]$DockerArgs)
    $result = & docker @DockerArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $result" }
    return $result
}

function Measure-Module {
    param([string]$Module, [string]$Phase, [int]$TimeoutSeconds = 0)
    if ($TimeoutSeconds -le 0) { $TimeoutSeconds = $RequestTimeoutSeconds }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $status = $null; $offline = $false; $healthy = $false; $errorText = $null
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:7005/$Module" -UseBasicParsing -TimeoutSec $TimeoutSeconds
        $status = [int]$response.StatusCode
        # The webapp serves a graceful "module is offline" page rather than an
        # error when its API cannot answer. A 200 carrying that page is a
        # failure, and the probe has to say so.
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
    Write-Host "$Phase $Module healthy=$healthy status=$status offline=$offline latency=$($sample.LatencyMs)ms"
    return $sample
}

function Invoke-CustomerWrite {
    param([string]$Phase, [int]$TimeoutSeconds = 0)
    if ($TimeoutSeconds -le 0) { $TimeoutSeconds = $RequestTimeoutSeconds }
    # The read paths and the write path both terminate at this database, so a
    # write is the sharpest signal: during the outage it should be refused
    # outright, which is the opposite of the kill-rabbitmq result where a write
    # is accepted and its event silently lost.
    $id = "chaos-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $body = @{
        CustomerId = $id; Name = "Chaos Probe $id"; Address = '1 Test Street'
        PostalCode = '1234AB'; City = 'Sandbox'; TelephoneNumber = '0600000000'
        EmailAddress = "$id@example.invalid"
    } | ConvertTo-Json
    $result = [pscustomobject]@{
        TimestampUtc = [DateTime]::UtcNow.ToString('o'); Phase = $Phase
        CustomerId = $id; StatusCode = $null; Accepted = $false; Error = $null
    }
    try {
        $response = Invoke-WebRequest -Uri "$script:customerApi/api/customers" -Method Post -Body $body `
            -ContentType 'application/json' -UseBasicParsing -TimeoutSec $TimeoutSeconds
        $result.StatusCode = [int]$response.StatusCode
        $result.Accepted = $result.StatusCode -ge 200 -and $result.StatusCode -lt 300
    }
    catch {
        $result.Error = $_.Exception.Message
        if ($_.Exception.Response) { $result.StatusCode = [int]$_.Exception.Response.StatusCode }
    }
    $writes.Add($result)
    Write-Host "$Phase write customerId=$id accepted=$($result.Accepted) status=$($result.StatusCode)"
    return $result
}

try {
    Assert-KillSwitchClear -StopFile $stopFile -Context 'before starting'

    $ids = @(Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'ps', '-q', $service))
    if ($ids.Count -ne 1 -or -not $ids[0]) { throw "Expected exactly one running $service container." }
    $container = "$($ids[0])".Trim()

    # This must never reach a Docker endpoint that is not the local sandbox.
    $context = (Invoke-Docker -DockerArgs @('context', 'show') | Out-String).Trim()
    $endpoint = (Invoke-Docker -DockerArgs @('context', 'inspect', $context, '--format', '{{.Endpoints.docker.Host}}') | Out-String).Trim()
    if ($env:DOCKER_HOST -or $endpoint -notmatch '^npipe://') { throw 'This experiment requires a local Windows Docker named-pipe context.' }

    $project = Assert-SandboxContainer -ComposeFile $composeFile -Service $service -ContainerId $container
    $lock = Enter-ExperimentLock -Path (Join-Path $PSScriptRoot 'experiment.lock')
    Write-Host "Target: $service (Compose project: $project), down for ${DowntimeSeconds}s"

    $portLine = (Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'port', 'customermanagementapi', '5100') | Out-String).Trim()
    if (-not $portLine) { throw 'Could not resolve the Customer API published port.' }
    $script:customerApi = "http://127.0.0.1:$(($portLine -split ':')[-1])"

    if (-not $Execute) {
        Write-Host ''
        Write-Host "DRY RUN. Would stop '$service' (container $($container.Substring(0, 12))) for $DowntimeSeconds seconds."
        Write-Host 'Expected: all three module pages fail and writes are refused. This is'
        Write-Host 'the widest blast radius in the catalog - every workflow ends at this'
        Write-Host 'database. Recovery takes roughly a minute after restart, because SQL'
        Write-Host 'Server start-up dominates it.'
        Write-Host "Customer API probe target: $script:customerApi"
        Write-Host 'Re-run with -Execute to inject. Nothing has been changed.'
        if ($lock) { $lock.Dispose() }
        return
    }

    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

    for ($round = 0; $round -lt $BaselineRounds; $round++) {
        foreach ($module in $modules) {
            if (-not (Measure-Module $module 'baseline').Healthy) {
                throw "Baseline failed for $module. No fault was injected."
            }
        }
        Start-Sleep -Seconds 1
    }
    if (-not (Invoke-CustomerWrite 'baseline').Accepted) {
        throw 'Baseline customer write failed. No fault was injected.'
    }

    Assert-KillSwitchClear -StopFile $stopFile -Context 'during baseline'

    # Started before the fault, so a crash between here and the stop still ends
    # with the database running.
    $watchdog = Start-RestartWatchdog -ContainerId $container -DelaySeconds $DowntimeSeconds
    Write-Host "Watchdog container: $($watchdog.Substring(0, [Math]::Min(12, $watchdog.Length)))"

    $stopped = $true
    $faultAt = [DateTime]::UtcNow
    Invoke-Docker -DockerArgs @('stop', $container) | Out-Null
    Write-Host 'SQL Server stopped (SIGTERM, clean shutdown).'

    $reason = 'Maximum downtime reached'
    $faultTimer = [Diagnostics.Stopwatch]::StartNew()

    # The first round always completes in full - every module plus a write -
    # even if that crosses the deadline. A live run that abandoned the round
    # mid-way left Workshop unprobed and no write attempted, so the report
    # claimed a total blast radius while holding evidence for two thirds of it.
    # One guaranteed round costs a few seconds and makes the headline finding
    # answerable from the data.
    $round = 0
    while ($true) {
        if (Test-Path $stopFile) { $reason = 'Manual kill switch'; break }
        foreach ($module in $modules) {
            Measure-Module $module 'fault' -TimeoutSeconds $FaultProbeTimeoutSeconds | Out-Null
        }
        Invoke-CustomerWrite 'fault' -TimeoutSeconds $FaultProbeTimeoutSeconds | Out-Null
        $round++

        if ($faultTimer.Elapsed.TotalSeconds -ge $DowntimeSeconds) { break }
        if (Test-Path $stopFile) { $reason = 'Manual kill switch'; break }
        Start-Sleep -Seconds 2
    }
    $observedFaultSeconds = [math]::Round($faultTimer.Elapsed.TotalSeconds, 1)
}
catch { $failure = $_.Exception.Message; $reason = "Experiment error: $failure" }
finally {
    if ($stopped) {
        try {
            # Start, never recreate. The container and its volume stay intact.
            & docker start $container 2>&1 | Out-Null
            $state = (& docker inspect -f '{{.State.Status}}' $container 2>&1 | Out-String).Trim()
            $restored = $state -eq 'running'
            # Recorded, not just printed. Without this the report would carry
            # Restored=false next to Error=null, which says that restoration
            # failed but not why.
            if (-not $restored) {
                $failure = "Database container did not stay running after start (state: $state)."
            }
            $restoreAt = [DateTime]::UtcNow
            Write-Host "SQL Server container state after start: $state"
        }
        catch { $failure = "Restart failed: $($_.Exception.Message)" }
    }
    if ($watchdog) {
        Stop-RestartWatchdog -WatchdogId $watchdog -RestoreVerified $restored -FiresWithinSeconds ($DowntimeSeconds + 15)
    }
    if ($lock) { $lock.Dispose(); $lock = $null }
}

# A running container proves nothing: SQL Server accepts connections long after
# the process starts, and the APIs need it answering queries. Recovery is
# therefore every page healthy AND a write accepted.
if ($restored) {
    $recoveryTimer = [Diagnostics.Stopwatch]::StartNew()
    while ($recoveryTimer.Elapsed.TotalSeconds -lt $RecoveryTimeoutSeconds) {
        $roundHealthy = $true
        foreach ($module in $modules) {
            if (-not (Measure-Module $module 'recovery').Healthy) { $roundHealthy = $false }
        }
        $write = Invoke-CustomerWrite 'recovery'
        if ($roundHealthy -and $write.Accepted) {
            $recovered = $true; $recoveredAt = [DateTime]::UtcNow; break
        }
        Start-Sleep -Seconds 5
    }
}

if (Test-Path $outputDir) {
    $samples | Export-Csv -Path (Join-Path $outputDir 'samples.csv') -NoTypeInformation
    $writes | Export-Csv -Path (Join-Path $outputDir 'writes.csv') -NoTypeInformation

    $faultSamples = @($samples | Where-Object { $_.Phase -eq 'fault' })
    $faultWrites = @($writes | Where-Object { $_.Phase -eq 'fault' })
    $faultModulesAffected = @($faultSamples | Where-Object { -not $_.Healthy } | Select-Object -ExpandProperty Module -Unique)
    $faultModulesProbed = @($faultSamples | Select-Object -ExpandProperty Module -Unique)
    $faultWritesAccepted = @($faultWrites | Where-Object { $_.Accepted }).Count

    # Derived from this run, not asserted ahead of it. An earlier version
    # carried a fixed sentence about a total blast radius and kept printing it
    # on a run that had probed two of three modules and attempted no write.
    $blastRadius = if ($faultModulesProbed.Count -lt $modules.Count) {
        "Incomplete: $($faultModulesProbed.Count) of $($modules.Count) modules were probed during the outage, so no claim is made about the others. Affected: $($faultModulesAffected -join ', ')."
    }
    elseif ($faultModulesAffected.Count -eq $modules.Count) {
        "Every module page failed ($($faultModulesAffected -join ', ')), unlike kill-service where two stay up and kill-rabbitmq where all three stay up. A synchronous shared dependency has no graceful degradation path here."
    }
    else {
        "Partial: $($faultModulesAffected.Count) of $($modules.Count) modules failed ($($faultModulesAffected -join ', ')). The rest kept serving, which is weaker than this experiment predicted and worth investigating."
    }

    $writeBehaviour = if ($faultWrites.Count -eq 0) {
        'No write was attempted during the outage, so this run says nothing about write behaviour.'
    }
    elseif ($faultWritesAccepted -eq 0) {
        "All $($faultWrites.Count) write attempts were refused during the outage rather than committed. That is the opposite of the kill-rabbitmq finding, where a write is accepted and its event never published. Refusal loses the request; divergence loses the truth."
    }
    else {
        "$faultWritesAccepted of $($faultWrites.Count) writes were accepted while the database was down. That should not be possible and needs investigating before this result is reported."
    }

    $summary = [ordered]@{
        RunId = $runId; Experiment = 'kill-database'; TargetService = $service; ContainerId = $container
        RequestedDowntimeSeconds = $DowntimeSeconds
        # Reported separately because they differ: a probe in flight when the
        # deadline passes carries the fault a little past the request.
        ObservedFaultSeconds = $observedFaultSeconds
        RollbackReason = $reason
        FaultStartedUtc = $faultAt; RestartCompletedUtc = $restoreAt; RecoveredUtc = $recoveredAt
        Restored = $restored; Recovered = $recovered; Error = $failure
        FaultModuleProbes = $faultSamples.Count
        FaultModuleFailures = @($faultSamples | Where-Object { -not $_.Healthy }).Count
        ModulesProbedDuringFault = $faultModulesProbed
        ModulesAffected = $faultModulesAffected
        FaultWrites = $faultWrites.Count
        FaultWritesAccepted = $faultWritesAccepted
        RecoverySeconds = $(if ($recovered -and $restoreAt) { [math]::Round(($recoveredAt - $restoreAt).TotalSeconds, 2) } else { $null })
        # The headline finding, and the contrast that justifies the experiment.
        # Both sentences are built from this run's samples above.
        BlastRadiusNote = $blastRadius
        WriteBehaviourNote = $writeBehaviour
        RecoveryBasis = 'Observed: every module page healthy and a customer write accepted. Container status is not recovery - SQL Server is running well before it answers queries.'
        DataSafetyNote = 'docker stop/start only. The container and the sqlserverdata volume are never removed, so no data is discarded by this experiment.'
        Samples = @($samples.ToArray())
        Writes = @($writes.ToArray())
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outputDir 'report.json') -Encoding UTF8
    Write-Host "Results: $outputDir"
}

if ($failure -or -not $recovered) {
    Write-Host ''
    Write-Host '=== recovery did not complete ==='
    try { Write-Host "container: $((& docker inspect -f '{{.State.Status}}' $container | Out-String).Trim())" } catch { }
    Write-Host 'SQL Server can take over a minute to answer queries after starting.'
    Write-Host 'If the pages are still failing well past that, restart the dependents:'
    Write-Host '  docker compose -f src/docker-compose.yml restart customermanagementapi vehiclemanagementapi workshopmanagementapi'
    throw "Experiment did not complete successfully. $failure Rollback: $reason"
}

$recoverySeconds = [math]::Round(($recoveredAt - $restoreAt).TotalSeconds, 1)
Write-Host "Recovered in ${recoverySeconds}s after restart. Rollback: $reason"
