# Định dạng gói dữ liệu bàn giao (Xuất toàn bộ dữ liệu)

Đáp ứng yêu cầu chất lượng 4.2 (dữ liệu bàn giao ở định dạng thông dụng, đọc, kiểm tra và tái sử dụng được) và mục 3.6 HSMT.

## Cách tạo

*Quản trị > Sao lưu > Xuất toàn bộ dữ liệu* (quyền `dataexport.admin`). Hệ thống tạo gói ở chế độ nền, gửi thông báo khi xong; tải gói tại *Báo cáo của tôi*. Gói được lưu tại volume `backups`, thư mục `exports/`.

Tùy chọn: kèm các phiên bản cũ của tệp; kèm nhật ký hoạt động.

## Cấu trúc tệp ZIP (mã hóa tên UTF-8)

```
bluedrive-du-lieu-ban-giao-YYYYMMDD-HHmm.zip
├─ README.md
├─ metadata.json
├─ metadata.csv
├─ audit-logs.csv                (nếu chọn kèm nhật ký)
├─ files/
│  ├─ <Tên nhóm tài nguyên>/<cây thư mục>/<tệp>
│  └─ Ca nhan - <tên đăng nhập>/<cây thư mục>/<tệp>
└─ versions/<mã tệp>/v<số phiên bản>-<tên tệp>   (nếu chọn kèm phiên bản cũ)
```

- Tệp trong `files/` là **bản hiện tại đã giải mã**, giữ nguyên tên gốc (ký tự không hợp lệ trên hệ điều hành được thay bằng `_`; trùng tên được thêm hậu tố ` (1)`).
- Thời gian sửa đổi của từng mục trong ZIP là thời gian sửa đổi tệp (giờ Việt Nam).
- Tệp đang trong thùng rác không được xuất.

## metadata.json

Đối tượng JSON (UTF-8, thụt lề) gồm:

| Khóa | Nội dung |
|---|---|
| `format` | `bluedrive-export/1.0` |
| `exportedAt`, `appVersion` | Thời điểm xuất (UTC, ISO 8601) và phiên bản phần mềm |
| `organizations[]` | `id, parentId, code, name, type (DonVi/PhongBan), address, phone, email, isActive` |
| `users[]` | `id, username, fullName, email, phone (đã giải mã), organizationId, position, isActive, quotaBytes, usedBytes, roles[]` (không có mật khẩu) |
| `userGroups[]` | `id, name, description, members[] (userId)` |
| `catalogs[]` | Danh mục dùng chung: `id, code, name, items[] {id, parentId, code, name, isActive}` |
| `workspaces[]` | `id, name, type (Personal/Group/Organization), organizationId, ownerId, quotaBytes, usedBytes, isLocked, members[] {userId, role}` |
| `folders[]` | `id, workspaceId, parentId, name, path (đường dẫn hiển thị), requiresApproval, breakInheritance, createdAt, createdBy` |
| `files[]` | `id, workspaceId, folderId, name, exportPath (vị trí trong ZIP), extension, mimeType, mediaType, sizeBytes, checksumSha256, title, description, tags[], approval, uploaderOrganizationId, createdAt, createdBy, updatedAt, updatedBy, catalogItems[], versions[] {versionNo, sizeBytes, checksum, changeNote, createdAt, createdBy, current}` |
| `permissions[]` | Phân quyền: `resourceType, resourceId, principalType, principalId, level (View/Download/Edit/Manage), inherit, expiresAt` |
| `shares[]` | Chia sẻ nội bộ: `resourceType, resourceId, sharedBy, sharedWithType, sharedWithId, level, message, createdAt` |
| `comments[]` | Thảo luận: `id, fileId, parentId, content, createdBy, createdAt, attachmentFileIds[]` |

Mọi `id` là UUID; thời gian theo UTC dạng ISO 8601.

## metadata.csv

UTF-8 có BOM (mở trực tiếp bằng Excel), dấu phân cách `,`, ô có dấu phẩy/ngoặc kép/xuống dòng được bao trong `"..."`. Cột:

`id, duong_dan, ten, tieu_de, mo_ta, the (phân tách ;), loai, dinh_dang, dung_luong_byte, sha256, nguoi_tao, ngay_tao, ngay_sua, phien_ban`

## audit-logs.csv

UTF-8 có BOM. Cột: `id, thoi_gian_utc, nguoi_dung, hanh_dong, loai_tai_nguyen, ma_tai_nguyen, ten_tai_nguyen, ip, hash`. Cột `hash` là mắt xích chuỗi băm SHA-256 của nhật ký gốc, dùng để đối chiếu tính toàn vẹn.

## Kiểm tra tính toàn vẹn

- So sánh `checksumSha256` trong `metadata.json` với SHA-256 của tệp tương ứng trong `files/` (PowerShell: `Get-FileHash -Algorithm SHA256 <tệp>`; Linux: `sha256sum <tệp>`).
- Số tệp trong `files/` bằng số phần tử `files[]`.
