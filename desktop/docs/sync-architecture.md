# BlueDrive Desktop — kiến trúc đồng bộ

Tài liệu này mô tả phần **máy chủ** (Sync-Phase 1) mà ứng dụng BlueDrive Desktop dựa vào. Các phần phía máy khách (engine,
Cloud Files API, giao diện) được bổ sung ở các phase sau, kèm `sync-algorithm.md` (bảng quyết định đối soát 3 chiều).

## 1. Tổng quan

```
BlueDrive Desktop ──HTTPS──► /api/v1/connect/token        (PKCE, phiên thiết bị)
       │            ──HTTPS──► /api/v1/sync/*             (roots, snapshot, changes, items, content, batch, policy)
       │            ──HTTPS──► /api/v1/files|folders|items (thao tác thường + If-Match)
       │            ──HTTPS──► /tus                        (tải lên tiếp tục được, metadata đồng bộ)
       └──WebSocket──► /hubs/sync                          (changes_available, device_status)

API ── SaveChanges ──► ChangeJournalInterceptor ──► change_journal (cùng transaction)
Worker ─ SaveChanges ─┘                                    ▲
SyncChangeBroadcaster (API, 1 giây/lần) ───────────────────┘──► SignalR nhóm "ws:{workspace}"
```

## 2. Nhật ký thay đổi (`change_journal`)

| Cột | Ý nghĩa |
|---|---|
| `seq` (identity, tăng đơn điệu) | Vị trí toàn cục; client lưu làm `cursor` |
| `workspace_id`, `item_id`, `item_type` (`file`/`folder`/`workspace`) | Mục bị thay đổi |
| `op` | `create`, `update_content`, `update_meta`, `rename`, `move`, `delete`, `restore`, `purge`, `permission_changed` |
| `parent_id`, `name`, `path`, `version_id`, `revision`, `size_bytes`, `content_hash` | Trạng thái **sau** thay đổi |
| `modified_by`, `device_id`, `modified_at`, `occurred_at` | Ai / thiết bị nào thay đổi (client bỏ qua thay đổi của chính mình để tránh vòng lặp) |

- Ghi bởi `ChangeJournalInterceptor` trong **cùng `SaveChanges`** với nghiệp vụ, nên không thể lệch. Thay đổi do Worker ghi
  (ảnh thu nhỏ, trạng thái quét mã độc) **không** được ghi và **không** tăng `revision`.
- Thay đổi quyền (`shares`, `resource_permissions`, `workspace_members`, cờ `break_inheritance`) ghi `permission_changed`.
- Thao tác hàng loạt bằng `ExecuteDelete` (xóa vĩnh viễn trong thùng rác) tự ghi dòng `purge`.
- Job `sync-journal-retention` (01:45 hằng ngày) xóa dòng cũ hơn `sync.journal_retention_days` (mặc định 90). Client có cursor
  cũ hơn mốc này nhận **410 Gone** và phải đồng bộ lại toàn bộ.

## 3. ETag và điều kiện `If-Match`

- Mỗi tệp/thư mục có cột `revision`, tăng khi có thay đổi được ghi journal. ETag = `"<revision>"`.
- Các API sẵn có `PUT /files/{id}/name`, `PUT /folders/{id}`, `POST /items/move`, `POST /items/delete` nhận header
  `If-Match` (một mục): lệch thì **412** kèm `current` (metadata hiện tại); thành công trả header `ETag` mới. Web/mobile không
  gửi header nên không bị ảnh hưởng.
- Tải lên (tus metadata hoặc form multipart): `base_etag` (412 nếu bản server đã đổi), `content_hash` (SHA-256 hex, kiểm tra
  sau khi ghép chunk → 422 nếu lệch), `conflict=fail` (409 khi trùng tên thay vì tự đổi thành `Tên (1).ext`),
  `workspace_id`/`parent_id`/`target_file_id` là bí danh của các khóa cũ.

## 4. API `/api/v1/sync`

