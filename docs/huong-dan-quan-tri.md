# Hướng dẫn quản trị BlueDrive

Dành cho cán bộ quản trị hệ thống (vai trò **Quản trị hệ thống**). Các màn hình quản trị nằm ở nhóm menu *Quản trị* bên trái và chỉ hiển thị theo quyền.

## 1. Cơ cấu tổ chức (HSMT #1–3)
*Quản trị > Cơ cấu tổ chức*.
- **Dạng cây**: kéo thả đơn vị để đổi đơn vị cha/thứ tự. Chọn một nút để xem chi tiết, sửa.
- **Dạng bảng**: tìm theo mã/tên (gõ có dấu hoặc không dấu), lọc trạng thái, loại, đơn vị cha.
- **Thêm/Sửa**: mã (duy nhất, chữ không dấu), tên, loại (Đơn vị/Phòng ban), địa chỉ, điện thoại, email, thứ tự.
- **Xóa**: chỉ xóa được đơn vị không còn người dùng, đơn vị con, nhóm tài nguyên. Nếu còn, hệ thống đề nghị chuyển sang *Ngừng hoạt động*.
- **Xuất Excel / PDF / In**: theo bộ lọc hiện tại, có tiêu đề, ngày xuất, người xuất (bản in có Quốc hiệu).

## 2. Người dùng và nhóm người dùng (#4–6)
*Quản trị > Người dùng*.
- Thêm/sửa tài khoản: họ tên, tên đăng nhập, email, điện thoại (được mã hóa trong CSDL), đơn vị, chức vụ, dung lượng, vai trò, nhóm. Để trống mật khẩu → hệ thống sinh mật khẩu tạm (hiển thị một lần). Người dùng phải đổi mật khẩu khi đăng nhập lần đầu.
- **Khóa/Mở khóa**, **Đặt lại mật khẩu**, **Thu hồi toàn bộ phiên**.
- **Gán hàng loạt**: chọn nhiều người dùng → gán đơn vị và/hoặc vai trò (thêm hoặc thay thế).
- **Nhập từ Excel**: tải tệp mẫu (có sheet hướng dẫn, danh sách mã đơn vị, mã vai trò) → *Kiểm tra trước* → *Nhập*. Có lỗi ở bất kỳ dòng nào thì không nhập dòng nào và hiển thị lỗi theo dòng.
- **Xóa**: chỉ xóa được tài khoản chưa có tư liệu; nếu đã có, chuyển *Ngừng hoạt động* để giữ dữ liệu và lịch sử.
- *Quản trị > Nhóm người dùng*: tạo nhóm, thêm thành viên (dùng để phân quyền, chia sẻ cho cả nhóm).

## 3. Vai trò và phân quyền chức năng (#7–9, #54)
- 4 vai trò hệ thống (không xóa được): **Quản trị hệ thống**, **Biên tập/Tải lên**, **Phê duyệt**, **Chỉ xem**.
- *Vai trò & phân quyền*: tạo vai trò mới (có thể sao chép quyền từ vai trò khác). Ma trận: dòng là chức năng theo phân hệ, cột là hành động (Xem, Tải lên/Thêm mới, Chỉnh sửa, Xóa, Chia sẻ, Phê duyệt, Quản trị); chọn cả dòng/cột. Mỗi lần lưu được ghi nhật ký *Đổi quyền*.
- *Bảng đối chiếu quyền*: toàn bộ chức năng × vai trò để rà soát, lọc theo phân hệ/từ khóa, xuất Excel/PDF.

## 4. Danh mục dùng chung (#10)
Loại danh mục (loại sản phẩm truyền thông, chủ đề, chiến dịch, đối tượng, nguồn, năm...) và các mục (phân cấp, thứ tự, bật/tắt). Danh mục dùng làm thuộc tính gắn cho tư liệu và bộ lọc tìm kiếm. Nhập/xuất Excel (cột: Mã, Tên, Mã mục cha, Thứ tự, Trạng thái).

## 5. Giám sát (#11–15)
- **Nhật ký hoạt động**: mọi thao tác đăng nhập/đăng xuất/đăng nhập sai, tải lên/xuống, xem, sửa, chia sẻ, hủy chia sẻ, đổi tên, di chuyển, sao chép, xóa, khôi phục, xóa vĩnh viễn, đổi quyền, đổi cấu hình. Lọc theo người dùng, đơn vị, thời gian, hành động, loại tài nguyên, tên tư liệu, IP; xem chi tiết JSON. Nút *Kiểm tra toàn vẹn nhật ký* xác minh chuỗi băm (phát hiện sửa đổi trái phép). Nhật ký không thể sửa/xóa.
- **Thống kê dung lượng**: theo tài khoản, đơn vị (cộng dồn đơn vị con), nhóm tài nguyên; tô màu khi ≥ 80% (cam) và ≥ 100% (đỏ).
- **Cảnh báo**: đăng nhập sai nhiều lần, dung lượng vượt ngưỡng/hạn mức, tải xuống bất thường (ngưỡng tệp/giờ cấu hình được), sao lưu thất bại, phát hiện mã độc. Cảnh báo hiển thị ở chuông thông báo (thời gian thực), gửi email cho quản trị (nếu cấu hình SMTP). Chọn và bấm *Xác nhận đã xử lý*.
- **Báo cáo**: các nút Xuất Excel/PDF chạy nền; hệ thống báo "Đang xử lý báo cáo..." và gửi thông báo khi xong; tải tại *Báo cáo của tôi*.

