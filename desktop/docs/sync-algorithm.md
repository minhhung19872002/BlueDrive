# BlueDrive Desktop — thuật toán đồng bộ

Engine: `desktop/src/BlueDrive.Sync.Core` (thuần .NET, không phụ thuộc hệ điều hành). Quyết định cho từng mục: `Reconciler.Decide`
(hàm thuần). Thực thi: `SyncEngine`. Trạng thái: `SyncStateDb` (SQLite, WAL).

## 1. Ba cây và một lượt đồng bộ

| Cây | Nguồn | Lưu ở |
|---|---|---|
| **Remote** | `/sync/snapshot` lần đầu (hoặc sau 410), sau đó `/sync/changes` theo cursor | bảng `remote_items` |
| **Local** | quét thư mục đồng bộ (bỏ qua danh sách 6.2, thư mục `_Không đồng bộ được`) | không lưu |
| **Base** | trạng thái đã đồng bộ gần nhất của từng mục (hai bên có cùng nội dung `hash`) | bảng `items` |

Một lượt (`SyncAsync`):

1. **Làm mới cây remote.** Áp từng thay đổi theo thứ tự `seq`. Lệnh `delete` chỉ gỡ đúng mục đó; sau khi áp xong cả đợt mới dọn các
   mục "mồ côi" (chuỗi cha bị đứt). Lý do: một mục con có thể đã được chuyển ra khỏi thư mục bị xóa ở một thay đổi phía sau của
   cùng đợt. `access_revoked` đánh dấu mục bị thu hồi quyền; `permission_changed` liệt kê lại nhánh (`snapshot?folder_id=`).
   Mục chỉ có quyền Xem (`canDownload = false`) không bao giờ vào cây. Cursor quá cũ (410) → snapshot lại toàn bộ, không xóa gì
   ở máy cho tới khi đối soát xong.
2. **Các vòng (tối đa 12)**: quét local → ghép với base → lập kế hoạch → thực thi; lặp tới khi một vòng không còn thao tác.
   Mỗi vòng dựa trên trạng thái mới, nên mọi thao tác đều làm trên dữ liệu vừa đọc.

### Ghép local ↔ base
- Theo **File ID** (NTFS, giữ nguyên khi đổi tên/di chuyển trong cùng ổ) → phát hiện đổi tên/di chuyển mà không tải lại.
- Mục chưa ghép được thì theo **đường dẫn** của base (trình soạn thảo lưu qua tệp tạm làm đổi File ID, ví dụ Word/Excel).
- Tệp được coi là **sửa** khi kích thước / mtime / File ID khác base **và** SHA-256 khác. Chỉ "chạm" mà nội dung giống thì chỉ
  cập nhật thông số.
- Di chuyển được xác định bằng **(thư mục cha, tên)**, không bằng đường dẫn đầy đủ, nên đổi tên một thư mục không làm mọi mục
  con trông như bị di chuyển.

## 2. Bảng quyết định (mục 6.3 của đặc tả)

