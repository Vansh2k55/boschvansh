<#
.SYNOPSIS
    Scores a chaos experiment's evidence and writes a resilience report.

.DESCRIPTION
    Step 1 of the design in docs/AI_ANALYSIS_DESIGN.md: the deterministic
    scorer. No LLM, no network, no Docker. It reads evidence files and nothing
    else, so it is safe to run at any time and gives the same answer every time.

    The scoring rules are published in docs/AI_ANALYSIS_DESIGN.md and
    implemented here in the same order. A number nobody can check by hand is
    not worth having.

    Dimensions that an experiment did not measure are excluded and the rest
    renormalised to 100. The report always states which dimensions were
    measured, because "not measured" must never read as "passed".
#>
[CmdletBinding()]
param(
    # Defaults to every report under results/, newest first.
    [string[]]$ReportPath,
    [string]$OutputDirectory,
    [ValidateRange(1, 600)][int]$RecoveryTargetSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'results/analysis' }
if (-not $ReportPath) {
    $ReportPath = @(Get-ChildItem (Join-Path $PSScriptRoot 'results') -Filter 'report.json' -Recurse |
        Sort-Object LastWriteTime -Descending | ForEach-Object { $_.FullName })
}
if (-not $ReportPath) { throw 'No report.json found. Run an experiment first.' }

# Which module page represents the injected service. A broker outage has no
# target module - every module is on the unaffected read path - so containment
# is measured across all of them.
$targetModules = @{ workshopmanagementapi = 'WorkshopManagement' }
$controlledRollbacks = @('Maximum fault duration reached', 'Manual kill switch')

function Get-Dimension {
    param([string]$Name, [int]$Weight, [nullable[double]]$Fraction, [string]$Detail)
    # A null fraction means the evidence to score this dimension is absent.
    [pscustomobject]@{
        Name = $Name; Weight = $Weight; Measured = ($null -ne $Fraction)
        Earned = $(if ($null -ne $Fraction) { [math]::Round($Weight * $Fraction, 2) } else { $null })
        Detail = $Detail
    }
}

