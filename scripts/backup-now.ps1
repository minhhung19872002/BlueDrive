<#
.SYNOPSIS
  Sao lưu đột xuất (CSDL + tệp) ngay lập tức từ dòng lệnh.
.EXAMPLE
  powershell -File scripts/backup-now.ps1          # tự chọn đầy đủ/tăng dần
  powershell -File scripts/backup-now.ps1 -Full    # bắt buộc bản đầy đủ
#>
param([switch]$Full)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy')
try {
    $mode = if ($Full) { 'full' } else { 'auto' }
    docker compose run --rm --no-deps worker backup $mode
    if ($LASTEXITCODE -ne 0) { throw 'Sao lưu thất bại. Xem log ở trên và trang Cảnh báo.' }
    Write-Host 'Sao lưu hoàn tất. Danh sách bản sao lưu: màn hình Quản trị > Sao lưu, hoặc thư mục volume "backups".' -ForegroundColor Green
}
finally { Pop-Location }
