<#
.SYNOPSIS
  Builds a BlueDrive Desktop release: self-contained publish, code signing, Velopack installer + update packages,
  optional MSIX package, and the summary releases.json read by the web page "Tải ứng dụng máy tính".
.DESCRIPTION
  Output (default artifacts/desktop/), served by the BlueDrive server under /downloads/desktop/:
    BlueDriveSetup.exe (stable) / BlueDriveSetup-beta.exe   installer for users (per user, no administrator rights)
    BlueDrive.Desktop-<channel>-Setup.exe, *-full.nupkg, releases.<channel>.json, assets.<channel>.json   Velopack update feed
    BlueDrive.msix (with -Msix)                                package for MSIX deployment (Intune / App Installer)
    releases.json                                              versions per channel, sizes, SHA-256
  Requires: PowerShell 7 (pwsh), .NET 10 SDK, vpk (dotnet tool install -g vpk --version 1.2.161), Windows SDK (signtool, makeappx).
  Signing: see sign.ps1 (certificate from environment variables, else a self-signed development certificate).
.EXAMPLE
  pwsh -File desktop/packaging/build.ps1 -Version 1.0.0 -Channel stable
  pwsh -File desktop/packaging/build.ps1 -Version 1.1.0-beta.1 -Channel beta -Msix
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Out = 'artifacts/desktop',
    [string]$ReleaseNotes,
    # BlueDrive server preset in the package (deploy.json): the wizard opens on it and updates come from it.
    [string]$Server,
    [switch]$Msix,
    [switch]$NoSign
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$project = Join-Path $root 'desktop/src/BlueDrive.Desktop/BlueDrive.Desktop.csproj'
$work = Join-Path $root "artifacts/desktop-build/$Runtime"
$publish = Join-Path $work 'publish'
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$sign = Join-Path $PSScriptRoot 'sign.ps1'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Phiên bản phải dạng 1.2.3 hoặc 1.2.3-beta.1: $Version" }
$numeric = ($Version -split '-')[0]

Write-Host "==> Biên dịch BlueDrive Desktop $Version ($Runtime)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r $Runtime --self-contained true -p:Version=$Version -p:FileVersion=$numeric.0 `
    -p:InformationalVersion=$Version -p:PublishReadyToRun=true -p:DebugType=none -o $publish -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish không thành công' }
if ($Server) {
    if ($Server -notmatch '^https?://') { throw "Địa chỉ máy chủ phải bắt đầu bằng https://: $Server" }
    $deploy = [ordered]@{ server = $Server.TrimEnd('/'); withOnlyOffice = $true; channel = $Channel }
    [IO.File]::WriteAllText((Join-Path $publish 'deploy.json'), ($deploy | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
}

Write-Host '==> Đóng gói Velopack'
$vpkArgs = @('pack', '--packId', 'BlueDrive.Desktop', '--packVersion', $Version, '--packDir', $publish, '--mainExe', 'BlueDrive.exe',
    '--packTitle', 'BlueDrive', '--packAuthors', 'BLUESTAR', '--icon', (Join-Path $root 'desktop/src/BlueDrive.Desktop/Assets/bluedrive.ico'),
    '--channel', $Channel, '--runtime', $Runtime, '--outputDir', $Out, '--delta', 'None', '--noPortable', '--shortcuts', 'Desktop,StartMenuRoot', '--yes')
if ($ReleaseNotes) { $vpkArgs += @('--releaseNotes', $ReleaseNotes) }
if (-not $NoSign) {
    # One lookup of signtool for the ~60 files Velopack signs one by one.
    $env:BLUEDRIVE_SIGNTOOL = (Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1).FullName
    # Velopack signs BlueDrive.exe, the DLLs, Update.exe and Setup.exe with our script.
    $vpkArgs += @('--signTemplate', "pwsh -NoProfile -ExecutionPolicy Bypass -File `"$sign`" {{file}}", '--signParallel', '1')
}
vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack không thành công' }

$setupName = if ($Channel -eq 'stable') { 'BlueDriveSetup.exe' } else { "BlueDriveSetup-$Channel.exe" }
Copy-Item (Join-Path $Out "BlueDrive.Desktop-$Channel-Setup.exe") (Join-Path $Out $setupName) -Force

$msixName = $null
if ($Msix) {
    Write-Host '==> Đóng gói MSIX'
    $msixName = if ($Channel -eq 'stable') { 'BlueDrive.msix' } else { "BlueDrive-$Channel.msix" }
    & (Join-Path $PSScriptRoot 'msix/build-msix.ps1') -PublishDir $publish -Version "$numeric.0" -Out (Join-Path $Out $msixName) -NoSign:$NoSign
}

Write-Host '==> releases.json'
$summaryPath = Join-Path $Out 'releases.json'
$summary = if (Test-Path $summaryPath) { Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable } else { @{} }
if (-not $summary.channels) { $summary.channels = @{} }
function Describe($name) {
    if (-not $name) { return $null }
    $f = Get-Item (Join-Path $Out $name)
    return [ordered]@{ file = $name; size = $f.Length; sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$summary.product = 'BlueDrive Desktop'
$summary.channels[$Channel] = [ordered]@{
    version     = $Version
    publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    runtime     = $Runtime
    minimumOs   = 'Windows 10 1809 (17763)'
    setup       = Describe $setupName
    msix        = Describe $msixName
    notes       = if ($ReleaseNotes) { Get-Content $ReleaseNotes -Raw -Encoding UTF8 } else { $null }
}
[IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
Write-Host "Xong: $Out"
Get-ChildItem $Out | Format-Table Name, Length -AutoSize
