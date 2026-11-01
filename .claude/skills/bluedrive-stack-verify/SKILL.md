---
name: bluedrive-stack-verify
description: Use this skill when verifying the BlueDrive system end-to-end on Docker — rebuild the deploy/docker-compose.yml stack, health check, smoke test API, run Playwright E2E (web/e2e), integration tests with Testcontainers (Postgres, MinIO S3, Mailpit SMTP), build the Expo APK and smoke test on an Android emulator, capture screenshots for docs. Triggers: "kiểm tra lại stack", "chạy thử toàn bộ", "E2E", "nghiệm thu", "build APK", "ci.ps1".
---

# BlueDrive — kiểm tra toàn bộ stack

## Khi nào dùng
- Trước khi commit kết thúc một phase hoặc trước khi báo "xong".
- Sau khi sửa Dockerfile, nginx/Caddy, cấu hình, migration.

## Quy trình
1. **CI cục bộ**: `powershell -File scripts/ci.ps1` (build, 59+ unit, integration Testcontainers gồm S3/MinIO + SMTP/Mailpit, web lint/build, mobile typecheck/lint, `docker compose config`). Cờ: `-SkipIntegration -SkipWeb -SkipMobile -SkipDocker -E2E`.
2. **Dựng stack**: `cd deploy; docker compose up -d --build` (lần đầu: `scripts/install.ps1` sinh `deploy/.env`). Máy dev dùng `HTTP_PORT=8088` (cổng 80 bị chiếm).
3. **Health**: `docker compose ps` (tất cả healthy) và `curl http://localhost:8088/api/health`.
4. **E2E web**: `cd web; $env:E2E_BASE_URL='http://localhost:8088'; $env:E2E_USERNAME='admin'; $env:E2E_PASSWORD='<mật khẩu admin>'; npm run test:e2e`.
5. **Tải lớn / hiệu năng** (khi đụng upload/storage): `scripts/test-large-upload.ps1`, `k6 run tests/load/k6-list-search.js`.
6. **Sao lưu/phục hồi** (khi đụng Operations): `scripts/backup-now.ps1` → xóa thử một tệp → `scripts/restore.ps1 <id>` → kiểm chuỗi audit hợp lệ.
7. **Mobile** (khi đụng `mobile/`): `pwsh -File scripts/build-apk.ps1` (Docker, cần ≥ 8 GB RAM trống — dừng stack BlueDrive trong lúc build nếu thiếu) → khởi động emulator (`emulator -avd Pixel_9a`) → `adb install -r mobile/build-output/bluedrive-<ver>.apk` → đăng nhập máy chủ `http://10.0.2.2:8088` → chụp màn hình `adb exec-out screencap -p`.
8. **Ảnh tài liệu**: cập nhật `docs/images/` nếu giao diện đổi.

## Lưu ý / Pitfalls
- `adb devices` báo `unauthorized` → `adb kill-server; adb start-server`.
- Docker Desktop có thể treo khi nhiều stack khác chạy cùng → giải phóng RAM rồi khởi động lại.
- nginx phải là `location /tus` (không có `/` cuối).
- ONLYOFFICE secret ≥ 32 ký tự; app mobile mở Office qua `?mobile=true`.
- Không build APK trực tiếp trên Windows ở đường dẫn dài (giới hạn 260 ký tự CMake) — dùng script Docker.
