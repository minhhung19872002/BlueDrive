# PROMPT CHO CLAUDE CODE — BLUEDRIVE DESKTOP SYNC
## Ứng dụng đồng bộ BlueDrive xuống ổ đĩa máy tính (giống Google Drive for Desktop / OneDrive)

> Prompt này là **phần mở rộng** của `SPEC.md` (BlueDrive web + API + mobile). Đọc `SPEC.md` trước để nắm data model, ACL, audit, tus upload. Làm theo từng PHASE ở mục 14; hết mỗi phase phải build xanh, test xanh và commit `sync-phase-N: ...`.

---

## 0. MỤC TIÊU

Xây dựng **BlueDrive Desktop**, ứng dụng chạy nền trên máy tính người dùng. Ứng dụng tạo một thư mục `BlueDrive` trên ổ đĩa và giữ cho nó **đồng bộ 2 chiều** với tư liệu trên server: tài liệu cá nhân, tài liệu nhóm và tài liệu được chia sẻ (nếu có quyền tải xuống).

Trải nghiệm phải tương đương OneDrive Files On-Demand / Google Drive for Desktop:

1. **Hiện tất cả file nhưng chỉ tải khi mở** (online-only placeholder). Có các lựa chọn "Luôn giữ trên thiết bị này" và "Giải phóng dung lượng".
2. **Biểu tượng trạng thái** trên từng file/thư mục trong File Explorer: đám mây (chỉ online), dấu tích xanh viền (đã có bản local), dấu tích xanh đặc (luôn giữ), mũi tên xoay (đang đồng bộ), dấu X đỏ (lỗi).
3. Mục **"BlueDrive – <Tên đơn vị>" ở thanh điều hướng trái** của File Explorer.
4. **Menu chuột phải**: Chia sẻ, Sao chép liên kết, Xem trên web, Lịch sử phiên bản, Luôn giữ trên thiết bị này, Giải phóng dung lượng.
5. **Icon khay hệ thống** (system tray): trạng thái, hoạt động gần đây, tạm dừng/tiếp tục, cài đặt.
6. **Đồng bộ chọn lọc** (selective sync): chọn workspace/thư mục nào được đồng bộ.
7. **Xử lý xung đột** an toàn, không bao giờ mất dữ liệu.
8. Hoạt động **offline**: sửa file khi mất mạng, có mạng lại thì tự đẩy lên.

**Nền tảng ưu tiên**: Windows 10 (1809+) và Windows 11 x64/ARM64 là bắt buộc, làm đầy đủ ở Phase 1–9. macOS (Phase 10) và Linux (Phase 11) để sau.

**Nguyên tắc số 1: KHÔNG BAO GIỜ LÀM MẤT DỮ LIỆU NGƯỜI DÙNG.** Khi nghi ngờ thì tạo bản xung đột, không ghi đè và không xóa. Mọi thao tác xóa đều đi qua thùng rác (thùng rác server, và Recycle Bin trên Windows với xóa local do sync gây ra).

---

## 1. TECH STACK

