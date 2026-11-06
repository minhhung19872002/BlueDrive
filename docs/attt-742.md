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
| Chống dò mật khẩu | Khóa tạm tài khoản 15 phút sau 5 lần sai (cấu hình được). Chặn tạm **địa chỉ IP** sau 30 lần đăng nhập sai trong 15 phút, cộng dồn mọi tài khoản (chống dò mật khẩu hàng loạt tài khoản; cấu hình được, đăng nhập đúng không bị tính nên cả cơ quan sau một địa chỉ NAT vẫn đăng nhập bình thường). Giới hạn tần suất đăng nhập 300 yêu cầu/phút/IP, quên/đặt lại mật khẩu 60 yêu cầu/phút/IP (`RateLimit__LoginPerMinute`, `RateLimit__AuthPerMinute`). So sánh thời gian bất biến khi tài khoản không tồn tại. Cảnh báo `brute_force` realtime + email cho quản trị. | `AuthTests.Five_wrong_passwords_lock_the_account_and_raise_brute_force_alert`, `AuthTests.Many_failed_logins_from_one_address_block_that_address_only`, `LoginIpGuardTests`; kiểm thử tải T3 (docs/bao-cao-kiem-thu-tai.md) |
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
| Phân quyền theo vai trò | RBAC động `function.action` (`Permissions.cs`), 4 vai trò hệ thống (Quản trị, Chỉnh sửa, Phê duyệt, Chỉ xem), ma trận phân quyền chỉnh được, bảng đối chiếu. | `AdminTests.Role_permission_matrix_changes_are_audited_and_enforced` |
| Phân quyền trên dữ liệu (đối tượng) | ACL thư mục/tệp/nhóm tài nguyên cho người dùng/nhóm/đơn vị/vai trò, kế thừa và ngắt kế thừa, chia sẻ, khóa nhóm (chỉ đọc), tư liệu chờ duyệt chỉ người có quyền thấy. `ResourceAuthorizationService` được kiểm tra lại ở mọi thao tác và mỗi lần truyền nội dung. | `ResourceAuthorizationMatrixTests` (unit), `SharingTests` |
| Đặc quyền tối thiểu | Không chia sẻ được quyền cao hơn quyền đang có; vai trò Chỉ xem không có quyền chỉnh sửa dù được chia sẻ quyền sửa; tài khoản admin mặc định không thể bị xóa/gỡ vai trò. Quản trị **không tự thấy nội dung** người khác: chỉ qua *Truy cập khẩn cấp* (≤ 24 giờ, bắt buộc lý do, nhật ký mức cao, thông báo chủ Drive / Quản lý Space; chế độ điều tra cần quyền riêng). Chỉnh sửa không tự nâng mình lên Quản lý (cần quyền *Quản lý quyền*). | `CollaborationTests` (chế độ xem), `SharingTests`, `SecurityTests.An_editor_cannot_raise_their_own_rights…`, `AdvancedAdminTests.Emergency_access_is_temporary…` |
| Mô hình phân quyền kiểu Drive / SharePoint | Space, thư viện, cấp quyền (25 quyền nguyên tử), ACL theo scope + ltree, nhóm lồng, đơn vị gồm đơn vị con, quyền có hạn, nhãn phân loại; mọi kênh (danh sách, tìm kiếm, dashboard, thông báo, thumbnail, preview, tải xuống, ONLYOFFICE, đồng bộ, mobile, báo cáo) lọc trong SQL trước khi phân trang; không có quyền xem → 404 (không lộ sự tồn tại). Mô tả: `permissions-model.md`, `permission-matrix.md`, `sharing-policies.md`. | `PermissionEngineProperties` (property test so với bộ tham chiếu), `PermissionMatrixDocTests`, `PermissionModelTests`, `SecurityTests.Ids_of_someone_elses_items…` |
| Chống dò liên kết | Token liên kết chỉ lưu băm SHA-256, tra theo chỉ mục; token sai / sửa một ký tự trả lời giống hệt nhau; điểm vào công khai và đăng nhập có giới hạn tần suất; khách không tra cứu được người dùng. | `SecurityTests.Link_tokens_answer_the_same…`, `SharingModelTests.Guest_invited_by_email…` |
| Lưu giữ và giữ pháp lý | Chính sách lưu giữ (kho lưu giữ ẩn), giữ pháp lý chặn mọi xóa vĩnh viễn (kể cả quản trị, job), mọi thao tác ghi nhật ký `hold_applied` / `hold_removed`. | `AdvancedAdminTests.A_hold_blocks_every_purge…` |

