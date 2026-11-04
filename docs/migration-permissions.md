# Chuyển sang mô hình lưu trữ & phân quyền kiểu Google Drive / SharePoint

Đặc tả: `PROMPT_BlueDrive_Storage_Permissions.md` (thay các phần workspace, ACL, chia sẻ của `SPEC.md`; HSMT #28–31, #49, #54–62).
Tài liệu này ghi (1) mô hình hiện tại, (2) cái gì bị thay thế / di chuyển, (3) giả định và quyết định, (4) cách chuyển dữ liệu theo phase.

## 1. Mô hình hiện tại (đến 04/11/2026)

| Thành phần | Hiện có | Vị trí |
|---|---|---|
| Không gian | `workspaces` (`Personal`, `Group`, `Organization`), chủ `owner_id`, quota, `is_locked` | `Domain/Entities/Storage.cs` |
| Thành viên | `workspace_members` với vai trò `Owner, Manager, Editor, Uploader, Approver, Viewer` | như trên |
| Cây | `folders.path` = `/{id}/{id}/…/` (văn bản, gồm chính nó), `files.folder_id`; cờ `break_inheritance` trên thư mục và tệp | như trên |
| Quyền | `resource_permissions` (đối tượng × principal × `AccessLevel` 0–4 × `inherit` × `expires_at`); `shares` (không hạn); `share_links` | như trên |
| Principal | người dùng, nhóm (phẳng, không lồng), đơn vị (kèm đơn vị cha theo `path`), vai trò | `Identity.cs` |
| Tính quyền | `ResourceAuthorizationService.GetAccessAsync`: quản trị (`doc.admin`) → Quản lý; chủ → Quản lý; thành viên theo vai trò (ngắt kế thừa chỉ giữ Owner/Manager); ACL + chia sẻ trên chuỗi thư mục đến chỗ ngắt; lấy mức cao nhất; giới hạn theo vai trò hệ thống (không `doc.edit` thì tối đa Tải xuống); tệp chưa duyệt ẩn với người không sửa/duyệt | `Application/Security/ResourceAuthorization.cs` |
| Lọc trong SQL | viết tay ở 6 chỗ: danh sách thư mục, tìm kiếm, danh sách không gian, "Được chia sẻ với tôi", dashboard, cây đồng bộ (`WorkspaceAccessMap`) | xem mục 5 |

Những chỗ các bản sao quy tắc đang lệch nhau (sẽ hết khi mọi kênh dùng chung một bộ lọc — Perm-Phase 3):
- lọc SQL so `principal_id` mà không so loại principal; danh sách không gian và danh sách thư mục bỏ qua hạn của quyền;
- cây thư mục và dashboard nhóm bỏ qua ngắt kế thừa; liên kết công khai của thư mục bỏ qua ngắt kế thừa bên dưới;
- `WorkspaceAccessMap` (đồng bộ) xử lý quản trị khác và thiếu quy tắc "Uploader sửa tệp của mình";
- quản trị (`doc.admin`) thấy mọi nội dung ở tìm kiếm và trang tệp nhưng không ở danh sách không gian / được chia sẻ.

## 2. Cái gì bị thay thế / di chuyển

| Cũ | Mới | Ghi chú |
|---|---|---|
| `workspaces` Personal | **Drive của tôi** (`personal_drives`) + Library mặc định | id giữ nguyên để URL, đồng bộ desktop và nhật ký không đổi |
| `workspaces` Group / Organization | **Space** (`spaces`) + Library "Tài liệu" | Space thuộc tổ chức; `owner_id` cũ thành thành viên nhóm Chủ sở hữu |
| `workspace_members.member_role` | nhóm mặc định của Space: Owner/Manager → Chủ sở hữu (Quản lý); Editor → Thành viên (Biên tập); Uploader → nhóm "Đóng góp"; Approver → nhóm "Duyệt nội dung"; Viewer → Khách xem (Người xem) | |
| `folders`, `files` | giữ bảng, thêm bản ghi 1-1 trong `securables` (cùng id) | không gộp bảng: mọi truy vấn hiện có vẫn chạy |
| `folders.break_inheritance`, `files.break_inheritance` | `securables.has_unique_acl` + `acl_scope_id` | |
| `resource_permissions`, `shares` | `acl_entries` trên scope (ngắt kế thừa kiểu sao chép tại nút có quyền riêng, như SharePoint) | `shares` cũ không có hạn → ACE không hạn |
| `AccessLevel` 0–4 | `permission_levels` (cờ 64 bit `Permission`): Xem → Người xem, Tải xuống → Người xem, Sửa → Biên tập, Quản lý → Chủ sở hữu/Quản lý | cũ "Xem" không tải được → **Chỉ xem (không tải)** để không mở rộng quyền |
| nhóm phẳng, đơn vị theo path | `principals` + `principal_memberships` làm phẳng (nhóm lồng nhóm, đơn vị "gồm đơn vị con") | |
| `ResourceAuthorizationService` | `IPermissionService` mới (Get/Has/Demand/Explain) + `ReferencePermissionEvaluator` | Perm-Phase 2 |
| `share_links` | `share_links` mới: 4 loại (Bất kỳ ai / Người trong đơn vị / Người cụ thể / Người đã có quyền), là một principal | Perm-Phase 4 |

## 3. Giả định và quyết định

1. **Một tenant.** Hệ thống phục vụ một đơn vị sử dụng (Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số); `tenant_id` có trong lược đồ nhưng chỉ một giá trị.
2. **Không mật khẩu liên kết** (quyết định ngày 04/11/2026, xác nhận lại cùng ngày; giống Google Drive): mục 5.3 / 16 của đặc tả nói "tùy chọn mật khẩu" — không làm. Liên kết cũ từng có mật khẩu đã chuyển sang "Hạn chế".
3. **Quản trị không tự thấy nội dung** (đặc tả 3.3). Hiện quản trị (`doc.admin`) thấy mọi tệp; sau Perm-Phase 3 chỉ thấy qua "Truy cập khẩn cấp" (Perm-Phase 7). Trong lúc chuyển tiếp, quyền cũ vẫn là nguồn chính.
4. **Chế độ chia sẻ** mặc định `sharepoint_scope` (chia sẻ một mục trong Space tự ngắt kế thừa kiểu sao chép); `google_additive` làm theo cờ tenant.
5. **Chuyển dữ liệu không làm mất quyền:** với mọi cặp (người dùng, mục), quyền mới ⊇ quyền cũ, trừ hai chỗ cố ý chặt hơn: quản trị (giả định 3) và "Hạn chế" thay mật khẩu (giả định 2). Script đối chiếu in danh sách khác biệt.
6. **Chuyển theo từng bước, hệ thống luôn chạy được:** Perm-Phase 1 dựng mô hình mới và **chiếu** (project) dữ liệu cũ sang (trong migration và bằng job dựng lại); mô hình cũ vẫn là nguồn ghi. Perm-Phase 2 chuyển việc ghi và tính quyền sang mô hình mới; Perm-Phase 3 thay mọi bộ lọc SQL viết tay. Bảng cũ chỉ xóa khi không còn chỗ đọc.
7. Đường dẫn cây dùng **ltree** (`securables.path`, nhãn = id dạng 32 ký tự hex) để ngắt/khôi phục kế thừa bằng một câu UPDATE.

## 4. Kế hoạch dữ liệu theo phase

| Phase | Dữ liệu |
|---|---|
| 1 | Tạo `spaces`, `personal_drives`, `libraries`, `securables`, `principals`, `principal_memberships`, `space_groups`, `permission_levels`, `acl_scopes`, `acl_entries`, `scope_grants`. Migration chiếu dữ liệu cũ; job `PermissionProjection` dựng lại khi dữ liệu cũ đổi; lệnh đối chiếu cũ ⊆ mới trên mẫu 10 000 cặp |
| 2 | Ghi quyền vào `acl_entries`; `IPermissionService` mới thay `ResourceAuthorizationService`; cache theo `membership_version` / `acl_version` |
| 3 | Mọi kênh lọc bằng `scope_grants` trong SQL; xóa 6 bộ lọc viết tay |
| 4–10 | theo đặc tả mục 15 |

## 4a. Perm-Phase 1 — cách chiếu dữ liệu cũ sang mô hình mới

**Nút (securable):** mỗi không gian, thư mục, tệp có một dòng `securables` cùng id. Không gian → `kind = space` (Group/Organization) hoặc
`drive` (Personal). Mỗi không gian có một Library mặc định (id mới, `kind = library`) nằm giữa không gian và các thư mục gốc.
`path` (ltree) = chuỗi id từ không gian xuống chính nút.

**Principal:** id của người dùng, nhóm, vai trò dùng lại làm id principal. Đơn vị có hai principal: *đúng đơn vị* (id đơn vị) và
*đơn vị và đơn vị con* (id mới). Mọi quyền cũ cấp cho đơn vị đều có nghĩa "gồm đơn vị con" nên chiếu sang principal thứ hai. Thêm
`everyone` (Mọi người trong đơn vị sử dụng) và nhóm của mỗi không gian. `principal_memberships(user_id, principal_id)` gồm chính người dùng,
các nhóm (kể cả nhóm cha khi lồng nhau), vai trò, đơn vị của mình (đúng đơn vị), "đơn vị và đơn vị con" của mình và mọi đơn vị cha,
các nhóm không gian, `everyone`. Mỗi người dùng có `membership_version`.

**Nhóm của không gian:** Chủ sở hữu (Quản lý), Thành viên (Biên tập), Đóng góp, Duyệt nội dung, Khách xem. Khách xem chuyển từ dữ liệu cũ
nhận **Chỉ xem (không tải)**: thành viên vai trò Người xem trước đây không tải được, chuyển sang "Người xem" sẽ mở rộng quyền (lần đối chiếu
trên prod ngày 04/11/2026 thấy đúng 1 cặp như vậy; quyết định ngày 04/11/2026: giữ quyền hiện tại). Space tạo mới từ Perm-Phase 5 dùng mặc định của đặc tả: Khách xem → Người xem. Chủ cũ
(`owner_id`) của không gian nhóm/đơn vị vào Chủ sở hữu. Drive của tôi: chủ có toàn quyền theo quy tắc chủ sở hữu.

**Cấp quyền cũ → mới:** Xem → *Chỉ xem (không tải)*, Tải xuống → *Người xem*, Sửa → *Biên tập*, Quản lý → *Chủ sở hữu / Quản lý*.

**Scope:** gốc không gian luôn có ACL riêng gồm các nhóm không gian và quyền cấp trên chính không gian (`resource_permissions` loại
workspace). Một thư mục/tệp có ACL riêng khi (a) có cờ ngắt kế thừa, hoặc (b) có quyền / chia sẻ trực tiếp, hoặc (c) nút cha có quyền
"không kế thừa" (`inherit = false`). ACL riêng được dựng như SharePoint:
- (a) ngắt kế thừa: chỉ giữ nhóm Chủ sở hữu (cũ: chỉ Owner/Manager còn quyền dưới chỗ ngắt) + quyền trực tiếp của nút;
- (b) sao chép scope của cha + quyền trực tiếp của nút (cùng principal thì lấy cấp cao hơn, giữ hạn của từng quyền);
- (c) sao chép scope của cha **bỏ** các quyền "không kế thừa" của cha.

`scope_grants(scope_id, principal_id, permissions)` = OR các cấp quyền của ACE còn hạn theo principal. Ở Phase 1, quyền đã hết hạn
không vào `scope_grants`; job dựng lại chạy định kỳ.

**Hạn chế (áp sau OR, giống quy tắc cũ):** không gian bị khóa → bỏ mọi quyền ghi; vai trò hệ thống không có `doc.edit` → bỏ quyền ghi
có được qua thành viên không gian (quyền cấp trực tiếp không bị bỏ, như cũ); tệp chưa duyệt → chỉ người tạo, người có quyền sửa hoặc
duyệt thấy.

**Dựng lại / đối chiếu:** job `permission-projection` (15 phút một lần) và `POST /api/v1/admin/permission-model/rebuild` dựng lại
mô hình mới; `GET /api/v1/admin/permission-model/verify?samples=10000` (quyền `settings.edit`) chọn ngẫu nhiên cặp (người dùng, mục), tính quyền cũ
(`ResourceAuthorizationService`) và quyền mới (`ReferencePermissionEvaluator`: đi từ nút lên gốc, không dùng `acl_scope_id`,
không cache), quy về thang cũ và in mọi cặp mới < cũ (ngoài trường hợp cố ý: quản trị không tự thấy nội dung; liên kết không nằm trong phép so). Kiểm thử:
`PermissionModelTests.The_current_model_is_projected_without_losing_any_right` (3 000 cặp ngẫu nhiên trên toàn bộ dữ liệu kiểm thử: 0 cặp hẹp hơn).

## 5. Các kênh phải chuyển sang bộ lọc chung (Perm-Phase 3)

Danh sách thư mục (`DriveQueries`), cây thư mục, chi tiết tệp, gần đây, có gắn sao, tìm kiếm (`FileSearchService`), danh sách không gian (`WorkspaceFeatures.AccessibleAsync`), được chia sẻ với tôi / người chia sẻ (`ShareFeatures`), dashboard cá nhân và nhóm, thảo luận và @nhắc tên, vé nội dung (tải, xem, ảnh thu nhỏ, xem trước, đính kèm), tải ZIP, ONLYOFFICE (cấu hình, callback, danh sách đang sửa), phiên bản, thao tác trên mục, tải lên, hộp thoại quyền, chia sẻ, liên kết công khai, phê duyệt, thùng rác, đồng bộ (gốc, ảnh chụp cây, luồng thay đổi, `WorkspaceAccessMap`, delta, LAN, ONLYOFFICE desktop), SignalR, nhật ký thay đổi, đính kèm yêu cầu hỗ trợ, báo cáo/xuất dữ liệu.
