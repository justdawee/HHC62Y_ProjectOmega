# SmartPantry - local Release runner (PowerShell)
# Reads .env, exports the variables to the process environment, then runs
# `dotnet run -c Release` in the SmartPantry project. Use Ctrl+C to stop.

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$EnvFile  = Join-Path $RepoRoot '.env'
$ProjDir  = Join-Path $RepoRoot 'SmartPantry'

if (-not (Test-Path $EnvFile)) {
    Write-Host ""
    Write-Host "  .env not found at $EnvFile" -ForegroundColor Red
    Write-Host "  Copy .env.example to .env and fill in your OPENAI_API_KEY first." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "  Loading .env ..." -ForegroundColor Cyan
Get-Content $EnvFile | ForEach-Object {
    $line = $_.Trim()
    if ($line -and -not $line.StartsWith('#') -and $line -match '^([^=]+)=(.*)$') {
        $name  = $Matches[1].Trim()
        $value = $Matches[2].Trim()
        Set-Item -Path "Env:$name" -Value $value
        if ($name -eq 'OPENAI_API_KEY') {
            $masked = if ($value.Length -gt 12) { $value.Substring(0, 8) + '...' + $value.Substring($value.Length - 4) } else { '***' }
            Write-Host "    $name = $masked"
        } else {
            Write-Host "    $name = $value"
        }
    }
}

if (-not $env:OPENAI_API_KEY) {
    Write-Host ""
    Write-Host "  OPENAI_API_KEY missing from .env - recipe generation will fail." -ForegroundColor Yellow
    Write-Host ""
}

# Honour PORT from .env if set; default to 5000 for local runs (so it
# doesn't clash with the Docker default of 8080 if they're side by side).
$port = if ($env:PORT) { $env:PORT } else { '5000' }
if (-not $env:ASPNETCORE_URLS) {
    $env:ASPNETCORE_URLS = "http://localhost:$port"
}
if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Production' }

Write-Host ""
Write-Host "  Starting SmartPantry on http://localhost:$port ..." -ForegroundColor Green
Write-Host "  (Ctrl+C to stop)" -ForegroundColor DarkGray
Write-Host ""

Push-Location $ProjDir
try {
    & dotnet run -c Release
} finally {
    Pop-Location
}
