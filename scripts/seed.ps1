<#
.SYNOPSIS
  Nạp dữ liệu mẫu (đơn vị, 5 người dùng, 2 nhóm tài nguyên, tệp mẫu các loại) để trình diễn / vận hành thử.
.DESCRIPTION
  Bật SEED_DEMO=true trong deploy/.env và khởi động lại API (bộ nạp chỉ chạy khi chưa có dữ liệu mẫu).
  Mật khẩu người dùng mẫu: DEMO_PASSWORD trong deploy/.env (để trống thì tự sinh ngẫu nhiên và ghi vào .env; bắt buộc đổi khi đăng nhập lần đầu).
#>
$ErrorActionPreference = 'Stop'
$deploy = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy'
$envFile = Join-Path $deploy '.env'
if (-not (Test-Path $envFile)) { throw 'Chưa có deploy/.env. Chạy scripts/install.ps1 trước.' }
$content = Get-Content $envFile -Raw
$content = [regex]::Replace($content, '(?m)^SEED_DEMO=.*$', 'SEED_DEMO=true')
# No shared default: a missing or empty DEMO_PASSWORD gets a random one (upper, lower, digit and symbol for the password policy).
if ($content -notmatch '(?m)^DEMO_PASSWORD=\S+') {
    $chars = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $bytes = [byte[]]::new(10); [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $generated = 'Dm@' + (-join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })) + '7'
    $content = [regex]::Replace($content, '(?m)^DEMO_PASSWORD=[ \t]*\r?\n?', '')
    $content = $content.TrimEnd() + "`nDEMO_PASSWORD=$generated`n"
}
Set-Content $envFile $content -Encoding utf8
$demoPassword = [regex]::Match($content, '(?m)^DEMO_PASSWORD=(.+?)\r?$').Groups[1].Value
Push-Location $deploy
try {
    docker compose up -d api
    Write-Host "Đã bật dữ liệu mẫu. Người dùng mẫu: canbo1..canbo5 / $demoPassword" -ForegroundColor Green
}
finally { Pop-Location }
