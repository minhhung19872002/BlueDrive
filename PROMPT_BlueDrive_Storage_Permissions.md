# PROMPT CHO CLAUDE CODE — BLUEDRIVE: LƯU TRỮ & PHÂN QUYỀN KIỂU GOOGLE DRIVE / SHAREPOINT

> Prompt này là **phần mở rộng và nâng cấp** của `SPEC.md` (BlueDrive). Nó **thay thế** các phần về workspace, ACL, chia sẻ và phân quyền tài nguyên trong SPEC.md (mục 3 "Không gian lưu trữ", "Phân quyền tài nguyên", "Chia sẻ"; mục 4.4; chức năng #28–31, #49, #54–62). Các phần còn lại của SPEC.md giữ nguyên.
>
> Đọc `SPEC.md` và code hiện tại trước. Làm theo PHASE ở mục 15; hết mỗi phase phải build xanh, test xanh, cập nhật `docs/traceability.md` và commit `perm-phase-N: ...`.
>
> **Nguyên tắc tối thượng:**
> 1. Không ai được thấy hay chạm vào dữ liệu mà họ không có quyền, trên **mọi kênh**: web, API, tìm kiếm, dashboard, thông báo, thumbnail, preview, ONLYOFFICE, app mobile, app desktop sync, WebDAV, xuất báo cáo, link chia sẻ.
> 2. Mọi quyết định cấp quyền **giải thích được** ("vì sao người này có quyền này").

---

## 0. MỤC TIÊU

Nâng BlueDrive lên mô hình lưu trữ và phân quyền tương đương Google Drive + SharePoint Online:

| Khái niệm Google Drive / SharePoint | Trong BlueDrive |
|---|---|
| My Drive / OneDrive cá nhân | **Drive của tôi** (Personal Drive) |
| Shared Drive / SharePoint Site | **Không gian dùng chung** (Space) |
| Document Library | **Thư viện tài liệu** (Library), mỗi Space có 1 hoặc nhiều Library |
| Folder / Item | Thư mục / Tệp |
| Owner, Manager, Content manager, Contributor, Commenter, Viewer | Các **cấp quyền** (Permission Level) mặc định, xem mục 3 |
| SharePoint Permission Levels (Full Control, Edit, Contribute, Read, View Only...) | Cấp quyền **tùy biến được** từ các quyền nguyên tử |
| Site Owners / Members / Visitors | Nhóm mặc định của Space: **Chủ sở hữu / Thành viên / Khách xem** |
| Break / Restore inheritance, Unique permissions | **Ngắt / Khôi phục kế thừa**, Quyền riêng |
| General access: Restricted / Anyone in org / Anyone with link | **Quyền truy cập chung** |
| Sharing links: Anyone / People in org / Specific people / Existing access | **4 loại liên kết chia sẻ** |
| Request access | **Yêu cầu quyền truy cập** |
| Shortcuts | **Lối tắt** |
| Check permissions | **Kiểm tra quyền** |
| Metadata columns, Views | **Cột thông tin, Dạng xem** |
| Major/minor versions, Check-out | **Phiên bản chính/phụ, Lấy ra (check-out)** |
| 2-stage recycle bin | **Thùng rác 2 tầng** |
| Retention / Legal hold, Sensitivity labels | **Chính sách lưu giữ / Giữ pháp lý, Nhãn phân loại** |

---

## 1. MÔ HÌNH LƯU TRỮ

### 1.1. Cây đối tượng có thể phân quyền (Securable)
```
Tenant (đơn vị sử dụng hệ thống)
├─ Drive của tôi (mỗi user một drive)
│  └─ Library mặc định
│     └─ Thư mục → Thư mục con → Tệp
└─ Không gian dùng chung (Space)  ← sở hữu bởi TỔ CHỨC, không thuộc cá nhân
   ├─ Library 1 ("Tài liệu")
   │  └─ Thư mục → ... → Tệp
   └─ Library 2 ("Video chiến dịch 2026") ...
```
- Mọi node (Space, Library, Folder, File) là một **Securable**, có `securable_id`, `parent_securable_id` và `acl_scope_id` (xem mục 4).
- **Quyền sở hữu**:
  - Trong *Drive của tôi*: tệp và thư mục thuộc **người tạo** (owner).
  - Trong *Space*: tệp thuộc **Space**. User rời đơn vị thì tệp vẫn còn, giống Shared Drive của Google. Trường `created_by` vẫn ghi người tạo.
- **Chuyển quyền sở hữu** (Drive của tôi): owner chuyển cho user khác trong tenant; người nhận phải chấp nhận (giống Google). Owner cũ thành Biên tập viên.
- **Di chuyển giữa Drive của tôi và Space**:
  - Chuyển từ Drive của tôi vào Space: quyền sở hữu chuyển sang Space. Thao tác yêu cầu người thực hiện là owner của tệp và có quyền "Thêm nội dung" ở đích.
  - Chuyển từ Space ra ngoài: yêu cầu quyền Quản lý ở Space nguồn và tuân thủ chính sách Space (có thể bị cấm).
  - Khi di chuyển, tệp **mất quyền kế thừa cũ, nhận quyền kế thừa ở đích**, nhưng **giữ các quyền cấp trực tiếp** (giống Google). Trước khi thực hiện, hiện cảnh báo liệt kê ai sẽ mất hoặc được thêm quyền.

### 1.2. Lối tắt (Shortcut)
- User thêm lối tắt của bất kỳ tệp/thư mục nào mình có quyền vào Drive của tôi (hoặc vào thư mục trong Space nếu có quyền thêm).
- Lối tắt **không mang quyền**: mở lối tắt mà không có quyền với đích thì hiện "Bạn cần quyền truy cập" và nút Yêu cầu quyền.
- Xóa lối tắt không xóa đích. Đích bị xóa thì lối tắt hiện trạng thái "đã bị xóa".
- App desktop sync và WebDAV: lối tắt tới thư mục hiện như thư mục; lối tắt tới tệp tải nội dung đích (chỉ khi có quyền).

### 1.3. Các chế độ xem
Gồm: Drive của tôi, Không gian dùng chung (danh sách Space được tham gia), **Được chia sẻ với tôi** (nhóm theo người chia sẻ, lọc theo loại/ngày), **Tôi đã chia sẻ**, Gần đây, Có gắn sao, Thùng rác, Bộ nhớ (danh sách tệp theo dung lượng giảm dần).
- Tệp được chia sẻ riêng nằm sâu trong cây mà user không có quyền ở thư mục cha: hiện trong "Được chia sẻ với tôi". Breadcrumb chỉ hiển thị từ node cao nhất mà user có quyền (không làm lộ tên thư mục cha).

### 1.4. Thư viện tài liệu (Library) — cấu hình kiểu SharePoint
Mỗi Library có cài đặt riêng:
- **Phiên bản**: chỉ phiên bản chính (1, 2, 3…) hoặc chính + phụ (1.0, 1.1, 1.2 → Xuất bản 2.0). Bản phụ (bản nháp) chỉ người có quyền "Xem bản nháp" thấy. Có giới hạn số phiên bản giữ lại (mặc định 100 bản chính, 10 bản phụ/bản chính) và tùy chọn tự xóa bản cũ hơn N ngày. Dung lượng các phiên bản **tính vào quota**.
- **Bắt buộc check-out** trước khi sửa (tùy chọn): file bị khóa bởi người check-out; người khác chỉ xem. Có check-in kèm ghi chú, hủy check-out, và Quản lý được ép check-in.
- **Phê duyệt nội dung** (tích hợp luồng phê duyệt đã có trong SPEC): tệp mới/sửa ở trạng thái "Chờ duyệt"; người không có quyền duyệt chỉ thấy bản đã duyệt gần nhất.
- **Cột thông tin (metadata columns)**: Văn bản 1 dòng, Văn bản nhiều dòng, Số, Ngày giờ, Lựa chọn (đơn/đa), Có/Không, Người/Nhóm, Tra cứu danh mục dùng chung (catalog), Liên kết, Thẻ. Mỗi cột có các thuộc tính: bắt buộc, giá trị mặc định (có thể theo thư mục), duy nhất, kiểm tra hợp lệ. Có **cột hệ thống** chỉ đọc: Tên, Loại, Dung lượng, Người tạo, Ngày tạo, Người sửa, Ngày sửa, Phiên bản, Trạng thái duyệt, Nhãn phân loại, Đang check-out bởi.
- **Dạng xem (Views)**: chọn cột, sắp xếp, lọc, nhóm theo cột, hiển thị phẳng (không thư mục), lưới chỉnh sửa nhanh (sửa metadata hàng loạt như Excel). Có dạng xem công khai (cho mọi người dùng Library) và dạng xem cá nhân. Đặt được dạng xem mặc định.
- **Mẫu tài liệu** (template) cho nút "Mới" của Library.
- Định dạng tệp cho phép/chặn và dung lượng tối đa riêng cho Library (không vượt mức của tenant).

---

## 2. CHỦ THỂ ĐƯỢC CẤP QUYỀN (Principal)

| Loại | Mô tả |
|---|---|
| `user` | Người dùng nội bộ |
| `group` | Nhóm người dùng do quản trị tạo (đã có trong SPEC), hỗ trợ **nhóm lồng nhóm** (chống vòng lặp) |
| `org_unit` | Đơn vị/phòng ban; tùy chọn **"bao gồm đơn vị con"** |
| `space_group` | Nhóm mặc định của mỗi Space: Chủ sở hữu, Thành viên, Khách xem; Space tạo thêm nhóm riêng được |
| `everyone_in_tenant` | Mọi người trong đơn vị (tương đương "Anyone in organization") |
| `guest` | Khách bên ngoài, được mời qua email, đăng nhập bằng **mã OTP gửi email** (không cần mật khẩu), có hạn dùng |
| `link` | Liên kết chia sẻ (mục 5.3), là một principal đặc biệt |

- Bảng `principal_memberships` được **làm phẳng sẵn** (materialized): mỗi user có tập principal_id mà nó thuộc về (chính nó, các group kể cả lồng nhau, org_unit và các org_unit cha nếu được cấp kiểu "bao gồm đơn vị con", space_group, everyone). Cập nhật khi thay đổi thành viên nhóm/đơn vị (qua domain event). Mỗi user có `membership_version` để vô hiệu cache.
- Khách (guest): quản trị bật/tắt; giới hạn domain email được mời (allowlist/blocklist); khách chỉ thấy những gì được chia sẻ trực tiếp, không tìm được người dùng nội bộ, không thấy "Mọi người trong đơn vị"; tài khoản khách tự hết hạn sau N ngày không dùng.

---

## 3. QUYỀN NGUYÊN TỬ & CẤP QUYỀN

### 3.1. Quyền nguyên tử (seed trong code, enum `Permission` dạng flags 64-bit)
Nhóm **Xem**: `ViewItems` (thấy trong danh sách + xem metadata), `OpenItems` (xem nội dung/preview), `Download` (tải xuống, sync xuống máy, WebDAV đọc), `Print`, `CopyContent` (sao chép nội dung trong trình xem), `ViewVersions`, `ViewDrafts` (thấy bản phụ/chưa duyệt), `ViewActivity` (xem ai đã xem/sửa).
Nhóm **Đóng góp**: `AddItems` (tải lên, tạo mới), `EditItems` (sửa nội dung, metadata, tạo phiên bản), `EditOwnItemsOnly` (chỉ sửa/xóa tệp do mình tạo; dùng cho cấp "Đóng góp"), `DeleteItems` (đưa vào thùng rác), `RenameMove`, `CreateFolders`, `Comment`, `CheckOutOverride`, `RestoreVersions`.
Nhóm **Chia sẻ/Quản lý**: `ShareInternal` (chia sẻ cho người trong đơn vị), `ShareExternal` (khách/liên kết "Bất kỳ ai"), `CreateLinks`, `ManagePermissions` (thêm/bớt quyền, ngắt kế thừa), `ApproveItems`, `ManageLibrarySettings` (cột, dạng xem, phiên bản, phê duyệt), `ManageSpace` (thành viên, cài đặt, quota, thùng rác tầng 2), `ManageRetention`.

### 3.2. Cấp quyền mặc định (Permission Level), quản trị tùy biến hoặc tạo mới được
| Cấp quyền (tên hiển thị) | Tương đương | Quyền chính |
|---|---|---|
| **Chủ sở hữu / Quản lý** | Google Manager, SP Full Control | Tất cả |
| **Quản lý nội dung** | Google Content manager | Mọi thứ trừ `ManageSpace`, `ManageRetention`; có `ManagePermissions` ở cấp thư mục/tệp (tùy chính sách Space) |
| **Biên tập** | Google Contributor/Editor, SP Edit | Xem đầy đủ + Thêm, Sửa, Xóa, Đổi tên/Di chuyển, Tạo thư mục, Bình luận, Khôi phục phiên bản; chia sẻ nội bộ nếu chính sách cho phép |
| **Đóng góp** | SP Contribute | Xem + Thêm, `EditOwnItemsOnly`, Bình luận |
| **Người nhận xét** | Google Commenter | Xem + Tải xuống (nếu không bị chặn) + Bình luận |
| **Người xem** | Google Viewer, SP Read | `ViewItems`, `OpenItems`, `Download`, `Print`, `CopyContent`, `ViewVersions` |
| **Chỉ xem (không tải)** | SP View Only, Google "chặn tải/in/sao chép" | `ViewItems`, `OpenItems` (trình xem có **watermark** tên người xem + thời gian; không tải, không in, không sao chép; không đồng bộ xuống máy) |
| **Duyệt nội dung** | SP Approve | Người xem + `ViewDrafts`, `ApproveItems` |

Màn hình quản trị **"Cấp quyền"**: dạng ma trận checkbox quyền nguyên tử × cấp quyền, có mô tả tiếng Việt cho từng quyền. Cấp quyền mặc định không xóa được nhưng sao chép ra để tùy biến được. Khi sửa một cấp quyền thì cache quyền của mọi người dùng nó bị vô hiệu.

### 3.3. Quy tắc tổ hợp
- Quyền hiệu lực = **HỢP** (OR) quyền của mọi ACE áp dụng cho các principal của user tại scope hiện hành, sau đó **TRỪ** các hạn chế (mục 3.4).
- **Không có ACE "Từ chối"** (giống Google/SharePoint). Muốn hạn chế thì ngắt kế thừa.
- Owner của tệp trong Drive của tôi luôn có toàn quyền với tệp đó.
- Quản trị tenant **không tự động thấy nội dung** của người khác. Phải dùng chức năng "Truy cập khẩn cấp" (mục 8.5) có ghi audit và thông báo.

### 3.4. Hạn chế ghi đè (áp dụng SAU khi tính OR)
Các hạn chế dưới đây có thể đến từ: chính sách tenant, chính sách Space, nhãn phân loại (mục 7), cài đặt trên chính tệp (Google: "Người xem và người nhận xét có thể thấy tùy chọn tải xuống, in, sao chép"), trạng thái tệp (check-out bởi người khác, đang bị Giữ pháp lý, Space bị khóa/lưu trữ), và loại principal (khách không có `ShareInternal`/`ShareExternal` trừ khi được cho phép).
- Ví dụ: tệp mang nhãn "Hạn chế" thì loại bỏ `ShareExternal`, `CreateLinks` loại "Bất kỳ ai", và loại `Download` với người ngoài Space.
- Ví dụ: Space bị khóa thì loại bỏ mọi quyền ghi.

---

## 4. KẾ THỪA & TÍNH QUYỀN HIỆU LỰC (phần cốt lõi, phải làm thật kỹ)

### 4.1. Mô hình scope (theo cách SharePoint)
- Mỗi securable có `has_unique_acl` (bool) và `acl_scope_id` = securable_id của **node tổ tiên gần nhất (kể cả chính nó) có `has_unique_acl = true`**. Space, Library gốc và Drive của tôi luôn có ACL riêng.
- **Ngắt kế thừa** tại node X:
  - Người dùng chọn **"Sao chép quyền hiện tại"** (mặc định) hoặc **"Bắt đầu trống (chỉ giữ Quản lý)"**.
  - Đặt `has_unique_acl = true`, rồi cập nhật `acl_scope_id` của toàn bộ cây con đang trỏ về scope cũ. Dùng một câu UPDATE theo `path` (ltree), không lặp đệ quy trong code.
- **Khôi phục kế thừa** tại X: xóa ACL riêng của X, đặt `has_unique_acl = false`, cây con trỏ về scope của cha. Có tùy chọn "Khôi phục kế thừa cho toàn bộ mục con có quyền riêng" (giống SharePoint "Delete unique permissions").
- **Cấp quyền trực tiếp cho 1 tệp/thư mục** trong Space (người dùng bấm Chia sẻ):
  - Nếu node chưa có ACL riêng thì **tự động ngắt kế thừa kiểu sao chép** rồi thêm ACE (giống SharePoint). Đây là cách mặc định.
  - Cờ tenant `sharing_mode = google_additive` thì thay bằng cách lưu ACE bổ sung (`acl_additive`) trên node mà không ngắt kế thừa. Khi đó quyền hiệu lực = scope ACL ∪ các ACE bổ sung trên node và tổ tiên. **Cài đặt cả 2 chế độ**; mặc định `sharepoint_scope`.
- Di chuyển node: tính lại `acl_scope_id` cho cây con (giữ ACL riêng nếu có), ghi audit `permission_inherited_changed`.

### 4.2. Truyền quyền ngược lên ("Limited access" ẩn)
User có quyền với tệp sâu bên trong nhưng không có quyền ở thư mục cha: **không cấp quyền đọc thư mục cha**. Họ truy cập qua "Được chia sẻ với tôi" hoặc link. API trả node dưới dạng "gốc ảo" cho user đó.

### 4.3. Thuật toán kiểm tra quyền
```
EffectivePermissions(user, securable):
  1. principals = PrincipalSet(user)                       // cache theo membership_version
  2. scope = securable.acl_scope_id
  3. perms = OR(level.permissions for ace in acl_entries(scope)
                where ace.principal ∈ principals and not expired)
     (+ ACE bổ sung nếu sharing_mode = google_additive)
  4. if securable thuộc Drive của tôi và user là owner → perms = ALL
  5. perms |= quyền đến từ link token trong request (nếu có, mục 5.3)
  6. perms &= ~Restrictions(securable, user)               // mục 3.4
  7. return perms
```
- `IPermissionService` gồm: `GetEffective(user, id)`, `Has(user, id, Permission)`, `Demand(...)` (ném 403/404), `Explain(user, id)` trả danh sách nguồn quyền (ACE nào, cấp từ đâu, qua nhóm nào, hạn chế nào đã loại bỏ quyền gì).
- **Không làm lộ sự tồn tại**: user không có `ViewItems` thì trả **404** chứ không phải 403. Riêng mở link hoặc mở trực tiếp URL thì hiện trang "Bạn cần quyền truy cập" có nút Yêu cầu quyền, chỉ hiển thị tên chủ sở hữu và không hiện tên tệp nếu cấu hình yêu cầu.
- **Cache**:
  - L1 theo request.
  - L2 in-memory (IMemoryCache) theo key `(user_id, scope_id, membership_version, scope_acl_version)`. Bảng `acl_scopes` có cột `acl_version` tăng mỗi khi sửa ACL.
  - Có thể thêm Redis tùy chọn nếu chạy nhiều instance API.
- Viết thêm **`ReferencePermissionEvaluator`**: chậm, đi ngược từng node lên gốc, không cache, không dùng acl_scope_id. Dùng làm chuẩn để so sánh trong test (mục 13).

### 4.4. Lọc theo quyền (security trimming) cho truy vấn danh sách/tìm kiếm
- Mọi truy vấn danh sách, tìm kiếm, dashboard, thống kê, "Gần đây", thông báo, gợi ý @mention, đồng bộ (change feed), WebDAV PROPFIND **phải lọc theo quyền ngay trong SQL**. Không được lọc sau khi đã phân trang.
- Cách làm:
  - Bảng `scope_grants(scope_id, principal_id, permissions bigint)` là bản làm phẳng của ACL: mỗi dòng gồm scope, principal và OR-mask.
  - Truy vấn: `WHERE s.acl_scope_id IN (SELECT scope_id FROM scope_grants WHERE principal_id = ANY(@principals) AND permissions & @ViewItems <> 0)`, cộng với nhánh owner và nhánh ACE bổ sung.
  - Đánh index `scope_grants(principal_id, scope_id)` và `items(acl_scope_id)`.
- Change feed của desktop sync: sự kiện thay đổi ACL phải sinh ra `access_granted` / `access_revoked` cho đúng người (diff tập user trước/sau). Làm bằng job nền.

### 4.5. Mục tiêu hiệu năng
- 1 triệu tệp, 50.000 scope riêng, 5.000 user, 2.000 nhóm: `Has()` khi có cache dưới 1 ms, khi không có cache dưới 20 ms.
- Liệt kê thư mục 1.000 mục có lọc quyền: p95 dưới 300 ms. Tìm kiếm toàn hệ thống có lọc quyền: p95 dưới 1 giây.
- Ngắt kế thừa trên thư mục có 100.000 mục con: dưới 10 giây, chạy trong transaction. Nếu lớn hơn thì chạy job nền và hiển thị tiến độ, trong lúc đó khóa ghi trên cây con.
- Viết benchmark (BenchmarkDotNet + seed dữ liệu lớn) và ghi kết quả vào `docs/perf-permissions.md`.

---

## 5. CHIA SẺ (giống Google Drive + SharePoint)

### 5.1. Hộp thoại Chia sẻ (một dialog dùng chung cho web, mobile, menu chuột phải desktop)
- Ô "Thêm người, nhóm, đơn vị hoặc email" gợi ý theo tên, email, đơn vị; chỉ gợi ý người mà user được phép thấy.
- Chọn **cấp quyền** (dropdown: Quản lý nội dung / Biên tập / Đóng góp / Người nhận xét / Người xem / Chỉ xem), **hạn truy cập** (ngày hết hạn, tùy chọn), và tùy chọn "Gửi thông báo" kèm lời nhắn.
- Danh sách **"Người có quyền truy cập"**: avatar, tên, cấp quyền, **nguồn quyền** ("Trực tiếp", "Kế thừa từ thư mục Chiến dịch 2026", "Qua nhóm Phòng Truyền thông", "Thành viên Space"). Có các thao tác: đổi cấp quyền, đặt/bỏ hạn, xóa quyền (chỉ với quyền trực tiếp; quyền kế thừa thì có link "Quản lý ở thư mục cha"), chuyển quyền sở hữu.
- **Quyền truy cập chung** (General access):
  - *Bị hạn chế*: chỉ người được thêm.
  - *Mọi người trong <Tên đơn vị>*: kèm cấp quyền; tùy chọn "có thể tìm thấy trong tìm kiếm" hay "chỉ ai có link".
  - *Bất kỳ ai có liên kết*: kèm cấp quyền; chỉ hiện khi chính sách cho phép.
- **Cài đặt chia sẻ nâng cao** (biểu tượng bánh răng):
  - "Người biên tập có thể thay đổi quyền và chia sẻ" (mặc định bật ở Drive của tôi; ở Space theo chính sách).
  - "Người xem và người nhận xét có thể thấy tùy chọn tải xuống, in và sao chép" (tắt thì thành Chỉ xem).
- Nút **Sao chép liên kết** (liên kết theo quyền truy cập chung).
- Hiện cảnh báo khi chia sẻ ra ngoài đơn vị và khi tệp có nhãn hạn chế.

### 5.2. Yêu cầu quyền truy cập
- User không có quyền mở link thì thấy trang "Bạn cần quyền truy cập", có nút **Yêu cầu quyền** (chọn cấp mong muốn + lời nhắn).
- Yêu cầu được gửi tới owner hoặc người có `ManagePermissions` (theo cấu hình Space: Quản lý Space hoặc owner tệp). Người duyệt nhận thông báo + email và **duyệt/từ chối ngay trong thông báo**, có thể chọn cấp khác.
- Màn hình "Yêu cầu truy cập đang chờ" cho từng Space và từng người. Yêu cầu tự hết hạn sau 30 ngày.

### 5.3. Liên kết chia sẻ (kiểu SharePoint)
Bảng `share_links`, mỗi link là một principal. Có 4 loại:
1. **Bất kỳ ai** (ẩn danh). Phải có chính sách cho phép; có hạn tối đa theo chính sách (mặc định 30 ngày); tùy chọn mật khẩu; tùy chọn chặn tải xuống.
2. **Người trong đơn vị**: phải đăng nhập.
3. **Người cụ thể**: chỉ những người/email được chỉ định mới dùng được link (khách dùng OTP).
4. **Người đã có quyền**: chỉ là đường dẫn, không cấp thêm quyền.

Thuộc tính của link: cấp quyền (Xem / Nhận xét / Biên tập / Chỉ xem), hạn dùng, mật khẩu, chặn tải xuống, số lượt tối đa, ghi audit mỗi lượt truy cập (IP, user nếu có). Link có thể sửa hoặc thu hồi. Token ngẫu nhiên 256-bit; DB chỉ lưu hash.

Màn hình **"Liên kết"** trên mỗi tệp/thư mục: danh sách link, ai tạo, số lượt dùng, chỉnh sửa/thu hồi.

### 5.4. Chính sách chia sẻ ra ngoài (nhiều tầng, **tầng chặt nhất thắng**)
`Tenant ≥ Space ≥ Nhãn phân loại ≥ Tệp`. Các mức:
- `off`: chỉ nội bộ.
- `existing_guests`: chỉ khách đã có trong hệ thống.
- `new_and_existing_guests`: được mời khách mới qua email.
- `anyone`: được tạo link ẩn danh.

Chính sách Space còn có: "Thành viên được chia sẻ cho người ngoài Space", "Cho phép chia sẻ tệp/thư mục riêng lẻ (ngắt kế thừa)", "Ai được duyệt yêu cầu truy cập".

### 5.5. Thông báo & nhắc hạn
Thông báo khi: được chia sẻ, được thêm vào Space, yêu cầu truy cập mới, yêu cầu được duyệt/từ chối, quyền sắp hết hạn (trước 3 ngày, cho cả người được cấp và người cấp, kèm nút **Gia hạn**), quyền đã hết hạn, chuyển quyền sở hữu, link sắp hết hạn.

---

## 6. KHÔNG GIAN DÙNG CHUNG (SPACE)

- **Tạo Space**: theo chính sách, chỉ quản trị hoặc cả người có quyền "Tạo Space" (cấp theo vai trò/đơn vị). Wizard: tên, mô tả, ảnh bìa, đơn vị chủ quản, thành viên ban đầu theo nhóm, mẫu Space (trống / Phòng ban / Dự án / Chiến dịch truyền thông, mỗi mẫu có sẵn Library, cột thông tin, thư mục).
- **Thành viên**: thêm user, nhóm hoặc đơn vị vào 3 nhóm mặc định (Chủ sở hữu → Quản lý; Thành viên → Biên tập; Khách xem → Người xem) hoặc nhóm tùy biến. **Space phải luôn có ít nhất 1 Chủ sở hữu**: chặn xóa Chủ sở hữu cuối cùng.
- **Cài đặt Space**: chính sách chia sẻ (mục 5.4), quota, nhãn phân loại mặc định, cho phép tải xuống/sync/WebDAV, thời gian giữ thùng rác, ai thấy tab Hoạt động.
- **Trạng thái**: Hoạt động → **Lưu trữ** (chỉ đọc, ẩn khỏi danh sách mặc định, vẫn tìm thấy) → **Xóa** (vào thùng rác tenant 93 ngày; bị chặn nếu đang có chính sách lưu giữ/giữ pháp lý).
- **Trang chủ Space**: mô tả, thành viên, tài liệu gần đây trong Space, dung lượng, hoạt động gần đây, các Library.
- **Trang "Quyền của Space"** (kiểu SharePoint Advanced permissions): danh sách các scope có quyền riêng trong Space ("Thư mục X, tệp Y có quyền riêng"), cho phép xem, khôi phục kế thừa, và báo cáo theo tệp.

---

## 7. NHÃN PHÂN LOẠI, LƯU GIỮ, GIỮ PHÁP LÝ

### 7.1. Nhãn phân loại (Sensitivity label)
- Quản trị định nghĩa nhãn. Mặc định: **Công khai**, **Nội bộ**, **Hạn chế**. Mỗi nhãn gồm: màu, mô tả và chính sách (chặn chia sẻ ra ngoài, chặn link ẩn danh, bắt buộc Chỉ xem với người ngoài Space, **watermark** khi xem, chặn sync xuống máy, chặn WebDAV).
- Gán nhãn cho tệp, thư mục (các mục con kế thừa), Space (làm mặc định). Hạ nhãn phải ghi lý do và cần quyền Quản lý. Mọi thay đổi nhãn đều ghi audit.
- **Lưu ý cho tài liệu**: hệ thống KHÔNG dùng để lưu trữ thông tin thuộc bí mật nhà nước. Ghi rõ điều này trong tài liệu hướng dẫn và trong mô tả nhãn. Không đặt nhãn mặc định có tên "Mật", "Tối mật", "Tuyệt mật".

### 7.2. Chính sách lưu giữ (Retention)
- Chính sách gồm: phạm vi (Space / Library / nhãn / loại nội dung theo catalog), thời gian giữ (N năm kể từ ngày tạo hoặc ngày sửa cuối), hành động khi hết hạn (không làm gì / đưa vào danh sách chờ xóa có duyệt / tự xóa).
- Trong thời gian lưu giữ: người dùng vẫn "xóa" được (tệp biến mất khỏi giao diện) nhưng bản gốc được chuyển vào **kho lưu giữ** (preservation store) ẩn, chỉ quản trị lưu giữ thấy. Không bị purge khỏi thùng rác.

### 7.3. Giữ pháp lý (Legal hold)
- Quản trị đặt giữ cho Space, thư mục hoặc toàn bộ tệp của một user. Tất cả phiên bản bị khóa xóa vĩnh viễn, kể cả bởi quản trị, cho đến khi gỡ hold.
- Hold có tên, lý do, người đặt, thời điểm; danh sách hold đang hiệu lực.

---

## 8. THÙNG RÁC, QUOTA, NGHỈ VIỆC, TRUY CẬP KHẨN CẤP, BÁO CÁO

### 8.1. Thùng rác 2 tầng (kiểu SharePoint)
- **Tầng 1**: thùng rác của người dùng và của Space. User tự khôi phục hoặc xóa vĩnh viễn được (thực chất chuyển xuống tầng 2).
- **Tầng 2**: thùng rác quản trị Space/tenant. Chỉ Quản lý Space và quản trị thấy; khôi phục được.
- Tổng thời gian **93 ngày** (cấu hình được), tính từ lúc xóa lần đầu. Hết hạn thì purge, trừ khi đang bị retention/hold.
- Khôi phục về vị trí gốc, **khôi phục cả quyền cũ** (ACL riêng lưu cùng bản ghi xóa). Nếu thư mục cha không còn thì tạo lại thư mục cha.
- Dung lượng: tầng 1 tính vào quota; tầng 2 tính vào quota Space/tenant (cấu hình được).

### 8.2. Quota
- Mức tenant → Space → Drive của tôi (theo user, mặc định theo đơn vị/vai trò).
- Cảnh báo ở 80%/90%/100%. Khi đầy thì chặn tải lên và tạo phiên bản mới (vẫn xem/tải xuống được).
- Màn hình **"Bộ nhớ"** liệt kê tệp lớn nhất và phiên bản cũ chiếm chỗ, có nút "Xóa phiên bản cũ".

### 8.3. Người dùng nghỉ việc / chuyển đơn vị (Offboarding)
Wizard quản trị khi vô hiệu hóa user:
1. Chọn người nhận quyền sở hữu toàn bộ Drive của tôi. Tệp được chuyển vào thư mục "Dữ liệu từ <tên user>" trong Drive của người nhận, **giữ nguyên mọi quyền chia sẻ** đang có.
2. Xử lý các link chia sẻ do user tạo: giữ hoặc thu hồi.
3. Gỡ khỏi tất cả nhóm và Space. Cảnh báo những Space mà user là Chủ sở hữu duy nhất.
4. Xuất báo cáo bàn giao (PDF/Excel).

Chuyển đơn vị: tự cập nhật membership org_unit, kèm báo cáo các quyền thay đổi theo.

### 8.4. Báo cáo quyền truy cập (quản trị & Quản lý Space)
- **Kiểm tra quyền**: nhập user/nhóm và chọn tệp/thư mục/Space, hiện quyền hiệu lực + giải thích (từ `Explain`).
- **"Người này truy cập được gì?"**: liệt kê mọi Space/thư mục/tệp user có quyền (phân trang), kèm cấp quyền và nguồn.
- **Báo cáo chia sẻ ra ngoài**: tệp chia sẻ cho khách hoặc link ẩn danh, ai chia sẻ, khi nào, hạn đến bao giờ.
- **Báo cáo liên kết**: tất cả link đang hiệu lực, link không hạn, link mật khẩu.
- **Báo cáo quyền riêng**: các scope ngắt kế thừa.
- **Báo cáo quyền sắp/đã hết hạn**.
- **Thao tác hàng loạt**: thu hồi toàn bộ link ẩn danh của Space/tenant, gỡ toàn bộ quyền của 1 user/khách, xóa khách không hoạt động.
- Tất cả xuất được Excel/PDF; báo cáo lớn chạy nền (theo SPEC).

### 8.5. Truy cập khẩn cấp của quản trị
Quản trị tenant tự cấp quyền tạm thời (tối đa 24 giờ) vào Drive của user hoặc một Space. Bắt buộc nhập lý do; ghi audit mức cao; gửi thông báo cho chủ Drive/Quản lý Space (trừ khi chọn "điều tra", cần quyền riêng `AuditInvestigation` và cũng được audit). Quyền tự thu hồi khi hết hạn.

### 8.6. Tab Hoạt động & người đã xem
Mỗi tệp/thư mục có tab **Hoạt động**: ai đã xem, sửa, chia sẻ, đổi quyền, di chuyển, đổi tên (lấy từ audit_logs, lọc theo quyền `ViewActivity`). Có tùy chọn ẩn lịch sử xem của bản thân (giống Google "Activity dashboard privacy"), trừ khi chính sách tenant bắt buộc hiển thị.

---

## 9. MÔ HÌNH DỮ LIỆU (bổ sung/thay thế)

- `spaces` (`id`, `tenant_id`, `name`, `description`, `cover_file_id`, `org_unit_id`, `status` [active | archived | deleted], `policy jsonb`, `quota_bytes`, `used_bytes`, `default_label_id`, `template`)
- `personal_drives` (`id`, `user_id`, `quota_bytes`, `used_bytes`)
- `libraries` (`id`, `space_id` | `personal_drive_id`, `name`, `settings jsonb` [versioning, require_checkout, content_approval, allowed_types, max_size, template_ids], `is_default`)
- `securables` (bảng chung cho cây): `id`, `kind` [space | library | folder | file | shortcut], `parent_id`, `library_id`, `space_id`, `personal_drive_id`, `owner_user_id` (chỉ Drive của tôi), `path ltree`, `name`, `has_unique_acl`, `acl_scope_id`, `label_id`, `is_deleted`, `deleted_stage` [0 | 1 | 2], `deleted_at`, `deleted_by`, `original_parent_id`, `retention_until`, `on_hold`.
  - Bảng `files` và `folders` hiện có chuyển thành bảng chi tiết 1-1 với `securables`, hoặc gộp hẳn. Viết migration giữ dữ liệu cũ.
- `shortcuts` (`securable_id`, `target_id`)
- `acl_scopes` (`scope_id` = securable_id, `acl_version`)
- `acl_entries` (`id`, `scope_id`, `principal_type`, `principal_id`, `permission_level_id`, `expires_at`, `granted_by`, `granted_at`, `source` [direct | space_default | link], `is_additive` bool, `additive_target_id`)
- `permission_levels` (`id`, `name`, `description`, `permissions bigint`, `is_system`)
- `scope_grants` (`scope_id`, `principal_id`, `permissions bigint`): bản làm phẳng, cập nhật trong cùng transaction với acl_entries.
- `principals` (`id`, `type`, `ref_id`, `display_name`); `principal_memberships` (`user_id`, `principal_id`); `group_members` (hỗ trợ lồng nhau); `space_groups`.
- `guests` (`id`, `email`, `display_name`, `invited_by`, `expires_at`, `last_login_at`, `status`)
- `share_links` (`id`, `securable_id`, `type` [anyone | org | specific | existing], `permission_level_id`, `token_hash`, `password_hash`, `expires_at`, `block_download`, `max_uses`, `use_count`, `created_by`, `revoked_at`); `share_link_recipients`
- `access_requests` (`id`, `securable_id`, `requester_id`, `requested_level_id`, `message`, `status`, `decided_by`, `decided_level_id`, `decided_at`)
- `item_restrictions` (`securable_id`, `viewers_can_download`, `editors_can_share`)
- `columns` (`library_id`, `name`, `internal_name`, `type`, `settings jsonb`, `required`, `default`, `order`); `item_field_values` (`securable_id`, `column_id`, `value jsonb`). Đánh index GIN cho truy vấn/lọc.
- `views` (`library_id`, `owner_id` null = công khai, `definition jsonb`, `is_default`)
- `checkouts` (`file_id`, `user_id`, `checked_out_at`)
- `file_versions` bổ sung `major`, `minor`, `is_published`, `approval_status`, `comment`.
- `sensitivity_labels`, `retention_policies`, `retention_assignments`, `legal_holds`, `legal_hold_scopes`, `preservation_items`
- `emergency_access_grants`
- `audit_logs` bổ sung action: `permission_granted`, `permission_revoked`, `permission_level_changed`, `inheritance_broken`, `inheritance_restored`, `link_created`, `link_used`, `link_revoked`, `access_requested`, `access_request_decided`, `ownership_transferred`, `label_changed`, `hold_applied`, `hold_removed`, `emergency_access`, `guest_invited`, `guest_login`.

---

## 10. API (REST `/api/v1`, mọi endpoint đều đi qua `IPermissionService`)

- `spaces` (CRUD, archive, restore), `spaces/{id}/members`, `spaces/{id}/groups`, `spaces/{id}/policy`, `spaces/{id}/unique-scopes`
- `drives/me`, `libraries` (CRUD + settings), `libraries/{id}/columns`, `libraries/{id}/views`
- `items/{id}` (get, patch metadata, move, copy, rename), `items/{id}/children?view=&filter=&sort=&page=`
- `items/{id}/permissions` (GET danh sách kèm nguồn; POST cấp; PATCH đổi cấp/hạn; DELETE)
- `items/{id}/inheritance` (POST break `{copy: bool}`, DELETE restore `{recursive: bool}`)
- `items/{id}/effective-permissions?user=` và `items/{id}/explain?user=` (cần quyền quản lý)
- `items/{id}/general-access`, `items/{id}/restrictions`
- `items/{id}/links` (CRUD), `links/{token}` (resolve, có kiểm tra mật khẩu)
- `items/{id}/access-requests`, `access-requests/{id}/decide`
- `items/{id}/transfer-ownership`, `ownership-transfers/{id}/accept`
- `items/{id}/checkout`, `/checkin`, `/discard-checkout`
- `items/{id}/versions` (bổ sung publish, major/minor)
- `items/{id}/label`, `items/{id}/activity`
- `shortcuts`, `shared-with-me`, `shared-by-me`, `starred`, `storage`
- `trash` (stage 1/2, restore, purge)
- `admin/permission-levels`, `admin/sharing-policy`, `admin/labels`, `admin/retention`, `admin/holds`, `admin/guests`, `admin/offboarding`, `admin/emergency-access`, `admin/reports/*`, `admin/bulk/*`
- Trả lỗi 404 cho tài nguyên không có `ViewItems`; trả 403 kèm mã lỗi tiếng Việt cụ thể khi có xem nhưng không có quyền thao tác (ví dụ `PERM_DOWNLOAD_BLOCKED`, `PERM_EXTERNAL_SHARING_DISABLED`, `ITEM_CHECKED_OUT`, `ITEM_ON_HOLD`).

---

## 11. GIAO DIỆN WEB (React + AntD, tiếng Việt)

1. **Điều hướng trái**: Drive của tôi, Không gian dùng chung (có ô tìm Space), Được chia sẻ với tôi, Tôi đã chia sẻ, Gần đây, Có gắn sao, Bộ nhớ, Thùng rác.
2. **Trình duyệt tệp**: lưới/danh sách, cột theo dạng xem của Library, chọn dạng xem, lọc theo cột, nhóm, lưới sửa nhanh metadata; biểu tượng trên từng mục: đã chia sẻ, có quyền riêng, có link, nhãn, đang check-out, chờ duyệt, lối tắt.
3. **Hộp thoại Chia sẻ** (mục 5.1), **Quản lý quyền truy cập nâng cao** (dạng SharePoint: bảng principal × cấp quyền × nguồn; nút Ngắt/Khôi phục kế thừa; Kiểm tra quyền).
4. **Panel chi tiết bên phải** gồm các tab: Chi tiết (metadata + cột tùy biến sửa được), Hoạt động, Phiên bản, Quyền, Liên kết.
5. **Trang yêu cầu quyền** (khi không có quyền) và **trang link công khai** (mật khẩu, xem trước, tải xuống nếu được phép, watermark).
6. **Space**: danh sách dạng thẻ, wizard tạo, trang chủ Space, cài đặt (Chung, Thành viên, Chính sách chia sẻ, Library, Quota, Trạng thái).
7. **Cài đặt Library**: Phiên bản, Phê duyệt, Check-out, Cột thông tin, Dạng xem, Mẫu, Định dạng cho phép.
8. **Quản trị**: Cấp quyền, Chính sách chia sẻ tenant, Khách, Nhãn phân loại, Lưu giữ, Giữ pháp lý, Nghỉ việc, Truy cập khẩn cấp, các Báo cáo (mục 8.4), Thao tác hàng loạt.
9. **Watermark** cho Chỉ xem và nhãn yêu cầu: trình xem ảnh/PDF/video phủ watermark động (tên, email, thời gian, IP) bằng canvas overlay; ONLYOFFICE dùng tính năng watermark của Document Server; tắt nút tải xuống, in và chuột phải.

Cập nhật cho **app mobile**, **app desktop sync** và **WebDAV** (nếu đã có) để dùng `IPermissionService` + security trimming:
- Mục Chỉ xem không sync, không WebDAV GET.
- Lối tắt hiển thị đúng.
- Sự kiện `access_revoked` được phát đúng người.

---

## 12. DI CHUYỂN DỮ LIỆU TỪ MÔ HÌNH CŨ (SPEC.md)
- `workspaces` personal → `personal_drives` + Library mặc định; group/organization → `spaces` + Library "Tài liệu".
- `workspace_members.member_role` → nhóm mặc định của Space (owner/manager → Chủ sở hữu; editor/uploader → Thành viên với cấp Biên tập/Đóng góp; approver → nhóm tùy biến cấp Duyệt nội dung; viewer → Khách xem).
- `resource_permissions` → `acl_entries` (ngắt kế thừa kiểu sao chép tại node có quyền riêng).
- `shares`/`share_links` cũ → ACE trực tiếp / `share_links` mới.
- Viết migration EF + script kiểm tra sau migrate: với mẫu ngẫu nhiên 10.000 cặp (user, item), quyền cũ ⊆ quyền mới, và in báo cáo khác biệt.

---

## 13. KIỂM THỬ (BẮT BUỘC, đặt trọng tâm vào phân quyền)
1. **Unit test cấp quyền**: mỗi cấp quyền × mỗi thao tác API (bảng ma trận trong `docs/permission-matrix.md`, sinh test tự động từ bảng này).
2. **Property-based test** (FsCheck): sinh ngẫu nhiên cây (đến 5.000 node), nhóm lồng nhau, đơn vị phân cấp, ACE, ngắt/khôi phục kế thừa, di chuyển, link, nhãn, hold. Kiểm tra `PermissionService` (có cache, scope) **luôn bằng** `ReferencePermissionEvaluator` với mọi cặp (user, node), kể cả sau từng thao tác thay đổi (để kiểm tra vô hiệu cache).
3. **Test security trimming**: với mỗi kênh (list, search, recent, starred, shared-with-me, dashboard, thống kê, thông báo, @mention, thumbnail, preview, download, ONLYOFFICE config/callback, change feed, WebDAV, export báo cáo, mobile API), tạo dữ liệu có mục bị cấm và khẳng định không lộ tên, số lượng hay dung lượng.
4. **Test chia sẻ**: 4 loại link, mật khẩu, hết hạn, số lượt, chặn tải, khách OTP, yêu cầu truy cập, chính sách nhiều tầng (tầng chặt nhất thắng).
5. **Test vòng đời**: thùng rác 2 tầng + khôi phục quyền, retention, hold chặn purge, offboarding giữ chia sẻ, chuyển quyền sở hữu, di chuyển giữa Drive và Space.
6. **Test hiệu năng** theo mục 4.5.
7. **Test bảo mật**: IDOR (đổi id trên URL/API), dò token link (rate limit + không lộ khác biệt thời gian), escalation (Biên tập tự nâng mình thành Quản lý khi `editors_can_share` tắt), khách cố liệt kê người dùng.
8. **E2E Playwright** cho các luồng: chia sẻ cho người khác rồi người đó mở; yêu cầu quyền rồi duyệt; ngắt kế thừa rồi kiểm tra quyền; link ẩn danh có mật khẩu; nghỉ việc.

---

## 14. TÀI LIỆU
- `docs/permissions-model.md` (khái niệm, sơ đồ, thuật toán, ví dụ "vì sao A thấy tệp X"), `docs/permission-matrix.md`, `docs/sharing-policies.md`, `docs/perf-permissions.md`, `docs/migration-permissions.md`.
- Hướng dẫn sử dụng tiếng Việt: chia sẻ, link, Space, quyền riêng, yêu cầu quyền, nhãn. Hướng dẫn quản trị: chính sách, khách, báo cáo, lưu giữ, hold, nghỉ việc, truy cập khẩn cấp.
- Cập nhật `docs/traceability.md` (các chức năng HSMT #28–31, #49, #54–62 trỏ sang module mới) và `docs/attt-742.md` (kiểm soát truy cập, phân quyền tối thiểu, nhật ký).

---

## 15. KẾ HOẠCH THEO PHASE
1. **Perm-Phase 1 — Mô hình & migration**: securables, spaces, personal drives, libraries, principals + memberships làm phẳng, permission levels, acl_scopes/entries/scope_grants; migration từ mô hình cũ + script đối chiếu.
2. **Perm-Phase 2 — Permission engine**: `IPermissionService` (Get/Has/Demand/Explain), ReferenceEvaluator, cache + vô hiệu, ngắt/khôi phục kế thừa, hạn chế, property-based test. **Chưa làm UI.**
3. **Perm-Phase 3 — Security trimming toàn hệ thống**: áp vào mọi query/kênh, kèm test ở mục 13.3.
4. **Perm-Phase 4 — Chia sẻ**: hộp thoại chia sẻ, quyền truy cập chung, cài đặt nâng cao, 4 loại link, khách OTP, yêu cầu truy cập, chính sách nhiều tầng, thông báo & nhắc hạn.
5. **Perm-Phase 5 — Space & Drive của tôi**: tạo Space + mẫu, thành viên/nhóm, cài đặt, trạng thái, trang chủ; các chế độ xem; lối tắt; chuyển quyền sở hữu; di chuyển Drive ↔ Space.
6. **Perm-Phase 6 — Library kiểu SharePoint**: phiên bản chính/phụ, phê duyệt, check-out, cột thông tin, dạng xem, lưới sửa nhanh, mẫu.
7. **Perm-Phase 7 — Quản trị nâng cao**: cấp quyền tùy biến, nhãn phân loại + watermark, lưu giữ, hold, thùng rác 2 tầng, quota nhiều tầng, nghỉ việc, truy cập khẩn cấp.
8. **Perm-Phase 8 — Báo cáo & thao tác hàng loạt**: kiểm tra quyền, "người này truy cập được gì", chia sẻ ngoài, link, quyền riêng, hết hạn; xuất Excel/PDF.
9. **Perm-Phase 9 — Đồng bộ các client**: mobile, desktop sync (access_granted/revoked, Chỉ xem không sync), WebDAV, ONLYOFFICE (quyền sửa/nhận xét/xem, watermark).
10. **Perm-Phase 10 — Hiệu năng, bảo mật, E2E, tài liệu**: benchmark mục 4.5, test bảo mật mục 13.7, Playwright, hoàn thiện tài liệu.

---

## 16. TIÊU CHÍ HOÀN THÀNH
- [ ] Chia sẻ một tệp trong Space cho 1 người ngoài Space: người đó thấy tệp trong "Được chia sẻ với tôi", không thấy thư mục cha hay các tệp khác.
- [ ] Ngắt kế thừa một thư mục và bỏ nhóm Thành viên: thành viên Space không còn thấy thư mục đó ở bất kỳ kênh nào (web, tìm kiếm, mobile, desktop sync, WebDAV, dashboard).
- [ ] "Kiểm tra quyền" giải thích đúng nguồn quyền (trực tiếp / kế thừa / qua nhóm lồng / qua đơn vị cha / qua link) và hạn chế đang áp dụng.
- [ ] Người Chỉ xem mở được tệp có watermark, không tải/in/sao chép được, tệp không xuất hiện trong desktop sync.
- [ ] Link "Bất kỳ ai" có mật khẩu và hạn dùng hoạt động; bị chặn khi chính sách tenant/Space/nhãn không cho phép.
- [ ] Yêu cầu truy cập: gửi, duyệt với cấp khác, người yêu cầu nhận thông báo và mở được.
- [ ] Quyền có hạn tự hết hiệu lực và gửi nhắc trước 3 ngày.
- [ ] Nghỉ việc: chuyển toàn bộ Drive của tôi cho người khác, các chia sẻ cũ vẫn hoạt động.
- [ ] Hold chặn xóa vĩnh viễn, kể cả với quản trị.
- [ ] Property-based test: PermissionService ≡ ReferenceEvaluator trên 10.000 kịch bản.
- [ ] Đạt mục tiêu hiệu năng mục 4.5.
- [ ] Toàn bộ UI/thông báo/tài liệu tiếng Việt; không TODO/stub.

**Bắt đầu với Perm-Phase 1. Trước khi code, đọc `SPEC.md` và code hiện tại, liệt kê những gì sẽ bị thay thế hoặc di chuyển, nêu giả định, rồi tiến hành.**