## 6. Phiên đăng nhập (#16–17)
*Quản trị > Phiên đăng nhập*: danh sách phiên toàn hệ thống (thiết bị, IP, thời gian). Thu hồi một phiên, thu hồi toàn bộ phiên của người dùng, khóa tài khoản.

## 7. Nhóm tài nguyên và dung lượng (#57–60)
- *Quản trị > Nhóm tài nguyên*: danh sách tất cả nhóm, mức sử dụng; đặt hạn mức nhóm; khóa/mở khóa (khóa = chỉ xem, không ghi).
- *Cấu hình dung lượng cá nhân*: đặt dung lượng cho người dùng cụ thể hoặc cả đơn vị (có/không gồm đơn vị con). Mặc định cho tài khoản/nhóm mới đặt ở *Cấu hình hệ thống*.
- Dung lượng chỉ được trả lại khi tệp bị **xóa vĩnh viễn** khỏi thùng rác. Thùng rác tự xóa vĩnh viễn sau 30 ngày (cấu hình được).

## 8. Cấu hình hệ thống
Chính sách mật khẩu, ngưỡng khóa tài khoản, thời gian tự đăng xuất, dung lượng mặc định, ngưỡng cảnh báo dung lượng, thời gian giữ thùng rác, kích thước tải lên tối đa, định dạng cho phép, ngưỡng tải xuống bất thường, tên đơn vị/phần mềm/logo, dòng cơ quan chủ quản và Quốc hiệu trên bản in. Mọi thay đổi được ghi *Nhật ký thay đổi hệ thống* và nhật ký *Đổi cấu hình*.

## 9. Sao lưu và phục hồi
*Quản trị > Sao lưu*.
- **Lịch tự động**: hằng ngày theo giờ, hằng tuần, hoặc cron tùy chỉnh; số bản giữ lại; ngày sao lưu đầy đủ trong tuần (các ngày khác sao lưu tăng dần); tùy chọn sao chép lên S3.
- **Sao lưu ngay**: tạo bản sao lưu đột xuất (tùy chọn đầy đủ).
- Mỗi bản gồm: `db.dump` (toàn bộ dữ liệu thuộc tính, pg_dump), `files.tar` (tệp số hóa đã mã hóa), `manifest.json` (checksum SHA-256). Tải xuống từng phần trên màn hình.
- Sao lưu thất bại → cảnh báo và email.
- **Phục hồi** (trên máy chủ):
  ```powershell
  pwsh -File scripts/restore.ps1 <mã-bản-sao-lưu>     # hoặc ./scripts/restore.sh <mã>
  ```
  Script kiểm tra checksum cả chuỗi (bản đầy đủ + tăng dần), dừng API/Worker, phục hồi CSDL và tệp rồi khởi động lại. Lưu ý: cần cùng `STORAGE_MASTER_KEY` với lúc sao lưu.
- Sao lưu từ dòng lệnh: `pwsh -File scripts/backup-now.ps1 [-Full]`.

## 10. Hỗ trợ người dùng, sự cố (SLA)
- Người dùng gửi yêu cầu qua nút *Hỗ trợ* (mức độ Khẩn cấp / Không khẩn cấp, mô tả, ảnh chụp).
- *Quản trị > Hỗ trợ & sự cố*: giao người xử lý, cập nhật trạng thái, ghi chú, kết quả. Hạn xử lý tự tính: **khẩn cấp 4 giờ, không khẩn cấp 24 giờ**; yêu cầu quá hạn tô đỏ. Báo cáo SLA xuất Excel/PDF.

## 11. Nhật ký thay đổi và trạng thái hệ thống
- *Nhật ký thay đổi hệ thống*: phiên bản đang chạy (kèm mã commit), lịch sử triển khai/nâng cấp (tự ghi), thay đổi cấu hình (tự ghi), thêm ghi nhận thủ công.
- *Trạng thái hệ thống*: CSDL, kho lưu trữ, ONLYOFFICE, ClamAV, dịch vụ xử lý nền, dung lượng đĩa, bản sao lưu gần nhất, số job chờ/lỗi.

## 12. Bàn giao và xóa dữ liệu
- *Xuất toàn bộ dữ liệu*: gói ZIP gồm tệp gốc đã giải mã theo cây thư mục, `metadata.json`, `metadata.csv`, `audit-logs.csv`, `README.md` (xem `docs/data-export-format.md`).
- Kết thúc hợp đồng: `pwsh -File scripts/secure-wipe.ps1` → biên bản xóa dữ liệu có mã băm.

## 13. Xử lý sự cố thường gặp
| Hiện tượng | Kiểm tra |
|---|---|
| Không mở được tài liệu Office | *Trạng thái hệ thống* → ONLYOFFICE; `docker compose logs onlyoffice`; `ONLYOFFICE_JWT_SECRET` ≥ 32 ký tự và giống nhau ở api/onlyoffice |
| Không có ảnh thu nhỏ | Worker đang chạy (`docker compose ps worker`), xem `docker compose logs worker` |
| Không gửi được email | Biến `SMTP_*` trong `.env`; log API có dòng "Chưa cấu hình SMTP" |
| Hết dung lượng đĩa | *Trạng thái hệ thống*; giảm số bản sao lưu giữ lại, dọn thùng rác |
| Người dùng bị khóa do sai mật khẩu | Tự mở sau 15 phút hoặc quản trị *Mở khóa* |