| Endpoint | Mô tả |
|---|---|
| `POST /sync/devices` | Cập nhật đăng ký của thiết bị đang gọi (tên máy, hệ điều hành, phiên bản) |
| `GET /sync/devices/current?appVersion=` | Trạng thái thiết bị: `active` / `revoked` / `wipe_pending` / `wiped`, `updateRequired`, lệnh xóa từ xa đã ký |
| `POST /sync/devices/current/wiped` | Báo đã xóa xong dữ liệu theo lệnh |
| `GET /sync/policy` | Chính sách: bật/tắt, kích thước tối đa, đuôi tệp bị chặn, nhóm chỉ trực tuyến, phiên bản tối thiểu, xem-chỉ-đọc dạng liên kết, chu kỳ poll |
| `GET /sync/roots` | "Tài liệu của tôi" (`ws:{id}`, kind `personal`), mỗi nhóm/đơn vị (`ws:{id}`), "Được chia sẻ với tôi" (`shared`) |
| `GET /sync/snapshot?root_id=&page_token=&folder_id=` | Toàn bộ cây (1000 mục/trang, thư mục cha trước con, rồi tệp), `cursor` = vị trí journal lúc bắt đầu; `folder_id` để liệt kê lại một nhánh sau khi đổi quyền |
| `GET /sync/changes?cursor=&limit=500` | `{ changes[], nextCursor, hasMore }`; chỉ thay đổi người dùng được thấy, kèm metadata hiện tại; mất quyền → `access_revoked`; cursor quá cũ → 410 |
| `GET /sync/items/{id}` | Metadata một mục (header `ETag`) |
| `GET /sync/items/{id}/content?version=` | Chuyển hướng tới URL ký ngắn hạn, hỗ trợ **HTTP Range**; áp dụng chính sách kích thước/đuôi tệp; ghi nhật ký Tải xuống |
| `GET /sync/items/{id}/web-url` | Địa chỉ mở trên web (menu "Xem trên web") |
| `POST /sync/items/{id}/share-link` | Tạo liên kết chia sẻ (mật khẩu, hạn dùng, yêu cầu đăng nhập) — menu "Chia sẻ… / Sao chép liên kết" |
| `POST /sync/batch` | Tối đa 500 thao tác `create_folder` (`parent_ref` tham chiếu thư mục tạo trong lô), `move`, `rename`, `delete` (có `if_match`); kết quả theo từng thao tác (200/412/4xx) |

Mục đồng bộ (`SyncItemDto`): `id`, `type`, `rootId`, `workspaceId`, `parentId` (null = cấp gốc), `name`, `eTag`, `revision`,
`versionId`, `size`, `hash` (SHA-256), `modifiedAt`, `canDownload`, `canEdit`.

**Quyền**: `WorkspaceAccessMap` tính quyền hiệu lực cho toàn bộ cây trong bộ nhớ (thành viên, ACL kế thừa, chia sẻ, ngắt kế
thừa, quy trình phê duyệt) theo cùng quy tắc với `ResourceAuthorizationService`, để một cây 100.000 mục chỉ cần vài truy vấn.
Tư liệu chỉ có quyền **Xem** có `canDownload = false`: không tải nội dung xuống máy (endpoint content trả 403); client hiện
liên kết "Mở trên web" hoặc ẩn đi theo `viewOnlyAsLink`.

Gốc "Được chia sẻ với tôi" gồm các mục trong nhóm/không gian **không** thuộc thư mục gốc của người dùng nhưng được chia sẻ hoặc
phân quyền trực tiếp; mục có thư mục cha không được thấy nằm ở cấp gốc (`parentId = null`).

## 5. Thông báo đẩy `/hubs/sync`

- Kết nối (Bearer token, hoặc `?access_token=` cho WebSocket) tham gia nhóm `ws:{id}` cho mọi workspace đang đồng bộ và
  `device:{id}`. Gọi `Resubscribe()` sau khi nhận `permission_changed`.
- `changes_available { workspaceId, latestSeq }`: do `SyncChangeBroadcaster` đọc journal mỗi giây (gom cả thay đổi do Worker
  ghi), tức gộp/debounce 1 giây; client gọi `/sync/changes`. Không có kết nối thì poll theo `pollSeconds` (mặc định 30).
- `device_status { deviceId, status }` khi thiết bị bị thu hồi hoặc có lệnh xóa từ xa.

## 6. Thiết bị, đăng nhập, chính sách

- **Đăng nhập (OAuth 2.0 Authorization Code + PKCE, RFC 7636/8252)**: ứng dụng mở trình duyệt hệ thống tới
  `/connect/authorize?client_id=bluedrive-desktop&redirect_uri=http://127.0.0.1:{port}/callback&code_challenge=…&code_challenge_method=S256&state=…&device_name=…`.
  Người dùng đăng nhập web BlueDrive và bấm **Cho phép**; trình duyệt quay về địa chỉ loopback với `code` (một lần, 2 phút).
  Ứng dụng gọi `POST /api/v1/connect/token` (`grant_type=authorization_code`, `code_verifier`, `device_name`, `device_os`,
  `app_version`, `machine_id`) và nhận `access_token`, `refresh_token` (mặc định 90 ngày, `sync.device_refresh_days`),
  `device_id`, `device_secret` (chỉ trả một lần, lưu DPAPI). Làm mới: `grant_type=refresh_token`.
  *Giả định*: tự cài đặt luồng PKCE trên hệ JWT/phiên sẵn có thay vì OpenIddict, để giữ nguyên thu hồi phiên, audit và chuỗi
  băm nhật ký; endpoint tương đương `/connect/authorize` và `/connect/token` của spec.
