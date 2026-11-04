# Chính sách chia sẻ nhiều tầng

Chia sẻ ra ngoài và liên kết chịu nhiều tầng chính sách; **tầng chặt nhất thắng**, và mỗi màn hình chia sẻ cho biết tầng nào đang chặn
("Chính sách chia sẻ (Nhãn "Hạn chế": Chỉ nội bộ) không cho phép…"). Mã: `SharingPolicy` (`src/BlueDrive.Application/Features/Access/SharingPolicy.cs`).

## 1. Các mức chia sẻ ra ngoài

Từ chặt đến rộng:

| Mức | Ý nghĩa |
|---|---|
| **Chỉ nội bộ** (`off`) | Chỉ người dùng, nhóm, đơn vị trong hệ thống. |
| **Chỉ khách đã có** (`existing_guests`) | Thêm khách đã được mời trước đó. |
| **Khách mới và khách đã có** (`new_and_existing_guests`) | Mời khách mới qua email (đăng nhập bằng mã một lần). |
| **Bất kỳ ai** (`anyone`) | Thêm liên kết ẩn danh "Bất kỳ ai có liên kết". |

## 2. Các tầng

| Tầng | Ở đâu | Đặt được |
|---|---|---|
| Hệ thống (tenant) | *Quản trị → Cấu hình hệ thống → Chia sẻ* | mức chia sẻ ra ngoài, thời hạn tối đa của liên kết "Bất kỳ ai" (mặc định 30 ngày), bật / tắt khách, tên miền khách được mời, số ngày khách không hoạt động thì hết hạn |
| Space / Drive của tôi | *Space → Cài đặt → Chính sách chia sẻ* | mức chia sẻ ra ngoài (chỉ chặt hơn hệ thống), thành viên có được chia sẻ cho người ngoài Space không, có được chia sẻ riêng từng mục (ngắt kế thừa) không, ai duyệt yêu cầu truy cập (người quản lý mục / chỉ chủ Space) |
| Nhãn phân loại | *Quản trị → Nhãn phân loại* | chặn chia sẻ ra ngoài (= Chỉ nội bộ), chặn liên kết "Bất kỳ ai" (≤ Khách mới và khách đã có), người ngoài Space chỉ xem, watermark, chặn đồng bộ |
| Thư mục | bảng thông tin → *Chính sách chia sẻ* | mức chia sẻ ra ngoài cho thư mục và mọi thứ bên dưới (chỉ chặt hơn) |
| Tệp | bảng thông tin → *Chính sách chia sẻ* | mức chia sẻ ra ngoài của riêng tệp (chỉ chặt hơn) |

Mức hiệu lực của một mục = nhỏ nhất của: hệ thống, Space / Drive, mọi thư mục phía trên, chính mục đó, nhãn hiệu lực (nhãn của tệp, nếu không có thì
của thư mục gần nhất, rồi nhãn mặc định của Space). API `GET /items/{id}/sharing-policy` trả mức hiệu lực và nguồn của nó.

## 3. Liên kết

| Loại | Ai mở được | Ghi chú |
|---|---|---|
| Người đã có quyền | chỉ người đã có quyền trên mục | để gửi đường dẫn, không cấp thêm quyền |
| Trong đơn vị | mọi người dùng nội bộ đăng nhập | người mở được ghi vào thành viên của liên kết |
| Người cụ thể | người được chọn | |
| Bất kỳ ai | ai có liên kết, không cần đăng nhập | cần mức **Bất kỳ ai** ở mọi tầng; hạn dùng bắt buộc trong thời hạn tối đa; số lượt mở tối đa; chặn tải xuống |

- Token liên kết chỉ lưu dạng băm SHA-256; tra bằng chỉ mục nên thời gian như nhau dù token có tồn tại hay không; điểm vào công khai có giới hạn tần suất.
- **Không có mật khẩu liên kết** (lệch có chủ đích so với đặc tả mục 5.3 / 16, quyết định ngày 04/11/2026, giống Google Drive): dùng *Người cụ thể* hoặc *Trong đơn vị* khi cần giới hạn người mở; liên kết cũ có mật khẩu đã chuyển sang "Hạn chế" (chỉ người cụ thể). Xem [`migration-permissions.md`](migration-permissions.md), giả định 2.

## 4. Quyền có hạn, yêu cầu truy cập

- Quyền và liên kết có hạn tự hết hiệu lực (cache không giữ quá hạn); người được cấp và người cấp nhận nhắc trước 3 ngày, có trang *Gia hạn*.
- Yêu cầu truy cập: người không có quyền thấy trang "Bạn cần quyền truy cập" (chỉ tên chủ sở hữu), gửi yêu cầu; người quản lý mục (hoặc chỉ chủ Space theo chính sách) duyệt với cấp quyền tùy chọn; người yêu cầu nhận thông báo.

## 5. Ví dụ

Space **Hợp tác quốc tế** đặt *Khách mới và khách đã có*; thư mục **Hồ sơ đối tác** mang nhãn *Hạn chế* (chặn chia sẻ ra ngoài).

- Mời khách vào một tệp trong **Hồ sơ đối tác**: bị chặn — `Nhãn "Hạn chế": Chỉ nội bộ`.
- Tạo liên kết "Bất kỳ ai" cho tệp ở thư mục khác của Space: bị chặn — `Space "Hợp tác quốc tế": Khách mới và khách đã có`.
- Mời khách vào tệp ở thư mục khác: được (gửi email mã đăng nhập).

Kiểm chứng: `SharingModelTests` (bốn loại liên kết, hạn, số lượt, chặn tải, khách OTP, yêu cầu truy cập, tầng chặt nhất thắng), `AdvancedAdminTests.A_label_restricts_sharing…`.
