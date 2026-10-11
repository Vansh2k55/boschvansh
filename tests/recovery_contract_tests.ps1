# Unit/mocked tests. No Docker process, HTTP request or chaos script is started.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $repo 'chaos-scripts/ChaosSafety.psm1') -Force
$temp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
New-Item -ItemType Directory $temp | Out-Null
$passed = 0
function Assert($condition, $message) {
    if (-not $condition) { throw $message }
    $script:passed++
}
try {
    $stop = Join-Path $temp 'STOP'
    New-Item -ItemType File $stop | Out-Null
    $rejected = $false
    try { Assert-KillSwitchClear -StopFile $stop -Context 'unit test' } catch { $rejected = $true }
    Assert $rejected 'STOP must reject admission'
    $path = Join-Path $temp 'experiment.lock'
    $first = Enter-ExperimentLock -Path $path
    try {
        $rejected = $false
        try { $second = Enter-ExperimentLock -Path $path; $second.Dispose() } catch { $rejected = $true }
        Assert $rejected 'Concurrent lock must be rejected'
    } finally { $first.Dispose() }
    $next = Enter-ExperimentLock -Path $path
    $next.Dispose()
    Assert $true 'Lock is usable after release'

    # Both traffic-shaping experiments share this cleanup contract, so both get
    # their real `finally` block executed rather than read.
    foreach ($shaper in @('network-delay', 'packet-loss')) {
        $text = Get-Content (Join-Path $repo "chaos-scripts/$shaper.ps1") -Raw
        $cleanup = [regex]::Match($text, '(?ms)^finally \{(.*?)^\}').Groups[1].Value
        Assert ($cleanup -match 'Remove-Shaping') "$shaper cleanup block was extracted"

        foreach ($mode in @('verified', 'unverified', 'exception')) {
            $script:removed = @()
            $watchdog = 'synthetic-watchdog'; $injected = $true; $lock = $null
            $restored = $false; $removalVerified = $false; $failure = $null; $DurationSeconds = 5
            function docker { $script:removed += ($args -join ' ') }
            function Remove-Shaping {
                if ($mode -eq 'exception') { throw 'synthetic Docker removal error' }
                return $mode -eq 'verified'
            }
            . ([scriptblock]::Create($cleanup))
            Assert ($removed.Count -eq $(if ($mode -eq 'verified') { 1 } else { 0 })) "$shaper watchdog retention: $mode"
            Assert ($restored -eq ($mode -eq 'verified')) "$shaper must not declare recovery: $mode"
            if ($mode -eq 'exception') { Assert ($failure -like 'Removal failed:*') "$shaper cleanup failure stays visible" }
        }
    }

    # Execute the existing RabbitMQ fault-loop with synthetic telemetry. This
    # verifies threshold/STOP decisions, not broker or customer recovery.
    $text = Get-Content (Join-Path $repo 'chaos-scripts/kill-rabbitmq.ps1') -Raw
    $loop = [regex]::Match($text, '(?ms)    :FaultLoop while .*?^    \}').Value
    Assert (-not [string]::IsNullOrWhiteSpace($loop)) 'Found real fault loop'
    foreach ($mode in @('stop', 'unhealthy', 'slow')) {
        $stopFile = $(if ($mode -eq 'stop') { $stop } else { Join-Path $temp 'absent' })
        $DowntimeSeconds = 5; $MaxUnaffectedLatencyMs = 30
        $faultTimer = [Diagnostics.Stopwatch]::StartNew()
        $modules = @('CustomerManagement'); $reason = 'Maximum fault duration reached'
        function Measure-Module { [pscustomobject]@{ Healthy = ($mode -ne 'unhealthy'); LatencyMs = 40 } }
        . ([scriptblock]::Create($loop))
        Assert ($reason -eq $(if ($mode -eq 'stop') { 'Manual kill switch' } else { 'Safety threshold breached: CustomerManagement' })) "RabbitMQ fault-loop decision: $mode"
    }

    # kill-database has the widest blast radius in the catalog, so its cleanup
    # block is executed here rather than taken on trust. The case that matters
    # is a restart that does not take: recovery must not be declared, and the
    # watchdog must survive as the last route back.
    $text = Get-Content (Join-Path $repo 'chaos-scripts/kill-database.ps1') -Raw
    $cleanup = [regex]::Match($text, '(?ms)^finally \{(.*?)^\}').Groups[1].Value
    Assert ($cleanup -match 'docker start') 'kill-database cleanup block was extracted'
    Assert ($cleanup -notmatch 'docker\s+rm\s+\$container') 'Cleanup never removes the database container'
    Assert ($cleanup -notmatch 'volume\s+rm') 'Cleanup never removes the data volume'

    foreach ($mode in @('running', 'stuck')) {
        $script:calls = @()
        $stopped = $true; $watchdog = 'synthetic-watchdog'; $lock = $null
        $restored = $false; $failure = $null; $DowntimeSeconds = 10
        # 'stuck' models docker start reporting success while the container is
        # not actually running - the case where trusting the exit code would
        # declare a recovery that never happened.
        function docker {
            $script:calls += ($args -join ' ')
            if ($args[0] -eq 'inspect') { return $(if ($mode -eq 'running') { 'running' } else { 'exited' }) }
        }
        function Stop-RestartWatchdog {
            param([string]$WatchdogId, [bool]$RestoreVerified, [int]$FiresWithinSeconds)
            $script:watchdogRemoved = $RestoreVerified
        }
        $script:watchdogRemoved = $null
        . ([scriptblock]::Create($cleanup))

        Assert ($calls -contains "start $container" -or ($calls -join ' ') -match 'start') "kill-database restarts the target: $mode"
        Assert ($restored -eq ($mode -eq 'running')) "Recovery not declared on a stuck container: $mode"
        Assert ($script:watchdogRemoved -eq ($mode -eq 'running')) "Watchdog retained unless restore verified: $mode"
    }

} finally {
    # Only the exact directory created above, with no application data.
    Remove-Item -LiteralPath $temp -Recurse -Force
}
"PASS: $passed unit/mocked assertions. No live Docker or HTTP calls."
