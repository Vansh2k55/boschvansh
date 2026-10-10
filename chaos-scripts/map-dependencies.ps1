<#
.SYNOPSIS
    Builds the Pitstop service dependency and data-flow map.

.DESCRIPTION
    Three sources are combined, strongest evidence first:

      1. docker compose config   - services, images, ports, declared depends_on.
      2. docker inspect          - which containers are actually up, their health,
                                   and their container ids.
      3. dependency-overlay.json - REST and RabbitMQ event edges read out of the
                                   source, because Compose cannot express them.

    Writes graph.json (machine readable, for the experiment tooling) and
    docs/dependency-map.md (a Mermaid diagram for humans and judges).

    Read-only: it inspects Docker, it never changes the running stack.
#>
[CmdletBinding()]
param(
    [string]$OutputJson,
    [string]$OutputMarkdown
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$composeFile = Join-Path $repo 'src/docker-compose.yml'
$overlayFile = Join-Path $PSScriptRoot 'dependency-overlay.json'
if (-not $OutputJson) { $OutputJson = Join-Path $PSScriptRoot 'results/dependency-map/graph.json' }
if (-not $OutputMarkdown) { $OutputMarkdown = Join-Path $repo 'docs/dependency-map.md' }

function Invoke-Docker {
    param([string[]]$DockerArgs)
    $result = & docker @DockerArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $result" }
    return $result
}

# --- 1. Static topology from Compose ------------------------------------------
$config = (Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'config', '--format', 'json')) |
    Out-String | ConvertFrom-Json

$infrastructure = @('rabbitmq', 'sqlserver', 'mailserver', 'logserver')
$nodes = [ordered]@{}

foreach ($entry in $config.services.PSObject.Properties) {
    $name = $entry.Name
    $svc = $entry.Value
    $nodes[$name] = [ordered]@{
        name       = $name
        kind       = if ($infrastructure -contains $name) { 'infrastructure' } else { 'service' }
        image      = $svc.image
        ports      = @($svc.ports | ForEach-Object { if ($_.published) { "$($_.published)->$($_.target)" } else { "$($_.target)" } })
        dependsOn  = @($svc.depends_on.PSObject.Properties.Name)
        running    = $false
        health     = 'not running'
        publishes  = @()
        subscribes = @()
    }
}

# --- 2. Runtime state ---------------------------------------------------------
# A map built only from the compose file would claim services are present that
# never actually came up. Chaos experiments need to know what is really live.
foreach ($name in @($nodes.Keys)) {
    $id = ((Invoke-Docker -DockerArgs @('compose', '-f', $composeFile, 'ps', '-q', $name)) | Out-String).Trim()
    if (-not $id) { continue }
    $id = ($id -split "`n")[0].Trim()
    $state = (Invoke-Docker -DockerArgs @('inspect', '--format', '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}no healthcheck{{end}}', $id)) | Out-String
    $parts = $state.Trim() -split '\|'
    $nodes[$name].running = ($parts[0] -eq 'running')
    $nodes[$name].health = $parts[1]
    $nodes[$name].containerId = $id
}

# --- 3. Code-derived overlay --------------------------------------------------
$overlay = Get-Content $overlayFile -Raw | ConvertFrom-Json
$edges = [System.Collections.Generic.List[object]]::new()
$seenEdges = [System.Collections.Generic.HashSet[string]]::new()

function Add-Edge {
    param([string]$From, [string]$To, [string]$Kind, [string]$Label)
    if (-not $nodes.Contains($From) -or -not $nodes.Contains($To)) {
        Write-Warning "Skipping $Kind edge $From -> $To : not a compose service."
        return
    }
    # Several services log to Seq and talk to the same database, so the same
    # edge is discovered more than once.
    if (-not $seenEdges.Add("$From|$To|$Kind|$Label")) { return }
    $edges.Add([ordered]@{ from = $From; to = $To; kind = $Kind; label = $Label })
}

# Events do not flow service to service, they flow through the broker. Modelling
# them as publisher -> broker -> subscriber keeps RabbitMQ visible as the shared
# point of failure it is, instead of hiding it behind direct edges.
foreach ($p in $overlay.publishes.PSObject.Properties) {
    $nodes[$p.Name].publishes = @($p.Value)
    foreach ($evt in $p.Value) { Add-Edge $p.Name 'rabbitmq' 'publish' $evt }
}
foreach ($s in $overlay.subscribes.PSObject.Properties) {
    $nodes[$s.Name].subscribes = @($s.Value)
    foreach ($evt in $s.Value) { Add-Edge 'rabbitmq' $s.Name 'subscribe' $evt }
}

