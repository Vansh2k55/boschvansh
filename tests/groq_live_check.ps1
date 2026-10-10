<#
.SYNOPSIS
    Live Groq check: does a given model actually satisfy our strict schema?

.DESCRIPTION
    A genuine live-provider test, not a mocked one. It sends a real request to
    Groq using the exact recommendation schema the dashboard sends, fetched from
    /api/ai/schema so the two cannot drift apart.

    Why it exists: the first live call failed with 413 (payload too large, since
    fixed), and the second with
    `400 Failed to validate JSON. Please adjust your prompt.` Our schema is
    strict-compliant - every property is in `required`, `additionalProperties`
    is false on every object, and no unsupported keywords are used - which
    points at the model failing to produce conforming output rather than at the
    schema. This answers that with evidence instead of a guess.

    The key is read from $env:GROQ_API_KEY and is never printed, logged or
    written anywhere. Only the status code, the upstream message and whether the
    response parsed are reported.

.EXAMPLE
    ./tests/groq_live_check.ps1
    ./tests/groq_live_check.ps1 -Models 'openai/gpt-oss-20b','openai/gpt-oss-120b'
#>
[CmdletBinding()]
param(
    [string[]]$Models = @('openai/gpt-oss-20b', 'openai/gpt-oss-120b'),
    [string]$DashboardUrl = 'http://127.0.0.1:5280',
    [ValidateRange(10, 300)][int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

if (-not $env:GROQ_API_KEY) {
    Write-Host 'GROQ_API_KEY is not set in this window.'
    Write-Host 'Set it, then re-run. It is never printed or stored by this script.'
    exit 2
}

# The real schema, so this test cannot drift from what the dashboard sends.
try {
    $schema = (Invoke-WebRequest "$DashboardUrl/api/ai/schema" -UseBasicParsing -TimeoutSec 15).Content |
        ConvertFrom-Json
}
catch {
    Write-Host "Could not reach $DashboardUrl/api/ai/schema."
    Write-Host 'Start the dashboard from a build that includes the schema endpoint, then re-run.'
    exit 2
}

$prompt = @'
Recommend one chaos experiment. Allowed experiments: kill-service (target
workshopmanagementapi, 5-60s) and kill-rabbitmq (target rabbitmq, 5-120s).
Evidence: all three module pages healthy at about 12ms; rabbitmq has 4
dependents and sqlserver has 6; a previous broker outage showed a committed
customer row whose event was never published. Return discoveries as an empty
list.
'@

$results = @()
foreach ($model in $Models) {
    $body = @{
        model    = $model
        messages = @(
            @{ role = 'system'; content = 'You are a chaos engineering advisor. Answer only with the requested JSON.' },
            @{ role = 'user'; content = $prompt }
        )
        response_format = @{
            type        = 'json_schema'
            json_schema = @{
                name   = 'chaos_experiment_recommendation'
                strict = $true
                schema = $schema.recommendation
            }
        }
    } | ConvertTo-Json -Depth 30

    Write-Host "`n=== $model ==="
    $status = $null; $upstream = $null; $parsed = $false; $experiment = $null
    $missing = @()

    try {
        $response = Invoke-WebRequest 'https://api.groq.com/openai/v1/chat/completions' `
            -Method Post -Body $body -ContentType 'application/json' `
            -Headers @{ Authorization = "Bearer $env:GROQ_API_KEY" } `
            -UseBasicParsing -TimeoutSec $TimeoutSeconds
        $status = [int]$response.StatusCode

        $content = ($response.Content | ConvertFrom-Json).choices[0].message.content
        $recommendation = $content | ConvertFrom-Json
        $parsed = $true
        $experiment = $recommendation.experiment

        # Parsing only proves it is JSON. The schema's own `required` list is
        # what the dashboard depends on, so check the returned object against
        # it rather than labelling any parseable response schema-valid.
        # $schema.recommendation IS the schema, so `required` is at its top
        # level. Reaching one level deeper would hit the discoveries item's own
        # `required` - the same mistake that once produced a false positive.
        $required = @($schema.recommendation.required)
        $returned = @($recommendation.PSObject.Properties.Name)
        $missing = @($required | Where-Object { $_ -notin $returned })
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $null }
        # Report the upstream message only, never the request that carried the key.
        try {
            $raw = $_.ErrorDetails.Message
            $upstream = ($raw | ConvertFrom-Json).error.message
        }
        catch { $upstream = $_.Exception.Message }
    }

    Write-Host "  status            : $status"
    Write-Host "  parsed as JSON    : $parsed"
    Write-Host "  required fields   : $(if (-not $parsed) { 'n/a - nothing to check' } elseif ($missing.Count -eq 0) { 'all present' } else { "MISSING $($missing -join ', ')" })"
    if ($experiment) { Write-Host "  experiment chosen : $experiment" }
    if ($upstream) { Write-Host "  upstream said     : $upstream" }

    $results += [pscustomobject]@{
        Model = $model; Status = $status; Parsed = $parsed
        MissingFields = $missing; Conforms = ($parsed -and $missing.Count -eq 0)
        Upstream = $upstream
    }
}

Write-Host "`n=== VERDICT ==="
$working = @($results | Where-Object { $_.Conforms })
foreach ($r in $results | Where-Object { $_.Parsed -and -not $_.Conforms }) {
    Write-Host "$($r.Model): returned JSON but omitted $($r.MissingFields -join ', '). Not a pass."
}
if ($working.Count -gt 0) {
    Write-Host "Groq returned conforming output for: $(($working.Model) -join ', ')"
    Write-Host 'Set the default with GROQ_MODEL, or update dashboard/appsettings.json.'
    Write-Host ''
    Write-Host 'This is the standalone provider path only. Groq is not verified'
    Write-Host 'end-to-end until a real dashboard recommendation reports'
    Write-Host 'provider: groq - this check bypasses the application.'
    exit 0
}

Write-Host 'Groq live verification FAILED for every model tried.'
Write-Host 'The dashboard will keep falling back to Ollama, which is not a Groq pass.'
exit 1
