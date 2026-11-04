<#
.SYNOPSIS
  HSMT 3.4 vulnerability gate: fails when a NuGet or npm dependency has a known High/Critical vulnerability.
  Docker images are scanned by Trivy in GitHub Actions (job "images" in .github/workflows/ci-cd.yml).
.EXAMPLE
  powershell -File scripts/check-vulnerabilities.ps1
  powershell -File scripts/check-vulnerabilities.ps1 -SkipNpm
#>
param(
    [switch]$SkipDotnet,
    [switch]$SkipNpm,
    [string[]]$Severities = @('High', 'Critical')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failed = $false

if (-not $SkipDotnet) {
    Write-Host '==> NuGet: dotnet list package --vulnerable --include-transitive' -ForegroundColor Cyan
    $json = dotnet list (Join-Path $root 'BlueDrive.slnx') package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { Write-Host 'FAILED: dotnet list package' -ForegroundColor Red; exit $LASTEXITCODE }
    $report = ($json -join "`n") | ConvertFrom-Json
    $found = @()
    foreach ($project in $report.projects) {
        foreach ($fw in @($project.frameworks)) {
            if (-not $fw) { continue }
            foreach ($pkg in @($fw.topLevelPackages) + @($fw.transitivePackages)) {
                if (-not $pkg) { continue }
                foreach ($v in @($pkg.vulnerabilities)) {
                    if ($v -and $Severities -contains $v.severity) {
                        $found += '{0} {1} ({2}) {3} — {4}' -f $pkg.id, $pkg.resolvedVersion, $v.severity, $v.advisoryurl, (Split-Path -Leaf $project.path)
                    }
                }
            }
        }
    }
    if ($found.Count -gt 0) {
        $found | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        $failed = $true
    } else {
        Write-Host '  Không có gói NuGet nào có lỗ hổng mức High/Critical.' -ForegroundColor Green
    }
}

if (-not $SkipNpm) {
    foreach ($dir in @('web', 'mobile')) {
        Write-Host "==> npm audit ($dir, mức high trở lên)" -ForegroundColor Cyan
        Push-Location (Join-Path $root $dir)
        try {
            npm audit --audit-level=high
            if ($LASTEXITCODE -ne 0) { $failed = $true }
        } finally {
            Pop-Location
        }
    }
}

if ($failed) {
    Write-Host 'FAILED: có phụ thuộc chứa lỗ hổng mức High/Critical — nâng cấp gói (xem docs/quy-trinh-ung-pho-su-co.md, mục quản lý lỗ hổng).' -ForegroundColor Red
    exit 1
}
Write-Host 'Vulnerability scan OK' -ForegroundColor Green