$analyses = foreach ($path in $ReportPath) {
    $report = Get-Content $path -Raw | ConvertFrom-Json
    $experiment = if ($report.Experiment) { $report.Experiment } else { 'kill-service' }
    $targetModule = $targetModules[$report.TargetService]
    $faultSamples = @($report.Samples | Where-Object { $_.Phase -eq 'fault' })
    $dimensions = [System.Collections.Generic.List[object]]::new()

    # 1. Blast radius containment.
    $unaffected = @($faultSamples | Where-Object { $_.Module -ne $targetModule })
    if ($unaffected.Count -gt 0) {
        $healthy = @($unaffected | Where-Object { $_.Healthy }).Count
        $dimensions.Add((Get-Dimension 'Blast radius containment' 25 ($healthy / $unaffected.Count) `
            "$healthy of $($unaffected.Count) non-target probes stayed healthy during the fault"))
    }
    else { $dimensions.Add((Get-Dimension 'Blast radius containment' 25 $null 'No non-target probes recorded')) }

    # 2. Graceful degradation of the injected service.
    $target = @($faultSamples | Where-Object { $_.Module -eq $targetModule })
    if ($targetModule -and $target.Count -gt 0) {
        # A rendered offline page is a handled failure. A null status code is a
        # hard timeout - the caller waited and got nothing.
        $handled = @($target | Where-Object { $_.OfflinePage -or $null -ne $_.StatusCode }).Count
        $dimensions.Add((Get-Dimension 'Graceful degradation' 20 ($handled / $target.Count) `
            "$handled of $($target.Count) target probes degraded gracefully rather than timing out"))
    }
    elseif ($targetModule) {
        # The experiment has a target page, but the run ended before any fault
        # probe reached it - an early abort, not a clean result.
        $dimensions.Add((Get-Dimension 'Graceful degradation' 20 $null `
            "Target module $targetModule recorded no fault probes - the run ended before one was taken"))
    }
    else {
        $dimensions.Add((Get-Dimension 'Graceful degradation' 20 $null `
            'No single target module - the injected service has no user-facing page of its own'))
    }

    # 3. Recovery.
    if (-not $report.Recovered) {
        $dimensions.Add((Get-Dimension 'Recovery' 25 0.0 'Service did not recover within the timeout'))
    }
    elseif ($null -eq $report.RecoverySeconds) {
        $dimensions.Add((Get-Dimension 'Recovery' 25 1.0 'Recovered; no recovery time recorded'))
    }
    else {
        $fraction = [math]::Max(0.0, ($RecoveryTargetSeconds - $report.RecoverySeconds) / $RecoveryTargetSeconds)
        $dimensions.Add((Get-Dimension 'Recovery' 25 $fraction `
            "Recovered in $($report.RecoverySeconds)s against a ${RecoveryTargetSeconds}s target"))
    }

    # 4. Data integrity. Only experiments that exercise a write path can score this.
    if ($null -ne $report.DivergenceDetected) {
        $intact = -not $report.DivergenceDetected
        $dimensions.Add((Get-Dimension 'Data integrity' 20 $(if ($intact) { 1.0 } else { 0.0 }) `
            $(if ($intact) { 'No divergence between the database and the event stream' }
              else { "Write returned $($report.WriteDuringOutage.StatusCode) but the row was committed - no event was published" })))
    }
    else { $dimensions.Add((Get-Dimension 'Data integrity' 20 $null 'No write path was exercised')) }

    # 5. Safety controls.
    $controlled = $report.Restored -and (
        ($controlledRollbacks -contains $report.RollbackReason) -or $report.RollbackReason -like 'Safety threshold breached*')
    $dimensions.Add((Get-Dimension 'Safety controls' 10 $(if ($controlled) { 1.0 } else { 0.0 }) `
        "Restored=$($report.Restored), rollback reason '$($report.RollbackReason)'"))

    $measured = @($dimensions | Where-Object { $_.Measured })
    $availableWeight = ($measured | Measure-Object -Property Weight -Sum).Sum
    $earned = ($measured | Measure-Object -Property Earned -Sum).Sum
    $score = if ($availableWeight -gt 0) { [math]::Round(100 * $earned / $availableWeight) } else { $null }

    # Findings from fixed rules. The narrative layer, when it is built, adds
    # prose on top of these - it does not replace them.
    $findings = [System.Collections.Generic.List[object]]::new()
    if ($report.DivergenceDetected) {
        $findings.Add([pscustomobject][ordered]@{
            Severity = 'High'
            Title = 'Database and event stream diverge during a broker outage'
            Hypothesis = 'The database write and the event publish are not atomic, so there is no transactional outbox.'
            Evidence = @("WriteDuringOutage.StatusCode=$($report.WriteDuringOutage.StatusCode)",
                         "OutageWriteRowCommitted=$($report.OutageWriteRowCommitted)",
                         'DivergenceDetected=true')
            Recommendation = 'Write the event to an outbox table in the same transaction as the row and drain it with a relay, and make the write idempotent on CustomerId so a retry cannot duplicate the record.'
        })
    }
    if (-not $report.Recovered) {
        $findings.Add([pscustomobject][ordered]@{
            Severity = 'High'; Title = 'Service did not recover within the timeout'
            Hypothesis = 'Restore completed but the service did not return to a healthy state in the allotted window.'
            Evidence = @("Recovered=$($report.Recovered)", "Restored=$($report.Restored)", "Error=$($report.Error)")
            Recommendation = 'Investigate startup dependencies and whether the health check covers the path that is actually broken.'
        })
    }
    $gracefulDimension = $dimensions | Where-Object { $_.Name -eq 'Graceful degradation' }
    if ($gracefulDimension.Measured -and $gracefulDimension.Earned -lt ($gracefulDimension.Weight * 0.5)) {
        $findings.Add([pscustomobject][ordered]@{
            Severity = 'Medium'; Title = 'Injected service hard-times-out instead of failing fast'
            Hypothesis = 'The caller has no timeout or circuit breaker on this dependency, so a dead service costs a full request timeout per call.'
            Evidence = @($gracefulDimension.Detail)
            Recommendation = 'Add a short client timeout and a circuit breaker so the module renders its offline page immediately instead of blocking.'
        })
    }

    # A run that aborts after one probe can score high simply because almost
    # nothing was measured. The score is still correct for what it saw, so it is
    # reported with how much it saw rather than quietly adjusted.
    $confidence = if ($faultSamples.Count -ge 9) { 'high' }
                  elseif ($faultSamples.Count -ge 3) { 'medium' }
                  else { 'low' }

    [pscustomobject][ordered]@{
        RunId = $report.RunId; Experiment = $experiment; TargetService = $report.TargetService
        ReportPath = $path; ResilienceScore = $score
        FaultSampleCount = $faultSamples.Count; Confidence = $confidence
        ScoredDimensions = @($measured | ForEach-Object { $_.Name })
        UnmeasuredDimensions = @($dimensions | Where-Object { -not $_.Measured } | ForEach-Object { $_.Name })
        Dimensions = @($dimensions); Findings = @($findings)
    }
}

$analyses = @($analyses)

# --- Topology risk, inferred from the dependency map --------------------------
# This is deliberately kept separate from everything above. Scores and findings
# come from faults we actually injected and observed. What follows is inferred
# from the shape of the graph and has never been tested, so the report must not
# let the two read as the same kind of statement.
$topologyRisks = @()
$graphPath = Join-Path $PSScriptRoot 'results/dependency-map/graph.json'
$graph = $null
if (Test-Path $graphPath) {
    $graph = Get-Content $graphPath -Raw | ConvertFrom-Json
    foreach ($entry in $graph.mostDependedOn) {
        $kinds = @($graph.edges | Where-Object { $_.to -eq $entry.service } |
            ForEach-Object { $_.kind } | Sort-Object -Unique)
        # Degree alone is misleading: every service logs to Seq, which makes the
        # log sink the most connected node and the least critical one. What
        # matters is whether a dependent needs it to serve or to stay correct.
        $loadBearing = @($kinds | Where-Object { $_ -in @('database', 'publish', 'subscribe', 'rest') })
        $risk = if ($loadBearing.Count -eq 0) { 'low' }
                elseif ($entry.dependentCount -ge 4) { 'high' }
                elseif ($entry.dependentCount -ge 2) { 'medium' }
                else { 'low' }
        $rationale = if ($loadBearing.Count -eq 0) {
            "$($entry.dependentCount) dependents, but only via $($kinds -join '/'). Losing it costs observability, not function."
        } else {
            "$($entry.dependentCount) dependents over $($loadBearing -join '/') edges. A fault here stops work, not just reporting."
        }
        $topologyRisks += [pscustomobject][ordered]@{
            Service = $entry.service; DependentCount = $entry.dependentCount
            EdgeKinds = $kinds; Risk = $risk; Basis = 'inferred from dependency map; not injected'
            Rationale = $rationale; Dependents = $entry.dependents
        }
    }
    $topologyRisks = @($topologyRisks | Sort-Object -Property @{Expression={@{high=0;medium=1;low=2}[$_.Risk]}}, @{Expression='DependentCount';Descending=$true})
}

# --- Per-experiment aggregate ------------------------------------------------
$experiments = @()
foreach ($group in $analyses | Group-Object Experiment) {
    # Only runs that actually injected and probed enough to mean something feed
    # the headline score. A one-probe abort is kept in the history, not averaged
    # into the result.
    $credible = @($group.Group | Where-Object { $_.Confidence -ne 'low' -and $null -ne $_.ResilienceScore })
    $scored = if ($credible.Count -gt 0) { $credible } else { @() }
    $experiments += [pscustomobject][ordered]@{
        Experiment = $group.Name
        TotalRuns = $group.Count
        CredibleRuns = $credible.Count
        BestScore = $(if ($scored.Count -gt 0) { ($scored | Measure-Object -Property ResilienceScore -Maximum).Maximum } else { $null })
        WorstScore = $(if ($scored.Count -gt 0) { ($scored | Measure-Object -Property ResilienceScore -Minimum).Minimum } else { $null })
        RepresentativeScore = $(if ($scored.Count -gt 0) { [math]::Round(($scored | Measure-Object -Property ResilienceScore -Average).Average) } else { $null })
        ScoreBasis = $(if ($scored.Count -gt 0) { "mean of $($credible.Count) run(s) with medium or better confidence" } else { 'no credible run yet' })
        MeasuredDimensions = @($group.Group.ScoredDimensions | Sort-Object -Unique)
        NeverMeasured = @($group.Group[0].Dimensions.Name | Where-Object { $_ -notin @($group.Group.ScoredDimensions | Sort-Object -Unique) })
    }
}
$experiments = @($experiments | Sort-Object Experiment)

# --- Consolidated findings ----------------------------------------------------
# A problem seen in four runs is not four problems. Reproduction is what turns a
# single observation into something worth acting on, so it drives confidence.
$consolidated = @()
foreach ($group in ($analyses.Findings | Where-Object { $_ } | Group-Object Title)) {
    $count = $group.Count
    $confidence = if ($count -ge 3) { 'high' } elseif ($count -ge 2) { 'medium' } else { 'low' }
    $first = $group.Group[0]
    $consolidated += [pscustomobject][ordered]@{
        Title = $group.Name; Severity = $first.Severity; Status = 'observed'
        ReproducedInRuns = $count; Confidence = $confidence
        Hypothesis = $first.Hypothesis; Evidence = $first.Evidence
        Recommendation = $first.Recommendation
    }
}
$consolidated = @($consolidated | Sort-Object -Property @{Expression={@{High=0;Medium=1;Low=2}[$_.Severity]}}, @{Expression='ReproducedInRuns';Descending=$true})

# --- Coverage gaps ------------------------------------------------------------
$targeted = @($analyses.TargetService | Sort-Object -Unique)
$coverageGaps = @()
if ($graph) {
    foreach ($risk in $topologyRisks | Where-Object { $_.Risk -in @('high', 'medium') }) {
        if ($risk.Service -notin $targeted) {
            $coverageGaps += [pscustomobject][ordered]@{
                Kind = 'untested dependency'; Service = $risk.Service
                Detail = "$($risk.Risk) inferred risk ($($risk.DependentCount) dependents) and no experiment has ever targeted it."
            }
        }
    }
}
foreach ($e in $experiments) {
    foreach ($dimension in $e.NeverMeasured) {
        $coverageGaps += [pscustomobject][ordered]@{
            Kind = 'unmeasured dimension'; Service = $e.Experiment
            Detail = "$dimension has never been measured by this experiment, so its score says nothing about it."
        }
    }
}

# --- Cross-experiment comparison ---------------------------------------------
$comparison = $null
$scoredExperiments = @($experiments | Where-Object { $null -ne $_.RepresentativeScore })
if ($scoredExperiments.Count -ge 2) {
    $ranked = @($scoredExperiments | Sort-Object RepresentativeScore)
    $comparison = [ordered]@{
        WeakestExperiment = $ranked[0].Experiment
        Ranking = @($ranked | ForEach-Object { "$($_.Experiment): $($_.RepresentativeScore)" })
        Note = 'Scores are comparable only where the same dimensions were measured. Check NeverMeasured on each experiment before reading a gap as a real difference.'
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = [ordered]@{
    GeneratedUtc = [DateTime]::UtcNow.ToString('o')
    ScoringRules = 'docs/AI_ANALYSIS_DESIGN.md'
    RecoveryTargetSeconds = $RecoveryTargetSeconds
    NarrativeLayer = 'not enabled (deterministic scorer only)'
    Experiments = @($experiments)
    Comparison = $comparison
    ObservedFindings = @($consolidated)
    InferredTopologyRisks = @($topologyRisks)
    CoverageGaps = @($coverageGaps)
    Analyses = $analyses
}
$output | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $OutputDirectory 'analysis.json') -Encoding UTF8

$md = [System.Collections.Generic.List[string]]::new()
$md.Add('# Resilience analysis')
$md.Add('')
$md.Add("Generated $($output.GeneratedUtc) by ``chaos-scripts/analyze.ps1``.")
$md.Add('Scores are computed by the published rules in `docs/AI_ANALYSIS_DESIGN.md`.')
$md.Add('No language model was involved in any number on this page.')
$md.Add('')
foreach ($a in $analyses) {
    $md.Add("## $($a.Experiment) — $($a.RunId)")
    $md.Add('')
    $md.Add("**Resilience score: $($a.ResilienceScore)/100**, over $($a.ScoredDimensions.Count) of $($a.Dimensions.Count) dimensions.")
    $md.Add('')
    $md.Add("Confidence **$($a.Confidence)** — $($a.FaultSampleCount) fault probe(s) recorded. A run that ended early measures little, so a high score from few probes says less than the same score from many.")
    if ($a.UnmeasuredDimensions.Count -gt 0) {
        $md.Add('')
        $md.Add("Not measured by this experiment: $($a.UnmeasuredDimensions -join ', '). Unmeasured is not the same as passing.")
    }
    $md.Add('')
    $md.Add('| Dimension | Weight | Earned | Detail |')
    $md.Add('| --- | --- | --- | --- |')
    foreach ($d in $a.Dimensions) {
        $earned = if ($d.Measured) { $d.Earned } else { 'not measured' }
        $md.Add("| $($d.Name) | $($d.Weight) | $earned | $($d.Detail) |")
    }
    $md.Add('')
    if ($a.Findings.Count -eq 0) { $md.Add('No findings from the deterministic rules.') }
    foreach ($f in $a.Findings) {
        $md.Add("### $($f.Severity): $($f.Title)")
        $md.Add('')
        $md.Add("**Hypothesis.** $($f.Hypothesis)")
        $md.Add('')
        $md.Add("**Evidence.** $($f.Evidence -join '; ')")
        $md.Add('')
        $md.Add("**Recommendation.** $($f.Recommendation)")
        $md.Add('')
    }
}
$md -join "`n" | Set-Content -Path (Join-Path $OutputDirectory 'analysis.md') -Encoding UTF8

Write-Host "Analysed $($analyses.Count) run(s)."
foreach ($a in $analyses) { Write-Host "  $($a.Experiment) $($a.RunId): $($a.ResilienceScore)/100 ($($a.Findings.Count) finding(s))" }
Write-Host "Output: $OutputDirectory"
