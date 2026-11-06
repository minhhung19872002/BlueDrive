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
- 4 vai trò hệ thống (không xóa được): **Quản trị hệ thống**, **Chỉnh sửa**, **Phê duyệt**, **Chỉ xem** (cùng *Khách bên ngoài* cho khách mời). Mỗi tài khoản phải có ít nhất một vai trò.
- Vai trò hệ thống quyết định quyền với **tư liệu dùng chung của nhóm/đơn vị** (qua thành viên nhóm): ví dụ *Chỉ xem* thì chỉ xem, tải xuống tư liệu của nhóm dù vai trò trong nhóm là Chỉnh sửa. Vai trò **không** giới hạn *Tài liệu của tôi* (ai cũng toàn quyền trong kho riêng, chỉ giới hạn dung lượng) và **không** giới hạn quyền được chia sẻ trực tiếp trên tư liệu (tài khoản *Chỉ xem* được chia sẻ *Chỉnh sửa* thì sửa được tư liệu đó). Chi tiết: `docs/permission-matrix.md`.
- *Vai trò & phân quyền*: tạo vai trò mới (có thể sao chép quyền từ vai trò khác). Ma trận: dòng là chức năng theo phân hệ, cột là hành động (Xem, Tải lên/Thêm mới, Chỉnh sửa, Xóa, Chia sẻ, Phê duyệt, Quản trị); chọn cả dòng/cột. Mỗi lần lưu được ghi nhật ký *Đổi quyền*.
- *Bảng đối chiếu quyền*: toàn bộ chức năng × vai trò để rà soát, lọc theo phân hệ/từ khóa, xuất Excel/PDF.
- *Nhãn phân loại* (quyền *Cấu hình hệ thống*): thêm / sửa nhãn, màu, mức độ (số lớn hơn là nhạy cảm hơn) và quy tắc của nhãn. Thay đổi quy tắc áp dụng ngay cho mọi tệp mang nhãn. Không đặt các tên "Mật", "Tối mật", "Tuyệt mật" — hệ thống không phục vụ lưu trữ bí mật nhà nước.
- *Lưu giữ & giữ pháp lý* (quyền *Lưu giữ & giữ pháp lý*): **Chính sách lưu giữ** — chọn phạm vi (Space, thư viện, nhãn, giá trị danh mục), số năm tính từ ngày tạo hoặc ngày sửa cuối và việc làm khi hết hạn (không làm gì / đưa vào *Chờ duyệt xóa* / tự chuyển vào thùng rác). Trong thời gian lưu giữ, tệp người dùng xóa vĩnh viễn được chuyển vào **Kho lưu giữ** (người dùng không thấy): *Trả về thùng rác* hoặc *Xóa vĩnh viễn* khi đã hết hạn. **Giữ pháp lý** — đặt cho một Space/Drive, một thư mục hoặc toàn bộ tệp của một người dùng, có tên và lý do; trong phạm vi giữ, không ai (kể cả quản trị, kể cả job dọn thùng rác) xóa vĩnh viễn được tệp hay phiên bản nào; *Gỡ giữ* bắt buộc ghi lý do. Space đang có chính sách lưu giữ hoặc giữ pháp lý không xóa được.
- *Nghỉ việc / chuyển đơn vị* (quyền *Người dùng – Quản trị*): chọn người nghỉ việc → xem dữ liệu, liên kết, nhóm, Space (cảnh báo đỏ nếu là chủ sở hữu duy nhất) → chọn người nhận, chọn thu hồi hay giữ liên kết → **Bàn giao và vô hiệu hóa**. Toàn bộ Drive chuyển vào thư mục "Dữ liệu từ <tên>" trong Drive người nhận, mọi chia sẻ giữ nguyên; Space người đó sở hữu chuyển cho người nhận; tài khoản bị vô hiệu hóa. Xuất **biên bản bàn giao** Excel/PDF (tab *Biên bản đã lập*). Tab *Chuyển đơn vị*: đổi đơn vị và xem quyền mất / có thêm theo đơn vị mới.
- *Truy cập khẩn cấp* (quyền *Truy cập khẩn cấp*): quản trị không tự xem được nội dung của người khác. Khi cần (sự cố, yêu cầu pháp lý), chọn Drive/Space, thời hạn (tối đa 24 giờ) và lý do; quyền tự hết hạn, *Kết thúc ngay* được. Mọi lượt được ghi nhật ký mức cao và báo cho chủ Drive / Quản lý Space, trừ chế độ *Điều tra* (cần quyền *Điều tra*).
- *Báo cáo quyền truy cập* (quyền *Báo cáo quyền truy cập*; Quản lý Space mở được cho Space của mình): chọn loại báo cáo — *Kiểm tra quyền* (tư liệu + người dùng hoặc nhóm: quyền hiệu lực và vì sao), *Người này truy cập được gì?*, *Chia sẻ ra ngoài*, *Liên kết*, *Quyền riêng*, *Sắp / đã hết hạn* — lọc theo Space, **Xem báo cáo**, rồi *Xuất Excel / PDF*. Phần **Thao tác hàng loạt**: thu hồi mọi liên kết "Bất kỳ ai" (một Space hoặc toàn hệ thống), gỡ toàn bộ quyền đã cấp cho một người dùng / khách, xóa khách không đăng nhập N ngày. Mọi thao tác được ghi nhật ký.

