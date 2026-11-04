<#
.SYNOPSIS
  Xóa sạch toàn bộ dữ liệu khi kết thúc hợp đồng (HSMT mục 3.6) và lập biên bản xóa.
.DESCRIPTION
  - Ghi nhận danh sách volume, dung lượng, số tệp và mã băm danh mục trước khi xóa.
  - Nếu lưu trữ trên S3 (S3_BUCKET trong deploy/.env): xóa mọi đối tượng trong bucket (tệp số hóa và bản sao lưu backups/)
    trước khi dừng dịch vụ, ghi số đối tượng / dung lượng đã xóa vào biên bản. Phiên bản cũ của đối tượng (bucket bật
    versioning / object lock) phải được xóa thêm trên giao diện nhà cung cấp.
  - Dừng toàn bộ dịch vụ và xóa mọi volume (CSDL, tệp số hóa, bản sao lưu, ONLYOFFICE, log), deploy/.env (khóa mã hóa).
  - Xuất biên bản deploy/bien-ban-xoa-du-lieu-<thời gian>.txt kèm SHA-256 của biên bản.
  Nên thực hiện SAU khi đã bàn giao gói "Xuất toàn bộ dữ liệu" cho Chủ đầu tư.
.EXAMPLE
  powershell -File scripts/secure-wipe.ps1
#>
param([switch]$KeepEnv)
$ErrorActionPreference = 'Stop'
$deploy = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $deploy "bien-ban-xoa-du-lieu-$stamp.txt"

Write-Host 'CẢNH BÁO: thao tác này XÓA VĨNH VIỄN toàn bộ dữ liệu BlueDrive và không thể khôi phục.' -ForegroundColor Red
$answer = Read-Host "Gõ 'XOA TOAN BO DU LIEU' để xác nhận"
if ($answer -ne 'XOA TOAN BO DU LIEU') { Write-Host 'Đã hủy.'; exit 1 }
$operator = Read-Host 'Họ tên người thực hiện'
$witness = Read-Host 'Đại diện Chủ đầu tư chứng kiến'

Push-Location $deploy
try {
    $project = 'bluedrive'
    $volumes = docker volume ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}'
    $lines = @(
        'BIÊN BẢN XÓA DỮ LIỆU HỆ THỐNG BLUEDRIVE'
        "Thời điểm bắt đầu: $(Get-Date -Format 'dd/MM/yyyy HH:mm:ss zzz')"
        "Máy chủ: $env:COMPUTERNAME"
        "Người thực hiện: $operator"
        "Đại diện Chủ đầu tư: $witness"
        ''
        'DANH SÁCH VOLUME TRƯỚC KHI XÓA:'
    )
    foreach ($v in $volumes) {
        # Size, file count and a digest of the file listing, computed inside a throwaway container.
        $info = docker run --rm -v "${v}:/v:ro" alpine:3.20 sh -c 'du -sb /v | cut -f1; find /v -type f | wc -l; find /v -type f -exec ls -l {} + 2>/dev/null | sha256sum | cut -d" " -f1'
        $parts = @($info)
        $lines += "- $v : dung lượng $($parts[0]) byte, $($parts[1]) tệp, mã băm danh mục $($parts[2])"
    }
    $s3Remaining = 0
    $bucket = ''
    if (Test-Path '.env') {
        $bucketLine = Get-Content '.env' | Where-Object { $_ -match '^\s*S3_BUCKET\s*=' } | Select-Object -Last 1
        if ($bucketLine) { $bucket = ($bucketLine -replace '^\s*S3_BUCKET\s*=\s*', '').Trim() }
    }
    if ($bucket) {
        # Object storage: empty the bucket (blobs + backups/) while the stack is still configured.
        Write-Host "==> Xóa dữ liệu trên S3 (bucket $bucket)" -ForegroundColor Cyan
        $out = docker compose run --rm --no-deps -T worker wipe-s3
        $code = $LASTEXITCODE
        $kv = @{}
        foreach ($l in @($out)) { if ($l -match '^(S3_[A-Z_]+)=(.*)$') { $kv[$Matches[1]] = $Matches[2].Trim() } }
        $lines += ''
        if ($kv['S3_CONFIGURED'] -eq 'true') {
            $s3Remaining = [long]$kv['S3_REMAINING']
            $lines += "LƯU TRỮ S3 (bucket $($kv['S3_BUCKET'])):"
            $lines += "- Tệp số hóa đã xóa: $($kv['S3_DATA_OBJECTS']) đối tượng, $($kv['S3_DATA_BYTES']) byte"
            $lines += "- Bản sao lưu (backups/) đã xóa: $($kv['S3_BACKUP_OBJECTS']) đối tượng, $($kv['S3_BACKUP_BYTES']) byte"
            $lines += "- Đối tượng còn lại trong bucket: $s3Remaining"
            $lines += '- Lưu ý: nếu bucket bật versioning / object lock, các phiên bản cũ phải được xóa thêm trên giao diện nhà cung cấp S3.'
        }
        else {
            $s3Remaining = 1
            $lines += "LƯU TRỮ S3: không xóa được bucket $bucket (mã thoát $code) - CẦN XỬ LÝ THỦ CÔNG."
        }
    }
    Write-Host '==> Dừng dịch vụ và xóa volume' -ForegroundColor Cyan
    docker compose --profile clamav down --volumes --remove-orphans
    foreach ($v in $volumes) { docker volume rm -f $v 2>$null | Out-Null }
    $remaining = docker volume ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}'
    if (-not $KeepEnv -and (Test-Path '.env')) {
        # Overwrite before deleting: the file holds the master encryption key.
        $len = (Get-Item '.env').Length
        [System.IO.File]::WriteAllBytes((Join-Path $deploy '.env'), [byte[]]::new($len))
        Remove-Item '.env' -Force
        $lines += ''
        $lines += 'Đã ghi đè và xóa deploy/.env (chứa khóa chủ mã hóa STORAGE_MASTER_KEY): dữ liệu sao lưu còn sót ở nơi khác cũng không thể giải mã.'
    }
    $lines += ''
    $lines += "Volume còn lại sau khi xóa: $(if ($remaining) { $remaining -join ', ' } else { 'không còn' })"
    $lines += "Thời điểm kết thúc: $(Get-Date -Format 'dd/MM/yyyy HH:mm:ss zzz')"
    $lines += "Kết quả: $(if ($remaining -or $s3Remaining -gt 0) { 'CHƯA HOÀN TẤT' } else { 'ĐÃ XÓA TOÀN BỘ DỮ LIỆU' })"
    Set-Content $report ($lines -join [Environment]::NewLine) -Encoding utf8
    $hash = (Get-FileHash $report -Algorithm SHA256).Hash
    Add-Content $report "`r`nSHA-256 của biên bản (trước dòng này): $hash"
    Write-Host "Đã lập biên bản: $report" -ForegroundColor Green
}
finally { Pop-Location }
