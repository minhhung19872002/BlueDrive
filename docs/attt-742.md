# Đối chiếu yêu cầu an toàn thông tin cơ bản đối với phần mềm nội bộ

Căn cứ: Quyết định số 742/QĐ-BTTTT ngày 22/4/2022 của Bộ Thông tin và Truyền thông ban hành yêu cầu an toàn thông tin cơ bản đối với phần mềm nội bộ; Thông tư 39/2017/TT-BTTTT (danh mục tiêu chuẩn kỹ thuật).

Bảng dưới đây đối chiếu từng nhóm yêu cầu với cách hệ thống BlueDrive đáp ứng, kèm vị trí trong mã nguồn và kiểm thử tự động để kiểm chứng.

## 1. Xác thực

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Xác thực người dùng trước khi truy cập chức năng/dữ liệu | Mọi API (trừ đăng nhập, quên mật khẩu, liên kết chia sẻ công khai, callback ONLYOFFICE có chữ ký) yêu cầu JWT hợp lệ. `ApiControllerBase` gắn `[Authorize]` mặc định. | `AuthTests`, `AdminTests.Viewer_role_cannot_access_admin_functions` |
| Chính sách mật khẩu đủ mạnh, cấu hình được | Mặc định ≥ 8 ký tự, có chữ hoa, chữ thường, số, ký tự đặc biệt; hết hạn sau 90 ngày; không trùng 5 mật khẩu gần nhất. Cấu hình tại *Cấu hình hệ thống > Chính sách mật khẩu*. `PasswordService`. | `AuthTests.First_login_requires_password_change_before_other_endpoints` |
| Lưu trữ mật khẩu an toàn | Băm PBKDF2-HMAC-SHA512 (ASP.NET Core Identity v3, 100.000 vòng, salt ngẫu nhiên). Không lưu mật khẩu rõ, không ghi log mật khẩu/token. | `PasswordService.Hash` |
| Bắt buộc đổi mật khẩu khởi tạo | Tài khoản mới/được đặt lại mật khẩu có cờ `must_change_password`; máy chủ chặn mọi API khác (403) tới khi đổi (`PasswordChangeRequiredFilter`). | `AuthTests.First_login_requires_password_change_before_other_endpoints` |
| Chống dò mật khẩu | Khóa tạm 15 phút sau 5 lần sai (cấu hình được), giới hạn tần suất `/auth/*` (30 yêu cầu/phút/IP), so sánh thời gian bất biến khi tài khoản không tồn tại, cảnh báo `brute_force` realtime + email cho quản trị. | `AuthTests.Five_wrong_passwords_lock_the_account_and_raise_brute_force_alert` |
| Khôi phục mật khẩu an toàn | Liên kết qua email, token ngẫu nhiên 256 bit lưu dạng băm SHA-256, dùng một lần, hết hạn sau 30 phút; không tiết lộ tài khoản có tồn tại hay không. | `AuthTests.Forgot_password_sends_single_use_link` |

## 2. Quản lý phiên

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Thời hạn phiên | Access token 15 phút; refresh token 7 ngày, xoay vòng mỗi lần dùng, lưu dạng băm; tự đăng xuất khi không hoạt động (mặc định 30 phút, cấu hình được). | `AuthTests.Refresh_token_rotates_and_reuse_revokes_the_session` |
| Phát hiện dùng lại token bị đánh cắp | Dùng lại refresh token đã xoay → thu hồi cả phiên, ghi nhật ký. | như trên |
| Quản lý và thu hồi phiên | Người dùng xem/đăng xuất phiên khác; quản trị thu hồi phiên, thu hồi toàn bộ quyền truy cập của tài khoản (tăng security stamp, đưa phiên vào danh sách đen tới khi token hết hạn), khóa tài khoản. | `AuthTests.Admin_can_revoke_all_access_and_access_token_stops_working`, `User_can_list_and_revoke_own_sessions` |

## 3. Phân quyền

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Phân quyền theo vai trò | RBAC động `function.action` (`Permissions.cs`), 4 vai trò hệ thống (Quản trị, Biên tập/Tải lên, Phê duyệt, Chỉ xem), ma trận phân quyền chỉnh được, bảng đối chiếu. | `AdminTests.Role_permission_matrix_changes_are_audited_and_enforced` |
| Phân quyền trên dữ liệu (đối tượng) | ACL thư mục/tệp/nhóm tài nguyên cho người dùng/nhóm/đơn vị/vai trò, kế thừa và ngắt kế thừa, chia sẻ, khóa nhóm (chỉ đọc), tư liệu chờ duyệt chỉ người có quyền thấy. `ResourceAuthorizationService` được kiểm tra lại ở mọi thao tác và mỗi lần truyền nội dung. | `ResourceAuthorizationMatrixTests` (unit), `SharingTests` |
| Đặc quyền tối thiểu | Không chia sẻ được quyền cao hơn quyền đang có; vai trò Chỉ xem không có quyền chỉnh sửa dù được chia sẻ quyền sửa; tài khoản admin mặc định không thể bị xóa/gỡ vai trò. | `CollaborationTests` (chế độ xem), `SharingTests` |