## 4. Danh mục dùng chung (#10)
Loại danh mục (loại sản phẩm truyền thông, chủ đề, chiến dịch, đối tượng, nguồn, năm...) và các mục (phân cấp, thứ tự, bật/tắt). Danh mục dùng làm thuộc tính gắn cho tư liệu và bộ lọc tìm kiếm. Nhập/xuất Excel (cột: Mã, Tên, Mã mục cha, Thứ tự, Trạng thái).

## 5. Giám sát (#11–15)
- **Nhật ký hoạt động**: mọi thao tác đăng nhập/đăng xuất/đăng nhập sai, tải lên/xuống, xem, sửa, chia sẻ, hủy chia sẻ, đổi tên, di chuyển, sao chép, xóa, khôi phục, xóa vĩnh viễn, đổi quyền, đổi cấu hình. Lọc theo người dùng, đơn vị, thời gian, hành động, loại tài nguyên, tên tư liệu, IP; xem chi tiết JSON. Nút *Kiểm tra toàn vẹn nhật ký* xác minh chuỗi băm (phát hiện sửa đổi trái phép). Nhật ký không thể sửa/xóa.
- **Thống kê dung lượng**: theo tài khoản, đơn vị (cộng dồn đơn vị con), nhóm tài nguyên; tô màu khi ≥ 80% (cam) và ≥ 100% (đỏ).
- **Cảnh báo**: đăng nhập sai nhiều lần (một tài khoản sai liên tiếp → khóa tạm tài khoản; một địa chỉ IP sai nhiều lần trên nhiều tài khoản → chặn tạm địa chỉ đó, ngưỡng và thời gian đặt ở *Cấu hình → Đăng nhập & phiên*), dung lượng vượt ngưỡng/hạn mức, tải xuống bất thường (ngưỡng tệp/giờ cấu hình được), sao lưu thất bại, phát hiện mã độc. Cảnh báo hiển thị ở chuông thông báo (thời gian thực), gửi email cho quản trị (nếu cấu hình SMTP). Chọn và bấm *Xác nhận đã xử lý*.
- **Sự cố an toàn thông tin** (HSMT 3.4, quyền `incident.view/upload/edit/admin`): ở trang *Cảnh báo* bấm *Lập hồ sơ sự cố* để mở hồ sơ điền sẵn từ cảnh báo (cảnh báo được đánh dấu đã xử lý và hiện mã hồ sơ `SC-…`), hoặc tạo trực tiếp ở *Quản trị > Sự cố an toàn thông tin*. Trong hồ sơ: cập nhật trạng thái (Mới phát hiện → Đang điều tra → Đang ngăn chặn → Đang khắc phục → Đã khắc phục → Đã đóng), nguyên nhân, các bên phối hợp, biện pháp khắc phục, kết luận; dòng thời gian ghi mọi thay đổi. Chỉ `incident.admin` giao người xử lý, đóng và sửa hồ sơ đã đóng. Xuất hồ sơ PDF/Excel, xuất sổ theo dõi theo bộ lọc. Quy trình chi tiết, mức độ, thời hạn báo cáo cơ quan chức năng: `docs/quy-trinh-ung-pho-su-co.md`.
- **Báo cáo**: các nút Xuất Excel/PDF chạy nền; hệ thống báo "Đang xử lý báo cáo..." và gửi thông báo khi xong; tải tại *Báo cáo của tôi*.

