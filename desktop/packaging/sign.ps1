<#
.SYNOPSIS
  Signs files (EXE, DLL, MSIX) with signtool, for desktop/packaging/build.ps1 and Velopack (--signTemplate).
.DESCRIPTION
  Certificate (never stored in the repository):
    BLUEDRIVE_SIGN_PFX           path to the code signing certificate (.pfx), or
    BLUEDRIVE_SIGN_PFX_BASE64    the .pfx itself, base64 (CI secret)
    BLUEDRIVE_SIGN_PASSWORD      its password
    BLUEDRIVE_SIGN_TIMESTAMP     RFC 3161 timestamp server (default http://timestamp.digicert.com)
  Without a certificate a self-signed development certificate "CN=BLUESTAR Technology (dev)" is created once in
  desktop/packaging/.dev-cert/ (git-ignored): good for testing only, Windows SmartScreen warns about it.
.EXAMPLE
  pwsh -File desktop/packaging/sign.ps1 artifacts/desktop/BlueDriveSetup.exe
#>
param([Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Files)
$ErrorActionPreference = 'Stop'
# Cert: drive (New-SelfSignedCertificate); not auto-loaded when Windows PowerShell is started from PowerShell 7.
Import-Module Microsoft.PowerShell.Security -ErrorAction SilentlyContinue
Import-Module PKI -ErrorAction SilentlyContinue

function Find-SignTool {
    if ($env:BLUEDRIVE_SIGNTOOL -and (Test-Path $env:BLUEDRIVE_SIGNTOOL)) { return $env:BLUEDRIVE_SIGNTOOL } # set once by build.ps1
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw 'Không tìm thấy signtool.exe (cài Windows SDK).' }
    return $found.FullName
}

# Certificate: environment, else the development certificate.
$pfx = $env:BLUEDRIVE_SIGN_PFX
$password = $env:BLUEDRIVE_SIGN_PASSWORD
if (-not $pfx -and $env:BLUEDRIVE_SIGN_PFX_BASE64) {
    $pfx = Join-Path ([IO.Path]::GetTempPath()) "bluedrive-sign-$PID.pfx"
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:BLUEDRIVE_SIGN_PFX_BASE64))
    $tempPfx = $pfx
}
if (-not $pfx) {
    $devDir = Join-Path $PSScriptRoot '.dev-cert'
    $pfx = Join-Path $devDir 'bluestar-dev.pfx'
    $pwFile = Join-Path $devDir 'password.txt'
    if (-not (Test-Path $pfx)) {
        New-Item -ItemType Directory -Force $devDir | Out-Null
        $password = [Convert]::ToBase64String((1..24 | ForEach-Object { [byte](Get-Random -Maximum 256) }))
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=BLUESTAR Technology (dev)' -CertStoreLocation Cert:\CurrentUser\My `
            -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(3) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
        Export-Certificate -Cert $cert -FilePath (Join-Path $devDir 'bluestar-dev.cer') | Out-Null
        Set-Content $pwFile $password -NoNewline
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
        Write-Warning "Đã tạo chứng thư tự ký cho môi trường phát triển: $pfx (chỉ để thử nghiệm)."
    }
    $password = Get-Content $pwFile -Raw
}
$timestamp = if ($env:BLUEDRIVE_SIGN_TIMESTAMP) { $env:BLUEDRIVE_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }

$signtool = Find-SignTool
try {
    foreach ($f in $Files) {
        $args = @('sign', '/fd', 'SHA256', '/f', $pfx, '/p', $password)
        # Development certificates are not timestamped (works offline); release certificates are.
        if ($env:BLUEDRIVE_SIGN_PFX -or $env:BLUEDRIVE_SIGN_PFX_BASE64) { $args += @('/tr', $timestamp, '/td', 'SHA256') }
        & $signtool @args $f | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Ký số không thành công: $f" }
    }
}
finally { if ($tempPfx) { Remove-Item $tempPfx -Force -ErrorAction SilentlyContinue } }
