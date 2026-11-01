# BlueDrive — Hệ thống phần mềm lưu trữ sản phẩm truyền thông dân số

Hệ thống quản lý, lưu trữ, tìm kiếm, khai thác và chia sẻ sản phẩm truyền thông số (hình ảnh, video, âm thanh, tài liệu, infographic, ấn phẩm điện tử) — trải nghiệm tương tự Google Drive/OneDrive nội bộ — phục vụ Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số (Cục Dân số, Bộ Y tế).

## Thành phần

| Thư mục | Nội dung |
|---|---|
| `src/` | Backend .NET 10 (Clean Architecture): `Domain`, `Application` (MediatR CQRS), `Infrastructure` (EF Core/PostgreSQL 16, lưu trữ mã hóa AES-256-GCM, Hangfire, ONLYOFFICE, ClamAV, báo cáo Excel/PDF), `Api` (REST `/api/v1`, SignalR, tus), `Worker` (xử lý nền, sao lưu/phục hồi) |
| `web/` | Ứng dụng web React 18 + Vite + Ant Design 5 (tiếng Việt), E2E Playwright |
| `mobile/` | Ứng dụng di động Expo (Android/iOS) |
| `deploy/` | `docker-compose.yml` (postgres, api, worker, web, onlyoffice, clamav, caddy), `Caddyfile`, `nginx.conf`, `.env.example` |
| `scripts/` | `install`, `backup-now`, `restore`, `seed`, `secure-wipe`, `ci`, `test-large-upload` (.ps1 + .sh) |
| `tests/` | Unit test, integration test (Testcontainers PostgreSQL), load test k6 |
| `docs/` | Ma trận truy vết 84 chức năng, hướng dẫn cài đặt/quản trị/sử dụng, kịch bản vận hành thử, đối chiếu QĐ 742, định dạng dữ liệu bàn giao |

## Cài đặt nhanh

```bash
./scripts/install.sh drive.example.gov.vn admin@example.gov.vn        # Linux
pwsh -File scripts/install.ps1 -Domain drive.example.gov.vn            # Windows
```
Chi tiết: [docs/huong-dan-cai-dat.md](docs/huong-dan-cai-dat.md). Máy chủ tối thiểu 8 core / 16 GB RAM / 520 GB SSD.

## Tài liệu

- [Ma trận truy vết HSMT (84 chức năng)](docs/traceability.md)
- [Hướng dẫn sử dụng](docs/huong-dan-su-dung.md) · [Hướng dẫn quản trị](docs/huong-dan-quan-tri.md) · [Hướng dẫn cài đặt](docs/huong-dan-cai-dat.md)
- [Kịch bản vận hành thử](docs/kich-ban-van-hanh-thu.md) · [Đối chiếu ATTT QĐ 742/QĐ-BTTTT](docs/attt-742.md) · [Định dạng gói dữ liệu bàn giao](docs/data-export-format.md)
- Ứng dụng di động: [mobile/README.md](mobile/README.md)
- API: `/api/swagger`

## Phát triển và kiểm thử

```powershell
docker compose -f deploy/docker-compose.dev.yml up -d
dotnet test tests/BlueDrive.UnitTests
dotnet test tests/BlueDrive.IntegrationTests          # cần Docker
cd web; npm run dev | npm run lint | npm run build | npm run test:e2e
pwsh -File scripts/ci.ps1                              # toàn bộ
```

Kết quả kiểm thử gần nhất: 59 unit test, 47 integration test, 39 E2E (trên bản cài sạch), k6 50 người dùng đồng thời p95 < 110 ms, tải lên 2 GB resumable + tua video, sao lưu/phục hồi thực tế.