## 6. Phiên đăng nhập (#16–17)
*Quản trị > Phiên đăng nhập*: danh sách phiên toàn hệ thống (thiết bị, IP, thời gian). Thu hồi một phiên, thu hồi toàn bộ phiên của người dùng, khóa tài khoản.

## 6a. Thiết bị đồng bộ (BlueDrive Desktop)

- **Quản trị → Thiết bị đồng bộ** (`/admin/devices`, quyền `device.view` / `device.admin`): danh sách mọi máy tính cài BlueDrive
  Desktop (người dùng, hệ điều hành, phiên bản, lần hoạt động cuối, IP). **Thu hồi** để đăng xuất và dừng đồng bộ trên máy;
  **Xóa dữ liệu từ xa** khi máy bị mất (ứng dụng xóa thư mục đồng bộ và thông tin đăng nhập ở lần kết nối tới).
- Người dùng tự quản lý máy của mình tại **Thiết bị đã kết nối** (menu tài khoản, `/devices`).
- Chính sách ở **Cấu hình hệ thống → Đồng bộ máy tính**: bật/tắt, chỉ cho phép một số vai trò/đơn vị, kích thước tệp tối đa,
  đuôi tệp không đồng bộ, nhóm bắt buộc chỉ trực tuyến, phiên bản ứng dụng tối thiểu, thời hạn đăng nhập thiết bị.

## 7. Nhóm tài nguyên và dung lượng (#57–60)
- *Quản trị > Nhóm tài nguyên*: danh sách tất cả nhóm, mức sử dụng; đặt hạn mức nhóm; khóa/mở khóa (khóa = chỉ xem, không ghi).
- *Cấu hình dung lượng cá nhân*: đặt dung lượng cho người dùng cụ thể, cả đơn vị (có/không gồm đơn vị con) hoặc theo vai trò; tích *Đặt làm dung lượng mặc định* để người dùng mới của đơn vị / vai trò đó nhận mức này (vai trò ưu tiên hơn đơn vị, rồi đến mặc định ở *Cấu hình hệ thống*).
- Dung lượng 3 tầng: **toàn hệ thống** (*Cấu hình hệ thống → Dung lượng toàn hệ thống*, 0 = không giới hạn) → **Space** → **Drive của từng người**. Cảnh báo ở 80% (cấu hình được), 90% và 100% cho chủ Drive / chủ Space (thông báo) và quản trị (cảnh báo bất thường). Khi đầy: không tải lên, không tạo phiên bản mới (trình soạn thảo trực tuyến mở chế độ chỉ xem), vẫn xem và tải xuống được. Người dùng và Quản lý Space dọn chỗ ở trang **Bộ nhớ** (*Xóa phiên bản cũ*).
- Dung lượng chỉ được trả lại khi tệp bị **xóa vĩnh viễn** (cả hai tầng thùng rác tính vào dung lượng của Drive/Space). Thùng rác 2 tầng tự xóa vĩnh viễn sau 93 ngày kể từ lúc xóa (cấu hình được; Space có thể đặt riêng), trừ mục đang lưu giữ hoặc giữ pháp lý. Quản trị (quyền *Quản trị nhóm tài nguyên*) xem và khôi phục được thùng rác tầng 2 của mọi Space.

## 8. Cấu hình hệ thống
Định dạng tệp cho phép mặc định gồm cả tệp Office có macro (`.docm`, `.xlsm`, `.pptm`, giống SharePoint): chúng được lưu và đồng bộ, trên web chỉ xem (macro không chạy); nên bật quét mã độc ClamAV (`CLAMAV_ENABLED=true` trong `deploy/.env`). Tệp thực thi / kịch bản luôn bị chặn. Chính sách mật khẩu, ngưỡng khóa tài khoản, thời gian tự đăng xuất, dung lượng mặc định, ngưỡng cảnh báo dung lượng, thời gian giữ thùng rác, kích thước tải lên tối đa, định dạng cho phép, ngưỡng tải xuống bất thường, tên đơn vị/phần mềm/logo, dòng cơ quan chủ quản và Quốc hiệu trên bản in. Mọi thay đổi được ghi *Nhật ký thay đổi hệ thống* và nhật ký *Đổi cấu hình*.