# REST, database, mail and logging edges all come out of each service's
# appsettings.Production.json - that is where Pitstop actually resolves its
# dependencies at runtime (compose only sets DOTNET_ENVIRONMENT). Reading them
# here means the map follows the code if someone repoints a service.
foreach ($settings in Get-ChildItem (Join-Path $repo 'src') -Filter 'appsettings.Production.json' -Recurse -Depth 2) {
    $from = $settings.Directory.Name.ToLowerInvariant()
    if (-not $nodes.Contains($from)) { continue }
    $cfg = Get-Content $settings.FullName -Raw | ConvertFrom-Json

    foreach ($api in $cfg.APIServiceLocations.PSObject.Properties) {
        # Values look like "customermanagementapi:5100".
        Add-Edge $from (($api.Value -split ':')[0].ToLowerInvariant()) 'rest' 'HTTP'
    }
    foreach ($cn in $cfg.ConnectionStrings.PSObject.Properties) {
        if ($cn.Value -match '(?i)server=([^;]+)') {
            $target = ($matches[1] -split ',')[0].Trim().ToLowerInvariant()
            $database = if ($cn.Value -match '(?i)database=([^;]+)') { $matches[1].Trim() } else { 'SQL' }
            Add-Edge $from $target 'database' $database
        }
    }
    if ($cfg.Email.Host) { Add-Edge $from $cfg.Email.Host.ToLowerInvariant() 'smtp' 'SMTP' }
    foreach ($sink in $cfg.Serilog.WriteTo) {
        if ($sink.Args.serverUrl -and ([uri]$sink.Args.serverUrl).Host) {
            Add-Edge $from ([uri]$sink.Args.serverUrl).Host.ToLowerInvariant() 'logging' 'logs'
        }
    }
}

# --- 4. Most depended-on components -------------------------------------------
# Whatever a large share of the live services point at is the widest blast
# radius, and the ranking the Day 2 AI planner consumes when ordering
# experiments.
$ranked = @()
foreach ($name in $nodes.Keys) {
    $dependents = @($edges | Where-Object { $_.to -eq $name } | ForEach-Object { $_.from } | Sort-Object -Unique)
    if ($dependents.Count -gt 0) {
        $ranked += [ordered]@{ service = $name; dependentCount = $dependents.Count; dependents = $dependents }
    }
}
$ranked = @($ranked | Sort-Object -Property dependentCount -Descending)

$graph = [ordered]@{
    generatedUtc          = [DateTime]::UtcNow.ToString('o')
    composeFile           = $composeFile
    nodes                 = @($nodes.Values)
    edges                 = @($edges.ToArray())
    mostDependedOn        = $ranked
    overlayNotes          = $overlay.notes
}

New-Item -ItemType Directory -Path (Split-Path $OutputJson -Parent) -Force | Out-Null
$graph | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputJson -Encoding UTF8

# --- 5. Mermaid diagram -------------------------------------------------------
$md = [System.Collections.Generic.List[string]]::new()
$md.Add('# Pitstop dependency and data-flow map')
$md.Add('')
$md.Add("Generated by ``chaos-scripts/map-dependencies.ps1`` at $($graph.generatedUtc).")
$md.Add('Regenerate after any change to `src/docker-compose.yml` or to the event handlers.')
$md.Add('')
$md.Add('```mermaid')
$md.Add('graph LR')
$md.Add('  user([User]) --> webapp')
foreach ($n in $nodes.Values) {
    $shape = if ($n.kind -eq 'infrastructure') { "[($($n.name))]" } else { "[$($n.name)]" }
    $md.Add("  $($n.name)$shape")
}
foreach ($e in $edges) {
    $arrow = if ($e.kind -eq 'publish' -or $e.kind -eq 'subscribe') { '-. ' + $e.label + ' .->' } else { '-- ' + $e.label + ' -->' }
    $md.Add("  $($e.from) $arrow $($e.to)")
}
$md.Add('```')
$md.Add('')
$md.Add('## Live state at generation time')
$md.Add('')
$md.Add('| Service | Kind | Running | Health | Publishes | Subscribes |')
$md.Add('| --- | --- | --- | --- | --- | --- |')
foreach ($n in $nodes.Values) {
    $md.Add("| $($n.name) | $($n.kind) | $($n.running) | $($n.health) | $($n.publishes -join ', ') | $($n.subscribes -join ', ') |")
}
$md.Add('')
$md.Add('## Most depended-on components')
$md.Add('')
$md.Add('Ranked by how many other components point at them, which is a first pass at')
$md.Add('where a fault has the widest blast radius.')
$md.Add('')
$md.Add('Read the edge *kind* as well as the count. `logserver` is the most connected')
$md.Add('node only because every service ships logs to it; losing it costs observability,')
$md.Add('not function. `sqlserver` and `rabbitmq` carry `database` and `publish`/`subscribe`')
$md.Add('edges and are the genuine single points of failure - every user-visible workflow')
$md.Add('stops without them. Rank experiments on that distinction, not on degree alone.')
$md.Add('')
$md.Add('| Component | Dependents | Depended on by |')
$md.Add('| --- | --- | --- |')
foreach ($s in $ranked) {
    $md.Add("| $($s.service) | $($s.dependentCount) | $($s.dependents -join ', ') |")
}
$md.Add('')
New-Item -ItemType Directory -Path (Split-Path $OutputMarkdown -Parent) -Force | Out-Null
$md -join "`n" | Set-Content -Path $OutputMarkdown -Encoding UTF8

Write-Host "Graph:   $OutputJson"
Write-Host "Diagram: $OutputMarkdown"
Write-Host "Nodes: $($nodes.Count)  Edges: $($edges.Count)"