**Client**
- .NET 10 (LTS). Nếu thiếu SDK thì dùng .NET 8 và ghi chú lại.
- Project chia thành:
  - `BlueDrive.Sync.Core`: engine đồng bộ thuần .NET, không phụ thuộc OS, test được bằng fake.
  - `BlueDrive.Sync.Windows`: provider Windows dùng **Cloud Files API (cfapi)**, `Windows.Storage.Provider` và shell extension.
  - `BlueDrive.Sync.Client`: HTTP client tới API (dựa trên `HttpClient` + Polly cho retry/circuit breaker), tus client (`TusDotNetClient` hoặc tự viết), SignalR client.
  - `BlueDrive.Desktop`: UI **Avalonia 11** (chạy đa nền tảng: tray, cửa sổ cài đặt, wizard), MVVM với CommunityToolkit.Mvvm.
  - `BlueDrive.ShellExt`: COM `IExplorerCommand` cho menu chuột phải (C# với COM interop source-generated; nếu không ổn định thì viết C++ nhỏ).
  - `BlueDrive.Sync.Tests`, `BlueDrive.Sync.IntegrationTests`.
- Interop cfapi: **Vanara.PInvoke.CldApi** + `Vanara.PInvoke.Shell32` (hoặc CsWin32 sinh P/Invoke).
- State local: **SQLite** (Microsoft.Data.Sqlite + Dapper), chế độ WAL.
- Lưu token: Windows **Credential Manager / DPAPI** (`ProtectedData` với scope CurrentUser); trên macOS là Keychain.
- Logging: Serilog, file log xoay vòng tại `%LOCALAPPDATA%\BlueDrive\logs`.
- Đóng gói: **MSIX** (cần package identity để đăng ký `StorageProviderSyncRootManager` và shell extension) cộng thêm bản **sparse package + installer EXE** cho máy không cài được MSIX. Tự cập nhật bằng **Velopack**, hoặc với MSIX thì dùng AppInstaller file.
- Ký số code signing: chuẩn bị pipeline ký (signtool) đọc cert từ biến môi trường; nếu chưa có cert thì dùng self-signed cho dev.

**Server (bổ sung vào BlueDrive API hiện có)**: xem mục 3.

---

## 2. CẤU TRÚC REPO (thêm vào repo BlueDrive)

```
bluedrive/
├─ desktop/
│  ├─ src/
│  │  ├─ BlueDrive.Sync.Core/
│  │  ├─ BlueDrive.Sync.Client/
│  │  ├─ BlueDrive.Sync.Windows/
│  │  ├─ BlueDrive.ShellExt/
│  │  ├─ BlueDrive.Desktop/          (Avalonia app, entry point)
│  │  └─ BlueDrive.Sync.Mac/         (Phase 10)
│  ├─ tests/
│  │  ├─ BlueDrive.Sync.Tests/
│  │  └─ BlueDrive.Sync.IntegrationTests/
│  ├─ packaging/ (msix manifest, AppxManifest, velopack, icons, sign.ps1)
│  └─ docs/
│     ├─ sync-architecture.md
│     ├─ sync-algorithm.md          (bảng quyết định đầy đủ)
│     ├─ huong-dan-cai-dat-desktop.md
│     └─ huong-dan-su-dung-desktop.md
└─ src/ (API — bổ sung endpoint sync)
```

---

## 3. THAY ĐỔI PHÍA SERVER (BlueDrive API)

### 3.1. Change journal (nhật ký thay đổi dùng cho delta sync)
Tạo bảng `change_journal` (append-only):

| Cột | Ghi chú |
|---|---|
| `seq` bigserial PK | thứ tự toàn cục, tăng đơn điệu |
| `workspace_id` | |
| `item_id`, `item_type` [file \| folder] | |
| `op` | create, update_content, update_meta, rename, move, delete (vào thùng rác), restore, purge, permission_changed |
| `parent_id`, `name`, `path` | trạng thái **sau** thay đổi |
| `version_id`, `etag`, `size_bytes`, `content_hash` (SHA-256) | |
| `modified_at` (server time), `modified_by`, `device_id` (nullable: thay đổi đến từ thiết bị nào) | |
| `occurred_at` | |

- Ghi journal trong **cùng transaction** với thao tác nghiệp vụ, qua domain event + outbox, để không bao giờ lệch.
- Job dọn journal cũ hơn 90 ngày (cấu hình được). Nếu cursor của client đã quá cũ thì server trả `410 Gone` và client phải **resync toàn bộ** (xem 6.6).

### 3.2. API sync (`/api/v1/sync/...`)
- `POST /sync/devices`: đăng ký thiết bị (tên máy, OS, phiên bản app, machine id băm). Trả về `device_id`. Ghi audit.
- `GET /sync/roots`: danh sách gốc đồng bộ mà user được phép: "Tài liệu của tôi", mỗi workspace nhóm, "Được chia sẻ với tôi". Mỗi gốc kèm `root_id`, tên hiển thị, quyền, quota.
- `GET /sync/snapshot?root_id=&page_token=`: liệt kê toàn bộ cây (phân trang 1000 item/trang), kèm `cursor` tại thời điểm bắt đầu snapshot để không lọt thay đổi xảy ra trong lúc liệt kê.
- `GET /sync/changes?cursor=&limit=500`: trả `{ changes[], next_cursor, has_more }`. **Chỉ trả các thay đổi user có quyền thấy.** Khi user **mất quyền** với một item thì trả op `access_revoked` để client gỡ placeholder/file local (đưa file đã sửa nhưng chưa đẩy lên vào thư mục "Không đồng bộ được", không xóa).
- `GET /sync/items/{id}`: metadata đầy đủ.
- `GET /sync/items/{id}/content?version=`: tải nội dung, hỗ trợ **HTTP Range** để resume và để hydrate từng đoạn.
- Tạo/sửa/đổi tên/di chuyển/xóa dùng lại API files/folders hiện có, nhưng **bắt buộc header `If-Match: <etag>`** cho update/rename/move/delete từ client sync. Nếu lệch thì trả `412 Precondition Failed` kèm metadata hiện tại, để client xử lý xung đột.
- Upload nội dung mới / phiên bản mới qua **tus** (đã có). Metadata tus gồm: `parent_id`, `name`, `base_etag` (phiên bản client dựa vào), `client_mtime`, `content_hash`, `device_id`. Server **kiểm tra hash** sau khi ghép chunk.
- `POST /sync/batch`: tạo nhiều thư mục / move nhiều item trong một request (tối ưu khi người dùng copy cả cây thư mục).
- `GET /sync/items/{id}/web-url`, `POST /sync/items/{id}/share-link`: phục vụ menu chuột phải.
- Tên trùng: nếu tạo file trùng tên trong cùng thư mục thì server trả `409` và client tự đổi tên `Tên (1).ext`.

### 3.3. Thông báo đẩy
- SignalR hub `/hubs/sync`: khi có thay đổi trong root mà user có quyền, server gửi sự kiện `changes_available { root_id, latest_seq }` (gộp/debounce 1 giây). Client nhận thì gọi `/sync/changes`.
- Fallback: nếu không có SignalR thì client poll 30 giây/lần (cấu hình được).

### 3.4. Quản lý thiết bị & chính sách (trang Quản trị trên web)
- Bảng `devices`: `user_id`, `name`, `os`, `app_version`, `last_seen_at`, `last_ip`, `status` [active | revoked | wipe_pending | wiped].
- Màn hình **"Thiết bị đã kết nối"** cho user (của mình) và admin (toàn hệ thống): thu hồi thiết bị, **xóa dữ liệu từ xa** (remote wipe). Khi client nhận lệnh wipe: xóa thư mục sync và cache, xóa token, báo về server `wiped`, ghi audit.
- Chính sách trong `system_settings`:
  - Cho phép đồng bộ desktop: bật/tắt, theo vai trò/đơn vị.
  - Kích thước file tối đa được đồng bộ.
  - Danh sách đuôi file bị chặn.
  - Bắt buộc chế độ online-only cho workspace nhạy cảm (không cho "Luôn giữ trên thiết bị").
  - Phiên bản client tối thiểu (client cũ hơn bị yêu cầu cập nhật).
- **Tư liệu chỉ có quyền "Xem" (không có quyền tải xuống) KHÔNG được đồng bộ xuống máy.** Hiển thị trong app desktop dạng liên kết "Mở trên web" (file `.url`), hoặc ẩn đi, tùy cấu hình.
- Audit log: mọi thao tác từ client sync ghi `device_id`, `source = desktop_sync`.

### 3.5. Đăng nhập cho desktop
- **OAuth 2.0 Authorization Code + PKCE với loopback redirect** (`http://127.0.0.1:{port}/callback`): app mở trình duyệt hệ thống, người dùng đăng nhập trên web BlueDrive, rồi app nhận code và đổi lấy token.
- Bổ sung endpoint `/connect/authorize`, `/connect/token` (dùng OpenIddict) nếu API chưa có. Refresh token dành cho thiết bị có thời hạn dài (90 ngày, cấu hình được), gắn với `device_id`, và bị thu hồi khi revoke thiết bị.

---

## 4. KIẾN TRÚC CLIENT

```
┌────────────── BlueDrive.Desktop (Avalonia: tray, cửa sổ, wizard) ─────────────┐
│                         ▲ trạng thái, sự kiện (IObservable)                   │
│  ┌──────────────── BlueDrive.Sync.Core ──────────────────────────────────┐    │
│  │ SyncEngine  ── Reconciler (3-way)  ── OperationQueue (persist SQLite) │    │
│  │      │              │                         │                       │    │
│  │ RemoteObserver  LocalObserver            TransferManager              │    │
│  │ (SignalR+poll)  (ILocalFileSystem)       (upload tus / download Range)│    │
│  │      │              │                         │                       │    │
│  │ IRemoteStore    ILocalFileSystem         SyncStateDb (SQLite)         │    │
│  └──────┼──────────────┼─────────────────────────────────────────────────┘    │
│   BlueDrive.Sync.Client │  BlueDrive.Sync.Windows (CfApi provider: placeholder, │
│   (HTTP/tus/SignalR)    │  hydrate, pin, trạng thái, shell)                     │
└───────────────────────────────────────────────────────────────────────────────┘
```

- Engine chạy trong **một tiến trình nền** (app tray). Khi đóng cửa sổ, app chỉ ẩn xuống tray; nó tự khởi động cùng Windows (cấu hình được).
- **Single instance** (named mutex). Hỗ trợ **nhiều tài khoản / nhiều server** (mỗi tài khoản một sync root, một DB, một thư mục).
- Toàn bộ engine được trừu tượng hóa qua interface (`IRemoteStore`, `ILocalFileSystem`, `IClock`, `IPlaceholderProvider`) để unit test bằng **fake in-memory** và chạy được hàng nghìn kịch bản ngẫu nhiên (xem mục 12).

---

## 5. TRẠNG THÁI LOCAL (SQLite: `%LOCALAPPDATA%\BlueDrive\<account_id>\state.db`)

- `account`: server_url, user_id, device_id, sync_root_path, created_at.
- `roots`: root_id, local_relative_path, enabled (selective sync), cursor, last_full_sync_at.
- `items` (bản ghi "base" chung, nền tảng của 3-way merge):
  - `local_id` (khóa nội bộ), `remote_id`, `parent_remote_id`, `root_id`
  - `name`, `relative_path`, `is_folder`
  - `remote_etag`, `remote_version_id`, `remote_hash`, `remote_size`, `remote_mtime`
  - `local_file_id` (**NTFS File ID** 128-bit, dùng để nhận diện rename/move local), `local_size`, `local_mtime`, `local_hash` (tính lười, chỉ khi cần)
  - `hydration_state` [placeholder | partial | full], `pin_state` [unspecified | pinned | unpinned | excluded]
  - `sync_state` [in_sync | pending_upload | pending_download | conflict | error | excluded]
  - `last_synced_at`, `error_code`, `error_message`, `retry_count`, `next_retry_at`
- `operations` (hàng đợi bền vững, sống sót qua restart): `op_id`, `type` [upload_new, upload_version, download, create_folder_remote, create_folder_local, rename_remote, move_remote, delete_remote, rename_local, move_local, delete_local, update_placeholder], `item_local_id`, `payload json`, `status`, `attempts`, `created_at`, `tus_upload_url` (để resume).
- `conflicts`: item, loại xung đột, đường dẫn bản xung đột, thời điểm, đã xử lý hay chưa.
- `ignored_items`, `activity_log` (500 hoạt động gần nhất cho UI).
- Có migration schema DB client và bản sao lưu DB tự động trước khi migrate.

---

## 6. THUẬT TOÁN ĐỒNG BỘ (viết chi tiết trong `docs/sync-algorithm.md`)

### 6.1. Phát hiện thay đổi
- **Remote**: cursor + `/sync/changes`, kích hoạt bởi SignalR hoặc poll.
- **Local**:
  - Với cfapi: nhận callback `NOTIFY_DELETE`, `NOTIFY_RENAME` (đồng bộ trước khi OS thực hiện; có thể từ chối nếu không có quyền).
  - `FileSystemWatcher` / `ReadDirectoryChangesW` với buffer lớn (64 KB) để bắt create/modify. Khi **buffer tràn** thì chuyển sang quét toàn bộ.
  - **Quét định kỳ** (mặc định 15 phút) và quét đầy đủ khi khởi động, so sánh `size + mtime + file_id` với DB để bắt các thay đổi bị lỡ trong lúc app tắt.
  - **USN Journal** (tùy chọn, Phase 8) để quét nhanh trên NTFS.
- **Debounce**: gom sự kiện 2 giây. **Chờ file ổn định** trước khi upload: size/mtime không đổi trong 3 giây và mở được với `FileShare.Read` không lỗi share violation. File Office đang mở thì đợi đến khi đóng hoặc tới lúc Office lưu xong.

### 6.2. Danh sách bỏ qua mặc định (cấu hình được)
`~$*`, `*.tmp`, `~*.tmp`, `.~lock.*#`, `Thumbs.db`, `desktop.ini`, `.DS_Store`, `*.partial`, `*.crdownload`, `*.part`, `$RECYCLE.BIN`, `System Volume Information`, symbolic link/junction (bỏ qua và cảnh báo), file có thuộc tính hidden+system.

### 6.3. Đối soát 3 chiều (Base = DB, Local, Remote)
Viết `Reconciler` thuần hàm: nhận (base, local, remote) và trả danh sách operation. **Bắt buộc có bảng quyết định đầy đủ và test cho từng ô**:

| Local | Remote | Hành động |
|---|---|---|
| không đổi | không đổi | không làm gì |
| sửa nội dung | không đổi | upload phiên bản mới (If-Match base etag) |
| không đổi | sửa nội dung | cập nhật placeholder (nếu chưa hydrate) hoặc tải bản mới (nếu đã hydrate/pinned) |
| sửa | sửa, hash giống nhau | chỉ cập nhật DB (hội tụ) |
| sửa | sửa, hash khác | **XUNG ĐỘT**: giữ bản server ở tên gốc; đổi bản local thành `Tên (Bản xung đột - <Họ tên> - <Tên máy> - yyyy-MM-dd HHmm).ext` rồi upload bản đó lên cùng thư mục; ghi `conflicts` và thông báo |
| tạo mới | (không có) | upload_new; nếu trùng tên thì tự đổi tên `(1)` |
| (không có) | tạo mới | tạo placeholder |
| tạo mới | tạo mới cùng tên/cùng đường dẫn | hash giống thì gộp; khác thì xung đột như trên |
| đổi tên/di chuyển (cùng file_id) | không đổi | rename/move remote (không upload lại nội dung) |
| không đổi | đổi tên/di chuyển | rename/move local |
| đổi tên | đổi tên khác | ưu tiên remote, local đổi theo, ghi log |
| xóa | không đổi | delete remote (vào thùng rác server) |
| xóa | sửa nội dung | **khôi phục**: tải lại bản remote, không xóa remote |
| sửa nội dung | xóa | **khôi phục lên server**: upload lại thành file mới, không mất bản local |
| xóa | xóa | dọn DB |
| xóa thư mục cha | thêm file mới bên trong | khôi phục thư mục cha cùng file mới, xóa phần còn lại |
| bất kỳ | access_revoked | gỡ khỏi máy; nếu có thay đổi local chưa đẩy thì chuyển vào `BlueDrive\_Không đồng bộ được\` và thông báo |

- **Phát hiện move local**: dựa vào NTFS File ID (giữ nguyên khi rename/move trong cùng volume). Một sự kiện delete + create có cùng hash trong vòng 5 giây cũng coi là move.
- **Move giữa các root** (ví dụ từ Tài liệu của tôi sang workspace nhóm): server hỗ trợ thì gọi move; không thì copy rồi delete, có kiểm tra quyền và quota.
- **Xóa hàng loạt an toàn**: nếu một lần quét phát hiện **xóa > 100 file hoặc > 30%** số file của một root (thường do người dùng lỡ tay xóa, hoặc ổ đĩa lỗi/unmount) thì **tạm dừng** và hỏi xác nhận trên UI: "Bạn vừa xóa N tệp. Xóa cả trên BlueDrive?" (Có / Khôi phục các tệp).
- Thư mục sync root **bị xóa / ổ đĩa rút ra / volume khác**: dừng đồng bộ, báo lỗi, tuyệt đối không xóa remote.

### 6.4. Tên file & đường dẫn
- Ký tự hợp lệ trên server nhưng cấm trên Windows (`\ / : * ? " < > |`, ký tự điều khiển, tên kết thúc bằng dấu chấm hoặc khoảng trắng, tên dành riêng `CON, PRN, AUX, NUL, COM1–9, LPT1–9`): ánh xạ sang ký tự Unicode thay thế (ví dụ `:` thành `꞉` U+A789, giống OneDrive) và lưu bảng ánh xạ để đẩy ngược đúng tên.
- **Không phân biệt hoa thường** trên Windows: nếu remote có `A.docx` và `a.docx` cùng thư mục thì file thứ hai hiện thành `a (trùng tên chữ hoa-thường).docx` kèm cảnh báo.
- **Unicode tiếng Việt**: luôn chuẩn hóa **NFC** khi so sánh và gửi lên server (macOS trả NFD).
- Đường dẫn dài hơn 260 ký tự: bật long path (`\\?\`, manifest `longPathAware`). Server giới hạn tối đa 400 ký tự; vượt thì báo lỗi rõ cho file đó.

### 6.5. Truyền tải
- Upload: tus, chunk 8 MB (thích ứng theo tốc độ), **resume sau khi mất mạng/khởi động lại** (lưu `tus_upload_url` trong `operations`). Tính SHA-256 dạng streaming trong lúc đọc. Trước khi upload phải chụp **snapshot nội dung** (copy tạm hoặc đọc với `FileShare.ReadWrite` rồi kiểm tra lại mtime sau khi upload; nếu file bị đổi trong lúc upload thì upload lại).
- Download: Range request, ghi ra file tạm `.bluedrive-tmp` rồi kiểm tra hash, sau đó **thay thế nguyên tử** (`ReplaceFile`/`File.Move` overwrite). Giữ nguyên mtime theo server.
- Song song: mặc định 4 luồng upload + 4 luồng download (cấu hình được), ưu tiên file nhỏ trước, và file người dùng đang mở (hydrate) được **ưu tiên cao nhất**.
- **Giới hạn băng thông** upload/download (KB/s hoặc tự động), hỗ trợ proxy hệ thống và proxy cấu hình tay.
- Retry: exponential backoff có jitter (1s → tối đa 30 phút). Lỗi vĩnh viễn (403, 413, file bị chặn, hết quota) thì không retry, hiện lỗi trên UI.
- Tạm dừng khi máy dùng **pin dưới 20%** hoặc **mạng tính phí** (metered connection), cấu hình được.
- Hết quota: dừng upload, thông báo, các thay đổi vẫn được giữ local.

### 6.6. Đồng bộ ban đầu & resync
- Lần đầu: lấy snapshot và **tạo placeholder cho toàn bộ cây** (không tải nội dung). Mục tiêu: 100.000 item tạo xong dưới 5 phút. Tạo theo lô bằng `CfCreatePlaceholders`.
- Nếu thư mục đích đã có sẵn file (ví dụ cài lại máy): khớp theo đường dẫn + hash. Trùng thì liên kết; khác thì tạo bản xung đột. Không ghi đè.
- Resync (cursor hết hạn → 410): dựng lại snapshot, đối soát với DB, không xóa gì local cho tới khi đối soát xong.

---

## 7. WINDOWS: CLOUD FILES API (FILES ON-DEMAND)

### 7.1. Đăng ký sync root
- `StorageProviderSyncRootManager.Register(...)` với `StorageProviderSyncRootInfo`:
  - Id `BlueDrive!<SID>!<account_id>`, DisplayNameResource "BlueDrive – <Tên đơn vị>", IconResource.
  - `HydrationPolicy = Full` (hoặc `Progressive` cho video lớn), `HydrationPolicyModifier = AutoDehydrationAllowed | StreamingAllowed`.
  - `PopulationPolicy = AlwaysFull`, `InSyncPolicy = FileLastWriteTime | DirectoryLastWriteTime`.
  - `HardlinkPolicy = None`, `ShowSiblingsAsGroup = false`, `Version`, `RecycleBinUri` (link tới thùng rác web), `Context` (account id).
- Sau đó gọi `CfConnectSyncRoot` để đăng ký callback:
  - `FETCH_DATA`: hydrate. Tải đúng đoạn Range được yêu cầu, trả bằng `CfExecute(TRANSFER_DATA)` theo khối 4 KB-aligned, báo tiến độ `CfReportProviderProgress` (Explorer hiện thanh tiến độ). Hỗ trợ hủy qua `CANCEL_FETCH_DATA`. Lỗi mạng thì trả `STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE` để Windows hiện thông báo chuẩn.
  - `VALIDATE_DATA` (nếu dùng), `NOTIFY_FILE_OPEN_COMPLETION`, `NOTIFY_FILE_CLOSE_COMPLETION` (là lúc tốt để kiểm tra thay đổi và upload).
  - `NOTIFY_DELETE` / `NOTIFY_DELETE_COMPLETION`, `NOTIFY_RENAME` / `NOTIFY_RENAME_COMPLETION`: chuyển thành operation remote. Nếu người dùng không có quyền xóa/đổi tên trên server thì **từ chối** (trả `STATUS_CLOUD_FILE_ACCESS_DENIED`) để Explorer báo lỗi ngay.
  - `NOTIFY_DEHYDRATE`: cho phép "Giải phóng dung lượng" chỉ khi file đang in-sync.
- Placeholder: `CfCreatePlaceholders` (FileIdentity = remote_id + version), `CfConvertToPlaceholder` (file local mới sau khi upload xong), `CfUpdatePlaceholder` (khi remote đổi: cập nhật metadata, và nếu file đã hydrate mà chưa sửa local thì dehydrate rồi tải lại hoặc tải bản mới theo pin state), `CfSetInSyncState`, `CfSetPinState`, `CfDehydratePlaceholder`, `CfHydratePlaceholder` (khi pin "Luôn giữ").
- Khi gỡ app hoặc đăng xuất: `CfDisconnectSyncRoot`, sau đó hỏi người dùng giữ bản local hay xóa. Nếu giữ thì chuyển placeholder đã hydrate thành file thường (`CfRevertPlaceholder`), xóa placeholder chưa hydrate, rồi `Unregister`.

### 7.2. Trạng thái & tích hợp Explorer
- Icon trạng thái do Windows tự vẽ dựa trên in-sync state + pin state + hydration. Ngoài ra, cài đặt **`IStorageProviderItemPropertySource`** để thêm cột/biểu tượng tùy chỉnh: "Đã chia sẻ", "Có xung đột", "Lỗi đồng bộ".
- **Menu chuột phải** (`IExplorerCommand`, đăng ký trong MSIX manifest `desktop4:FileExplorerContextMenus`), tiếng Việt:
  - "Chia sẻ…" (mở cửa sổ chia sẻ nhỏ của app: chọn người/nhóm/đơn vị, quyền, hoặc tạo link có mật khẩu/hạn dùng, gọi API share hiện có)
  - "Sao chép liên kết BlueDrive"
  - "Xem trên web"
  - "Lịch sử phiên bản…" (cửa sổ liệt kê version, tải/khôi phục)
  - "Luôn giữ trên thiết bị này" / "Giải phóng dung lượng" (đã có sẵn từ cfapi, chỉ cần đảm bảo hoạt động)
  - "Giải quyết xung đột…" (chỉ hiện khi item đang xung đột)
- **Status UI** (`IStorageProviderStatusUISource`, Windows 11): hiện dung lượng đã dùng / quota và trạng thái đồng bộ trong flyout của Explorer.
- Thư mục sync mặc định: `%USERPROFILE%\BlueDrive - <Tên đơn vị>`. Cấu trúc bên trong:
  ```
  BlueDrive - <Đơn vị>\
  ├─ Tài liệu của tôi\
  ├─ Nhóm\
  │  ├─ <Tên nhóm 1>\
  │  └─ <Tên nhóm 2>\
  ├─ Được chia sẻ với tôi\
  │  └─ <Tên người chia sẻ>\...
  └─ _Không đồng bộ được\   (chỉ xuất hiện khi cần)
  ```
- Thư mục gốc "Nhóm" và "Được chia sẻ với tôi" là thư mục ảo, chỉ đọc ở cấp gốc (không cho tạo file trực tiếp ở đó; nếu người dùng cố tạo thì chuyển file vào `_Không đồng bộ được` và thông báo).
- Quyền chỉ đọc (viewer có quyền tải xuống) thì đặt file thành **ReadOnly**. Nếu người dùng vẫn sửa (bỏ thuộc tính) thì không upload, đưa vào xung đột "không có quyền chỉnh sửa".

### 7.3. Chế độ dự phòng "Đồng bộ đầy đủ" (không placeholder)
Cho máy không hỗ trợ cfapi (ổ FAT32/exFAT, ổ mạng, Windows cũ) hoặc khi người dùng chọn: tải toàn bộ nội dung các thư mục được chọn, theo dõi bằng FileSystemWatcher. Engine dùng chung, chỉ khác `IPlaceholderProvider` (NoOp). Chế độ này cũng là nền cho macOS/Linux ở Phase 10–11.

---

## 8. ỨNG DỤNG DESKTOP (UI Avalonia, 100% tiếng Việt)

### 8.1. Wizard lần đầu
1. Nhập địa chỉ máy chủ BlueDrive (có thể nhúng sẵn qua tham số cài đặt `/server=` hoặc file cấu hình triển khai).
2. "Đăng nhập", mở trình duyệt (PKCE), quay lại app.
3. Chọn vị trí thư mục đồng bộ (kiểm tra NTFS, dung lượng trống, quyền ghi).
4. Chọn nội dung đồng bộ: cây checkbox các root/thư mục (selective sync), mặc định chọn tất cả ở chế độ online-only.
5. Tùy chọn: khởi động cùng Windows, thông báo.
6. Hoàn tất, mở thư mục trong Explorer.

### 8.2. Icon khay & flyout (giống OneDrive)
- Icon thay đổi theo trạng thái: đã đồng bộ / đang đồng bộ (animation) / tạm dừng / ngoại tuyến / lỗi / cần đăng nhập lại.
- Flyout gồm:
  - Header: tên tài khoản, dung lượng đã dùng/quota (thanh tiến độ).
  - Trạng thái tổng quát: "Đã cập nhật", "Đang đồng bộ 12 tệp (35 MB còn lại, ~2 phút)", "Đã tạm dừng", "Ngoại tuyến – thay đổi sẽ được đồng bộ khi có mạng".
  - Danh sách hoạt động gần đây: icon loại file, tên, hành động (Đã tải lên / Đã tải xuống / Đã xóa / Đã đổi tên), thời gian, tiến độ từng file. Bấm vào thì mở file, chuột phải thì "Mở vị trí".
  - Banner lỗi/xung đột: "3 tệp không đồng bộ được – Xem".
  - Nút: Mở thư mục, Xem trên web, Tạm dừng (2 giờ / 8 giờ / 24 giờ / tới khi tiếp tục), Cài đặt, Trợ giúp, Thoát.
- Thông báo Windows (toast): xung đột mới, lỗi cần xử lý, chia sẻ mới cho bạn, sắp hết quota, cập nhật phiên bản.

### 8.3. Cửa sổ Cài đặt (các tab)
- **Tài khoản**: thông tin, dung lượng, thêm tài khoản khác, hủy liên kết máy này.
- **Đồng bộ**: chọn thư mục (selective sync), đổi vị trí thư mục sync (di chuyển an toàn), chế độ Files On-Demand bật/tắt, "Giải phóng dung lượng cho file không dùng sau N ngày" (tự dehydrate, giống Storage Sense), danh sách bỏ qua.
- **Mạng**: giới hạn băng thông, proxy, số luồng song song, tạm dừng khi mạng tính phí / khi dùng pin.
- **Chung**: khởi động cùng Windows, thông báo, ngôn ngữ, giao diện sáng/tối/theo hệ thống.
- **Vấn đề**: danh sách lỗi và xung đột, mỗi mục có hành động (Thử lại, Giữ cả hai, Giữ bản trên máy, Giữ bản trên BlueDrive, Mở vị trí, Bỏ qua).
- **Giới thiệu**: phiên bản, kiểm tra cập nhật, **"Xuất gói chẩn đoán"** (zip log + thông tin hệ thống, đã ẩn token) để gửi hỗ trợ, và nút gửi ticket hỗ trợ (dùng API ticket của BlueDrive).

### 8.4. Cửa sổ Giải quyết xung đột
Hiển thị 2 phiên bản cạnh nhau: kích thước, thời gian sửa, người sửa/thiết bị, có preview nếu là ảnh/văn bản. Lựa chọn: Giữ cả hai (mặc định), Giữ bản trên máy (upload đè thành version mới, bản server vẫn còn trong lịch sử phiên bản), Giữ bản trên BlueDrive.

---

## 9. BẢO MẬT CLIENT
- Token chỉ lưu trong Credential Manager/DPAPI, không ghi ra file cấu hình hay log.
- TLS bắt buộc; tùy chọn **certificate pinning** (cấu hình triển khai) cho server nội bộ.
- Hết hạn refresh token hoặc thiết bị bị thu hồi thì dừng sync, giữ nguyên dữ liệu local, hiện "Cần đăng nhập lại".
- **Remote wipe**: thực hiện đúng như mục 3.4. Có kiểm tra chữ ký lệnh từ server để tránh giả mạo.
- Không theo symlink/junction ra ngoài sync root (chống path traversal).
- Gói chẩn đoán phải lọc thông tin nhạy cảm (token, mật khẩu, nội dung file).
- Ghi audit trên server cho mọi thao tác kèm `device_id`.
- Tuân thủ chính sách online-only bắt buộc (không cho pin, tự dehydrate sau khi đóng file).

---

## 10. HIỆU NĂNG & ĐỘ TIN CẬY (mục tiêu đo được)
- 100.000 file / 5.000 thư mục: tạo placeholder lần đầu dưới 5 phút; quét khởi động (không đổi gì) dưới 30 giây; RAM nền dưới 200 MB; CPU nền khi rảnh gần 0%.
- Thay đổi trên web xuất hiện trên máy (placeholder cập nhật) **dưới 5 giây** khi có SignalR.
- Lưu file Office trên máy thì lên server dưới 10 giây (file < 10 MB).
- Upload/download 5 GB qua mạng chập chờn: resume đúng, hash khớp.
- Kill process ở bất kỳ thời điểm nào, khởi động lại: không mất dữ liệu, không tạo file trùng, hàng đợi tiếp tục đúng (đảm bảo nhờ operation queue idempotent + transaction SQLite).

---

## 11. ĐÓNG GÓI, CÀI ĐẶT, CẬP NHẬT
- MSIX (Windows 10 1809+): manifest khai báo `cloudfiles`, `desktop:Extension windows.startupTask`, `desktop3:CloudFiles`, `desktop4:FileExplorerContextMenus`, COM server cho shell extension.
- Installer EXE (Velopack) có tham số **cài im lặng cho triển khai hàng loạt**: `BlueDriveSetup.exe /silent /server=https://drive.example.gov.vn`. Bổ sung tài liệu triển khai qua GPO/Intune.
- Tự cập nhật: kênh stable/beta, server publish manifest phiên bản tại `/downloads/desktop/releases.json`. Trang web BlueDrive thêm mục **"Tải ứng dụng máy tính"**.
- Gỡ cài đặt sạch: unregister sync root, gỡ shell ext, hỏi người dùng giữ hay xóa file local.

---

## 12. KIỂM THỬ (BẮT BUỘC)
1. **Unit test Reconciler**: phủ toàn bộ bảng 6.3 + các trường hợp tên file 6.4.
2. **Simulation/Property-based test** (FsCheck): chạy `SyncEngine` với `FakeRemoteStore` + `FakeLocalFileSystem` + `FakeClock` qua 10.000 chuỗi thao tác ngẫu nhiên xen kẽ trên 2–3 "thiết bị" giả lập và web, có ngắt mạng và crash ngẫu nhiên. **Bất biến cần kiểm tra**:
   - Sau khi hội tụ, cây local khớp cây remote (trừ item bị bỏ qua/excluded).
   - Không nội dung nào từng được ghi bị mất: mọi phiên bản phải còn ở dạng file, version hoặc bản xung đột hoặc trong thùng rác.
   - Không có vòng lặp đồng bộ vô hạn (ping-pong).
3. **Integration test**: API thật (docker compose) + client trên thư mục tạm; trên runner Windows thì test cả cfapi thật.
4. **Kịch bản thủ công / E2E** (ghi trong `docs/kich-ban-kiem-thu-desktop.md`): sửa Word đồng thời từ 2 máy; đổi tên thư mục lớn; di chuyển thư mục giữa nhóm; xóa 500 file (phải hỏi xác nhận); rút mạng giữa lúc upload video 2 GB; hết quota; thu hồi quyền khi đang mở file; remote wipe; cài lại máy rồi trỏ vào thư mục cũ; tên file có ký tự `:` và tiếng Việt có dấu; đường dẫn > 260 ký tự; hai file chỉ khác hoa thường.
5. Đo hiệu năng theo mục 10, xuất báo cáo.

---

## 13. TÀI LIỆU
- `docs/sync-architecture.md` (sơ đồ, luồng dữ liệu), `docs/sync-algorithm.md` (bảng quyết định).
- `huong-dan-cai-dat-desktop.md` (cài đơn lẻ, cài hàng loạt, cấu hình server, firewall/proxy).
- `huong-dan-su-dung-desktop.md` (tiếng Việt, ảnh chụp màn hình: đăng nhập, chọn thư mục, biểu tượng trạng thái nghĩa là gì, giải phóng dung lượng, chia sẻ từ Explorer, xử lý xung đột).
- Bổ sung vào `docs/traceability.md` một mục "Mở rộng: Đồng bộ desktop", và vào `docs/attt-742.md` phần thiết bị đầu cuối (thu hồi, xóa từ xa, mã hóa token).

---

## 13A. CÙNG SOẠN THẢO TRÊN WINDOWS BẰNG ONLYOFFICE DESKTOP EDITORS

### Mục tiêu
Nhiều người cùng sửa một file Word/Excel/PowerPoint **theo thời gian thực từ phần mềm cài trên Windows** (không phải trình duyệt), bằng ONLYOFFICE Desktop Editors kết nối tới ONLYOFFICE Document Server của BlueDrive. Người trên web và người trên app desktop cùng soạn chung một phiên.

### Bước 0 — Nghiên cứu trước khi code (bắt buộc)
Đọc kỹ tài liệu chính thức rồi tóm tắt vào `docs/onlyoffice-desktop-integration.md`, ghi rõ phiên bản ONLYOFFICE Desktop Editors đã kiểm chứng:
- https://api.onlyoffice.com/docs/desktop-editors/usage-api/adding-a-dms-provider/ (thêm DMS provider: thư mục `providers`, `config.json`)
- Các mục "Configuring the interaction", "Login and logout", "Opening documents", "Changing a theme", "Sending notifications" trong Desktop Editors Usage API.
- Cách Nextcloud/Seafile tích hợp (tham khảo mã nguồn ONLYOFFICE desktop-apps trên GitHub, thư mục providers).
- Tham số cài đặt im lặng của bộ cài Windows (EXE/MSI) và đường dẫn thư mục `providers` trên Windows.
- Cách mở trực tiếp 1 tài liệu của provider từ bên ngoài (command line / URL scheme). **Nếu không có cách chính thức thì ghi rõ và đề xuất phương án thay thế** (ví dụ mở portal BlueDrive trong ONLYOFFICE rồi điều hướng tới tài liệu, hoặc fallback mở trình duyệt).

### Phía server (BlueDrive API + Web)
- Endpoint kiểm tra provider (`check.url` trong config.json, ví dụ `/api/v1/onlyoffice-desktop/status`).
- Trang đăng nhập dành cho Desktop Editors (`entryPage`): dùng lại đăng nhập BlueDrive. Sau khi đăng nhập thì gọi lệnh `portal:login` của Desktop Editors API để app hiện BlueDrive trong mục "Connected clouds"; đăng xuất thì gọi `portal:logout`.
- Nhận diện khi web BlueDrive đang chạy bên trong Desktop Editors (theo các cách tài liệu hướng dẫn), để **ẩn header/menu không cần thiết** và mở tài liệu ở chế độ editor của Desktop Editors (`editorPage`). Các link ngoài thì mở trình duyệt mặc định.
- Trang danh sách tài liệu rút gọn trong Desktop Editors: Tài liệu của tôi, Nhóm, Được chia sẻ, Gần đây.
- Dùng chung toàn bộ ACL, audit (ghi `source = onlyoffice_desktop`), phiên bản, callback lưu file của tích hợp ONLYOFFICE web đã có.
- Đồng bộ theme sáng/tối (`portal:uitheme`).

### Provider package (thư mục `desktop/onlyoffice-provider/bluedrive/`)
- `config.json` với: `provider: "bluedrive"`, `name: "BlueDrive"`, `check`, `entryPage`, `editorPage`, `extraLogout`, icon sáng/tối (SVG) theo đúng spec tài liệu.
- Script build đóng gói provider; phiên bản gắn với phiên bản BlueDrive Desktop.

### Bộ cài BlueDrive Desktop
- Tùy chọn trong bộ cài, **mặc định bật**: "Cài ONLYOFFICE Desktop Editors để cùng soạn thảo tài liệu".
  - Nếu máy chưa có ONLYOFFICE: tải bộ cài chính thức (x64/ARM64), kiểm tra chữ ký số/hash, cài im lặng.
  - Nếu đã có: kiểm tra phiên bản tối thiểu, nếu cũ thì đề nghị cập nhật.
- Copy provider `bluedrive` vào thư mục `providers` của ONLYOFFICE. Khi ONLYOFFICE cập nhật làm mất provider thì BlueDrive Desktop phát hiện và tự cài lại khi khởi động.
- Cài đặt im lặng hàng loạt: `BlueDriveSetup.exe /silent /server=... /with-onlyoffice=1`.
- Gỡ BlueDrive thì gỡ provider; hỏi người dùng có gỡ ONLYOFFICE hay không (mặc định giữ lại).
- Tuân thủ giấy phép AGPL v3 của ONLYOFFICE: phân phối bộ cài gốc không chỉnh sửa, ghi rõ nguồn và giấy phép trong màn hình "Giới thiệu" và trong tài liệu.

### Trải nghiệm trên Windows
- Menu chuột phải trên file `.docx/.xlsx/.pptx/.odt/.ods/.odp/.pdf` trong thư mục BlueDrive:
  - **"Cùng soạn thảo bằng ONLYOFFICE"**: mở đúng tài liệu đó trong ONLYOFFICE Desktop Editors, ở chế độ kết nối với BlueDrive (nếu chưa đăng nhập provider thì tự đăng nhập bằng phiên của BlueDrive Desktop, không bắt nhập lại; tìm cơ chế SSO/token phù hợp ở Bước 0).
  - **"Cùng soạn thảo trên trình duyệt"**: fallback, mở web BlueDrive.
- Tùy chọn trong Cài đặt → Đồng bộ: "Khi nhấp đúp file Office trong BlueDrive: [Mở bằng ứng dụng mặc định] / [Luôn mở chế độ cùng soạn (khi có mạng)]". Nếu chọn cái thứ hai: đăng ký BlueDrive làm handler (qua shell extension hoặc khai báo file association có phạm vi thư mục sync root), có mạng thì mở chế độ cùng soạn, mất mạng thì mở local bằng ứng dụng mặc định.
- Hiện **ai đang mở/sửa file**: trong flyout của tray và cột trạng thái tùy chỉnh ở Explorer ("Đang được Nguyễn Văn A, Trần Thị B chỉnh sửa"), lấy từ Document Server / SignalR.
- **Khóa mềm khi sửa offline/local**: nếu file đang có phiên cùng soạn online mà người dùng mở bản local bằng Word để sửa, thì hiện cảnh báo "Tài liệu đang được cùng soạn trực tuyến, thay đổi của bạn có thể tạo bản xung đột. Mở chế độ cùng soạn?".
- Sau khi phiên cùng soạn đóng và Document Server lưu phiên bản mới, app đồng bộ cập nhật file local (placeholder/hydrated) như mọi thay đổi remote khác.

### Kiểm thử
- 3 người cùng sửa 1 file: 1 người trên web, 2 người trên ONLYOFFICE Desktop (2 máy Windows). Mọi người thấy con trỏ và thay đổi của nhau; lưu thì tạo đúng 1 phiên bản mới; file local trên cả 2 máy được cập nhật.
- Người không có quyền sửa (chỉ xem) mở được ở chế độ chỉ xem.
- Mất mạng giữa phiên: ONLYOFFICE báo mất kết nối; có mạng lại thì tiếp tục; không mất nội dung.
- Cài mới, cập nhật, gỡ cài đặt: provider luôn đúng trạng thái.

---

## 14. KẾ HOẠCH THEO PHASE
1. **Sync-Phase 1 — Server**: change_journal + outbox, API `/sync/*`, If-Match/ETag, SignalR `/hubs/sync`, bảng devices, OAuth PKCE (OpenIddict), trang web "Thiết bị đã kết nối", chính sách. Kèm integration test.
2. **Sync-Phase 2 — Core engine**: interfaces, SQLite state, Reconciler + bảng quyết định, OperationQueue, fake FS/remote, unit test + simulation test. **Chưa làm UI.**
3. **Sync-Phase 3 — Client API & truyền tải**: HTTP client, tus upload resume, Range download, hash, retry/backoff, giới hạn băng thông, SignalR + poll.
4. **Sync-Phase 4 — Chế độ đồng bộ đầy đủ** (FileSystemWatcher, không placeholder) chạy end-to-end trên Windows. Đây là mốc demo đầu tiên.
5. **Sync-Phase 5 — Cloud Files API**: đăng ký sync root, placeholder, hydrate theo yêu cầu + tiến độ, pin/unpin, dehydrate, notify rename/delete, nav pane Explorer.
6. **Sync-Phase 6 — UI Avalonia**: wizard, tray + flyout, cài đặt, xung đột, lỗi, toast, nhiều tài khoản.
7. **Sync-Phase 7 — Tích hợp Explorer**: context menu (chia sẻ, link, web, lịch sử phiên bản), custom state icons, Status UI, selective sync, readonly cho viewer.
8. **Sync-Phase 8 — Độ tin cậy**: xóa hàng loạt an toàn, resync 410, USN journal, tên file/hoa-thường/long path/NFC, access_revoked, quota, pin/mạng tính phí, kill-test.
8b. **Sync-Phase 8b — Cùng soạn thảo bằng ONLYOFFICE Desktop Editors** (mục 13A): nghiên cứu → provider + trang đăng nhập/danh sách phía server → menu chuột phải + tùy chọn nhấp đúp → hiển thị người đang sửa → kiểm thử 3 người cùng soạn.
9. **Sync-Phase 9 — Đóng gói & phát hành** (bao gồm cài kèm ONLYOFFICE Desktop Editors + provider BlueDrive): MSIX + EXE Velopack, ký số, auto-update, cài im lặng, gỡ cài đặt sạch, trang tải trên web, tài liệu, đo hiệu năng.
10. **Sync-Phase 10 — macOS**: trước tiên dùng chế độ đồng bộ đầy đủ với engine .NET (FSEvents qua FileSystemWatcher, Keychain, tray Avalonia, NFD→NFC). Sau đó tùy chọn làm **File Provider extension** (Swift, `NSFileProviderReplicatedExtension`) giao tiếp với engine .NET qua XPC/gRPC cục bộ để có online-only giống Windows.
11. **Sync-Phase 11 — Linux (tùy chọn)**: chế độ đồng bộ đầy đủ + tray (AppIndicator), gói `.deb`/AppImage.
12. **Sync-Phase 12 (nâng cao, tùy chọn)**: delta sync theo khối (content-defined chunking FastCDC, server lưu block dedup) để chỉ upload phần thay đổi của file lớn; LAN sync giữa các máy cùng mạng.

---

## 15. TIÊU CHÍ HOÀN THÀNH
- [ ] Cài trên Windows 11 sạch, đăng nhập bằng trình duyệt, thư mục BlueDrive xuất hiện ở Explorer với đúng cây tư liệu dạng online-only.
- [ ] Mở file online-only thì tự tải (có tiến độ), icon chuyển trạng thái đúng; "Luôn giữ" và "Giải phóng dung lượng" hoạt động.
- [ ] Sửa/thêm/đổi tên/di chuyển/xóa trên máy thì phản ánh lên web dưới 10 giây, và ngược lại (từ web xuống máy) dưới 5 giây.
- [ ] Sửa đồng thời 2 nơi tạo bản xung đột đúng định dạng tên; không mất nội dung nào.
- [ ] Rút mạng, sửa 50 file, cắm lại: tất cả lên server đúng.
- [ ] Xóa hơn 100 file thì app hỏi xác nhận.
- [ ] Thu hồi thiết bị / remote wipe từ trang quản trị hoạt động; audit ghi đủ `device_id`.
- [ ] File chỉ có quyền Xem không bị tải xuống máy.
- [ ] Menu chuột phải "Chia sẻ…" tạo được link chia sẻ có mật khẩu.
- [ ] Simulation test 10.000 chuỗi đều xanh; kill-test không mất dữ liệu.
- [ ] Toàn bộ UI, thông báo, tài liệu bằng tiếng Việt; không còn TODO/stub.

**Bắt đầu với Sync-Phase 1. Trước khi code, hãy đọc `SPEC.md` và code hiện tại của API, tóm tắt những gì cần sửa phía server, liệt kê giả định, rồi tiến hành.**
