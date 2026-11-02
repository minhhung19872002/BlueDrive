# PROMPT CHO CLAUDE CODE — XÂY DỰNG "BLUEDRIVE"
## Hệ thống phần mềm lưu trữ sản phẩm truyền thông dân số

> Đặt file này ở thư mục gốc repo (có thể đổi tên thành `SPEC.md` và tóm tắt vào `CLAUDE.md`). Làm việc theo từng PHASE ở mục 12. Sau mỗi phase: build xanh, test xanh, cập nhật `docs/traceability.md`, commit.

---

## 0. VAI TRÒ & BỐI CẢNH

Bạn là senior full-stack engineer (.NET + React). Hãy xây dựng **BlueDrive**, một hệ thống quản lý, lưu trữ, tìm kiếm, khai thác và chia sẻ sản phẩm truyền thông số (hình ảnh, video, âm thanh, tài liệu văn phòng, infographic, ấn phẩm điện tử). Về trải nghiệm, hệ thống tương tự Google Drive/OneDrive nội bộ.

Hệ thống phục vụ gói thầu "Thuê hệ thống phần mềm lưu trữ sản phẩm truyền thông dân số" của Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số (Cục Dân số, Bộ Y tế). Sản phẩm sẽ được **vận hành thử theo kịch bản nghiệp vụ** và **nghiệm thu theo từng dòng chức năng** (mục 3.2, đánh số 1–84; HSMT không có mục 25). Vì vậy **mọi chức năng trong danh sách mục 6 phải có thật, chạy được và kiểm chứng được**. Không được để stub hay TODO trong code bàn giao.

Ràng buộc quan trọng:
- Thời gian triển khai tại khách hàng tối đa 10 ngày làm việc, nên hệ thống phải **cài đặt được bằng một lệnh `docker compose up -d`** cùng một file `.env`, có seed dữ liệu mẫu và tài khoản quản trị mặc định.
- Máy chủ đích: 01 cloud server **8 core, 16 GB RAM, 520 GB SSD**. Toàn bộ stack phải chạy được trên cấu hình này.
- Môi trường dev của tôi là **Windows (CMD/PowerShell) + Docker Desktop**. Mọi script phải có bản `.ps1`/`.cmd` hoặc chạy được qua `docker compose`, không phụ thuộc bash trên host.
- Giao diện **100% tiếng Việt**, Unicode, font hiển thị tốt trên Windows/Linux/macOS.

---

## 1. TECH STACK (BẮT BUỘC)

**Backend**
- .NET 10 (LTS) và ASP.NET Core Web API. Nếu máy không có SDK 10 thì dùng .NET 8 LTS và ghi chú lại.
- Kiến trúc Clean Architecture: `BlueDrive.Domain`, `BlueDrive.Application`, `BlueDrive.Infrastructure`, `BlueDrive.Api`, `BlueDrive.Worker` (background jobs).
- EF Core + Npgsql (PostgreSQL 16), migrations code-first, naming `snake_case` (EFCore.NamingConventions).
- MediatR (CQRS nhẹ), FluentValidation, Mapster hoặc mapping tay.
- Auth: JWT access token (15 phút) + refresh token (rotation, lưu DB, hash), mật khẩu băm bằng ASP.NET Core Identity `PasswordHasher` (PBKDF2) hoặc Argon2.
- Upload lớn: **tus protocol** (`tusdotnet`) cho upload resumable/chunked (video vài GB).
- Lưu file: interface `IFileStorage` với 2 implementation: `LocalFileStorage` (mặc định, docker volume) và `S3FileStorage` (tương thích S3, bật bằng cấu hình).
- Background jobs: Hangfire (storage PostgreSQL) hoặc Quartz.NET. Dùng cho thumbnail, transcode preview, backup, purge thùng rác, cảnh báo dung lượng, xuất báo cáo lớn.
- Media: SixLabors.ImageSharp (thumbnail ảnh), FFmpeg/FFprobe (thumbnail và metadata video/audio, có sẵn trong image Docker của Worker), PDF thumbnail bằng `pdftoppm` (poppler-utils).
- Xuất Excel: ClosedXML. Xuất PDF: QuestPDF (Community license), font Unicode nhúng (Be Vietnam Pro / Noto Sans).
- Logging: Serilog (console + file + bảng `system_logs`). Health checks.
- Realtime (thông báo, bình luận): SignalR.
- Swagger/OpenAPI có mô tả tiếng Việt.

**Soạn thảo trực tuyến Office**
- **ONLYOFFICE Document Server** (Community, container riêng), tích hợp qua Document Editor API: cấu hình JWT secret và callback URL `/api/office/callback/{fileId}`. Khi lưu, phiên bản mới được ghi lại.
- Hỗ trợ tạo mới và sửa online `.docx`, `.xlsx`, `.pptx`, `.txt`; xem online `.pdf`, `.odt`, `.doc`, `.xls`, `.ppt`.
- Thiết kế interface `IOfficeEditorProvider` để sau này có thể thay bằng Collabora.

**Frontend Web**
- React 18 + TypeScript + Vite.
- UI: **Ant Design v5** với `ConfigProvider locale={viVN}`, dayjs locale `vi`.
- State/data: TanStack Query + Zustand. Router: React Router v6.
- Upload: `tus-js-client` (kéo thả tệp và thư mục, hỗ trợ `webkitdirectory`, hiển thị tiến độ, tạm dừng/tiếp tục).
- Biểu đồ: Recharts (hoặc @ant-design/charts).
- Viewer: ảnh (lightbox, zoom), video/audio (HTML5 player), PDF (pdf.js / react-pdf), Office (ONLYOFFICE iframe).
- i18n: toàn bộ chuỗi gom vào `src/locales/vi.json` (react-i18next), không hard-code chuỗi rải rác.