## 4. Bảo vệ dữ liệu

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Mã hóa khi truyền | Caddy cấp TLS (Let's Encrypt) khi có tên miền, chỉ TLS 1.2+, HSTS 1 năm. | `deploy/Caddyfile` |
| Mã hóa khi lưu | Mỗi tệp/phiên bản được mã hóa AES-256-GCM theo khối 64 KB với khóa dữ liệu riêng, khóa dữ liệu được bọc bằng khóa chủ (envelope encryption); vẫn hỗ trợ HTTP Range. Ảnh thu nhỏ, báo cáo cũng được mã hóa. Khóa Data Protection được mã hóa bằng khóa chủ. Số điện thoại/email trong CSDL được mã hóa (Data Protection). | `StorageTests` (unit, phát hiện sửa đổi bản mã), `StorageTests.Tus_resumable_upload_then_complete_creates_encrypted_file`, `AdminTests.User_crud...` (điện thoại mã hóa) |
| Liên kết tải có thời hạn | Nội dung chỉ truyền qua URL ký ngắn hạn (Data Protection, 1–6 giờ), kiểm tra lại quyền ở mỗi yêu cầu; không lộ đường dẫn lưu trữ thật. | `ContentController` |
| Chia sẻ ra ngoài có kiểm soát | Liên kết công khai có mật khẩu, ngày hết hạn, giới hạn số lượt (tăng nguyên tử), yêu cầu đăng nhập, thu hồi; ghi nhật ký mọi lượt truy cập. | `SharingTests.Public_link_with_password_limit_and_revoke` |

## 5. Kiểm tra đầu vào

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Kiểm tra dữ liệu đầu vào | FluentValidation cho mọi lệnh, thông báo tiếng Việt; lỗi trả về dạng ProblemDetails. | `AdminTests` |
| Kiểm tra tệp tải lên | Kiểm tra chữ ký nhị phân (magic bytes) theo phần mở rộng, chặn tệp thực thi/kịch bản (kể cả đổi đuôi), danh sách định dạng cho phép và kích thước tối đa cấu hình được, làm sạch tên tệp, chống path traversal ở lớp lưu trữ. | `StorageTests.Magic_bytes_are_verified`, `File_names_are_sanitized`, `StorageTests.Blocked_and_mismatched_files_are_rejected` |
| Quét mã độc | ClamAV (bật `CLAMAV_ENABLED=true`, profile `clamav`): tệp ở trạng thái đang quét, nhiễm thì cách ly (cấm tải), cảnh báo `malware_detected`. | `MalwareScanStep` |
| Chống XSS/CSRF | SPA dùng Bearer token (không dùng cookie xác thực → không bị CSRF); CSP chặt cho SPA; nội dung người dùng (SVG/HTML) được trả với `Content-Security-Policy: sandbox`; kết quả tìm kiếm chỉ chèn thẻ `<mark>` sau khi đã mã hóa HTML. | `deploy/nginx.conf`, `SearchTextTests.Highlight_encodes_html` |

## 6. Nhật ký và giám sát

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Ghi nhật ký sự kiện | Đăng nhập/đăng xuất/sai mật khẩu, tải lên/xuống, xem, sửa, chia sẻ/hủy chia sẻ, đổi tên, di chuyển, sao chép, xóa, khôi phục, xóa vĩnh viễn, đổi quyền, đổi cấu hình, phê duyệt... (`IAuditLogger`, `AuditBehavior`). | `MonitoringTests.Every_operation_is_logged_and_filterable` |
| Bảo vệ nhật ký | Bảng `audit_logs` chỉ ghi thêm (trigger chặn UPDATE/DELETE/TRUNCATE), chuỗi băm SHA-256 nối tiếp, có chức năng kiểm tra toàn vẹn. | `FoundationTests.Audit_log_is_append_only_and_hash_chained`, `MonitoringTests` (verify) |
| Cảnh báo bất thường | Đăng nhập sai nhiều lần, vượt ngưỡng/hạn mức dung lượng, tải xuống bất thường, sao lưu thất bại, phát hiện mã độc → thông báo realtime, trang Cảnh báo, email. | `MonitoringTests.Unusual_downloads_raise_an_alert_that_can_be_acknowledged` |
| Không lộ thông tin lỗi | ProblemDetails tiếng Việt, không trả stack trace ở môi trường production; header bảo mật (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, CSP, HSTS), ẩn header máy chủ. | `GlobalExceptionHandler`, `SecurityHeaders` |

## 7. Sao lưu, phục hồi, xóa dữ liệu

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Sao lưu định kỳ và đột xuất | Lịch cron cấu hình được (mặc định 2h sáng hằng ngày), đầy đủ hằng tuần + tăng dần, giữ N bản, tùy chọn sao chép lên S3, checksum SHA-256, cảnh báo khi thất bại. | `OperationsTests.Backup_schedule_and_manual_backup_queue`, `scripts/backup-now.ps1` |
| Phục hồi | `scripts/restore.ps1 <mã>` kiểm tra checksum cả chuỗi rồi phục hồi CSDL + tệp. | xem *Hướng dẫn quản trị* |
| Bàn giao và xóa dữ liệu khi kết thúc | *Xuất toàn bộ dữ liệu* (tệp giải mã + metadata + nhật ký); `scripts/secure-wipe.ps1` xóa mọi volume, hủy khóa chủ và lập biên bản có mã băm. | `OperationsTests.Full_data_export_package_contains_files_and_metadata` |

## 8. Cấu hình an toàn khi triển khai

- Toàn bộ bí mật (mật khẩu CSDL, khóa JWT, khóa ONLYOFFICE, khóa chủ mã hóa) chỉ nằm trong `deploy/.env` (không commit), được `scripts/install.ps1` sinh ngẫu nhiên.
- Chỉ mở cổng 80/443 ra ngoài; PostgreSQL, API, Worker, ONLYOFFICE, ClamAV chỉ trong mạng nội bộ Docker.
- Giới hạn tài nguyên từng dịch vụ trong `docker-compose.yml` (phù hợp máy chủ 8 core/16 GB).
- Cập nhật hệ thống: dựng lại image (`docker compose up -d --build`); mỗi lần triển khai được ghi tự động vào *Nhật ký thay đổi hệ thống*.
