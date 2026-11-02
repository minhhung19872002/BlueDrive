# BlueDrive — hướng dẫn cho Claude Code

Hệ thống lưu trữ sản phẩm truyền thông dân số (kiểu Google Drive nội bộ) cho gói thầu của Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số (Cục Dân số, Bộ Y tế).
Spec đầy đủ: `PROMPT_BlueDrive_ClaudeCode.md` (đặc tả) và `docs/hsmt/yeu-cau-ky-thuat.txt` (trích HSMT gốc, 84 chức năng mục 3.2).

## Nguyên tắc bắt buộc
- Mọi chức năng HSMT (#1–#84, không có #25) phải chạy thật, có API + màn hình + permission + audit + test, ghi vào `docs/traceability.md`.
- Không để TODO/stub trong code bàn giao. Không hard-code secret (chỉ đọc từ `deploy/.env`).
- UI, thông báo lỗi, tài liệu: 100% tiếng Việt. Code, tên file, commit: tiếng Anh.
- Sau mỗi phase: `scripts/ci.ps1` xanh → commit `phase-N: ...` → push `origin main` (https://github.com/minhhung19872002/BlueDrive).

## Stack
- .NET 10 (SDK 10.0.403), Clean Architecture: `src/BlueDrive.{Domain,Application,Infrastructure,Api,Worker}`.
- EF Core 10 + Npgsql (PostgreSQL 16), snake_case (EFCore.NamingConventions). Enum lưu dạng chuỗi snake_case (`SnakeCaseEnumConverter`), riêng `AccessLevel` lưu int để so sánh thứ bậc.
- MediatR **12.5.0** (bản Apache-2.0 cuối — không nâng lên 13+ vì license thương mại). FluentValidation 12. FluentAssertions **7.2.0** (8+ là thương mại).
- Ảnh: **SkiaSharp** (MIT) thay ImageSharp (ImageSharp ≥3.2 yêu cầu license key khi build Release; 3.1.x có CVE).
- Hangfire (PostgreSQL storage, schema `hangfire`): API chỉ enqueue, Worker chạy server.
- Web: React 18 + Vite + TS + AntD 5 (viVN) + TanStack Query + Zustand + react-i18next (`web/src/locales/vi.json`).
- Mobile: Expo (React Native) trong `mobile/`.

## Quy ước
- API prefix `/api/v1`, phân trang `{ items, total, page, pageSize }`, lỗi ProblemDetails tiếng Việt (`AppException` + `GlobalExceptionHandler`).
- Quyền chức năng: `Perm` (`src/BlueDrive.Application/Security/Permissions.cs`) dạng `function.action`, action ∈ view/upload/edit/delete/share/approve/admin.
- Audit: `IAuditLogger` (append-only, hash chain, advisory lock) + `IAuditableRequest` qua MediatR `AuditBehavior`.
- Tìm kiếm không dấu: hàm SQL `f_unaccent(text)` (map LINQ `DbFn.Unaccent`), trigger `files.search_vector`.
- Migration: `dotnet ef migrations add <Name> -p src/BlueDrive.Infrastructure -s src/BlueDrive.Infrastructure -o Persistence/Migrations`.

## Lệnh thường dùng (PowerShell)
```powershell
dotnet build BlueDrive.slnx
dotnet test tests/BlueDrive.UnitTests
dotnet test tests/BlueDrive.IntegrationTests      # cần Docker (Testcontainers)
cd web; npm run dev | npm run build | npm run lint
docker compose -f deploy/docker-compose.dev.yml up -d   # postgres + onlyoffice + mailpit cho dev
cd deploy; docker compose up -d --build                 # toàn bộ stack
powershell -File scripts/ci.ps1
cd web; npm run test:e2e                                # Playwright, cần stack chạy (E2E_BASE_URL, E2E_USERNAME, E2E_PASSWORD)
cd mobile; npx tsc --noEmit; npx expo lint              # app Expo SDK 58 (đọc mobile/AGENTS.md trước khi sửa)
```

## Lưu ý đã gặp
- Tham số action tên `q` + query `?q=` làm MVC coi là prefix → luôn đặt tên tham số complex `query`.
- Rate limit chỉ áp cho login/forgot/reset; không áp cho `/auth/me`, `/content/*` (nhiều người dùng chung IP NAT).
- `DateTime` không có Kind được coi là UTC (`UtcDateTimeConverter`); lọc theo ngày dùng giờ Việt Nam (UTC+7).
- ONLYOFFICE: secret ≥ 32 ký tự (HS256), URL Document Server trả về được chuyển sang InternalUrl trước khi tải.
- nginx phải khớp `location /tus` (không có dấu `/`) vì tạo phiên tus là `POST /tus`.
- Caddy gửi ONLYOFFICE `X-Forwarded-Host {hostport}/office` (không dùng `{host}` — mất cổng, lỗi "Download failed" khi chạy cổng 8088).
- `UseExceptionHandler` đặt sau `UseSerilogRequestLogging`, nếu không lỗi 4xx đã xử lý bị log thành 500.
- Mobile: route `change-password` vẫn hợp lệ khi đã đăng nhập → sau đổi mật khẩu bắt buộc phải `router.replace('/(tabs)')`.
- Emulator x86_64 cần APK `-Abis x86_64`; xem Office trên emulator cần `adb reverse tcp:8088 tcp:8088`.
