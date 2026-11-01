<#
.SYNOPSIS
  BlueDrive CI: build + test backend, build + lint web, validate docker compose.
.EXAMPLE
  powershell -File scripts/ci.ps1            # full
  powershell -File scripts/ci.ps1 -SkipIntegration
#>
param(
    [switch]$SkipIntegration,
    [switch]$SkipWeb,
    [switch]$SkipDocker
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($name, [scriptblock]$block) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $name" -ForegroundColor Red; exit $LASTEXITCODE }
}

Step 'dotnet build' { dotnet build BlueDrive.slnx -c Release --nologo }
Step 'unit tests' { dotnet test tests/BlueDrive.UnitTests -c Release --no-build --nologo }
if (-not $SkipIntegration) {
    Step 'integration tests (Testcontainers, cần Docker)' { dotnet test tests/BlueDrive.IntegrationTests -c Release --no-build --nologo }
}
if (-not $SkipWeb) {
    Push-Location web
    Step 'npm ci' { npm ci --no-audit --no-fund }
    Step 'npm run lint' { npm run lint }
    Step 'npm run build' { npm run build }
    Pop-Location
}
if (-not $SkipDocker) {
    $env:POSTGRES_PASSWORD = 'ci'; $env:JWT_SECRET = 'ci_secret_ci_secret_ci_secret_ci_secret'
    $env:ADMIN_PASSWORD = 'Ci@12345'; $env:STORAGE_MASTER_KEY = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='; $env:ONLYOFFICE_JWT_SECRET = 'ci'
    Step 'docker compose config' { docker compose -f deploy/docker-compose.yml config --quiet }
}
Write-Host 'CI OK' -ForegroundColor Green
