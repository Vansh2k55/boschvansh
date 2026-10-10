<#
.SYNOPSIS
    Regression test for the write/event divergence on the customer publish path.

.DESCRIPTION
    The defect: CustomersController commits the row with SaveChangesAsync and
    then publishes CustomerRegistered. If the publish throws, the row is already
    committed, the caller gets a 500, and no event is ever produced - so the
    workshop handler, invoice service and notification service never learn the
    customer exists, and nothing replays.

    This test does not need a broker outage to reproduce it. It writes N
    customers against a healthy system and, for every failed write, checks
    whether the row is nevertheless present. Any such case is a divergence.

    Read-only with respect to infrastructure: it starts no faults and stops no
    containers. It only creates test customers, which is sandbox data.

.NOTES
    Exit code 0 when no divergence is observed, 1 otherwise, so it can gate a
    build. See docs/publish-integrity.md for the measured before/after.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 500)][int]$Writes = 25,
    [ValidateRange(1, 30)][int]$RequestTimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$composeFile = Join-Path $repo 'src/docker-compose.yml'

$portLine = (& docker compose -f $composeFile port customermanagementapi 5100 | Out-String).Trim()
if (-not $portLine) { throw 'Could not resolve the Customer API published port. Is the stack running?' }
$api = "http://localhost:$((($portLine -split ':')[-1]))"
Write-Host "Customer API: $api"
Write-Host "Writing $Writes customers against a healthy system."
Write-Host ''

$accepted = 0
$divergent = 0
$cleanFailures = 0
$divergentIds = [System.Collections.Generic.List[string]]::new()

for ($i = 1; $i -le $Writes; $i++) {
    $id = "regress-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $body = @{
        CustomerId = $id; Name = "Integrity probe $i"; Address = '1 Test Street'
        PostalCode = '1234AB'; City = 'Sandbox'; TelephoneNumber = '0600000000'
        EmailAddress = "$id@example.invalid"
    } | ConvertTo-Json

    $failed = $false
    try {
        Invoke-WebRequest -Uri "$api/api/customers" -Method Post -Body $body `
            -ContentType 'application/json' -UseBasicParsing -TimeoutSec $RequestTimeoutSeconds | Out-Null
        $accepted++
    }
    catch { $failed = $true }

    if (-not $failed) { continue }

    # The signature of the defect: the caller was told the write failed, but the
    # row is there. The event that should accompany it never was.
    $committed = $false
    try {
        Invoke-WebRequest -Uri "$api/api/customers/$id" -UseBasicParsing -TimeoutSec $RequestTimeoutSeconds | Out-Null
        $committed = $true
    }
    catch { }

    if ($committed) {
        $divergent++
        $divergentIds.Add($id)
        Write-Host "  DIVERGENT #${i}: write reported failure, row $id is committed, no event published"
    }
    else {
        $cleanFailures++
        Write-Host "  clean failure #${i}: write rejected and nothing was committed"
    }
}

Write-Host ''
Write-Host "accepted=$accepted  divergent=$divergent  failed-cleanly=$cleanFailures  of $Writes"

if ($divergent -gt 0) {
    $rate = [math]::Round(100 * $divergent / $Writes, 1)
    Write-Host ''
    Write-Host "FAIL: $divergent of $Writes writes ($rate%) left the database and the event stream disagreeing."
    Write-Host "Affected ids: $($divergentIds -join ', ')"
    exit 1
}

Write-Host ''
Write-Host 'PASS: every failed write also failed to commit. No divergence observed.'
exit 0
