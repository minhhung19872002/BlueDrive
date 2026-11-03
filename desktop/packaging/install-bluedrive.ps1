<#
.SYNOPSIS
  Mass deployment of BlueDrive Desktop (GPO logon script, Intune, SCCM, PDQ…): silent install for the signed-in user with the
  BlueDrive server and the ONLYOFFICE option preset.
.DESCRIPTION
  Writes the deployment policy (HKLM\SOFTWARE\Policies\BlueDrive when run as administrator, else HKCU):
    Server (REG_SZ), InstallOnlyOffice (DWORD 0/1), UpdateChannel (stable|beta), DisableUpdates (DWORD, optional), LanSync (DWORD 0/1, optional)
  then runs BlueDriveSetup.exe --silent (per-user install in %LOCALAPPDATA%\BlueDrive.Desktop, no administrator rights needed).
  BlueDrive starts at the user's next sign-in, opens the sign-in wizard on that server, and installs ONLYOFFICE Desktop Editors
  when InstallOnlyOffice=1 (one elevation prompt, or none when the user is an administrator with UAC prompts off).
  Equivalent of "BlueDriveSetup.exe /silent /server=… /with-onlyoffice=1" in the specification: Velopack 1.2.161 Setup cannot
  forward extra arguments (it stops with a clap error), so the server travels through the policy instead.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install-bluedrive.ps1 -Server https://bluedrive.bluestar.com.vn -WithOnlyOffice 1
  powershell -ExecutionPolicy Bypass -File install-bluedrive.ps1 -Server https://drive.example.gov.vn -Setup \\fileserver\soft\BlueDriveSetup.exe -WithOnlyOffice 0
  powershell -ExecutionPolicy Bypass -File install-bluedrive.ps1 -Server https://bluedrive.bluestar.com.vn -LanSync 1   # as administrator: also the firewall rule
.NOTES
  -LanSync 1 turns on "Đồng bộ trong mạng LAN" (files taken from BlueDrive computers nearby). Run as administrator, the script also allows
  BlueDrive.exe through Windows Defender Firewall on domain / private networks (TCP for transfers, UDP 47391 for discovery), so users are
  never asked; otherwise create the same rule by Group Policy.
#>
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [ValidateSet(0, 1)][int]$WithOnlyOffice = 1,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    # Local / UNC path or URL of BlueDriveSetup.exe; default: downloaded from the server.
    [string]$Setup,
    [switch]$DisableUpdates,
    # 1: "Đồng bộ trong mạng LAN" on (and the firewall rule when run as administrator), 0: off, -1: left to the user.
    [ValidateSet(-1, 0, 1)][int]$LanSync = -1,
    [switch]$PolicyOnly
)
$ErrorActionPreference = 'Stop'
$server = $Server.TrimEnd('/')
if ($server -notmatch '^https?://') { throw "Địa chỉ máy chủ phải bắt đầu bằng https://: $Server" }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$key = if ($admin) { 'HKLM:\SOFTWARE\Policies\BlueDrive' } else { 'HKCU:\SOFTWARE\Policies\BlueDrive' }
New-Item -Path $key -Force | Out-Null
Set-ItemProperty $key -Name Server -Value $server
Set-ItemProperty $key -Name InstallOnlyOffice -Value $WithOnlyOffice -Type DWord
Set-ItemProperty $key -Name UpdateChannel -Value $Channel
if ($DisableUpdates) { Set-ItemProperty $key -Name DisableUpdates -Value 1 -Type DWord }
if ($LanSync -ge 0) { Set-ItemProperty $key -Name LanSync -Value $LanSync -Type DWord }
if ($LanSync -eq 1) {
    $rule = 'BlueDrive - đồng bộ trong mạng LAN'
    if ($admin) {
        $program = Join-Path $env:LOCALAPPDATA 'BlueDrive.Desktop\current\BlueDrive.exe'
        Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $rule -Direction Inbound -Action Allow -Program $program -Profile Domain, Private | Out-Null
        Write-Host "Đã cho phép $program qua tường lửa (mạng miền / mạng riêng)."
    }
    else { Write-Warning 'Chưa tạo quy tắc tường lửa cho đồng bộ LAN (cần quyền quản trị hoặc Group Policy): Windows sẽ hỏi người dùng.' }
}
Write-Host "Đã ghi chính sách triển khai: $key (máy chủ $server, ONLYOFFICE=$WithOnlyOffice, kênh $Channel)"
if ($PolicyOnly) { return }

if (-not $Setup) { $Setup = "$server/downloads/desktop/" + $(if ($Channel -eq 'stable') { 'BlueDriveSetup.exe' } else { "BlueDriveSetup-$Channel.exe" }) }
$exe = $Setup
if ($Setup -match '^https?://') {
    $exe = Join-Path ([IO.Path]::GetTempPath()) 'BlueDriveSetup.exe'
    Write-Host "Đang tải $Setup"
    Invoke-WebRequest $Setup -OutFile $exe -UseBasicParsing
}
$sig = Get-AuthenticodeSignature $exe
if ($sig.Status -ne 'Valid') { Write-Warning "Chữ ký số của bộ cài: $($sig.Status) ($($sig.SignerCertificate.Subject))" }
$p = Start-Process -FilePath $exe -ArgumentList '--silent' -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "Cài đặt không thành công (mã $($p.ExitCode)). Xem %LOCALAPPDATA%\velopack\velopack.log" }
Write-Host 'Đã cài BlueDrive Desktop. Ứng dụng tự chạy ở lần đăng nhập tiếp theo (hoặc mở từ menu Start).'
