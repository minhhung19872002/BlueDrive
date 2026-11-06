# Ma trận cấp quyền × thao tác

Mỗi thao tác API cần một **quyền nguyên tử** (`AccessRight`, `src/BlueDrive.Domain/Permissions/AccessRight.cs`). Một người có quyền trên một mục là
hợp (OR) các quyền của mọi cấp quyền được cấp cho họ (trực tiếp, qua nhóm, nhóm lồng, đơn vị, nhóm Space, liên kết) trên scope của mục, sau đó
trừ đi các **hạn chế** (mục 3.4 đặc tả: vai trò Chỉ xem, Space khóa / lưu trữ, nhãn, chưa duyệt, đang check-out, Drive của người khác…).

Bảng dưới là 8 cấp quyền có sẵn. Cấp tùy biến do quản trị tạo ở *Quản trị → Cấp quyền truy cập*. Test `PermissionMatrixDocTests` đọc chính
bảng này và đối chiếu từng ô với mã: sửa cấp quyền mà không sửa bảng thì CI đỏ.

<!-- matrix:start -->
| Thao tác (API) | Quyền | `owner` | `content_manager` | `editor` | `contributor` | `commenter` | `viewer` | `view_only` | `approver` |
|---|---|---|---|---|---|---|---|---|---|
| Thấy trong danh sách, xem thông tin (`GET /files/{id}`, danh sách, tìm kiếm) | `ViewItems` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Xem nội dung, xem trước, mở ONLYOFFICE chế độ xem | `OpenItems` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Tải xuống, đồng bộ xuống máy (`download-url`, `sync/items/{id}/content`) | `Download` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | ✓ |
| In (trình xem, ONLYOFFICE) | `Print` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | ✓ |
| Sao chép nội dung trong trình xem | `CopyContent` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | ✓ |
| Xem các phiên bản (`/files/{id}/versions`) | `ViewVersions` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | ✓ |
| Thấy bản nháp, bản chưa duyệt | `ViewDrafts` | ✓ | ✓ | ✓ | — | — | — | — | ✓ |
| Xem hoạt động (ai đã xem, đã sửa) | `ViewActivity` | ✓ | ✓ | — | — | — | — | — | — |
| Tải lên, tạo tệp (`/files/upload`, `/office/new`) | `AddItems` | ✓ | ✓ | ✓ | ✓ | — | — | — | — |
| Tạo thư mục (`POST /folders`) | `CreateFolders` | ✓ | ✓ | ✓ | — | — | — | — | — |
| Sửa nội dung, thông tin, tạo phiên bản, ONLYOFFICE sửa | `EditItems` | ✓ | ✓ | ✓ | — | — | — | — | — |
| Chỉ sửa / xóa tệp do mình tạo | `EditOwnItemsOnly` | ✓ | ✓ | — | ✓ | — | — | — | — |
| Đổi tên, di chuyển (`/files/{id}/name`, `/items/move`) | `RenameMove` | ✓ | ✓ | ✓ | — | — | — | — | — |
| Xóa vào thùng rác (`/items/delete`) | `DeleteItems` | ✓ | ✓ | ✓ | — | — | — | — | — |
| Bình luận, ONLYOFFICE nhận xét | `Comment` | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — |
| Khôi phục phiên bản | `RestoreVersions` | ✓ | ✓ | ✓ | — | — | — | — | — |
| Hủy check-out của người khác | `CheckOutOverride` | ✓ | ✓ | — | — | — | — | — | — |
| Chia sẻ cho người trong đơn vị (`/items/{id}/permissions`) | `ShareInternal` | ✓ | ✓ | ✓ | — | — | — | — | — |
| Chia sẻ cho khách, liên kết "Bất kỳ ai" | `ShareExternal` | ✓ | ✓ | — | — | — | — | — | — |
| Tạo liên kết chia sẻ (`/share-links`) | `CreateLinks` | ✓ | ✓ | — | — | — | — | — | — |
| Quản lý quyền, ngắt / khôi phục kế thừa, hạ nhãn (`/items/{id}/inheritance`, `/items/{id}/label`) | `ManagePermissions` | ✓ | ✓ | — | — | — | — | — | — |
| Phê duyệt nội dung | `ApproveItems` | ✓ | ✓ | — | — | — | — | — | ✓ |
| Cài đặt thư viện (cột, dạng xem, phiên bản) | `ManageLibrarySettings` | ✓ | ✓ | — | — | — | — | — | — |
| Quản lý Space (thành viên, cài đặt, quota, thùng rác tầng 2) | `ManageSpace` | ✓ | — | — | — | — | — | — | — |
| Quản lý lưu giữ | `ManageRetention` | ✓ | — | — | — | — | — | — | — |
<!-- matrix:end -->

## Vai trò hệ thống (RBAC) giới hạn thêm

Giống Google Drive, vai trò hệ thống **không** giới hạn mọi thứ:

| Quyền đến từ | Vai trò hệ thống có giới hạn không? |
|---|---|
| **Tài liệu của tôi** (chủ Drive) | **Không**: chủ Drive toàn quyền trong Drive của mình (tải lên, tạo, sửa, xóa, chia sẻ, thùng rác), chỉ giới hạn dung lượng. |
| **Chia sẻ trên tư liệu** (cho người, nhóm người dùng, đơn vị, cả một Space) | **Không**: được chia sẻ cấp nào làm được cấp đó. Ví dụ tài khoản "Chỉ xem" được chia sẻ "Chỉnh sửa" thì sửa được tư liệu đó. |
| **Thành viên Space / nhóm** (vai trò trong nhóm) | **Có**, theo từng chức năng của vai trò: `doc.upload` → thêm mới, tạo thư mục; `doc.edit` → sửa, đổi tên/di chuyển, khôi phục phiên bản, quản lý Space; `doc.delete` → xóa; `doc.share` → chia sẻ, liên kết, quản lý quyền; `doc.approve` → phê duyệt. Xem, tải xuống, bình luận luôn còn. |

| Vai trò | Với tư liệu dùng chung của nhóm/đơn vị (qua thành viên) |
|---|---|
| Quản trị hệ thống | Như vai trò trong nhóm. Không tự thấy nội dung người khác (chỉ qua *Truy cập khẩn cấp*). |
| Chỉnh sửa | Như vai trò trong nhóm. |
| Phê duyệt | Xem, phê duyệt, chia sẻ; không thêm / sửa / xóa. |
| Chỉ xem | Chỉ xem, tải xuống, bình luận. |
| Khách | Chỉ thấy mục được chia sẻ trực tiếp; không chia sẻ tiếp, không tìm người dùng. |

Thùng rác (`trash.*`): thùng rác của *Tài liệu của tôi* luôn dùng được; thùng rác của nhóm cần chức năng tương ứng của vai trò.

## Hạn chế thường gặp

- **Space khóa / lưu trữ**: bỏ mọi quyền ghi (`Writes`), trả 423.
- **Nhãn** "Người ngoài Space chỉ xem": ai không phải thành viên Space chỉ còn `ViewItems | OpenItems`; nhãn có *watermark* thì trình xem phủ hình mờ và tắt tải / in / sao chép.
- **Chưa duyệt / bản nháp**: chỉ người tải lên và người có `EditItems`, `ApproveItems`, `ViewDrafts` thấy.
- **Đang check-out**: chỉ người check-out sửa nội dung; `CheckOutOverride` hủy được.
- **Giữ pháp lý / lưu giữ**: không xóa vĩnh viễn, không xóa phiên bản (kể cả quản trị).
