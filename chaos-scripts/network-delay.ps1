<#
.SYNOPSIS
    Chaos experiment: add bounded egress latency to the Workshop API.

.DESCRIPTION
    The two existing experiments stop a container outright, so the system only
    ever fails one way: completely. This one degrades it instead, which is the
    more common production failure and the one the dashboard has never measured.

    Technique adapted from the agent on origin/task2-kill-service (PR #2). `tc`
    runs in a throwaway sidecar sharing the target's network namespace, so the
    application image needs no `tc` and the application container never holds
    NET_ADMIN.

    Cleanup is the hard part, because the qdisc lives in the TARGET's namespace
    and outlives whatever applied it. Three independent layers, each verified in
    this environment before the experiment was registered:

      1. Normal path - the script deletes the qdisc and confirms it is gone.
      2. Orchestrator dies - a DETACHED watchdog container, owned by Docker
         rather than by this process, deletes it after the duration elapses.
         This survives PowerShell being killed outright, which an in-process
         Start-Job does not.
      3. Everything fails - restarting the container discards the namespace and
         the shaping with it. Documented for the operator.

    Recovery is judged on observed latency returning to baseline, never on the
    container still running - the container never stopped.
#>
[CmdletBinding()]
param(
    [ValidateRange(5, 60)][int]$DurationSeconds = 20,

    # Enough to be unmistakable, not enough to look like an outage. Above the
    # probe timeout the experiment would be indistinguishable from a crash.
    [ValidateRange(50, 2000)][int]$DelayMilliseconds = 400,

    [ValidateRange(1, 20)][int]$RequestTimeoutSeconds = 10,
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
$targetModule = 'WorkshopManagement'
$service = 'workshopmanagementapi'
$netImage = 'pitstop-netem:1.0'
$handle = '28:'

$container = $null
$lock = $null
$watchdog = $null
$injected = $false
$restored = $false
$recovered = $false
$shapingVerified = $false
$removalVerified = $false
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

function Invoke-Tc {
    # --pull=never: a missing image fails loudly rather than downloading one
    # part-way through an experiment.
    param([string[]]$TcArgs)
    return Invoke-Docker -DockerArgs (@(
        'run', '--rm', '--pull=never',
        '--network', "container:$container",
        '--cap-add', 'NET_ADMIN',
        $netImage) + $TcArgs)
}

function Get-Qdisc {
    return (Invoke-Tc -TcArgs @('qdisc', 'show', 'dev', 'eth0') | Out-String).Trim()
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

function Remove-Shaping {
    # Idempotent: safe to call when nothing is applied.
    $current = Get-Qdisc
    if ($current -match 'netem') {
        Invoke-Tc -TcArgs @('qdisc', 'del', 'dev', 'eth0', 'root', 'handle', $handle) | Out-Null
    }
    # Verified, not assumed. The whole experiment rests on this being true.
    $after = Get-Qdisc
    return ($after -notmatch 'netem')
}

try {
    Assert-KillSwitchClear -StopFile $stopFile -Context 'before starting'

    $ids = @(Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'ps', '-q', $service))
    if ($ids.Count -ne 1 -or -not $ids[0]) { throw "Expected exactly one running $service container." }
    $container = "$($ids[0])".Trim()

    $context = (Invoke-Docker -DockerArgs @('context', 'show') | Out-String).Trim()
    $endpoint = (Invoke-Docker -DockerArgs @('context', 'inspect', $context, '--format', '{{.Endpoints.docker.Host}}') | Out-String).Trim()
    if ($env:DOCKER_HOST -or $endpoint -notmatch '^npipe://') { throw 'This experiment requires a local Windows Docker named-pipe context.' }

    $project = Assert-SandboxContainer -ComposeFile $composeFile -Service $service -ContainerId $container
    $lock = Enter-ExperimentLock -Path (Join-Path $PSScriptRoot 'experiment.lock')
    Write-Host "Target: $service (Compose project: $project), delay ${DelayMilliseconds}ms for ${DurationSeconds}s"

    # The sidecar must already exist. Pulling one mid-experiment is both slow and
    # a silent dependency on the network we are about to degrade.
    try { Invoke-Docker -DockerArgs @('image', 'inspect', '--format', '{{.Id}}', $netImage) | Out-Null }
    catch { throw "Sidecar image '$netImage' is not present. Build it: docker build -t $netImage chaos-scripts/netem-image" }

    # Refuse to clobber traffic control somebody else configured.
    $before = Get-Qdisc
    if ($before -match 'netem' -or $before -notmatch 'qdisc noqueue') {
        throw "Refusing to replace an existing traffic-control configuration on ${service}: $before"
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
    $baselineTarget = @($samples | Where-Object { $_.Phase -eq 'baseline' -and $_.Module -eq $targetModule })
    $baselineMs = [math]::Round((($baselineTarget | Measure-Object -Property LatencyMs -Average).Average), 1)

    Assert-KillSwitchClear -StopFile $stopFile -Context 'during baseline'

    # Detached, so Docker owns it. If this script is killed the shaping still
    # comes off; an in-process job would die with the script.
    $watchdog = (Invoke-Docker -DockerArgs @(
        'run', '-d', '--rm', '--pull=never',
        '--network', "container:$container",
        '--cap-add', 'NET_ADMIN',
        '--entrypoint', 'sh',
        $netImage, '-c',
        "sleep $($DurationSeconds + 15); tc qdisc del dev eth0 root handle $handle 2>/dev/null || true"
    ) | Out-String).Trim()
    Write-Host "Watchdog container: $($watchdog.Substring(0, [Math]::Min(12, $watchdog.Length)))"

    $injected = $true
    $faultAt = [DateTime]::UtcNow
    Invoke-Tc -TcArgs @('qdisc', 'add', 'dev', 'eth0', 'root', 'handle', $handle, 'netem', 'delay', "${DelayMilliseconds}ms") | Out-Null

    # Confirm the fault actually landed. An experiment that silently failed to
    # inject would otherwise report a clean run and prove nothing.
    $applied = Get-Qdisc
    $shapingVerified = $applied -match 'netem' -and $applied -match "delay ${DelayMilliseconds}ms"
    if (-not $shapingVerified) { throw "Shaping did not apply as requested. qdisc: $applied" }
    Write-Host "Shaping verified: $applied"

    $reason = 'Maximum fault duration reached'
    $faultTimer = [Diagnostics.Stopwatch]::StartNew()
    :FaultLoop while ($faultTimer.Elapsed.TotalSeconds -lt $DurationSeconds) {
        foreach ($module in $modules) {
            if (Test-Path $stopFile) { $reason = 'Manual kill switch'; break FaultLoop }
            if ($faultTimer.Elapsed.TotalSeconds -ge $DurationSeconds) { break FaultLoop }
            $sample = Measure-Module $module 'fault'
            # The target is expected to be slow. Everything else is not.
            if ($module -ne $targetModule -and (-not $sample.Healthy -or $sample.LatencyMs -gt $MaxUnaffectedLatencyMs)) {
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
            $removalVerified = Remove-Shaping
            $restored = $removalVerified
            $restoreAt = [DateTime]::UtcNow
            Write-Host "Shaping removed and verified: $removalVerified"
        }
        catch { $failure = "Removal failed: $($_.Exception.Message)" }
    }
    if ($watchdog) {
        if ($removalVerified) {
            # Shaping is confirmed gone, so the watchdog has nothing left to do.
            & docker rm -f $watchdog 2>&1 | Out-Null
        }
        else {
            # Removal failed or could not be confirmed. The watchdog is the only
            # remaining thing that will clear the qdisc, so it is deliberately
            # left running - killing it here would destroy the safety net at the
            # exact moment it is needed.
            Write-Warning "Shaping removal was not confirmed. Leaving the watchdog container running to clear it."
            Write-Host "  Watchdog: $watchdog (fires within $($DurationSeconds + 15)s of injection)"
        }
    }
    if ($lock) { $lock.Dispose(); $lock = $null }
}

# Recovery is measured, not inferred from the container still running - it never
# stopped. Latency must come back to within a sane multiple of baseline.
$recoveryBudgetMs = [math]::Max(($baselineMs * 3), ($baselineMs + 150))
if ($restored) {
    $recoveryTimer = [Diagnostics.Stopwatch]::StartNew()
    while ($recoveryTimer.Elapsed.TotalSeconds -lt $RecoveryTimeoutSeconds) {
        $roundHealthy = $true
        foreach ($module in $modules) {
            $sample = Measure-Module $module 'recovery'
            if (-not $sample.Healthy) { $roundHealthy = $false }
            if ($module -eq $targetModule -and $sample.LatencyMs -gt $recoveryBudgetMs) { $roundHealthy = $false }
        }
        if ($roundHealthy) { $recovered = $true; $recoveredAt = [DateTime]::UtcNow; break }
        Start-Sleep -Seconds 2
    }
}

if (Test-Path $outputDir) {
    $samples | Export-Csv -Path (Join-Path $outputDir 'samples.csv') -NoTypeInformation
    $faultTarget = @($samples | Where-Object { $_.Phase -eq 'fault' -and $_.Module -eq $targetModule })
    $summary = [ordered]@{
        RunId = $runId; Experiment = 'network-delay'; TargetService = $service; ContainerId = $container
        RequestedDurationSeconds = $DurationSeconds; RequestedDelayMs = $DelayMilliseconds
        RollbackReason = $reason
        FaultStartedUtc = $faultAt; RestoreCommandCompletedUtc = $restoreAt; RecoveredUtc = $recoveredAt
        Restored = $restored; Recovered = $recovered; Error = $failure
        ShapingVerified = $shapingVerified; ShapingRemovalVerified = $removalVerified
        BaselineTargetLatencyMs = $baselineMs
        FaultTargetLatencyMs = $(if ($faultTarget.Count -gt 0) { [math]::Round((($faultTarget | Measure-Object -Property LatencyMs -Average).Average), 1) } else { $null })
        RecoveryBudgetMs = $recoveryBudgetMs
        RecoverySeconds = $(if ($recovered -and $restoreAt) { [math]::Round(($recoveredAt - $restoreAt).TotalSeconds, 2) } else { $null })
        RecoveryBasis = 'Observed latency returned to within the recovery budget. The container never stopped, so its status proves nothing here.'
        Samples = @($samples.ToArray())
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outputDir 'report.json') -Encoding UTF8
    Write-Host "Results: $outputDir"
}

if ($failure -or -not $recovered -or -not $removalVerified) {
    Write-Host ''
    Write-Host '=== traffic control state ==='
    try { Get-Qdisc | Out-Host } catch { Write-Host "could not read qdisc: $($_.Exception.Message)" }
    Write-Host "If shaping is still present, clear it with:"
    Write-Host "  docker compose -f src/docker-compose.yml restart $service"
    throw "Experiment did not complete successfully. $failure Rollback: $reason"
}
Write-Host "Recovered. Rollback: $reason (baseline ${baselineMs}ms, budget ${recoveryBudgetMs}ms)"
