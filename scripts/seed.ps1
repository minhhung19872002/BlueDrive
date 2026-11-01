<#
.SYNOPSIS
  Nạp dữ liệu mẫu (đơn vị, 5 người dùng, 2 nhóm tài nguyên, tệp mẫu các loại) để trình diễn / vận hành thử.
.DESCRIPTION
  Bật SEED_DEMO=true trong deploy/.env và khởi động lại API (bộ nạp chỉ chạy khi chưa có dữ liệu mẫu).
  Mật khẩu người dùng mẫu: Demo@12345 (bắt buộc đổi khi đăng nhập lần đầu).
#>
$ErrorActionPreference = 'Stop'
$deploy = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy'
$envFile = Join-Path $deploy '.env'
if (-not (Test-Path $envFile)) { throw 'Chưa có deploy/.env. Chạy scripts/install.ps1 trước.' }
$content = Get-Content $envFile -Raw
$content = [regex]::Replace($content, '(?m)^SEED_DEMO=.*$', 'SEED_DEMO=true')
Set-Content $envFile $content -Encoding utf8
Push-Location $deploy
try {
    docker compose up -d api
    Write-Host 'Đã bật dữ liệu mẫu. Người dùng mẫu: canbo1..canbo5 / Demo@12345' -ForegroundColor Green
}
finally { Pop-Location }
