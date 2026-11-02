# Hướng dẫn cài đặt BlueDrive

Tài liệu dành cho cán bộ kỹ thuật triển khai hệ thống trên máy chủ cloud của Chủ đầu tư.

## 1. Yêu cầu máy chủ

| Thành phần | Tối thiểu (theo HSMT 3.4) |
|---|---|
| CPU | 8 core |
| RAM | 16 GB |
| Ổ cứng | 520 GB SSD |
| Hệ điều hành | Ubuntu Server 22.04/24.04 LTS (khuyến nghị) hoặc Windows Server 2022 có Docker |
| Phần mềm | Docker Engine 24+ và Docker Compose v2, Git |
| Mạng | 01 IP tĩnh; mở cổng 80 và 443 (chỉ hai cổng này ra Internet) |

Phân bổ tài nguyên mặc định (`deploy/docker-compose.yml`): ONLYOFFICE 4 GB, PostgreSQL 3 GB, API 2 GB, Worker 2 GB, ClamAV 3 GB (nếu bật), web/Caddy 256 MB mỗi dịch vụ.

## 2. Cài đặt nhanh (một lệnh)

```bash
git clone https://github.com/minhhung19872002/BlueDrive.git /opt/bluedrive
cd /opt/bluedrive
./scripts/install.sh drive.example.gov.vn admin@example.gov.vn     # Linux
# hoặc trên Windows:  pwsh -File scripts/install.ps1 -Domain drive.example.gov.vn -AcmeEmail admin@example.gov.vn
```

Script sẽ:
1. Kiểm tra Docker.
2. Tạo `deploy/.env` từ `deploy/.env.example` với **các khóa bí mật ngẫu nhiên** (mật khẩu CSDL, JWT, ONLYOFFICE, khóa chủ mã hóa AES-256) và in ra mật khẩu quản trị ban đầu.
3. Build và khởi động toàn bộ dịch vụ: `postgres`, `api`, `worker`, `web`, `onlyoffice`, `reverse-proxy` (Caddy).
4. Chờ tới khi `/health` báo `Healthy`.

Khi có tên miền, Caddy tự cấp và gia hạn chứng chỉ TLS Let's Encrypt (bản ghi DNS A của tên miền phải trỏ về IP máy chủ trước khi chạy). Không có tên miền: để trống tham số, hệ thống chạy HTTP trên cổng 80 (chỉ dùng thử nghiệm nội bộ).

Các tùy chọn khác của `install.ps1`: `-HttpPort 8088` (đổi cổng), `-Demo` (nạp dữ liệu mẫu), `-EnableClamAv` (bật quét mã độc), `-AdminPassword '...'`.

> **QUAN TRỌNG**: sao lưu tệp `deploy/.env` ra nơi an toàn (két, ổ mã hóa). Mất `STORAGE_MASTER_KEY` thì **không thể giải mã** tệp đã lưu và các bản sao lưu.

## 3. Cài đặt thủ công

```bash
cd deploy
cp .env.example .env
# sửa .env: SITE_ADDRESS, PUBLIC_BASE_URL, POSTGRES_PASSWORD, JWT_SECRET (>=32 ký tự),
# ONLYOFFICE_JWT_SECRET (>=32 ký tự), STORAGE_MASTER_KEY (base64 32 byte), ADMIN_PASSWORD
docker compose up -d --build
docker compose ps
curl http://localhost/health
```

Sinh khóa chủ: Linux `head -c 32 /dev/urandom | base64`; PowerShell `[Convert]::ToBase64String((1..32|%{Get-Random -Max 256}))`.

## 4. Đăng nhập lần đầu

- Truy cập `PUBLIC_BASE_URL`, đăng nhập `admin` / `ADMIN_PASSWORD`.
- Hệ thống **bắt buộc đổi mật khẩu** trước khi dùng.
- Vào *Quản trị > Cấu hình hệ thống* để đặt chính sách mật khẩu, dung lượng mặc định, lịch sao lưu, cấu hình SMTP (trong `.env`) để gửi email quên mật khẩu/cảnh báo.

## 5. Thành phần tùy chọn

| Thành phần | Bật bằng |
|---|---|
| Quét mã độc ClamAV | `CLAMAV_ENABLED=true` trong `.env`, khởi động: `docker compose --profile clamav up -d` |
| Lưu tệp trên S3 | `STORAGE_PROVIDER=s3` và các biến `S3_*` |
| Sao chép bản sao lưu lên S3 | `BACKUP_S3_ENABLED=true` (hoặc bật trong màn hình Sao lưu) |
| Email (SMTP) | `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` |
| Dữ liệu mẫu | `scripts/seed.ps1` (5 người dùng `canbo1..canbo5`, mật khẩu `DEMO_PASSWORD` trong `deploy/.env`, mặc định `Demo@12345`) |

## 6. Kiểm tra sau cài đặt

```bash
docker compose ps                           # tất cả "Up"/"healthy"
curl -s https://drive.example.gov.vn/health # {"status":"Healthy",...}
pwsh -File scripts/test-large-upload.ps1 -BaseUrl https://drive.example.gov.vn -Username admin -Password '...'
```

Trang *Quản trị > Trạng thái hệ thống* hiển thị tình trạng CSDL, kho lưu trữ, ONLYOFFICE, ClamAV, dung lượng đĩa, bản sao lưu gần nhất và hàng đợi xử lý nền.

## 7. Nâng cấp phiên bản

```bash
cd /opt/bluedrive
git pull
cd deploy && docker compose up -d --build
```

Migration CSDL chạy tự động khi API khởi động; mỗi lần triển khai được ghi vào *Nhật ký thay đổi hệ thống*. Nên chạy `scripts/backup-now.ps1 -Full` trước khi nâng cấp.

## 8. Gỡ cài đặt / kết thúc hợp đồng

1. *Quản trị > Sao lưu > Xuất toàn bộ dữ liệu* → tải gói bàn giao cho Chủ đầu tư (định dạng: `docs/data-export-format.md`).
2. `pwsh -File scripts/secure-wipe.ps1` (hoặc `./scripts/secure-wipe.sh`): xóa mọi volume, hủy `.env` và lập biên bản xóa dữ liệu có mã băm.

## 9. Môi trường phát triển (Windows + Docker Desktop)

```powershell
docker compose -f deploy/docker-compose.dev.yml up -d   # postgres :5432, onlyoffice :8082, mailpit :8025
dotnet run --project src/BlueDrive.Api                  # http://localhost:8080
dotnet run --project src/BlueDrive.Worker
cd web; npm install; npm run dev                         # http://localhost:5173
powershell -File scripts/ci.ps1                          # build + test toàn bộ
```
Khi chạy API trên máy dev với ONLYOFFICE trong Docker, đặt `OnlyOffice__CallbackBaseUrl=http://host.docker.internal:8080`, `OnlyOffice__PublicUrl=http://localhost:8082`, `OnlyOffice__InternalUrl=http://localhost:8082`.
