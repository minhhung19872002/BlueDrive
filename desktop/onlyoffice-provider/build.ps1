<#
.SYNOPSIS
  Builds the ONLYOFFICE Desktop Editors provider "bluedrive" for one BlueDrive server.
.EXAMPLE
  powershell -File desktop/onlyoffice-provider/build.ps1 -Server https://bluedrive.bluestar.com.vn -Out build/providers
  # then copy build/providers/bluedrive to "%ProgramFiles%\ONLYOFFICE\DesktopEditors\providers\" (administrator)
#>
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$Out = "build/onlyoffice-provider"
)
$ErrorActionPreference = 'Stop'
$server = $Server.TrimEnd('/')
if ($server -notmatch '^https?://') { throw "Địa chỉ máy chủ phải bắt đầu bằng https://" }
$src = Join-Path $PSScriptRoot 'bluedrive'
$dest = Join-Path $Out 'bluedrive'
New-Item -ItemType Directory -Force (Join-Path $dest 'assets') | Out-Null
Copy-Item (Join-Path $src 'assets/*') (Join-Path $dest 'assets') -Force
$config = (Get-Content (Join-Path $src 'config.template.json') -Raw -Encoding UTF8).Replace('{{SERVER}}', $server)
[System.IO.File]::WriteAllText((Join-Path $dest 'config.json'), $config, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Provider BlueDrive cho ONLYOFFICE: $dest"
