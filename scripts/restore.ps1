<#
.SYNOPSIS
  Phục hồi hệ thống từ một bản sao lưu (CSDL + tệp số hóa).
.DESCRIPTION
  - Kiểm tra checksum của cả chuỗi (bản đầy đủ + các bản tăng dần).
  - Dừng api/worker, chạy pg_restore (--clean) và giải nén tệp theo đúng thứ tự, rồi khởi động lại.
  Mã bản sao lưu: cột "Mã" trên màn hình Quản trị > Sao lưu (GUID), hoặc tên thư mục trong volume backups.
.EXAMPLE
  powershell -File scripts/restore.ps1 01a1b2c3-...
#>
param(
    [Parameter(Mandatory = $true)][string]$BackupId,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
if (-not $Yes) {
    $answer = Read-Host "Phục hồi sẽ GHI ĐÈ dữ liệu hiện tại bằng bản sao lưu $BackupId. Gõ 'PHUC HOI' để tiếp tục"
    if ($answer -ne 'PHUC HOI') { Write-Host 'Đã hủy.'; exit 1 }
}
Push-Location (Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy')
try {
    Write-Host '==> Dừng api và worker' -ForegroundColor Cyan
    docker compose stop api worker
    Write-Host '==> Phục hồi' -ForegroundColor Cyan
    docker compose run --rm --no-deps worker restore $BackupId
    $code = $LASTEXITCODE
    Write-Host '==> Khởi động lại dịch vụ' -ForegroundColor Cyan
    docker compose start api worker
    if ($code -ne 0) { throw 'Phục hồi thất bại (xem log ở trên). Dữ liệu cũ có thể đã bị thay đổi một phần.' }
    Write-Host 'Phục hồi thành công.' -ForegroundColor Green
}
finally { Pop-Location }
