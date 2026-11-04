# BlueDrive - local load-test stack (Đợt 5). Never point this at a shared or production server.
#   powershell -File scripts/perf-up.ps1              # create deploy/.env.perf (random secrets), build, start, seed
#   powershell -File scripts/perf-up.ps1 -SkipSeed    # start only
#   powershell -File scripts/perf-up.ps1 -Down        # stop (keeps data)
#   powershell -File scripts/perf-up.ps1 -Down -Purge # stop and delete the perf volumes
# Seed size: -Users 100 -Files 200000 -AuditRows 2000000 (defaults). See docs/bao-cao-kiem-thu-tai.md.
param(
    [switch]$Down,
    [switch]$Purge,
    [switch]$SkipSeed,
    [switch]$NoBuild,
    [int]$Users = 100,
    [int]$Files = 200000,
    [int]$AuditRows = 2000000
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deploy = Join-Path $root 'deploy'
$envFile = Join-Path $deploy '.env.perf'
$compose = @('compose', '-p', 'bluedrive-perf', '--env-file', $envFile, '-f', (Join-Path $deploy 'docker-compose.yml'), '-f', (Join-Path $deploy 'docker-compose.perf.yml'))

function Random-Secret([int]$length) {
    $chars = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $bytes = [byte[]]::new($length)
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

function Read-EnvFile([string]$path) {
    $map = @{}
    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $map[$Matches[1]] = $Matches[2] }
    }
    $map
}

if ($Down) {
    if (-not (Test-Path $envFile)) { throw 'Chưa có deploy/.env.perf: môi trường kiểm thử tải chưa từng được tạo.' }
    $args2 = $compose + @('--profile', 'clamav', 'down')
    if ($Purge) { $args2 += '-v' }
    & docker @args2
    exit $LASTEXITCODE
}

if (-not (Test-Path $envFile)) {
    Write-Host '==> Tạo deploy/.env.perf với khóa bí mật ngẫu nhiên (không commit)' -ForegroundColor Cyan
    $masterKey = [byte[]]::new(32); [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($masterKey)
    @(
        '# Môi trường kiểm thử tải local (scripts/perf-up.ps1). Sinh tự động, KHÔNG commit.'
        'SITE_ADDRESS=:80'
        'HTTP_PORT=8090'
        'HTTPS_PORT=8453'
        'PERF_EDGE_PORT=8091'
        'PERF_PG_PORT=55432'
        'PERF_DELAY_MS=12'
        'PERF_RATE_MBIT=300'
        'PUBLIC_BASE_URL=http://localhost:8091'
        'TRUSTED_PROXIES=172.16.0.0/12'
        'POSTGRES_DB=bluedrive'
        'POSTGRES_USER=bluedrive'
        "POSTGRES_PASSWORD=$(Random-Secret 32)"
        "JWT_SECRET=$(Random-Secret 48)"
        "ONLYOFFICE_JWT_SECRET=$(Random-Secret 40)"
        "STORAGE_MASTER_KEY=$([Convert]::ToBase64String($masterKey))"
        'STORAGE_ENCRYPT_AT_REST=true'
        "ADMIN_PASSWORD=Bd@$(Random-Secret 12)7"
        "PERF_USER_PASSWORD=Pf@$(Random-Secret 12)7"
        'SEED_DEMO=false'
        'CLAMAV_ENABLED=true'
        'CLAMAV_MAX_SCAN_MB=200'
        'APP_VERSION=perf'
        'GIT_SHA=perf'
    ) | Set-Content -Path $envFile -Encoding utf8
}
$cfg = Read-EnvFile $envFile

New-Item -ItemType Directory -Force (Join-Path $deploy 'downloads') | Out-Null

Write-Host '==> Khởi động stack kiểm thử tải (project bluedrive-perf, CPU 0-7)' -ForegroundColor Cyan
$up = $compose + @('--profile', 'clamav', 'up', '-d')
if (-not $NoBuild) { $up += '--build' }
& docker @up
if ($LASTEXITCODE -ne 0) { throw 'docker compose up lỗi.' }

$base = "http://localhost:$($cfg.HTTP_PORT)"
Write-Host "==> Chờ API sẵn sàng tại $base/health" -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(5)
while ($true) {
    try { if ((Invoke-WebRequest "$base/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { break } } catch { }
    if ((Get-Date) -gt $deadline) { throw 'API không sẵn sàng sau 5 phút (xem: docker compose -p bluedrive-perf logs api).' }
    Start-Sleep -Seconds 3
}

# edge-shaper installs tc (apk) before applying netem: wait for it to print the qdisc or to exit.
Write-Host '==> Chờ edge-shaper áp độ trễ / băng thông (tc netem)' -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 3
    $shaper = (& docker @($compose + @('logs', 'edge-shaper', '--tail', '5')) 2>&1) -join "`n"
    $running = (& docker @($compose + @('ps', '-q', '--status', 'running', 'edge-shaper'))) -ne $null
} while ($shaper -notmatch 'netem' -and $running -and (Get-Date) -lt $deadline)
if ($shaper -notmatch 'netem') {
    Write-Warning 'Không áp được tc netem (nhân WSL thiếu sch_netem?): dùng độ trễ của Toxiproxy thay thế (không giới hạn băng thông chung).'
    $toxic = @{ name = 'latency'; type = 'latency'; stream = 'downstream'; attributes = @{ latency = 25; jitter = 3 } } | ConvertTo-Json
    # Toxiproxy refuses browser-like user agents (CSRF guard), PowerShell's default one included.
    try { Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8474/proxies/bluedrive/toxics' -Body $toxic -ContentType 'application/json' -UserAgent 'bluedrive-perf-up' | Out-Null }
    catch { if ($_.Exception.Message -notmatch '409') { throw } }
}
else { Write-Host ($shaper -split "`n" | Select-String 'netem' | Select-Object -First 1) }

if (-not $SkipSeed) {
    Write-Host "==> Nạp dữ liệu kiểm thử: $Users người dùng, $Files tệp, $AuditRows dòng nhật ký" -ForegroundColor Cyan
    $env:PERF_CONNECTION = "Host=127.0.0.1;Port=$($cfg.PERF_PG_PORT);Database=$($cfg.POSTGRES_DB);Username=$($cfg.POSTGRES_USER);Password=$($cfg.POSTGRES_PASSWORD);Command Timeout=0"
    $env:PERF_BASE_URL = $base
    $env:PERF_ADMIN_PASSWORD = $cfg.ADMIN_PASSWORD
    $env:PERF_USER_PASSWORD = $cfg.PERF_USER_PASSWORD
    try {
        dotnet run --file (Join-Path $root 'tests/load/seed/PerfSeed.cs') -c Release -- --users $Users --files $Files --audit $AuditRows
        if ($LASTEXITCODE -ne 0) { throw 'Nạp dữ liệu kiểm thử lỗi.' }
    }
    finally { Remove-Item Env:PERF_CONNECTION, Env:PERF_ADMIN_PASSWORD, Env:PERF_USER_PASSWORD -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "Sẵn sàng. Web: $base  |  Qua edge (độ trễ + 300 Mbit/s): http://localhost:$($cfg.PERF_EDGE_PORT)" -ForegroundColor Green
Write-Host 'Chạy kịch bản: powershell -File tests/load/run.ps1 -Test t1' -ForegroundColor Green