### 8a. Giới hạn của các thành phần nguồn mở đi kèm
- **ONLYOFFICE Document Server bản Community** (image `onlyoffice/documentserver`) cho phép tối đa **20 kết nối soạn thảo/xem đồng thời** trên toàn hệ thống (mỗi cửa sổ tài liệu đang mở là một kết nối, kể cả xem trên app mobile và liên kết công khai). Vượt giới hạn, trình soạn thảo hiện cảnh báo của ONLYOFFICE (theo bản dịch có sẵn của ONLYOFFICE) và mở tài liệu ở chế độ chỉ xem hoặc từ chối mở; giao diện BlueDrive hiện thêm thông báo "Trình soạn thảo báo lỗi" kèm hướng dẫn. Các chức năng khác (duyệt, tải lên/xuống, xem ảnh/video/PDF qua trình xem riêng) không bị ảnh hưởng.
  - Ước lượng: với khoảng 100 người dùng đồng thời, thường chỉ một phần nhỏ mở Office cùng lúc; nếu thường xuyên có trên 20 người soạn thảo, cần mua giấy phép ONLYOFFICE Docs Enterprise (theo số kết nối) hoặc chuyển sang bản Developer, rồi thay image trong `deploy/docker-compose.yml` — không phải sửa mã BlueDrive.
  - Theo dõi: `docker compose logs onlyoffice | grep -i "connection"`; người dùng nên đóng tab tài liệu không còn dùng.
- **ClamAV**: tệp lớn hơn `CLAMAV_MAX_SCAN_MB` (mặc định 200 MB) không được quét, hiển thị "Bỏ qua quét (tệp quá lớn)" và tải được kèm cảnh báo. Tăng giới hạn thì clamd cần thêm RAM (khoảng 2–3 lần kích thước tệp). Khi ClamAV lỗi liên tục, tệp chuyển sang "Không quét được" sau 3 lần thử và hệ thống tạo cảnh báo *Không quét được mã độc*.

## 9. Sao lưu và phục hồi
*Quản trị > Sao lưu*.
- **Lịch tự động**: hằng ngày theo giờ, hằng tuần, hoặc cron tùy chỉnh; số bản giữ lại; ngày sao lưu đầy đủ trong tuần (các ngày khác sao lưu tăng dần); tùy chọn sao chép lên S3.
- **Sao lưu ngay**: tạo bản sao lưu đột xuất (tùy chọn đầy đủ).
- Mỗi bản gồm: `db.dump` (toàn bộ dữ liệu thuộc tính, pg_dump), `files.tar` (tệp số hóa đã mã hóa, lấy từ ổ đĩa máy chủ hoặc từ bucket S3 nếu `STORAGE_PROVIDER=s3`), `manifest.json` (checksum SHA-256). Tải xuống từng phần trên màn hình.
- **Sao chép lên S3** (bật trên màn hình hoặc `BACKUP_S3_ENABLED=true` trong `deploy/.env`): mỗi bản sao lưu được chép vào `backups/<thư mục bản sao lưu>/` trong bucket cấu hình ở `S3_*`, nên vẫn phục hồi được khi mất cả máy chủ. Chính sách giữ N bản áp dụng cho cả bản trên máy chủ và bản trên S3.
- Sao lưu thất bại → cảnh báo và email.
- **Phục hồi** (trên máy chủ):
  ```powershell
  pwsh -File scripts/restore.ps1 <mã-bản-sao-lưu>     # hoặc ./scripts/restore.sh <mã>
  ```
  Script kiểm tra checksum cả chuỗi (bản đầy đủ + tăng dần), dừng API/Worker, phục hồi CSDL và tệp (vào ổ đĩa hoặc ghi lại vào bucket S3) rồi khởi động lại. Bản nào trong chuỗi không còn trên máy chủ sẽ tự tải về từ bản sao trên S3 (máy chủ mới: cài đặt, khai báo cùng `S3_*` rồi chạy lệnh trên với mã bản sao lưu). Lưu ý: cần cùng `STORAGE_MASTER_KEY` với lúc sao lưu.
- Sao lưu từ dòng lệnh: `pwsh -File scripts/backup-now.ps1 [-Full]`.