| Trên máy | Trên máy chủ | Hành động (`SyncAction`) |
|---|---|---|
| không đổi | không đổi | không làm gì |
| sửa nội dung | không đổi | `UploadVersion` (If-Match revision base) |
| không đổi | sửa nội dung | `Download` (hoặc cập nhật placeholder khi Files On-Demand) |
| sửa | sửa, cùng hash | `Converge` (chỉ cập nhật base) |
| sửa | sửa, khác hash | `Conflict`: giữ bản máy chủ ở tên gốc; bản trên máy đổi thành `Tên (Bản xung đột - <Họ tên> - <Tên máy> - yyyy-MM-dd HHmm).ext` rồi tải lên cùng thư mục; ghi bảng `conflicts` |
| tạo mới | (không có) | `UploadNew`; trùng tên trên máy chủ (409) → đổi thành `Tên (1).ext` |
| (không có) | tạo mới | `CreateLocal` (tải xuống / placeholder / thư mục) |
| tạo mới | tạo mới cùng đường dẫn | thư mục hoặc cùng hash → `Converge` (liên kết, lấy đúng cách viết hoa/thường của máy chủ); khác → `Conflict` |
| đổi tên/di chuyển | không đổi | `MoveRemote` (không tải lại nội dung) |
| không đổi | đổi tên/di chuyển | `MoveLocal` |
| đổi tên | đổi tên khác | ưu tiên máy chủ: `MoveLocal` |
| đổi tên | đổi tên giống | `Converge` |
| xóa | không đổi | `DeleteRemote` (vào thùng rác máy chủ) |
| xóa | sửa nội dung hoặc di chuyển | `RestoreLocal`: tải lại bản máy chủ, **không** xóa trên máy chủ |
| sửa nội dung | xóa | `ReuploadAsNew`: tải lên lại thành tệp mới, không mất bản trên máy |
| xóa | xóa | `ForgetBase` |
| xóa thư mục cha | thêm tệp mới bên trong | thư mục: `RestoreLocal` (giữ lại cùng tệp mới); các tệp con không đổi: `DeleteRemote` |
| bất kỳ | `access_revoked` | không đổi → `DeleteLocal`; có thay đổi chưa đẩy → `Quarantine` vào `_Không đồng bộ được\` |
| sửa | không có quyền chỉnh sửa | `Quarantine` (bản sửa sang `_Không đồng bộ được\`) + `Download` (khôi phục bản máy chủ) |

Nội dung và vị trí độc lập nhau: ví dụ "sửa trên máy" + "máy chủ đổi tên" → `UploadVersion | MoveLocal`. Khi hai bên cùng đổi
tên tới **cùng một chỗ** mà máy chủ còn nội dung mới, base chỉ ghi nhận vị trí, rồi bước tải xuống vẫn chạy.

## 3. Thứ tự thực thi trong một vòng

1. Tạo thư mục (máy chủ và máy), nông trước sâu.
2. `MoveRemote` (thay đổi từ máy), rồi `MoveLocal` (thay đổi từ máy chủ), nông trước sâu.
3. Nội dung: tải lên / tải xuống / xung đột / khôi phục.
4. Xóa: tệp trước, thư mục sâu trước. Thư mục chỉ bị xóa khi các mục con đã xử lý xong.

Trước khi chạy, mỗi bước **kiểm tra lại điều kiện**: tệp còn đúng như lúc quét, đích còn trống, thư mục cha đã có. Sai điều kiện thì
bỏ qua, vòng sau lập kế hoạch lại. Tải lên dùng **bản chụp nội dung** (đọc trọn rồi tính SHA-256); nếu tệp đổi trong lúc tải lên thì
base giữ thông số cũ để lượt sau tải lên lại. Tải xuống ghi qua tệp tạm `*.bluedrive-tmp` rồi thay thế nguyên tử, kiểm tra hash và
giữ mtime theo máy chủ.

## 4. Không bao giờ mất dữ liệu

- Xóa luôn qua thùng rác (máy chủ) / Thùng rác Windows (máy).
- Xóa/ghi đè trên máy chỉ khi bản trên máy **không đổi** so với base (đã có trên máy chủ).
- Thay đổi xung đột → bản xung đột; không có quyền → `_Không đồng bộ được`.
- **Xóa hàng loạt an toàn**: hơn 100 tệp, hoặc hơn 30% số tệp của một gốc (khi có từ 10 tệp trở lên) → tạm dừng xóa trên máy chủ,
  hỏi người dùng ("Xóa cả trên BlueDrive" / "Khôi phục các tệp") qua `ResolveMassDelete`.
- 412 (bản máy chủ đã đổi) → cập nhật cây, lập lại kế hoạch. Khi chỉ metadata đổi (không đổi nội dung/vị trí), base nhận revision
  mới để If-Match không bị kẹt. Lệnh di chuyển trên máy dùng revision hiện tại khi máy chủ chỉ đổi nội dung.
- Tệp **đang mở / bị khóa** (không đọc được để tính hash hoặc tải lên): mục đó đứng yên trong lượt này và được báo "sẽ đồng bộ khi tệp được đóng"; lỗi đọc/ghi ổ đĩa ở một bước chỉ dừng bước đó (như crash), không dừng cả lượt.
- 404: chỉ gỡ đúng mục máy chủ báo thiếu (có thể là thư mục đích), không gỡ mục đang thao tác.
- **Chống kẹt**: nếu hai bên di chuyển tạo vòng (mỗi bên chuyển một thư mục vào thư mục kia), máy chủ từ chối (400) hoặc không
  bước nào chạy được → hoàn tác di chuyển trên máy (ưu tiên máy chủ), rồi áp bố cục của máy chủ. Các di chuyển của máy chủ
  chặn nhau trên máy (thư mục "F1" đổi tên thành "a.txt" trong khi tệp "a.txt" chuyển vào thư mục con mới của nó) → mục đang
  chiếm chỗ (cũng đang chờ di chuyển) được tạm đổi sang tên trống, bản ghi gốc theo tên tạm, vòng sau áp tiếp di chuyển.
- **Crash bất kỳ lúc nào**: base cập nhật ngay sau từng thao tác; lượt sau thấy trạng thái cũ hoặc mới và hội tụ. Ví dụ tải lên xong
  mà mất phản hồi → hai bên cùng có tệp cùng hash → liên kết, không tạo bản trùng.

### Chính sách của máy chủ (mục 8.2)

- Tệp có đuôi bị chặn tải xuống hoặc lớn hơn giới hạn đồng bộ: bỏ khỏi cả 3 cây khi lập kế hoạch (như thư mục không chọn đồng bộ) — không
  tải về, bản đã có trên máy không bị xóa trên máy chủ, sửa trên máy không được tải lên. Bỏ chặn → vòng sau tải về như tệp mới.
- Tệp chỉ xem (`canDownload = false`): không có trong cây máy chủ của engine; bản đã tải trước đây bị gỡ khỏi máy (không phải xóa trên máy chủ).
  Có quyền tải lại → tải về.
- Liên kết `<tên> - Mở trên web.url` cho hai loại trên được tạo/gỡ cuối mỗi vòng (`MaterializeLinksAsync`); liên kết luôn bị bỏ qua khi quét.
- Tải lên (tệp mới hoặc phiên bản mới): đuôi không được phép / quá kích thước → lỗi vĩnh viễn của riêng tệp đó (`RemotePermanentException`
  413, không phải 403 nên không bị chuyển vào `_Không đồng bộ được`), tệp ở yên trên máy và hiện trong Vấn đề.

## 5. Tên tệp (mục 6.4)

`NameMapper`: ký tự Windows cấm → ký tự Unicode tương tự (`:`→`꞉`, `?`→`？`, `*`→`＊`, `"`→`＂`, `<>|\/` → dạng full-width),
dấu chấm/khoảng trắng cuối và tên dành riêng (`CON`, `NUL`, `COM1`…) được làm hợp lệ, luôn NFC. Base lưu cả tên trên máy và tên
gốc trên máy chủ, nên đổi tên đẩy ngược đúng ký tự gốc. Hai mục chỉ khác hoa/thường trong cùng thư mục: mục thứ hai hiện thành
`a (trùng tên chữ hoa-thường).docx`.