- **Phiên thiết bị** (`user_sessions.device_id`): token có claim `did`, **không** bị đăng xuất khi không hoạt động (timeout rảnh
  của web), bị thu hồi cùng thiết bị. Mọi thao tác ghi nhật ký kèm `deviceId` và `source = desktop_sync`.
- **Bảng `devices`**: tên máy, hệ điều hành, phiên bản ứng dụng, băm machine id, lần thấy cuối, IP cuối, trạng thái
  `active` / `revoked` / `wipe_pending` / `wiped`. Đăng nhập lại trên cùng máy dùng lại bản ghi.
- **Thu hồi** (trang "Thiết bị đã kết nối" của người dùng hoặc quản trị `device.admin`): thu hồi mọi phiên của thiết bị.
- **Xóa dữ liệu từ xa**: trạng thái `wipe_pending`; ứng dụng nhận lệnh qua `/sync/devices/current` hoặc `device_status`, kiểm tra
  chữ ký `HMAC-SHA256(device_secret, "wipe:{deviceId}:{requestedAt:O}")` (chống lệnh giả), xóa thư mục đồng bộ, bộ nhớ đệm, token,
  rồi gọi `…/wiped` → `wiped` và thu hồi phiên.
- **Chính sách** (`/admin/settings`, nhóm "Đồng bộ máy tính"): `sync.enabled`, `sync.allowed_roles`, `sync.allowed_organizations`
  (gồm đơn vị con), `sync.max_file_size_mb`, `sync.blocked_extensions`, `sync.online_only_workspaces`, `sync.min_client_version`,
  `sync.view_only_as_link`, `sync.device_refresh_days`, `sync.journal_retention_days`, `sync.poll_seconds`.

## 7. Thư viện máy khách `BlueDrive.Sync.Client`

| Thành phần | Việc làm |
|---|---|
| `DesktopAuthenticator` | Đăng nhập OAuth 2.0 + PKCE (S256): mở trình duyệt tới `/connect/authorize`, nhận mã qua socket loopback `127.0.0.1:<cổng ngẫu nhiên>/callback` (kiểm tra `state`), đổi mã tại `/api/v1/connect/token`. |
| `DeviceAuthHandler` | Gắn access token; hết hạn hoặc gặp 401 → làm mới một lần (token xoay vòng). Làm mới thất bại (thiết bị bị thu hồi/xóa từ xa) → xóa token, báo `SignInRequiredException`. |
| `HttpRemoteStore` | Cài đặt `IRemoteStore`: snapshot/changes, tải xuống qua URL ký có **Range** (mất kết nối → tải tiếp từ byte đã nhận), tải lên ≤ 4 MB bằng multipart, lớn hơn bằng **tus** (khối 8 MB, `HEAD` lấy offset để tải tiếp), tạo thư mục / đổi tên / di chuyển / xóa qua `/sync/batch` với `if_match`. |
| Ánh xạ lỗi | 409 → trùng tên, 412 → bản máy chủ đã đổi (kèm mục hiện tại), 404 → đúng mục bị thiếu (mục hay thư mục đích), 410 → đồng bộ lại toàn bộ, 401 → đăng nhập lại, 408/429/5xx/mạng → tạm thời, còn lại (403, 413, 415, chặn loại tệp…) → vĩnh viễn. |
| Thử lại | Polly: chỉ các yêu cầu **lũy đẳng** (liệt kê, tải xuống, khối tus) được thử lại với backoff lũy thừa + jitter, ngắt mạch khi máy chủ lỗi liên tục. Thao tác thay đổi dữ liệu không gửi lại mù quáng; engine lập lại kế hoạch ở vòng sau (mất phản hồi → hai bên cùng hash → liên kết, không tạo bản trùng). |
| `BandwidthLimiter`, `ThrottledStream` | Giới hạn băng thông tải lên/tải xuống (byte/giây, đổi được khi đang chạy, 0 = không giới hạn). |
| `SyncNotifications` | SignalR `/hubs/sync` (`changes_available`, `device_status`, tự kết nối lại + `Resubscribe`) và hỏi định kỳ dự phòng (5 phút; 30 giây khi mất kết nối hub). |

Kiểm thử: `tests/BlueDrive.IntegrationTests/DesktopClientTests.cs` chạy engine thật trên API thật (Testcontainers): 2 máy tạo/sửa/đổi tên/xung đột/xóa, tải tiếp bằng Range, ánh xạ lỗi, làm mới token, đẩy SignalR; `desktop/tests/BlueDrive.Sync.Tests/ClientTests.cs`: PKCE loopback, chặn `state` giả, giới hạn băng thông.