## 10. Hỗ trợ người dùng, sự cố (SLA)
- Người dùng gửi yêu cầu qua nút *Hỗ trợ* (mức độ Khẩn cấp / Không khẩn cấp, mô tả, ảnh chụp).
- *Quản trị > Hỗ trợ & sự cố*: giao người xử lý, cập nhật trạng thái, ghi chú, kết quả. Hạn xử lý tự tính: **khẩn cấp 4 giờ, không khẩn cấp 24 giờ**; yêu cầu quá hạn tô đỏ. Báo cáo SLA xuất Excel/PDF.

## 11. Nhật ký thay đổi và trạng thái hệ thống
- *Nhật ký thay đổi hệ thống*: phiên bản đang chạy (kèm mã commit), lịch sử triển khai/nâng cấp (tự ghi), thay đổi cấu hình (tự ghi), thêm ghi nhận thủ công.
- *Trạng thái hệ thống*: CSDL, kho lưu trữ, ONLYOFFICE, ClamAV, dịch vụ xử lý nền, dung lượng đĩa, bản sao lưu gần nhất, số job chờ/lỗi.
- Khi thêm ghi nhận thủ công, chọn *Thông báo cho người dùng* và số ngày hiển thị (1–90) để đăng ngay nội dung thay đổi thành thông báo hệ thống.
- *Thông báo hệ thống* (quyền `announcement.view/edit`): tiêu đề, nội dung, loại (Thông tin / Lưu ý / Bảo trì), bật/tắt, thời gian hiệu lực (để trống = ngay lập tức / không hết hạn). Thông báo đang hiệu lực hiện ở đầu mọi trang cho mọi người dùng, cập nhật thời gian thực; người dùng ẩn được, sửa thông báo thì hiện lại. Dùng để báo trước lịch bảo trì, nâng cấp hoặc sự cố đang xử lý.

## 12. Bàn giao và xóa dữ liệu
- *Xuất toàn bộ dữ liệu*: gói ZIP gồm tệp gốc đã giải mã theo cây thư mục, `metadata.json`, `metadata.csv`, `audit-logs.csv`, `README.md` (xem `docs/data-export-format.md`).
- Kết thúc hợp đồng: `pwsh -File scripts/secure-wipe.ps1` (hoặc `./scripts/secure-wipe.sh`) → biên bản xóa dữ liệu có mã băm.
  - Nếu dùng S3 (`S3_BUCKET` trong `deploy/.env`), script xóa mọi đối tượng trong bucket (tệp số hóa và bản sao lưu `backups/`) trước khi dừng dịch vụ; biên bản ghi số đối tượng, dung lượng đã xóa và số còn lại (khác 0 → kết quả *CHƯA HOÀN TẤT*).
  - Bucket bật versioning hoặc object lock: các phiên bản cũ không bị xóa qua API thường, phải xóa thêm (hoặc xóa cả bucket) trên giao diện nhà cung cấp S3 và ghi vào biên bản.
  - Bản sao lưu đã tải xuống máy khác (từ màn hình Sao lưu) phải được hủy riêng; khóa chủ trong `deploy/.env` bị hủy nên các bản đó cũng không còn giải mã được tệp.

## 13. Xử lý sự cố thường gặp
| Hiện tượng | Kiểm tra |
|---|---|
| Không mở được tài liệu Office | *Trạng thái hệ thống* → ONLYOFFICE; `docker compose logs onlyoffice`; `ONLYOFFICE_JWT_SECRET` ≥ 32 ký tự và giống nhau ở api/onlyoffice |
| Office báo vượt số kết nối / chỉ mở được chế độ xem | Bản Community giới hạn 20 kết nối đồng thời (mục 8a); đóng bớt tài liệu hoặc nâng cấp giấy phép ONLYOFFICE |
| Tệp "Đang chờ quét" lâu | `docker compose ps clamav worker`; xem *Cảnh báo* → *Không quét được mã độc* |
| Không có ảnh thu nhỏ | Worker đang chạy (`docker compose ps worker`), xem `docker compose logs worker` |
| Không gửi được email | Biến `SMTP_*` trong `.env`; log API có dòng "Chưa cấu hình SMTP" |
| Hết dung lượng đĩa | *Trạng thái hệ thống*; giảm số bản sao lưu giữ lại, dọn thùng rác |
| Người dùng bị khóa do sai mật khẩu | Tự mở sau 15 phút hoặc quản trị *Mở khóa* |