**Mobile (iOS/Android)**
- **React Native + Expo (TypeScript)**, dùng chung API. Đặt ở thư mục `mobile/`. Chi tiết ở mục 6.V.

**Hạ tầng**
- Docker Compose gồm các service: `postgres`, `api`, `worker`, `web` (nginx serve build React + reverse proxy `/api`, `/hubs`, `/files`, `/tus`), `onlyoffice`, `reverse-proxy` (Caddy, tự cấp TLS Let's Encrypt khi có domain; HTTP khi chạy local).
- Volume: `pg_data`, `file_storage`, `backups`, `onlyoffice_data`, `logs`.
- Giới hạn tài nguyên trong compose cho vừa 16 GB RAM (gợi ý: onlyoffice 4 GB, postgres 3 GB, api 2 GB, worker 2 GB).

---

## 2. CẤU TRÚC REPO

```
bluedrive/
├─ src/
│  ├─ BlueDrive.Domain/
│  ├─ BlueDrive.Application/
│  ├─ BlueDrive.Infrastructure/
│  ├─ BlueDrive.Api/
│  └─ BlueDrive.Worker/
├─ tests/
│  ├─ BlueDrive.UnitTests/
│  └─ BlueDrive.IntegrationTests/   (Testcontainers PostgreSQL)
├─ web/                              (React + Vite)
├─ mobile/                           (Expo React Native)
├─ deploy/
│  ├─ docker-compose.yml
│  ├─ docker-compose.dev.yml
│  ├─ Caddyfile
│  ├─ nginx.conf
│  └─ .env.example
├─ scripts/  (install.ps1, backup-now.ps1, restore.ps1, seed.ps1 + bản .sh)
├─ docs/
│  ├─ traceability.md      (ma trận 84 chức năng ↔ API ↔ màn hình ↔ test)
│  ├─ huong-dan-su-dung.md (tiếng Việt, có ảnh chụp màn hình)
│  ├─ huong-dan-quan-tri.md
│  ├─ huong-dan-cai-dat.md
│  ├─ kich-ban-van-hanh-thu.md
│  ├─ attt-742.md          (đối chiếu QĐ 742/QĐ-BTTTT)
│  └─ data-export-format.md
└─ CLAUDE.md
```

---

## 3. MÔ HÌNH DỮ LIỆU (PostgreSQL)

Mọi bảng có `id uuid` (UUIDv7), `created_at`, `created_by`, `updated_at`, `updated_by` (timestamptz, UTC). Hiển thị giờ theo `Asia/Ho_Chi_Minh`.

**Tổ chức & người dùng**
- `organizations`: đơn vị/phòng ban dạng cây (`parent_id`, `code`, `name`, `type` [don_vi | phong_ban], `address`, `phone`, `email`, `sort_order`, `is_active`, `path` ltree hoặc materialized path).
- `users`: `username` (unique), `email`, `full_name`, `phone`, `organization_id`, `position`, `avatar_file_id`, `password_hash`, `is_active`, `is_locked`, `lockout_end`, `failed_login_count`, `must_change_password`, `last_login_at`, `quota_bytes`, `used_bytes`.
- `user_groups` (nhóm người dùng phục vụ phân quyền) và `user_group_members`.
- `roles` (`code`, `name`, `description`, `is_system`) và `user_roles`.
- `permissions`: danh mục quyền chức năng được seed từ code, dạng `module.action`, ví dụ `org.view`, `org.create`, `user.manage`, `doc.upload`, `doc.edit`, `doc.delete`, `doc.share`, `doc.approve`, `admin.audit.view`, `admin.backup`.
- `role_permissions`.
- `refresh_tokens`, `user_sessions` (`device`, `ip`, `user_agent`, `created_at`, `last_seen_at`, `revoked_at`, `revoked_by`).
- `password_reset_tokens`.

**Danh mục dùng chung**
- `catalogs` (loại danh mục: loại sản phẩm truyền thông, chủ đề, chiến dịch, đối tượng truyền thông, nguồn, năm...) và `catalog_items` (`catalog_id`, `code`, `name`, `parent_id`, `sort_order`, `is_active`). Đây là danh mục động: admin tự thêm loại danh mục mới.
- Seed sẵn loại sản phẩm: Hình ảnh, Video, Âm thanh, Tài liệu, Infographic, Ấn phẩm điện tử.

**Không gian lưu trữ**
- `workspaces` (nhóm tài nguyên): `name`, `description`, `organization_id` (nullable), `owner_id` (trưởng nhóm), `quota_bytes`, `used_bytes`, `is_locked`, `type` [personal | group | organization].
  - Mỗi user có 1 workspace `personal`, tự tạo khi tạo user.
- `workspace_members`: `workspace_id`, `user_id`, `member_role` [owner | manager | editor | uploader | approver | viewer], `added_at`.
- `folders`: `workspace_id`, `parent_id`, `name`, `path`, `is_deleted`, `deleted_at`, `deleted_by`, `original_parent_id`.
- `files`: `workspace_id`, `folder_id`, `name`, `extension`, `mime_type`, `media_type` [image | video | audio | document | spreadsheet | presentation | pdf | infographic | epub | archive | other], `size_bytes`, `current_version_id`, `checksum_sha256`, `catalog_item_ids` (bảng nối `file_catalog_items`), `title`, `description`, `tags text[]`, `approval_status` [draft | pending | approved | rejected], `width`, `height`, `duration_seconds`, `thumbnail_key`, `preview_key`, `is_deleted`, `deleted_at`, `deleted_by`, `original_folder_id`, `search_vector tsvector`.
- `file_versions`: `file_id`, `version_no`, `storage_key`, `size_bytes`, `checksum`, `change_note`, `created_by`, `created_at`, `is_encrypted`.
- `user_favorites` (đánh dấu quan trọng): `user_id`, `item_type`, `item_id`.
- `recent_items`: `user_id`, `item_id`, `action`, `accessed_at`.

**Phân quyền tài nguyên (ACL)**
- `resource_permissions`: `resource_type` [workspace | folder | file], `resource_id`, `principal_type` [user | group | organization | role], `principal_id`, `access_level` [view | download | edit | manage], `inherit` bool, `expires_at`.
- Quyền thư mục được kế thừa xuống con, trừ khi có `break_inheritance`.

**Chia sẻ**
- `shares` (chia sẻ nội bộ): `resource_type`, `resource_id`, `shared_by`, `shared_with_type` [user | group | organization], `shared_with_id`, `access_level`, `message`, `created_at`.
- `share_links` (chia sẻ ra ngoài): `token` (random 32 byte, base64url), `resource_type`, `resource_id`, `allow_download` bool, `allow_view` bool, `password_hash` nullable, `expires_at` nullable, `max_access_count` nullable, `access_count`, `is_revoked`, `require_login` bool.

**Bình luận / thảo luận**
- `comments`: `file_id`, `parent_id` (trả lời), `content`, `mentions uuid[]`, `is_edited`, `is_deleted`.
- `notifications`: `user_id`, `type`, `title`, `body`, `link`, `is_read`.

**Nhật ký & giám sát**
- `audit_logs` (append-only; không có API sửa/xóa): `user_id`, `username`, `action` [login | logout | login_failed | upload | download | view | edit | share | unshare | rename | move | copy | delete | restore | purge | permission_change | config_change | ...], `resource_type`, `resource_id`, `resource_name`, `workspace_id`, `ip`, `user_agent`, `detail jsonb`, `occurred_at`. Đánh index theo `(occurred_at)`, `(user_id, occurred_at)`, `(action, occurred_at)`. Partition theo tháng nếu được.
- `alerts`: `type` [brute_force | quota_exceeded | quota_warning | unusual_download | backup_failed], `severity`, `message`, `related_user_id`, `related_workspace_id`, `is_acknowledged`, `acknowledged_by`.
- `system_settings` (key/value jsonb): chính sách mật khẩu, ngưỡng khóa tài khoản, ngưỡng cảnh báo dung lượng (%), lịch backup, số bản backup giữ lại, thời gian giữ thùng rác, dung lượng mặc định cho cá nhân và nhóm, định dạng file cho phép, kích thước upload tối đa.
- `backup_jobs`: `type` [scheduled | manual], `status`, `started_at`, `finished_at`, `db_dump_key`, `files_archive_key`, `size_bytes`, `error`.
- `support_tickets` (đầu mối hỗ trợ, đáp ứng mục 5.1 chất lượng): `code`, `title`, `content`, `priority` [khan_cap | khong_khan_cap], `status`, `reporter_id`, `assignee_id`, `received_at`, `resolved_at`, `resolution`, và bảng `ticket_events`.
- `change_logs` (ghi nhận thay đổi cấu hình/phiên bản, đáp ứng mục 5.2 chất lượng): `version`, `change_type`, `description`, `applied_by`, `applied_at`.

**Tìm kiếm**
- Bật extension `unaccent`, `pg_trgm`. Cột `search_vector` sinh từ `unaccent(name || title || description || tags)` với trigger. Tìm được cả **có dấu và không dấu** ("dan so" khớp "dân số"). Có index GIN cho search_vector và trigram trên `name`.

---

## 4. BẢO MẬT & AN TOÀN THÔNG TIN (đối chiếu QĐ 742/QĐ-BTTTT, TT 39/2017)

1. **Xác thực**: đăng nhập bằng username/mật khẩu. Chính sách mật khẩu cấu hình được (mặc định tối thiểu 8 ký tự, có chữ hoa, chữ thường, số, ký tự đặc biệt; hết hạn sau N ngày; không trùng 5 mật khẩu gần nhất). Có đổi mật khẩu, quên mật khẩu qua email (token dùng 1 lần, hết hạn sau 30 phút) và admin reset mật khẩu. Bắt buộc đổi mật khẩu ở lần đăng nhập đầu.
2. **Chống brute-force**: sai mật khẩu 5 lần (cấu hình được) thì khóa tạm 15 phút, ghi log `login_failed`, tạo `alert` brute_force và đẩy thông báo realtime cho admin. Có rate-limit (ASP.NET Core RateLimiter) cho `/auth/*`.
3. **Phiên đăng nhập**: quản lý danh sách phiên. User tự đăng xuất được phiên khác. Admin thu hồi phiên hoặc thu hồi toàn bộ quyền truy cập của 1 user (vô hiệu refresh token, đưa access token vào blacklist trong cache đến khi hết hạn). Có timeout khi không hoạt động (cấu hình được).
4. **Phân quyền 2 lớp**:
   - Quyền chức năng (RBAC) qua `[Authorize(Policy = "perm:xxx")]` sinh động từ bảng permissions.
   - Quyền tài nguyên (ACL) qua `IResourceAuthorizationService.CanAsync(user, resource, accessLevel)`. Service này kiểm tra lần lượt: owner, vai trò trong workspace, ACL trực tiếp, ACL kế thừa từ thư mục cha, share, rồi đến workspace bị khóa. Viết unit test đầy đủ cho ma trận này.
5. **Mã hóa khi truyền**: chỉ cho phép HTTPS (Caddy), HSTS, TLS 1.2+.
6. **Mã hóa khi lưu**: mã hóa file ở tầng ứng dụng bằng AES-256-GCM theo từng file (envelope encryption: data key mỗi file được mã hóa bằng master key lấy từ biến môi trường hoặc file key mount vào). Bật/tắt bằng cấu hình `Storage__EncryptAtRest`, mặc định bật. Dữ liệu cá nhân nhạy cảm trong DB (số điện thoại, email) mã hóa bằng ASP.NET Data Protection.
7. **Kiểm tra đầu vào**: validate mọi request (FluentValidation, thông báo lỗi tiếng Việt rõ ràng). Kiểm tra định dạng file bằng **magic bytes** chứ không chỉ dựa vào đuôi file. Chặn file thực thi (`.exe`, `.bat`, `.ps1`, `.sh`, `.js` đứng riêng...). Sanitize tên file. Chống path traversal.
8. **Quét mã độc**: tích hợp **ClamAV** (container `clamav`, tùy chọn bật trong compose). File upload ở trạng thái `scanning`, nếu nhiễm thì cách ly, ghi log và tạo alert.
9. **Header bảo mật**: CSP, X-Frame-Options (cho phép ONLYOFFICE theo domain), X-Content-Type-Options, Referrer-Policy. Cấu hình CORS chặt. Chống CSRF cho cookie nếu có dùng.
10. **Không lộ thông tin**: lỗi trả về theo ProblemDetails, không trả stack trace ở production. Không log mật khẩu hay token.
11. **Audit**: ghi log mọi thao tác ở mục 6 #11. Bảng audit append-only, có cột `hash` chuỗi (hash của bản ghi trước + bản ghi hiện tại) để phát hiện chỉnh sửa trái phép.
12. Tạo `docs/attt-742.md` đối chiếu từng yêu cầu ATTT cơ bản cho phần mềm nội bộ với cách hệ thống đáp ứng.

---

## 5. YÊU CẦU PHI CHỨC NĂNG

- **Hiệu năng**: mọi API giao dịch thông thường phải dưới 2 giây (yêu cầu HSMT là dưới 10 giây). Danh sách lớn phải phân trang phía server. Báo cáo hoặc xuất dữ liệu lớn chạy nền bằng job; UI hiển thị thông báo "Hệ thống đang xử lý báo cáo, bạn sẽ nhận thông báo khi hoàn tất" kèm tiến độ, xong thì gửi notification và link tải.
- **Streaming**: tải xuống và xem video/audio hỗ trợ HTTP Range (tua video). File lớn không load vào RAM.
- **Thumbnail/preview** sinh bất đồng bộ: ảnh (nhiều kích thước), video (frame tại giây thứ 3 + metadata), PDF (trang 1), Office (trang 1 qua ONLYOFFICE conversion API).
- **Tương thích trình duyệt**: Chrome, Edge, Firefox, Safari bản đang được hỗ trợ. Giao diện responsive.
- **Đa thiết bị**: web dùng tốt trên tablet và điện thoại, kèm app mobile.
- **Định dạng hỗ trợ tối thiểu**: ảnh (jpg, jpeg, png, gif, webp, svg, bmp, tiff, heic), video (mp4, mov, avi, mkv, webm), âm thanh (mp3, wav, aac, m4a, ogg, flac), tài liệu (doc/x, xls/x, ppt/x, pdf, txt, odt, ods, odp, rtf), ấn phẩm điện tử (pdf, epub), thiết kế (psd, ai, indd: chỉ lưu và tải, không preview), nén (zip, rar, 7z).
- **Bàn giao dữ liệu** (mục 4.2 chất lượng): có chức năng admin **"Xuất toàn bộ dữ liệu"**, tạo gói gồm cây thư mục + file gốc (đã giải mã) + `metadata.json`/`metadata.csv` (thuộc tính, phiên bản, phân quyền, nhật ký) + `README` mô tả cấu trúc. Mô tả định dạng trong `docs/data-export-format.md`.
- **Xóa dữ liệu khi kết thúc hợp đồng** (mục 3.6): script `scripts/secure-wipe.ps1` xóa sạch volume và DB, xuất biên bản xóa (file log có thời gian và checksum).

---

## 6. CHỨC NĂNG CHI TIẾT (ĐỐI CHIẾU 1–1 VỚI HSMT MỤC 3.2)

> Mỗi mục dưới đây phải có: API endpoint, màn hình/thành phần UI, permission tương ứng, ghi audit log (nếu là thao tác), và ít nhất 1 test. Ghi tất cả vào `docs/traceability.md` theo dạng bảng: `STT HSMT | Chức năng | API | Màn hình | Permission | Test`.

### I. QUẢN TRỊ HỆ THỐNG

**Quản lý cơ cấu tổ chức (#1–3)**
- #1: Thêm, sửa, xóa đơn vị/phòng ban dạng cây (kéo thả để đổi cha và thứ tự). Không cho xóa nếu còn user hoặc đơn vị con; nếu vẫn muốn xóa thì cho phép "ngừng hoạt động" thay vì xóa cứng. Validate trùng mã.
- #2: Xem danh sách dạng cây và dạng bảng; tra cứu theo mã, tên, trạng thái, đơn vị cha; tìm không dấu.
- #3: In (bản in CSS print-friendly) và xuất Excel, PDF theo bộ lọc hiện tại. Có tiêu đề, ngày xuất, người xuất.

**Quản lý người dùng, nhóm người dùng (#4–6)**
- #4: CRUD tài khoản người dùng (họ tên, username, email, điện thoại, đơn vị, chức vụ, dung lượng, trạng thái) và CRUD nhóm người dùng (tên, mô tả, thành viên). Có thêm: khóa/mở khóa, reset mật khẩu, import user hàng loạt từ Excel theo template tải về (validate từng dòng, báo lỗi theo dòng).
- #5: Gán user vào đơn vị/phòng ban và một hoặc nhiều vai trò. Gán hàng loạt.
- #6: Tra cứu, lọc theo đơn vị, vai trò, trạng thái, nhóm; xuất Excel/PDF.

**Thiết lập vai trò, phân quyền chức năng (#7–9)**
- #7: CRUD vai trò. Seed 4 vai trò hệ thống không xóa được: **Quản trị hệ thống**, **Biên tập/Tải lên**, **Phê duyệt**, **Chỉ xem**.
- #8: Màn hình ma trận phân quyền: dòng là chức năng (nhóm theo module), cột là hành động (Xem, Tải lên, Chỉnh sửa, Xóa, Chia sẻ, Phê duyệt, Quản trị), mỗi ô là checkbox. Có chọn cả dòng/cột. Lưu thì ghi audit `permission_change`.
- #9: Màn hình "Bảng đối chiếu" hiển thị toàn bộ chức năng × toàn bộ vai trò trên một bảng (chỉ đọc), có lọc và xuất Excel, để rà soát khi phân quyền.

**Danh mục dùng chung (#10)**
- CRUD loại danh mục và mục danh mục (có phân cấp, sắp xếp, bật/tắt). Danh mục được dùng làm metadata gắn cho tư liệu (loại sản phẩm, chủ đề, chiến dịch, năm, đối tượng...) và làm bộ lọc khi tìm kiếm. Có import/export Excel.

**Giám sát hoạt động hệ thống (#11–15)**
- #11: Ghi log đăng nhập, đăng xuất, đăng nhập sai, upload, download, xem, chỉnh sửa, chia sẻ, hủy chia sẻ, đổi tên, di chuyển, sao chép, xóa, khôi phục, xóa vĩnh viễn, đổi quyền. Ghi qua `IAuditLogger` gọi từ Application layer và qua middleware/pipeline behavior để không bỏ sót.
- #12: Màn hình nhật ký: lọc theo người dùng, đơn vị, khoảng thời gian, hành động, loại tài nguyên, từ khóa tên tư liệu, IP; phân trang server; xem chi tiết JSON.
- #13: Thống kê dung lượng đã dùng/còn lại theo **tài khoản** và theo **đơn vị**, kèm theo nhóm/workspace. Có bảng + biểu đồ, sắp xếp theo mức dùng, cảnh báo màu khi vượt 80%/100%.
- #14: Cảnh báo bất thường:
  - Đăng nhập sai nhiều lần (ngưỡng cấu hình được).
  - Dung lượng cá nhân/nhóm vượt ngưỡng cảnh báo (mặc định 90%) hoặc vượt hạn mức.
  - Tải xuống bất thường (ví dụ trên 200 file/giờ, cấu hình được).
  - Backup thất bại.
  - Cảnh báo hiện ở chuông thông báo realtime (SignalR), trang "Cảnh báo" (xác nhận đã xử lý) và gửi email cho admin (SMTP cấu hình được).
- #15: In và xuất báo cáo giám sát (nhật ký, dung lượng, cảnh báo) ra Excel/PDF theo bộ lọc.

**Xác thực & kiểm soát truy cập (#16–17)**
- #16: Đăng nhập, đổi mật khẩu, quên/khôi phục mật khẩu (xem mục 4).
- #17: Màn hình "Phiên đăng nhập" (của tôi và của toàn hệ thống cho admin): thiết bị, IP, thời gian. Có các nút: thu hồi phiên, thu hồi toàn bộ phiên của user, khóa tài khoản.

### II. HỆ THỐNG LƯU TRỮ SẢN PHẨM TRUYỀN THÔNG

**Quản lý tư liệu theo cá nhân (#18–27)** (trong workspace personal, menu trái "Tài liệu của tôi")
- #18: "Tài liệu của tôi" và "Gần đây" (lấy từ `recent_items`, sắp theo thời gian truy cập/sửa).
- #19: "Được chia sẻ với tôi" (lọc theo người chia sẻ, thời gian) và xem tệp đính kèm trong bình luận.
- #20: Tìm kiếm và lọc theo từ khóa (có dấu/không dấu), loại tài liệu (media_type), khoảng thời gian, danh mục, người tạo.
- #21: Nút "Mới" để tạo thư mục, tài liệu Word, bảng tính Excel, trình chiếu PowerPoint, văn bản .txt. File được tạo từ template trống rồi mở ngay trong ONLYOFFICE.
- #22: Tải lên tệp và tải lên thư mục (giữ nguyên cấu trúc cây), kéo thả, resumable, kiểm tra quota trước khi upload.
- #23: Mở (viewer theo loại), chia sẻ (dialog chia sẻ), tải xuống (file đơn; nhiều file hoặc thư mục thì nén zip streaming), đánh dấu quan trọng (ngôi sao, có mục "Quan trọng" riêng).
- #24: Đổi tên, sao chép, cắt/dán, "Sao chép tới..." (dialog chọn thư mục đích, kể cả sang workspace nhóm nếu có quyền), xem thông tin (panel phải: kích thước, loại, người tạo, ngày tạo/sửa, vị trí, danh mục, mô tả, tags, phiên bản, ai có quyền truy cập), xóa (vào thùng rác).
- #26: Quản lý phiên bản: danh sách phiên bản (số, người sửa, thời gian, kích thước, ghi chú), tải lên phiên bản mới, xem/tải phiên bản cũ, khôi phục phiên bản cũ thành phiên bản hiện tại (tạo version mới). Mỗi lần lưu từ ONLYOFFICE tạo 1 version.
- #27: Tab "Thảo luận" trong màn hình chi tiết tài liệu: bình luận, trả lời, @nhắc tên, đính kèm tệp, sửa/xóa bình luận của mình. Realtime qua SignalR và gửi thông báo cho người được nhắc hoặc chủ sở hữu. Chỉ người có quyền xem tài liệu mới thấy và bình luận được.

**Quản lý tư liệu theo nhóm/đơn vị (#28–38)**
- #28: Tạo và quản lý workspace nhóm/đơn vị cùng thư mục dùng chung.
- #29: Màn hình "Tổng hợp tư liệu nhóm": xem toàn bộ tư liệu do các thành viên tải lên, lọc theo thành viên, thống kê số lượng và dung lượng theo thành viên.
- #30: Phân quyền tư liệu (thư mục/tệp) cho từng thành viên trong nhóm.
- #31: Lịch sử chỉnh sửa theo từng thành viên: timeline thao tác trên tư liệu nhóm, lọc theo thành viên.
- #32–38: Đầy đủ các chức năng như #20–#27 nhưng trong ngữ cảnh workspace nhóm. **Tái sử dụng chung component/API**, chỉ khác tham số workspace.

**Tải lên, biên tập tư liệu (#39–43)**
- #39: Upload từng tệp, hỗ trợ đầy đủ định dạng ở mục 5. Chọn/kéo thả tệp là tải lên ngay (không hiện hộp thoại nhập thông tin); metadata (tiêu đề, mô tả, danh mục, tags) nhập sau trong khung "Thông tin" của tệp, hoặc áp dụng hàng loạt cho nhiều tệp đã chọn ("Cập nhật thông tin hàng loạt").
- #40: Upload cả thư mục.
- #41: Tạo mới văn bản/bảng tính/trình chiếu.
- #42: Chỉnh sửa online qua ONLYOFFICE, cho phép nhiều người đồng soạn thảo.
- #43: Quản lý version (như #26).
- Luồng phê duyệt (để có quyền "Phê duyệt"): tư liệu trong thư mục được cấu hình "cần phê duyệt" khi upload sẽ ở trạng thái `pending`. Người có vai trò Phê duyệt duyệt hoặc từ chối (kèm lý do). Chỉ tư liệu `approved` mới hiện với vai trò Chỉ xem.

**Thao tác trên thư mục/tệp (#44–49)**
- #44–48: Xem/mở, chia sẻ/tải xuống, đổi tên/sao chép/cắt-di chuyển, đánh dấu quan trọng/xem thông tin, xóa. Hỗ trợ chọn nhiều, menu chuột phải, thanh công cụ, phím tắt (Del, F2, Ctrl+C/X/V), kéo thả để di chuyển. Có chế độ xem lưới (thumbnail) và danh sách.
- #49: Chia sẻ:
  - Nội bộ cho user, nhóm hoặc đơn vị với quyền Xem / Xem + Tải xuống / Chỉnh sửa.
  - Ra ngoài qua link: bật/tắt cho phép tải xuống, đặt mật khẩu, ngày hết hạn, giới hạn số lượt truy cập, yêu cầu đăng nhập hay không, thu hồi link, copy link, sinh QR code.
  - Trang public `/s/{token}` xem được ảnh, video, PDF và Office (chế độ chỉ xem). Ghi log mọi lượt truy cập link.

**Tra cứu, tìm kiếm, khai thác (#50–53)**
- #50–51: Trang "Tìm kiếm nâng cao" toàn hệ thống (chỉ trả kết quả user có quyền): từ khóa, loại tư liệu, danh mục, **đơn vị đăng tải**, khoảng thời gian, người đăng, định dạng, dung lượng. Kết quả có highlight và có thể lưu bộ lọc.
- #52: Truy cập từ xa qua Internet trên mọi thiết bị (web responsive + app mobile).
- #53: Chia sẻ được ngay khi truy cập từ xa (web mobile và app).

**Phân quyền theo vai trò (#54–56)**
- #54: 4 vai trò: quản trị, biên tập/tải lên, phê duyệt, chỉ xem (đã seed, có thể chỉnh).
- #55: Phân quyền truy cập tư liệu theo đơn vị hoặc nhóm.
- #56: Phân quyền theo từng thư mục/tệp (dialog "Quản lý quyền truy cập": danh sách người/nhóm/đơn vị + mức quyền + kế thừa / ngắt kế thừa).

**Cấu hình (#57–62)** (màn hình "Cấu hình nhóm tài nguyên" và "Cấu hình dung lượng")
- #57: Thêm/xóa thành viên nhóm tài nguyên.
- #58: Phân quyền sử dụng nhóm (vai trò thành viên) và **khóa nhóm** (khóa thì chỉ xem, không ghi được).
- #59: Cấu hình dung lượng cá nhân (từng user, theo đơn vị, giá trị mặc định).
- #60: Cấu hình dung lượng nhóm.
- #61: Thùng rác theo nhóm (và thùng rác cá nhân): ai xóa, khi nào, vị trí gốc.
- #62: Khôi phục về vị trí gốc (nếu thư mục cha đã mất thì khôi phục về gốc) và xóa vĩnh viễn (chọn nhiều, "Dọn sạch thùng rác"). Job tự xóa vĩnh viễn sau N ngày (mặc định 30, cấu hình được). Dung lượng chỉ được trả lại khi đã xóa vĩnh viễn.

### III. DASHBOARD CÁ NHÂN (#63–66) — trang chủ sau đăng nhập
- #63: Thẻ dung lượng: đã dùng / tổng được cấp (progress bar + %), cùng các liên kết nhanh: Tài liệu của tôi, Được chia sẻ, Quan trọng, Thùng rác, Tải lên.
- #64: Biểu đồ tròn phân bổ dung lượng theo loại file (Ảnh, Video, Âm thanh, Tài liệu, Bảng tính, Trình chiếu, PDF, Khác).
- #65: Biểu đồ cột xếp hạng top 10 thư mục cá nhân theo dung lượng; bấm vào cột thì mở thư mục.
- #66: Bảng tài liệu gần đây: tên, người sở hữu, dung lượng, thời gian sửa gần nhất.

### IV. DASHBOARD NHÓM (#67–70)
- #67: Thẻ: số nhóm đang là thành viên, số nhóm đang làm trưởng nhóm, kèm liên kết nhanh tới từng nhóm.
- #68: Biểu đồ **cột ngang** dung lượng sử dụng của từng nhóm (so với hạn mức).
- #69: Biểu đồ tỷ lệ % loại file trong từng nhóm (stacked bar 100% hoặc chọn nhóm rồi hiện pie).
- #70: Bảng tài liệu nhóm gần đây: tên tài liệu, tên nhóm, dung lượng, thời gian sửa gần nhất.
- Thêm: Dashboard quản trị (toàn hệ thống: tổng dung lượng, số user, số file theo loại, upload theo ngày 30 ngày gần nhất, top đơn vị theo dung lượng, cảnh báo chưa xử lý). Dữ liệu dashboard được cache 1–5 phút.

### V. ỨNG DỤNG MOBILE iOS/ANDROID (#71–84) — `mobile/` Expo
- Màn hình: Đăng nhập (lưu token trong SecureStore; tùy chọn mở khóa bằng sinh trắc học), Trang chủ (dung lượng + gần đây), Tài liệu của tôi (#71), Được chia sẻ (#72), Gần đây (#73), Nhóm (#74), Tìm kiếm (#75), Cây thư mục (#82), Chi tiết tệp (#83: thông tin + preview), Cài đặt/Đăng xuất.
- Thao tác: Mở tài liệu (#76: ảnh, video, audio, PDF native; Office mở qua ONLYOFFICE mobile view trong WebView), Đổi tên (#77), Đánh dấu quan trọng (#78), Sao chép (#79) và Di chuyển (#80) với picker chọn thư mục, Xóa (#81), Tải xuống về thiết bị (#84, qua expo-file-system + chia sẻ hoặc lưu vào Files/Gallery).
- Bổ sung: kéo để làm mới, phân trang vô hạn, chia sẻ link, upload ảnh/video từ thư viện hoặc camera, giao diện tiếng Việt, dark mode.
- Cấu hình `app.json`/`eas.json` để build APK/AAB và iOS qua EAS Build. Ghi rõ hướng dẫn build trong `mobile/README.md`.

---

## 7. QUẢN LÝ & HỖ TRỢ DỊCH VỤ (mục 5 của bảng chất lượng)
- **Tiếp nhận hỗ trợ/sự cố**: nút "Hỗ trợ" trong app để user gửi yêu cầu (mức độ, mô tả, ảnh chụp). Màn hình quản trị ticket: phân loại khẩn cấp / không khẩn cấp, giao người xử lý, trạng thái, thời điểm tiếp nhận và xử lý. **Tự tính SLA**: khẩn cấp 4 giờ, không khẩn cấp 24 giờ; tô màu ticket quá hạn. Báo cáo SLA xuất Excel/PDF.
- **Giám sát & thay đổi**: màn hình "Nhật ký thay đổi hệ thống" ghi version đang chạy (lấy từ assembly + git sha), lịch sử nâng cấp, thay đổi cấu hình (tự ghi khi sửa `system_settings`).
- Trang `/health` và trang trạng thái nội bộ: DB, storage, ONLYOFFICE, dung lượng đĩa còn lại, job backup gần nhất.

---

## 8. SAO LƯU & PHỤC HỒI
- Màn hình admin "Sao lưu":
  - Cấu hình lịch tự động (hằng ngày theo giờ, hằng tuần, tùy chỉnh cron), số bản giữ lại, đích lưu (volume local và/hoặc S3 ngoài).
  - Nút "Sao lưu ngay" (đột xuất).
  - Danh sách bản sao lưu: trạng thái, dung lượng, tải xuống.
- Nội dung sao lưu: `pg_dump` (dữ liệu thuộc tính) **và** file số hóa (incremental theo storage_key; full mỗi tuần), có kiểm tra checksum.
- Phục hồi: script `scripts/restore.ps1 <backup-id>` cùng tài liệu từng bước. Có test phục hồi trong môi trường dev.
- Backup thất bại thì tạo alert và gửi email.

---

## 9. API — QUY ƯỚC
- REST, prefix `/api/v1`. Phân trang `?page=&pageSize=&sort=&q=`, response `{ items, total, page, pageSize }`.
- Nhóm endpoint: `auth`, `organizations`, `users`, `user-groups`, `roles`, `permissions`, `catalogs`, `workspaces`, `folders`, `files`, `versions`, `shares`, `share-links`, `public/s/{token}`, `comments`, `favorites`, `recent`, `search`, `trash`, `audit-logs`, `alerts`, `storage-stats`, `dashboards/personal|group|admin`, `reports/export`, `settings`, `backups`, `sessions`, `tickets`, `office` (config + callback), `tus` (upload).
- Lỗi theo ProblemDetails, `title`/`detail` tiếng Việt.
- File download qua endpoint có kiểm tra quyền + HTTP Range. Không bao giờ expose đường dẫn storage thật.

---

## 10. GIAO DIỆN WEB — BỐ CỤC
- Layout: header (logo, ô tìm kiếm toàn cục, nút Tải lên/Mới, chuông thông báo, avatar menu), sider trái (Trang chủ, Tài liệu của tôi, Được chia sẻ, Gần đây, Quan trọng, Nhóm của tôi [danh sách workspace], Thùng rác, phần Quản trị chỉ hiện theo quyền, thanh dung lượng ở cuối), vùng nội dung có breadcrumb.
- File explorer: chuyển lưới/danh sách, sắp xếp, chọn nhiều, context menu, kéo thả upload, panel thông tin bên phải có thể đóng/mở, hàng đợi upload góc phải dưới.
- Theme màu chủ đạo xanh dương nhà nước (gợi ý `#1677ff`/`#0b5394`), logo và tên đơn vị cấu hình được trong settings.
- Mọi thông báo, xác nhận, lỗi validate đều bằng tiếng Việt. Empty state có hướng dẫn.
- Bản in (#3, #15) dùng trang print riêng có quốc hiệu/tiêu đề tùy chọn.

---

## 11. SEED, DEMO & TÀI LIỆU
- Seed: tài khoản `admin` (mật khẩu lấy từ `.env`, bắt buộc đổi lần đầu); 4 vai trò; toàn bộ permission; danh mục mẫu; cơ cấu tổ chức mẫu (Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số với các phòng); 5 user mẫu; 2 workspace nhóm; vài file mẫu đủ các loại (chỉ khi `SEED_DEMO=true`).
- `docs/kich-ban-van-hanh-thu.md`: kịch bản kiểm thử nghiệp vụ đi lần lượt 84 chức năng (bước, dữ liệu, kết quả mong đợi, cột Đạt/Không đạt) để chủ đầu tư dùng khi vận hành thử.
- `docs/huong-dan-su-dung.md`, `docs/huong-dan-quan-tri.md`, `docs/huong-dan-cai-dat.md`: tiếng Việt. Dùng Playwright chụp ảnh màn hình tự động để chèn vào tài liệu.

---

## 12. KẾ HOẠCH THỰC HIỆN THEO PHASE

Làm tuần tự. Hết mỗi phase phải chạy `dotnet build`, `dotnet test`, `npm run build`, `npm run lint`, `docker compose up` thành công, rồi commit với message `phase-N: ...`.

1. **Phase 1 — Nền tảng**: solution, cấu trúc repo, docker-compose (postgres, api, web, onlyoffice, caddy), EF Core + migration đầu, Serilog, health check, Swagger, React skeleton (Vite + AntD + router + layout + i18n vi), CI script.
2. **Phase 2 — Auth & Quản trị**: đăng nhập/refresh/đăng xuất, chính sách mật khẩu, lockout, quên mật khẩu, phiên đăng nhập, RBAC động, tổ chức, user, nhóm user, vai trò, ma trận phân quyền, danh mục dùng chung, import/export Excel/PDF (#1–10, #16–17).
3. **Phase 3 — Lưu trữ lõi**: IFileStorage + mã hóa at-rest, tus upload (file + thư mục), workspace cá nhân/nhóm, folder/file CRUD, copy/move/rename, favorites, recent, download (zip streaming, Range), quota, thùng rác (#18–24, #28, #39–40, #44–48, #57–62).
4. **Phase 4 — ACL, chia sẻ, phê duyệt**: IResourceAuthorizationService, kế thừa quyền, share nội bộ, share link public, phê duyệt (#30, #49, #53–56) + test ma trận quyền.
5. **Phase 5 — Office, phiên bản, preview, bình luận**: ONLYOFFICE tạo/sửa/đồng soạn thảo, versioning, worker thumbnail/preview (ImageSharp, FFmpeg, poppler), viewer, bình luận realtime + thông báo (#21, #26–27, #29, #31, #41–43).
6. **Phase 6 — Tìm kiếm**: full-text tiếng Việt không dấu, tìm kiếm nâng cao, lưu bộ lọc (#20, #32, #50–52).
7. **Phase 7 — Giám sát & báo cáo**: audit log đầy đủ + hash chain, màn hình nhật ký, thống kê dung lượng, cảnh báo (brute force, quota, tải bất thường), báo cáo nền + export (#11–15).
8. **Phase 8 — Dashboard**: cá nhân, nhóm, quản trị (#63–70).
9. **Phase 9 — Vận hành**: backup/restore, ticket hỗ trợ + SLA, nhật ký thay đổi, ClamAV, xuất toàn bộ dữ liệu, secure wipe, security headers, rate limit, `docs/attt-742.md`.
10. **Phase 10 — Mobile**: app Expo đầy đủ #71–84 + upload từ camera/thư viện.
11. **Phase 11 — Hoàn thiện & nghiệm thu**: Playwright E2E cho các luồng chính, load test nhẹ (k6: 50 user đồng thời, p95 dưới 2 giây cho danh sách/tìm kiếm), hoàn thiện `docs/traceability.md` (đủ 84 dòng, không dòng nào trống), tài liệu hướng dẫn, kịch bản vận hành thử, `scripts/install.ps1` cài trọn gói trên server sạch.

---

## 13. TIÊU CHÍ HOÀN THÀNH (DEFINITION OF DONE)
- [ ] 84/84 chức năng HSMT có trong `docs/traceability.md` với API, màn hình, permission, test.
- [ ] `docker compose up -d` trên máy sạch: hệ thống chạy, login được bằng admin, tạo/sửa Word online được.
- [ ] Upload video 2 GB resumable thành công; tua video được.
- [ ] Tìm "dan so" ra tài liệu "Dân số".
- [ ] Sai mật khẩu 5 lần thì tài khoản bị khóa và có cảnh báo realtime.
- [ ] Mọi thao tác ở #11 xuất hiện trong nhật ký và lọc được.
- [ ] Backup đột xuất + restore thành công.
- [ ] Xuất toàn bộ dữ liệu ra gói đọc được, có metadata.
- [ ] App mobile build được APK và chạy đủ #71–84.
- [ ] Toàn bộ UI, thông báo, tài liệu bằng tiếng Việt.
- [ ] Không còn TODO/stub; test xanh; không có secret hard-code (chỉ đọc từ `.env`).

**Bắt đầu với Phase 1. Trước khi code, hãy tóm tắt lại kế hoạch Phase 1 và liệt kê các giả định của bạn, rồi tiến hành.**
