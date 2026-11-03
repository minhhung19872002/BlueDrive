<#
.SYNOPSIS
  Packs a published BlueDrive Desktop folder into a signed MSIX (called by ../build.ps1 -Msix).
.EXAMPLE
  pwsh -File desktop/packaging/msix/build-msix.ps1 -PublishDir artifacts/desktop-build/win-x64/publish -Version 1.0.0.0 -Out artifacts/desktop/BlueDrive.msix
  # Developer Mode, unsigned layout: Add-AppxPackage -Register artifacts/desktop-build/msix-layout/AppxManifest.xml
#>
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Out,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$Publisher,
    # BlueDrive.Shell.dll (Windows 11 context menu, NativeAOT): published by ../build.ps1.
    [string]$ShellDll,
    [switch]$NoSign
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../../..')

function Find-SdkTool($name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $found = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw "Không tìm thấy $name (cài Windows SDK)." }
    return $found.FullName
}

# Publisher = subject of the signing certificate (same rules as sign.ps1).
if (-not $Publisher) {
    $Publisher = 'CN=BLUESTAR Technology (dev)'
    $pfx = $env:BLUEDRIVE_SIGN_PFX
    if (-not $pfx -and $env:BLUEDRIVE_SIGN_PFX_BASE64) {
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($env:BLUEDRIVE_SIGN_PFX_BASE64), $env:BLUEDRIVE_SIGN_PASSWORD)
        $Publisher = $cert.Subject
    }
    elseif ($pfx) { $Publisher = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $env:BLUEDRIVE_SIGN_PASSWORD).Subject }
}

$layout = Join-Path $root 'artifacts/desktop-build/msix-layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item (Join-Path $PublishDir '*') $layout -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $layout 'Assets') -Recurse -Force
if ($ShellDll) { Copy-Item $ShellDll (Join-Path $layout 'BlueDrive.Shell.dll') -Force }
elseif (-not (Test-Path (Join-Path $layout 'BlueDrive.Shell.dll'))) { throw 'Thiếu BlueDrive.Shell.dll (menu chuột phải Windows 11): truyền -ShellDll.' }
$manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw -Encoding UTF8).
    Replace('{{PUBLISHER}}', [Security.SecurityElement]::Escape($Publisher)).Replace('{{VERSION}}', $Version).Replace('{{ARCH}}', $Arch)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

$makeappx = Find-SdkTool 'makeappx.exe'
if (Test-Path $Out) { Remove-Item $Out -Force }
& $makeappx pack /o /h SHA256 /d $layout /p $Out | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx không thành công' }
if (-not $NoSign) { & (Join-Path $PSScriptRoot '../sign.ps1') $Out }
Write-Host "MSIX: $Out (Publisher: $Publisher)"