## 6. Kiểm thử

- `ReconcilerTests`: từng ô của bảng trên, kể cả các tổ hợp nội dung + vị trí; `NameMapperTests`; `SyncStateDbTests`.
- `EngineScenarioTests`: các kịch bản đầu cuối trên máy chủ giả (tải lần đầu, đổi tên không tải lại, xung đột, xóa vs sửa, xóa thư mục
  có tệp mới, xóa hàng loạt, lưu kiểu Office, 410, mất phản hồi + crash, không quyền sửa, tên Windows).
- `SimulationTests` (FsCheck): chuỗi thao tác ngẫu nhiên trên 2 máy + web, mạng chập chờn (lỗi trước hoặc sau khi máy chủ xử lý) và
  crash ngẫu nhiên. Bất biến: (1) hội tụ — hai máy và máy chủ cùng một cây; (2) engine không bao giờ xóa/ghi đè nội dung mà máy chủ
  chưa có và không còn bản sao; (3) không ping-pong — sau hội tụ, một lượt nữa không có thao tác nào. CI chạy 300 chuỗi
  (`SYNC_SIM_RUNS`); bản phát hành chạy **10.000 chuỗi: đạt** (2 lần liên tiếp, ~2 phút/lần). Chuỗi lỗi được in ra và phát lại bằng
  `SYNC_SIM_REPRO`. Quá trình phát triển đã dùng simulation tìm và sửa 14 lỗi của engine (vòng 412 vô hạn, mất đổi tên khi tải nội
  dung, xóa nhánh trước khi áp di chuyển ra ngoài, kẹt do di chuyển tạo vòng, khác hoa/thường…).