## 4. Bảo vệ dữ liệu

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Mã hóa khi truyền | Caddy cấp TLS (Let's Encrypt) khi có tên miền, chỉ TLS 1.2+, HSTS 1 năm. | `deploy/Caddyfile` |
| Mã hóa khi lưu | Mỗi tệp/phiên bản được mã hóa AES-256-GCM theo khối 64 KB với khóa dữ liệu riêng, khóa dữ liệu được bọc bằng khóa chủ (envelope encryption); vẫn hỗ trợ HTTP Range. Ảnh thu nhỏ, báo cáo cũng được mã hóa. Khóa Data Protection được mã hóa bằng khóa chủ. Số điện thoại/email trong CSDL được mã hóa (Data Protection). | `StorageTests` (unit, phát hiện sửa đổi bản mã), `StorageTests.Tus_resumable_upload_then_complete_creates_encrypted_file`, `AdminTests.User_crud...` (điện thoại mã hóa) |
| Liên kết tải có thời hạn | Nội dung chỉ truyền qua URL ký ngắn hạn (Data Protection, 1–6 giờ), kiểm tra lại quyền ở mỗi yêu cầu; không lộ đường dẫn lưu trữ thật. | `ContentController` |
| Chia sẻ ra ngoài có kiểm soát | Liên kết mặc định "Hạn chế" (phải đăng nhập và có quyền với tư liệu); "Bất kỳ ai có liên kết" có ngày hết hạn, giới hạn số lượt (tăng nguyên tử), vai trò Chỉ xem / Người chỉnh sửa (quản trị tắt được chỉnh sửa qua liên kết), thu hồi; ghi nhật ký mọi lượt truy cập. Mật khẩu liên kết đã bỏ (04/11/2026): liên kết cũ có mật khẩu chuyển sang "Hạn chế". | `SharingTests.Public_link_with_access_limit_and_revoke`, `SharingTests.Public_folder_link_lists_and_streams_children_and_require_login` |

## 5. Kiểm tra đầu vào

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Kiểm tra dữ liệu đầu vào | FluentValidation cho mọi lệnh, thông báo tiếng Việt; lỗi trả về dạng ProblemDetails. | `AdminTests` |
| Kiểm tra tệp tải lên | Kiểm tra chữ ký nhị phân (magic bytes) theo phần mở rộng, chặn tệp thực thi/kịch bản (kể cả đổi đuôi), danh sách định dạng cho phép và kích thước tối đa cấu hình được, làm sạch tên tệp, chống path traversal ở lớp lưu trữ. | `StorageTests.Magic_bytes_are_verified`, `File_names_are_sanitized`, `StorageTests.Blocked_and_mismatched_files_are_rejected` |
| Quét mã độc | ClamAV (bật `CLAMAV_ENABLED=true`, profile `clamav`): tệp đang quét chỉ người tải lên mở được (người khác nhận "Tệp đang được kiểm tra virus"); nhiễm thì cách ly (cấm tải ở mọi đường: URL ký, liên kết công khai, ZIP, ONLYOFFICE), cảnh báo `malware_detected`. Tệp lớn hơn `CLAMAV_MAX_SCAN_MB` (mặc định 200 MB, compose đặt cùng giá trị cho `StreamMaxLength` của clamd) → "Bỏ qua quét (tệp quá lớn)", tải được kèm cảnh báo; ClamAV lỗi sau 3 lần thử → "Không quét được" + cảnh báo `malware_scan_failed`. | `MalwareScanStep`, `ScanGate`; IT `ScanGateTests`, UT `MalwareScanPolicyTests` |
| Chống XSS/CSRF | SPA dùng Bearer token (không dùng cookie xác thực → không bị CSRF); CSP chặt cho SPA; nội dung người dùng (SVG/HTML) được trả với `Content-Security-Policy: sandbox`; kết quả tìm kiếm chỉ chèn thẻ `<mark>` sau khi đã mã hóa HTML. | `deploy/nginx.conf`, `SearchTextTests.Highlight_encodes_html` |

## 6. Nhật ký và giám sát

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Ghi nhật ký sự kiện | Đăng nhập/đăng xuất/sai mật khẩu, tải lên/xuống, xem, sửa, chia sẻ/hủy chia sẻ, đổi tên, di chuyển, sao chép, xóa, khôi phục, xóa vĩnh viễn, đổi quyền, đổi cấu hình, phê duyệt... (`IAuditLogger`, `AuditBehavior`). | `MonitoringTests.Every_operation_is_logged_and_filterable` |
| Bảo vệ nhật ký | Bảng `audit_logs` chỉ ghi thêm (trigger chặn UPDATE/DELETE/TRUNCATE), chuỗi băm SHA-256 nối tiếp, có chức năng kiểm tra toàn vẹn. | `FoundationTests.Audit_log_is_append_only_and_hash_chained`, `MonitoringTests` (verify) |
| Cảnh báo bất thường | Đăng nhập sai nhiều lần, vượt ngưỡng/hạn mức dung lượng, tải xuống bất thường, sao lưu thất bại, phát hiện mã độc → thông báo realtime, trang Cảnh báo, email. | `MonitoringTests.Unusual_downloads_raise_an_alert_that_can_be_acknowledged` |
| Quản lý sự cố an toàn thông tin | Màn hình *Sự cố an toàn thông tin* (`/api/v1/security-incidents`, quyền `incident.*`): lập hồ sơ (kể cả từ cảnh báo), phân loại theo TT 20/2017/TT-BTTTT, giao người xử lý, theo dõi các bước điều tra → ngăn chặn → khôi phục → kết luận → đóng, dòng thời gian, xuất hồ sơ và sổ theo dõi PDF/Excel. Quy trình: `docs/quy-trinh-ung-pho-su-co.md`. | `IncidentAndAnnouncementTests.Incident_opened_from_alert_is_handled_closed_and_exported`, `Incident_endpoints_require_incident_permissions` |
| Không lộ thông tin lỗi | ProblemDetails tiếng Việt, không trả stack trace ở môi trường production; header bảo mật (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, CSP, HSTS), ẩn header máy chủ. | `GlobalExceptionHandler`, `SecurityHeaders` |

## 7. Sao lưu, phục hồi, xóa dữ liệu

| Yêu cầu | Cách đáp ứng | Kiểm chứng |
|---|---|---|
| Sao lưu định kỳ và đột xuất | Lịch cron cấu hình được (mặc định 2h sáng hằng ngày), đầy đủ hằng tuần + tăng dần, giữ N bản, tùy chọn sao chép lên S3 (cả CSDL và tệp, kể cả khi tệp lưu trên S3), checksum SHA-256, cảnh báo khi thất bại. | `OperationsTests.Backup_schedule_and_manual_backup_queue`, `BackupRestoreTests.S3_backup_survives_losing_the_server_and_secure_wipe_empties_the_bucket`, `scripts/backup-now.ps1` |
| Phục hồi | `scripts/restore.ps1 <mã>` kiểm tra checksum cả chuỗi rồi phục hồi CSDL + tệp; bản không còn trên máy chủ được tải về từ S3. | `BackupRestoreTests.Restore_brings_back_deleted_rows_and_blobs_with_a_valid_audit_chain` (chuỗi nhật ký vẫn toàn vẹn sau phục hồi) |
| Bàn giao và xóa dữ liệu khi kết thúc | *Xuất toàn bộ dữ liệu* (tệp giải mã + metadata + nhật ký); `scripts/secure-wipe.ps1` xóa bucket S3 (nếu có), mọi volume, hủy khóa chủ và lập biên bản có mã băm. | `OperationsTests.Full_data_export_package_contains_files_and_metadata`, `BackupRestoreTests.S3_backup_survives_losing_the_server_and_secure_wipe_empties_the_bucket` |

## 8. Cấu hình an toàn khi triển khai

- Toàn bộ bí mật (mật khẩu CSDL, khóa JWT, khóa ONLYOFFICE, khóa chủ mã hóa) chỉ nằm trong `deploy/.env` (không commit), được `scripts/install.ps1` sinh ngẫu nhiên.
- Chỉ mở cổng 80/443 ra ngoài; PostgreSQL, API, Worker, ONLYOFFICE, ClamAV chỉ trong mạng nội bộ Docker.
- Giới hạn tài nguyên từng dịch vụ trong `docker-compose.yml` (phù hợp máy chủ 8 core/16 GB).
- Cập nhật hệ thống: dựng lại image (`docker compose up -d --build`); mỗi lần triển khai được ghi tự động vào *Nhật ký thay đổi hệ thống*.
- Quản lý lỗ hổng: CI chặn phụ thuộc NuGet/npm và image Docker (Trivy) có lỗ hổng High/Critical đã có bản vá, quét lại hằng tuần, Dependabot đề xuất cập nhật; bản lỗi không được tự động triển khai. Thời hạn vá và cách chấp nhận rủi ro: `docs/quy-trinh-ung-pho-su-co.md` mục 6.

## 9. Thiết bị đầu cuối đồng bộ (BlueDrive Desktop)

- **Đăng nhập**: OAuth 2.0 Authorization Code + PKCE (S256), chỉ chấp nhận địa chỉ quay lại loopback trên chính máy
  (`127.0.0.1`/`localhost`, RFC 8252); mã một lần, hiệu lực 2 phút, lưu dạng băm. Mật khẩu không bao giờ nhập vào ứng dụng.
- **Token**: phiên thiết bị riêng (claim `did`), refresh token xoay vòng, phát hiện dùng lại thì thu hồi cả phiên; thời hạn
  cấu hình được (`sync.device_refresh_days`). Ứng dụng lưu token và khóa bí mật thiết bị bằng DPAPI (Windows), Keychain (macOS),
  tệp chỉ người dùng đọc được (Linux, quyền 0600); không ghi ra nhật ký, gói chẩn đoán lọc token/mật khẩu.
- **Kết nối**: TLS bắt buộc; tùy chọn ghim chứng thư (certificate pinning) cho máy chủ nội bộ bằng mã SPKI SHA-256
  (chính sách `CertificatePins` hoặc `deploy.json`), áp cho API, đăng nhập và kênh thông báo SignalR.
- **Bộ cài**: ký số Authenticode (chứng thư của đơn vị qua biến bí mật CI), bộ cài ONLYOFFICE chỉ chạy khi chữ ký của
  Ascensio System SIA hợp lệ.
- **Thu hồi thiết bị**: người dùng (thiết bị của mình) hoặc quản trị (`device.admin`) thu hồi tại "Thiết bị đã kết nối"; mọi
  phiên của thiết bị bị vô hiệu ngay.
- **Xóa dữ liệu từ xa**: lệnh được ký `HMAC-SHA256` bằng khóa bí mật riêng của thiết bị (lưu mã hóa trên máy chủ), ứng dụng
  kiểm tra chữ ký trước khi xóa thư mục đồng bộ, bộ nhớ đệm và token, rồi xác nhận về máy chủ.
- **Chính sách**: bật/tắt đồng bộ, giới hạn theo vai trò/đơn vị, kích thước tối đa, đuôi tệp bị chặn, nhóm chỉ trực tuyến,
  phiên bản ứng dụng tối thiểu. Tư liệu chỉ có quyền Xem không bao giờ được tải xuống máy.
- **Nhật ký**: mọi thao tác từ ứng dụng ghi `deviceId` và `source = desktop_sync` trong chuỗi nhật ký có băm.
- **Đồng bộ trong mạng LAN** (tắt mặc định; bật trong Cài đặt → Mạng hoặc chính sách `LanSync`): máy chỉ lấy tệp từ máy khác khi có
  **vé của máy chủ** — cấp sau đúng các kiểm tra của một lần tải xuống (chính sách đồng bộ, quyền tải, ghi nhật ký tải xuống), dùng
  trong 2 phút, tối đa 3 máy xác nhận. Máy cho tải chỉ gửi sau khi máy chủ xác nhận vé, và chỉ khi chính nó là thiết bị BlueDrive đã
  đăng nhập có quyền xem tệp; nó chỉ gửi bản đã đồng bộ, không đổi kể từ đó (không gửi bản đang sửa), không gửi tệp chưa tải về.
  Nội dung mã hóa AES-256-GCM bằng khóa riêng của vé (khung 1 MiB, số thứ tự khung được xác thực, có khung kết thúc), máy nhận
  kiểm tra SHA-256 cả tệp trước khi ghi; sai bất kỳ bước nào thì tải từ máy chủ. Máy chỉ nhận ra máy cùng máy chủ BlueDrive
  (UDP 47391, mạng miền/riêng); quy tắc tường lửa do `install-bluedrive.ps1 -LanSync 1` (quyền quản trị) hoặc Group Policy tạo.

