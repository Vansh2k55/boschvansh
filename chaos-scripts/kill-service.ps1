[CmdletBinding()]
param(
    [ValidateRange(1, 60)][int]$DurationSeconds = 30,
    [ValidateRange(1, 10)][int]$RequestTimeoutSeconds = 5,
    [ValidateRange(1, 10)][int]$BaselineRounds = 3,
    [ValidateRange(1, 30000)][int]$MaxUnaffectedLatencyMs = 3000,
    [ValidateRange(10, 180)][int]$RecoveryTimeoutSeconds = 90
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

function Invoke-Docker {
    param([string[]]$DockerArgs)
    $result = & docker @DockerArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $result" }
    return $result
}

function Measure-Module {
    param([string]$Module, [string]$Phase)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $status = $null
    $offline = $false
    $healthy = $false
    $errorText = $null
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

try {
    Assert-KillSwitchClear -StopFile $stopFile -Context 'before starting'
    $ids = @(Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'ps', '-q', 'workshopmanagementapi'))
    if ($ids.Count -ne 1 -or -not $ids[0]) { throw 'Expected exactly one running workshopmanagementapi container.' }
    $container = "$($ids[0])".Trim()
    $context = (Invoke-Docker -DockerArgs @('context', 'show') | Out-String).Trim()
    $endpoint = (Invoke-Docker -DockerArgs @('context', 'inspect', $context, '--format', '{{.Endpoints.docker.Host}}') | Out-String).Trim()
    if ($env:DOCKER_HOST -or $endpoint -notmatch '^npipe://') { throw 'This experiment requires a local Windows Docker named-pipe context.' }

    # Blast radius: the container must belong to this checkout, and no other
    # experiment may be running anywhere on the machine.
    $project = Assert-SandboxContainer -ComposeFile $composeFile -Service 'workshopmanagementapi' -ContainerId $container
    $lock = Enter-ExperimentLock -Path (Join-Path $PSScriptRoot 'experiment.lock')
    Write-Host "Target: workshopmanagementapi (Compose project: $project)"

    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    for ($round = 0; $round -lt $BaselineRounds; $round++) {
        foreach ($module in $modules) {
            $sample = Measure-Module $module 'baseline'
            if (-not $sample.Healthy) {
                throw "Baseline failed for $module. No fault was injected."
            }
        }
        Start-Sleep -Seconds 1
    }

    # The operator may have engaged the kill switch during the baseline,
    # which takes several seconds. Without this, the fault still goes in.
    Assert-KillSwitchClear -StopFile $stopFile -Context 'during baseline'

    # A detached container, not a Start-Job. The disposable audit proved the
    # job never ran once its parent was killed, which is the only failure a
    # watchdog exists for. Docker owns this one.
    $watchdog = Start-RestartWatchdog -ContainerId $container -DelaySeconds $DurationSeconds
    Write-Host "Watchdog container: $($watchdog.Substring(0, [Math]::Min(12, $watchdog.Length)))"
    $injected = $true
    $faultAt = [DateTime]::UtcNow
    Invoke-Docker -DockerArgs @('kill', $container) | Out-Null
    $reason = 'Maximum fault duration reached'
    $faultTimer = [Diagnostics.Stopwatch]::StartNew()
    :FaultLoop while ($faultTimer.Elapsed.TotalSeconds -lt $DurationSeconds) {
        foreach ($module in $modules) {
            if (Test-Path $stopFile) { $reason = 'Manual kill switch'; break FaultLoop }
            if ($faultTimer.Elapsed.TotalSeconds -ge $DurationSeconds) { break FaultLoop }
            $sample = Measure-Module $module 'fault'
            if ($module -ne 'WorkshopManagement' -and (-not $sample.Healthy -or $sample.LatencyMs -gt $MaxUnaffectedLatencyMs)) {
                $reason = "Safety threshold breached: $module"
                break FaultLoop
            }
        }
        Start-Sleep -Seconds 1
    }
}
catch { $failure = $_.Exception.Message; $reason = "Experiment error: $failure" }
finally {
    if ($injected) {
        try {
            Invoke-Docker -DockerArgs @('start', $container) | Out-Null
            # A zero exit code means the start command was accepted, not that
            # the container stayed up. This is verified because the value below
            # decides whether Stop-RestartWatchdog removes the only independent
            # route back: a container that starts and immediately dies would
            # otherwise have its safety net deleted on the strength of an exit
            # code. kill-database already worked this way; this matches it.
            $state = (& docker inspect -f '{{.State.Status}}' $container 2>&1 | Out-String).Trim()
            $restored = $state -eq 'running'
            if (-not $restored) {
                $failure = "Container did not stay running after start (state: $state)."
            }
            $restoreAt = [DateTime]::UtcNow
        }
        catch { $failure = "Restore failed: $($_.Exception.Message)" }
    }
    if ($lock) { $lock.Dispose(); $lock = $null }
    # Removed only when the restart command succeeded. Otherwise the watchdog
    # is the only thing left that will bring the target back, so killing it
    # here would destroy the safety net at the moment it is needed.
    Stop-RestartWatchdog -WatchdogId $watchdog -RestoreVerified $restored -FiresWithinSeconds ($DurationSeconds + 15)
}

if ($restored) {
    $recoveryTimer = [Diagnostics.Stopwatch]::StartNew()
    while ($recoveryTimer.Elapsed.TotalSeconds -lt $RecoveryTimeoutSeconds) {
        $roundHealthy = $true
        foreach ($module in $modules) {
            if (-not (Measure-Module $module 'recovery').Healthy) { $roundHealthy = $false }
        }
        if ($roundHealthy) { $recovered = $true; $recoveredAt = [DateTime]::UtcNow; break }
        Start-Sleep -Seconds 2
    }
}

if (Test-Path $outputDir) {
    $samples | Export-Csv -Path (Join-Path $outputDir 'samples.csv') -NoTypeInformation
    $summary = [ordered]@{
        RunId = $runId; TargetService = 'workshopmanagementapi'; ContainerId = $container
        RequestedDurationSeconds = $DurationSeconds; RollbackReason = $reason
        FaultStartedUtc = $faultAt; RestoreCommandCompletedUtc = $restoreAt; RecoveredUtc = $recoveredAt
        Restored = $restored; Recovered = $recovered; Error = $failure
        RecoverySeconds = $(if ($recovered) { [math]::Round(($recoveredAt - $restoreAt).TotalSeconds, 2) } else { $null })
        Samples = @($samples.ToArray())
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outputDir 'report.json') -Encoding UTF8
    Write-Host "Results: $outputDir"
}
if ($failure -or -not $recovered) { throw "Experiment did not complete successfully. $failure Rollback: $reason" }
Write-Host "Service recovered. Rollback: $reason"
