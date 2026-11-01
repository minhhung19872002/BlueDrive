<#
.SYNOPSIS
  Xóa sạch toàn bộ dữ liệu khi kết thúc hợp đồng (HSMT mục 3.6) và lập biên bản xóa.
.DESCRIPTION
  - Ghi nhận danh sách volume, dung lượng, số tệp và mã băm danh mục trước khi xóa.
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
    $lines += "Kết quả: $(if ($remaining) { 'CHƯA HOÀN TẤT' } else { 'ĐÃ XÓA TOÀN BỘ DỮ LIỆU' })"
    Set-Content $report ($lines -join [Environment]::NewLine) -Encoding utf8
    $hash = (Get-FileHash $report -Algorithm SHA256).Hash
    Add-Content $report "`r`nSHA-256 của biên bản (trước dòng này): $hash"
    Write-Host "Đã lập biên bản: $report" -ForegroundColor Green
}
finally { Pop-Location }
