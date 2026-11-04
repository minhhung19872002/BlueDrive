# BlueDrive - runs one load-test scenario against the local perf stack (scripts/perf-up.ps1) and collects, next to the k6 summary,
# container CPU/memory every 10 s and the heaviest PostgreSQL statements. Results: tests/load/results/<name>.*
#   powershell -File tests/load/run.ps1 -Test t1                  # 100 users, 15 minutes
#   powershell -File tests/load/run.ps1 -Test t2 -Peak 400        # ramp to 400
#   powershell -File tests/load/run.ps1 -Test t3                  # 200 logins in one minute, same IP
#   powershell -File tests/load/run.ps1 -Test t4 -Duration 4h     # soak (T1 for 4 hours)
#   powershell -File tests/load/run.ps1 -Test t5 | t6 | t7 | t8 | t9
# -Name suffixes the result files (e.g. -Name cpu6 for the 6-CPU round). -Env passes extra k6 variables: -Env @{ VUS = '50' }.
param(
    [Parameter(Mandatory = $true)][ValidateSet('t1', 't2', 't3', 't4', 't5', 't6', 't7', 't8', 't9')][string]$Test,
    [string]$Duration,
    [int]$Peak = 400,
    [string]$Name,
    [hashtable]$Env = @{},
    [switch]$WithT1,
    # Internal: a T1 started by t7/t8 next to another scenario (no reset of pg_stat_statements, no second sampler).
    [switch]$Background
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$load = $PSScriptRoot
$results = Join-Path $load 'results'
$envFile = Join-Path $root 'deploy/.env.perf'
if (-not (Test-Path $envFile)) { throw 'Chưa có deploy/.env.perf: chạy scripts/perf-up.ps1 trước.' }
New-Item -ItemType Directory -Force $results | Out-Null

$cfg = @{}
foreach ($line in Get-Content $envFile) { if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $cfg[$Matches[1]] = $Matches[2] } }
$compose = @('compose', '-p', 'bluedrive-perf', '--env-file', $envFile, '-f', (Join-Path $root 'deploy/docker-compose.yml'), '-f', (Join-Path $root 'deploy/docker-compose.perf.yml'))
$k6 = (Get-Command k6 -ErrorAction SilentlyContinue).Source
if (-not $k6) { $k6 = 'C:\Program Files\k6\k6.exe' }
$edge = "http://localhost:$($cfg.PERF_EDGE_PORT)"
$direct = "http://localhost:$($cfg.HTTP_PORT)"
$runName = if ($Name) { "$Test-$Name" } else { $Test }

function Psql([string]$sql) {
    & docker @($compose + @('exec', '-T', 'postgres', 'psql', '-U', $cfg.POSTGRES_USER, '-d', $cfg.POSTGRES_DB, '-v', 'ON_ERROR_STOP=1', '-c', $sql))
}

function Start-Sampler([string]$file) {
    Start-Job -ArgumentList $file -ScriptBlock {
        param($file)
        while ($true) {
            $at = (Get-Date).ToString('o')
            docker stats --no-stream --format '{{json .}}' | Where-Object { $_ -match 'bluedrive-perf' } | ForEach-Object { "{`"at`":`"$at`",`"s`":$_}" } | Add-Content -Path $file -Encoding utf8
            Start-Sleep -Seconds 10
        }
    }
}

function Write-StatsSummary([string]$file, [string]$out) {
    if (-not (Test-Path $file)) { return }
    $rows = Get-Content $file | ForEach-Object { ($_ | ConvertFrom-Json).s }
    $lines = $rows | Group-Object Name | Sort-Object Name | ForEach-Object {
        $cpu = $_.Group | ForEach-Object { [double]($_.CPUPerc -replace '%', '') }
        $mem = $_.Group | ForEach-Object { $_.MemUsage.Split('/')[0].Trim() }
        '{0,-36} CPU tb {1,6:N1}%  CPU max {2,6:N1}%  RAM cuối {3}' -f $_.Name, ($cpu | Measure-Object -Average).Average, ($cpu | Measure-Object -Maximum).Maximum, ($mem | Select-Object -Last 1)
    }
    $lines | Set-Content -Path $out -Encoding utf8
    $lines | ForEach-Object { Write-Host $_ }
}

