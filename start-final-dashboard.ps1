<#+
Starts the final-demo dashboard with optional local Groq credentials.
The .env file is deliberately ignored by Git and is never printed.
#>
[CmdletBinding()]
param(
    [int]$Port = 5280
)

$envFile = Join-Path $PSScriptRoot '.env'
if (Test-Path -LiteralPath $envFile) {
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^\s*(GROQ_API_KEY|GROQ_MODEL)\s*=\s*(.+?)\s*$') {
            Set-Item -Path "Env:$($matches[1])" -Value $matches[2].Trim('"').Trim("'")
        }
    }
}

if (-not $env:GROQ_API_KEY) {
    Write-Warning 'GROQ_API_KEY is not set. The dashboard can still use Ollama or deterministic analysis.'
}

dotnet run --project (Join-Path $PSScriptRoot 'dashboard/Dashboard.csproj') --urls "http://127.0.0.1:$Port"
