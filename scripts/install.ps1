<#
.SYNOPSIS
  Cài đặt trọn gói BlueDrive trên một máy chủ sạch (Windows/Linux có PowerShell 7 + Docker).
.DESCRIPTION
  1. Kiểm tra Docker / Docker Compose.
  2. Tạo deploy/.env với các khóa bí mật ngẫu nhiên (nếu chưa có).
  3. Build và khởi động toàn bộ dịch vụ, chờ hệ thống sẵn sàng.
.EXAMPLE
  powershell -File scripts/install.ps1
  powershell -File scripts/install.ps1 -Domain drive.example.gov.vn -AcmeEmail admin@example.gov.vn
  powershell -File scripts/install.ps1 -HttpPort 8088 -Demo
#>
param(
    [string]$Domain = '',
    [string]$AcmeEmail = '',
    [int]$HttpPort = 80,
    [int]$HttpsPort = 443,
    [string]$AdminPassword = '',
    [switch]$Demo,
    [switch]$EnableClamAv
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deploy = Join-Path $root 'deploy'
$envFile = Join-Path $deploy '.env'

function Random-Secret([int]$length) {
    $chars = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $bytes = [byte[]]::new($length)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

Write-Host '==> Kiểm tra Docker' -ForegroundColor Cyan
docker version --format '{{.Server.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker chưa chạy. Vui lòng cài đặt/khởi động Docker trước.' }
docker compose version | Out-Null

if (-not (Test-Path $envFile)) {
    Write-Host '==> Tạo deploy/.env với khóa bí mật ngẫu nhiên' -ForegroundColor Cyan
    $masterKey = [byte[]]::new(32); [System.Security.Cryptography.RandomNumberGenerator]::Fill($masterKey)
    if (-not $AdminPassword) { $AdminPassword = 'Bd@' + (Random-Secret 12) }
    $gitSha = git -C $root rev-parse --short HEAD 2>$null
    if (-not $gitSha) { $gitSha = 'dev' }
    $baseUrl = if ($Domain) { "https://$Domain" } elseif ($HttpPort -ne 80) { "http://localhost:$HttpPort" } else { 'http://localhost' }
    $content = (Get-Content (Join-Path $deploy '.env.example') -Raw)
    $replacements = [ordered]@{
        '(?m)^SITE_ADDRESS=.*$'          = "SITE_ADDRESS=$(if ($Domain) { $Domain } else { ':80' })"
        '(?m)^PUBLIC_BASE_URL=.*$'       = "PUBLIC_BASE_URL=$baseUrl"
        '(?m)^ACME_EMAIL=.*$'            = "ACME_EMAIL=$AcmeEmail"
        '(?m)^HTTP_PORT=.*$'             = "HTTP_PORT=$HttpPort"
        '(?m)^HTTPS_PORT=.*$'            = "HTTPS_PORT=$HttpsPort"
        '(?m)^POSTGRES_PASSWORD=.*$'     = "POSTGRES_PASSWORD=$(Random-Secret 32)"
        '(?m)^JWT_SECRET=.*$'            = "JWT_SECRET=$(Random-Secret 48)"
        '(?m)^ONLYOFFICE_JWT_SECRET=.*$' = "ONLYOFFICE_JWT_SECRET=$(Random-Secret 40)"
        '(?m)^STORAGE_MASTER_KEY=.*$'    = "STORAGE_MASTER_KEY=$([Convert]::ToBase64String($masterKey))"
        '(?m)^ADMIN_PASSWORD=.*$'        = "ADMIN_PASSWORD=$AdminPassword"
        '(?m)^SEED_DEMO=.*$'             = "SEED_DEMO=$(if ($Demo) { 'true' } else { 'false' })"
        '(?m)^CLAMAV_ENABLED=.*$'        = "CLAMAV_ENABLED=$(if ($EnableClamAv) { 'true' } else { 'false' })"
        '(?m)^GIT_SHA=.*$'               = "GIT_SHA=$gitSha"
    }
    foreach ($k in $replacements.Keys) { $content = [regex]::Replace($content, $k, $replacements[$k]) }
    Set-Content -Path $envFile -Value $content -Encoding utf8
    Write-Host "    Mật khẩu quản trị ban đầu (đổi ngay khi đăng nhập lần đầu): $AdminPassword" -ForegroundColor Yellow
    Write-Host '    QUAN TRỌNG: sao lưu deploy/.env ở nơi an toàn — mất STORAGE_MASTER_KEY sẽ không giải mã được dữ liệu.' -ForegroundColor Yellow
} else {
    Write-Host '==> Dùng deploy/.env hiện có' -ForegroundColor Cyan
}

Push-Location $deploy
try {
    $profiles = @(); if ($EnableClamAv -or (Select-String -Path $envFile -Pattern '^CLAMAV_ENABLED=true' -Quiet)) { $profiles = @('--profile', 'clamav') }
    Write-Host '==> Build và khởi động dịch vụ (lần đầu có thể mất 10-20 phút để tải image)' -ForegroundColor Cyan
    docker compose @profiles up -d --build
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up thất bại.' }

    $port = (Select-String -Path $envFile -Pattern '^HTTP_PORT=(\d+)').Matches[0].Groups[1].Value
    $url = "http://localhost:$port/health"
    Write-Host "==> Chờ hệ thống sẵn sàng ($url)" -ForegroundColor Cyan
    $ok = $false
    for ($i = 0; $i -lt 90 -and -not $ok; $i++) {
        try { $ok = (Invoke-RestMethod $url -TimeoutSec 5).status -eq 'Healthy' } catch { Start-Sleep 5 }
    }
    if (-not $ok) { throw 'Hệ thống chưa sẵn sàng sau 7 phút. Xem log: docker compose logs api' }
    $publicUrl = (Select-String -Path $envFile -Pattern '^PUBLIC_BASE_URL=(.+)$').Matches[0].Groups[1].Value
    Write-Host ''
    Write-Host "BlueDrive đã sẵn sàng tại $publicUrl" -ForegroundColor Green
    Write-Host 'Đăng nhập: admin / ADMIN_PASSWORD trong deploy/.env (bắt buộc đổi mật khẩu lần đầu).' -ForegroundColor Green
}
finally { Pop-Location }