function Invoke-K6([string]$script, [string]$result, [hashtable]$vars) {
    $args2 = @('run', '--quiet', '--out', "csv=$(Join-Path $results "$result.csv.gz")")
    $all = @{ BASE_URL = $edge; PERF_USER_PASSWORD = $cfg.PERF_USER_PASSWORD; RESULT_NAME = $result }
    foreach ($k in $vars.Keys) { $all[$k] = $vars[$k] }
    foreach ($k in $Env.Keys) { $all[$k] = $Env[$k] }
    foreach ($k in $all.Keys) { $args2 += @('-e', "$k=$($all[$k])") }
    $args2 += (Join-Path $load $script)
    Push-Location $root
    try { & $k6 @args2 | Out-Host; return $LASTEXITCODE } finally { Pop-Location }
}

function Get-AdminToken {
    $body = @{ username = 'perf001'; password = $cfg.PERF_USER_PASSWORD } | ConvertTo-Json
    (Invoke-RestMethod -Method Post -Uri "$direct/api/v1/auth/login" -Body $body -ContentType 'application/json').accessToken
}

if ($Background) {
    exit (Invoke-K6 't1-mixed.js' $runName @{ DURATION = $Duration })
}
Write-Host "==> $runName : đặt lại pg_stat_statements" -ForegroundColor Cyan
Psql 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements; SELECT pg_stat_statements_reset();' | Out-Null
$statsFile = Join-Path $results "$runName-docker-stats.jsonl"
Remove-Item $statsFile -ErrorAction SilentlyContinue
$sampler = Start-Sampler $statsFile
$started = Get-Date
$code = 0
try {
    switch ($Test) {
        't1' { $code = Invoke-K6 't1-mixed.js' $runName @{ DURATION = $(if ($Duration) { $Duration } else { '15m' }) } }
        't4' { $code = Invoke-K6 't1-mixed.js' $runName @{ DURATION = $(if ($Duration) { $Duration } else { '4h' }) } }
        't2' { $code = Invoke-K6 't2-ramp.js' $runName @{ PEAK = "$Peak" } }
        't3' { $code = Invoke-K6 't3-login-burst.js' $runName @{} }
        't5' { $code = Invoke-K6 't5-large-files.js' $runName @{ DURATION = $(if ($Duration) { $Duration } else { '20m' }) } }
        't7' {
            $bg = $null
            if ($WithT1) {
                $bg = Start-Job -ArgumentList $PSCommandPath, $Name -ScriptBlock { param($p, $n) & powershell -NoProfile -ExecutionPolicy Bypass -File $p -Test t1 -Background -Duration 12m -Name $(if ($n) { "$n-bg" } else { 't7-bg' }) }
                Start-Sleep -Seconds 150   # let T1 log in and settle
            }
            $code = Invoke-K6 't7-large-data.js' $runName @{ DURATION = $(if ($Duration) { $Duration } else { '10m' }) }
            if ($bg) { Receive-Job $bg -Wait -ErrorAction Continue 2>&1 | Out-Host }   # k6 reports crossed thresholds on stderr
        }
        't9' { $code = Invoke-K6 't9-sync.js' $runName @{} }
        't6' {
            Push-Location $root
            try {
                $env:BASE_URL = $edge; $env:PERF_USER_PASSWORD = $cfg.PERF_USER_PASSWORD
                foreach ($k in $Env.Keys) { Set-Item "Env:$k" $Env[$k] }
                node (Join-Path $load 't6-onlyoffice.mjs')
                $code = $LASTEXITCODE
            }
            finally { Remove-Item Env:PERF_USER_PASSWORD -ErrorAction SilentlyContinue; Pop-Location }
        }
        't8' {
            # Backup under load: T1 (100 users) runs while a full backup is taken; then the restore time (RTO) is measured.
            $bg = Start-Job -ArgumentList $PSCommandPath, $Name -ScriptBlock { param($p, $n) & powershell -NoProfile -ExecutionPolicy Bypass -File $p -Test t1 -Background -Duration 20m -Name $(if ($n) { "$n-t8" } else { 't8' }) }
            Start-Sleep -Seconds 240
            $token = Get-AdminToken
            $h = @{ Authorization = "Bearer $token" }
            $t0 = Get-Date
            $job = Invoke-RestMethod -Method Post -Uri "$direct/api/v1/backups?full=true" -Headers $h
            Write-Host "Sao lưu đầy đủ $($job.id) bắt đầu lúc $($t0.ToString('HH:mm:ss'))"
            do {
                Start-Sleep -Seconds 10
                $now = (Invoke-RestMethod -Uri "$direct/api/v1/backups?page=1&pageSize=5" -Headers $h).items | Where-Object { $_.id -eq $job.id }
            } while ($now.status -in @('queued', 'running'))
            $backupSeconds = [int]((Get-Date) - $t0).TotalSeconds
            Write-Host "Sao lưu: $($now.status) sau $backupSeconds giây, $([math]::Round($now.sizeBytes / 1GB, 2)) GB" -ForegroundColor Green
            Receive-Job $bg -Wait -ErrorAction Continue 2>&1 | Out-Host
            if ($now.status -ne 'succeeded') { throw "Sao lưu lỗi: $($now.error)" }

            $before = (Psql 'SELECT count(*) FROM files;' | Select-Object -Index 2).Trim()
            Write-Host '==> Đo RTO: dừng api/worker, phục hồi bản vừa sao lưu, khởi động lại' -ForegroundColor Cyan
            $r0 = Get-Date
            & docker @($compose + @('stop', 'api', 'worker'))
            & docker @($compose + @('run', '--rm', '--no-deps', 'worker', 'restore', "$($job.id)"))
            $restoreCode = $LASTEXITCODE
            & docker @($compose + @('start', 'api', 'worker'))
            while ($true) {
                try { if ((Invoke-WebRequest "$direct/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { break } } catch { }
                Start-Sleep -Seconds 2
            }
            $null = Get-AdminToken
            $rto = [int]((Get-Date) - $r0).TotalSeconds
            $after = (Psql 'SELECT count(*) FROM files;' | Select-Object -Index 2).Trim()
            $report = @(
                "Sao lưu đầy đủ khi có tải: $backupSeconds giây, $([math]::Round($now.sizeBytes / 1GB, 2)) GB, trạng thái $($now.status)"
                "Phục hồi (RTO, từ lúc dừng dịch vụ tới khi đăng nhập lại được): $rto giây, mã thoát $restoreCode"
                "Số tệp trong CSDL: trước $before, sau phục hồi $after"
            )
            $report | Set-Content -Path (Join-Path $results "$runName.txt") -Encoding utf8
            $report | ForEach-Object { Write-Host $_ -ForegroundColor Green }
            $code = $restoreCode
        }
    }
}
finally {
    Stop-Job $sampler; Remove-Job $sampler -Force
}

Write-Host "`n==> Tài nguyên container ($runName)" -ForegroundColor Cyan
Write-StatsSummary $statsFile (Join-Path $results "$runName-docker-stats.txt")

Write-Host "`n==> Câu lệnh SQL tốn thời gian nhất" -ForegroundColor Cyan
$top = Psql @"
SELECT round(total_exec_time)::bigint AS total_ms, calls, round(mean_exec_time::numeric, 1) AS mean_ms, round(max_exec_time::numeric) AS max_ms,
       left(regexp_replace(query, '\s+', ' ', 'g'), 220) AS query
FROM pg_stat_statements WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
ORDER BY total_exec_time DESC LIMIT 25;
"@
$top | Set-Content -Path (Join-Path $results "$runName-pg-top.txt") -Encoding utf8
$top | Select-Object -First 15 | ForEach-Object { Write-Host $_ }
Write-Host "`nThời gian chạy: $([int]((Get-Date) - $started).TotalMinutes) phút. Kết quả: tests/load/results/$runName.*" -ForegroundColor Green
exit $code
