<#
.SYNOPSIS
    Safety primitives shared by every chaos experiment.

.DESCRIPTION
    Blast-radius and concurrency guards that every experiment must pass before
    it injects anything. Kept in one module so a new experiment inherits them
    rather than reimplementing them, and so a weakening is a single visible diff.

    The ownership verification and the OS-level lock are adapted from the agent
    on origin/task2-kill-service (PR #2, Tigisha P. Pradhan / Arpit Pal). Both
    are stronger than what the scripts previously had.
#>

function Assert-SandboxContainer {
    <#
    .SYNOPSIS
        Refuses any container not owned by this checkout's Compose project.

    .DESCRIPTION
        Resolving a service name through `docker compose ps -q` already scopes
        to a project, but it trusts whatever project the current directory
        resolves to. Reading the container's own Compose labels and comparing
        the recorded config file against the compose file we were handed closes
        that gap: a container belonging to another project, or to another
        checkout of this one, is refused before anything is injected.
    #>
    param(
        [Parameter(Mandatory)][string]$ComposeFile,
        [Parameter(Mandatory)][string]$Service,
        [Parameter(Mandatory)][string]$ContainerId
    )

    $raw = & docker inspect $ContainerId 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect container ${ContainerId}: $raw" }

    $labels = ($raw | Out-String | ConvertFrom-Json)[0].Config.Labels
    $project = $labels.'com.docker.compose.project'
    $labelledService = $labels.'com.docker.compose.service'

    if ($labelledService -ne $Service) {
        throw "Container $ContainerId is labelled service '$labelledService', not '$Service'. Refusing."
    }

    $configFiles = @(([string]$labels.'com.docker.compose.project.config_files') -split ',' |
        Where-Object { $_ } |
        ForEach-Object { [IO.Path]::GetFullPath($_.Trim()) })

    if ([IO.Path]::GetFullPath($ComposeFile) -notin $configFiles) {
        throw "Container $ContainerId belongs to Compose project '$project', which was not created from $ComposeFile. Refusing a container this checkout does not own."
    }

    return $project
}

function Enter-ExperimentLock {
    <#
    .SYNOPSIS
        Takes an exclusive OS-level lock so two experiments cannot overlap.

    .DESCRIPTION
        The dashboard already refuses a second concurrent run, but that lock
        lives in one process. Two scripts started from two terminals would not
        see each other, and overlapping faults make every measurement
        meaningless and the blast radius unpredictable.

        The handle must be disposed by the caller; releasing the file is what
        releases the lock, and the OS does it even if the process is killed.
    #>
    param([Parameter(Mandatory)][string]$Path)

    try {
        return [System.IO.File]::Open($Path, 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch [System.IO.IOException] {
        throw "Another experiment is already running (lock held on $Path). Overlapping experiments are not permitted."
    }
}

function Assert-KillSwitchClear {
    <#
    .SYNOPSIS
        Fails if the STOP file is present.

    .DESCRIPTION
        Called twice: once before the baseline, and again immediately before
        injection. Without the second check, engaging the kill switch during a
        baseline that takes several seconds still lets the fault through - the
        operator asked it to stop and it injected anyway.
    #>
    param(
        [Parameter(Mandatory)][string]$StopFile,
        [string]$Context = 'before starting'
    )

    if (Test-Path $StopFile) {
        throw "Kill switch engaged ($StopFile) $Context. No fault was injected."
    }
}

function Start-RestartWatchdog {
    <#
    .SYNOPSIS
        Starts a detached container that restarts the target after a delay.

    .DESCRIPTION
        Replaces the PowerShell Start-Job watchdogs, which were described in
        comments as independent and are not. The disposable audit in
        tests/disposable_recovery_audit.py proved it: with the parent forcibly
        terminated, and again when the session merely exited, the job never ran
        and the target stayed stopped past its deadline. A job is a thread in
        the orchestrator's process, so it dies with the thing it is supposed to
        outlive.

        A detached container is owned by Docker. It survives the orchestrator
        being killed, which is the only failure this watchdog exists for. This
        is the same pattern already proven for network-delay.

        Trade-off, stated plainly: the watchdog mounts the Docker socket, so for
        its bounded lifetime it can drive the Docker API. That is the same
        authority the experiment script already holds, granted to a throwaway
        container for seconds rather than to anything persistent. The
        alternative is no independent recovery at all, which the audit rated
        High severity.
    #>
    param(
        [Parameter(Mandatory)][string]$ContainerId,
        [Parameter(Mandatory)][int]$DelaySeconds,
        [string]$Image = 'docker:cli',

        # Beyond the fault window, so the watchdog is a backstop rather than a
        # race against the script's own restore.
        [int]$GraceSeconds = 15
    )

    $check = & docker image inspect --format '{{.Id}}' $Image 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Watchdog image '$Image' is not present. Pull it once: docker pull $Image"
    }

    $total = $DelaySeconds + $GraceSeconds
    # --pull=never so a missing image fails here rather than downloading while a
    # fault is active.
    $id = & docker run -d --rm --pull=never `
        -v /var/run/docker.sock:/var/run/docker.sock `
        $Image sh -c "sleep $total; docker start $ContainerId || true" 2>&1

    if ($LASTEXITCODE -ne 0) { throw "Could not start the recovery watchdog: $id" }
    return "$id".Trim()
}

function Stop-RestartWatchdog {
    <#
    .SYNOPSIS
        Removes the watchdog, but only when recovery is confirmed.

    .DESCRIPTION
        If restoration was not verified, the watchdog is the only thing left
        that will bring the target back, so it is deliberately left running.
        Removing it here would destroy the safety net at the exact moment it is
        needed.
    #>
    param(
        [string]$WatchdogId,
        [Parameter(Mandatory)][bool]$RestoreVerified,
        [int]$FiresWithinSeconds = 0
    )

    if (-not $WatchdogId) { return }

    if ($RestoreVerified) {
        & docker rm -f $WatchdogId 2>&1 | Out-Null
        return
    }

    Write-Warning "Restore was not confirmed. Leaving the recovery watchdog running to restart the target."
    Write-Host "  Watchdog: $WatchdogId (fires within ${FiresWithinSeconds}s of injection)"
}

Export-ModuleMember -Function Assert-SandboxContainer, Enter-ExperimentLock, Assert-KillSwitchClear, Start-RestartWatchdog, Stop-RestartWatchdog
