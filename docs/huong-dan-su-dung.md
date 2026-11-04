# Hướng dẫn sử dụng BlueDrive

BlueDrive là hệ thống lưu trữ, tìm kiếm, khai thác và chia sẻ sản phẩm truyền thông dân số (hình ảnh, video, âm thanh, tài liệu, infographic, ấn phẩm điện tử). Hệ thống dùng được trên máy tính, máy tính bảng, điện thoại (trình duyệt) và ứng dụng di động BlueDrive (Android/iOS).

Ảnh minh họa trong tài liệu được chụp tự động từ hệ thống (`web/e2e/pages.spec.ts`), thư mục `docs/images/`. Hướng dẫn cho quản trị viên: `docs/huong-dan-quan-tri.md`.

## 1. Đăng nhập, mật khẩu, phiên đăng nhập (HSMT #16–17)

- Truy cập địa chỉ hệ thống do đơn vị cung cấp, nhập **Tên đăng nhập** và **Mật khẩu**.
- **Lần đầu đăng nhập** (hoặc khi mật khẩu hết hạn), hệ thống yêu cầu **đổi mật khẩu**. Mật khẩu mới phải đủ độ dài và có chữ hoa, chữ thường, số, ký tự đặc biệt; danh sách điều kiện hiển thị và đánh dấu khi bạn gõ.
- **Quên mật khẩu**: bấm *Quên mật khẩu?*, nhập tên đăng nhập hoặc email → mở email, bấm liên kết (dùng một lần, hiệu lực 30 phút) để đặt mật khẩu mới.
- Nhập sai mật khẩu 5 lần liên tiếp, tài khoản bị **tạm khóa 15 phút**.
- Hệ thống tự đăng xuất khi không thao tác trong 30 phút (quản trị có thể thay đổi).
- Menu tài khoản (góc phải) > **Phiên đăng nhập**: xem các thiết bị đang đăng nhập (thiết bị, IP, thời gian), đăng xuất từng thiết bị hoặc *Đăng xuất khỏi tất cả thiết bị khác*.

![Phiên đăng nhập](images/phien-dang-nhap.png)

## 2. Trang chủ — Dashboard (#63–70)

![Trang chủ](images/trang-chu.png)

- **Dung lượng của bạn**: đã dùng / tổng dung lượng được cấp.
- **Truy cập nhanh**: Tài liệu của tôi, Được chia sẻ, Quan trọng, Thùng rác, Tải lên.
- **Dung lượng theo loại tệp** (ảnh, video, âm thanh, tài liệu, bảng tính, trình chiếu, PDF, khác).
- **Top 10 thư mục chiếm nhiều dung lượng** — bấm một cột để mở thư mục.
- **Tài liệu gần đây**: tên, người sở hữu, dung lượng, thời gian sửa.
- **Nhóm**: số nhóm bạn tham gia, số nhóm bạn làm trưởng nhóm, dung lượng từng nhóm so với hạn mức, tỷ lệ loại tệp trong nhóm, tài liệu nhóm gần đây.

## 3. Tài liệu của tôi (#18, #20–24, #44–48)

![Tài liệu của tôi](images/tai-lieu-cua-toi.png)

- Chuyển chế độ xem **danh sách / lưới (ảnh thu nhỏ)**, sắp xếp theo tên, kích thước, ngày sửa; lọc theo loại tư liệu; tìm trong thư mục hoặc trong toàn bộ Tài liệu của tôi (gõ có dấu hoặc không dấu).
- **Mới**: Thư mục, Tài liệu Word, Bảng tính Excel, Trình chiếu PowerPoint, Văn bản (.txt). Tài liệu mới mở ngay trong trình soạn thảo trực tuyến.
- **Tải lên**: tệp hoặc cả thư mục (giữ nguyên cây thư mục), hoặc **kéo thả** từ máy tính vào cửa sổ. Chọn tệp xong là tải lên ngay; tiêu đề, mô tả, thẻ, danh mục nhập sau trong **Thông tin** của tệp (chuột phải → Thông tin), hoặc chọn nhiều tệp → **Cập nhật thông tin hàng loạt**. Hàng đợi tải lên ở góc phải dưới: xem tiến độ, **tạm dừng / tiếp tục**, hủy, thử lại. Tệp lớn (vài GB) tiếp tục được từ chỗ dừng khi mạng gián đoạn.
- **Nhấp** vào thư mục/tệp để mở. Rê chuột vào một dòng để hiện nút nhanh cạnh tên: **⋯** (menu), **Chia sẻ**, **Sao chép liên kết** (liên kết nội bộ, người nhận đăng nhập và có quyền mới mở được), **Tải xuống**. Chọn mục bằng **ô chọn** đầu dòng (hoặc Ctrl/Shift + nhấp để chọn nhiều), **chuột phải** để mở menu: Mở, Tải xuống, Chia sẻ, Đổi tên, Sao chép tới..., Di chuyển tới..., Đánh dấu quan trọng, Thông tin, Phiên bản, Thảo luận, Quản lý quyền truy cập, Xóa. **Thông tin / Phiên bản / Thảo luận** mở ngăn bên phải đè lên danh sách (đóng bằng Esc hoặc nút ×); nút ⓘ trên thanh công cụ bật bảng thông tin cố định, khi đó danh sách cuộn ngang để xem đủ các cột.
- **Phím tắt**: `Del` xóa, `F2` đổi tên, `Ctrl+C` / `Ctrl+X` rồi `Ctrl+V` để sao chép/di chuyển vào thư mục đang mở, `Enter` mở. **Kéo thả** tệp vào một thư mục để di chuyển.
- Tải nhiều tệp/thư mục cùng lúc: hệ thống nén thành một tệp ZIP.
- **Panel thông tin** (nút ⓘ): kích thước, loại, người tạo, ngày tạo/sửa, vị trí, danh mục, mô tả, thẻ, phiên bản, **ai có quyền truy cập**. Có thể sửa tiêu đề, mô tả, thẻ, danh mục.
- Định dạng hỗ trợ: ảnh (jpg, png, gif, webp, svg, bmp, tiff, heic), video (mp4, mov, avi, mkv, webm), âm thanh (mp3, wav, aac, m4a, ogg, flac), tài liệu (doc/x, xls/x, ppt/x, pdf, txt, odt, ods, odp, rtf), ấn phẩm (pdf, epub), thiết kế (psd, ai, indd — chỉ lưu và tải), nén (zip, rar, 7z). Tệp chương trình (.exe, .bat, .ps1...) và tệp giả mạo đuôi bị chặn.

## 4. Xem và soạn thảo tài liệu (#21, #26–27, #41–43)

- Mở tệp: ảnh (phóng to, xoay), video/âm thanh (tua tự do), PDF, văn bản. Tài liệu Office: bấm **Mở bằng trình soạn thảo** để sửa trực tuyến; **nhiều người có thể cùng soạn thảo** một tài liệu, thấy thay đổi của nhau theo thời gian thực. Mỗi lần lưu tạo một phiên bản mới.
- Tab **Phiên bản**: số phiên bản, người sửa, thời gian, dung lượng, ghi chú; xem/tải phiên bản cũ; **Khôi phục** một phiên bản cũ (tạo phiên bản mới); **Tải lên phiên bản mới**.
- Tab **Thảo luận**: bình luận, trả lời, **@nhắc tên** đồng nghiệp (người được nhắc nhận thông báo), đính kèm tệp, sửa/xóa bình luận của mình. Chỉ người có quyền xem tài liệu mới thấy thảo luận.

## 5. Chia sẻ (#19, #23, #49, #53)

- Chuột phải > **Chia sẻ**:
  - **Người có quyền truy cập**: thêm người dùng, nhóm người dùng hoặc đơn vị (gồm cả đơn vị con) với một cấp quyền (*Quản lý nội dung*, *Biên tập*, *Đóng góp*, *Người nhận xét*, *Người xem*, *Chỉ xem (không tải)*, *Duyệt nội dung*), có thể đặt ngày hết hạn và lời nhắn; người nhận được thông báo. Danh sách bên dưới cho biết mỗi người có quyền từ đâu (*Trực tiếp*, *Kế thừa từ …*, *Thành viên Space*); quyền trực tiếp đổi cấp, đổi hạn hoặc gỡ ngay tại chỗ. **Quyền truy cập chung**: *Hạn chế* (chỉ người có tên ở trên) hoặc *Mọi người trong tổ chức* với một cấp quyền. Mục **Kế thừa quyền**: *Ngừng kế thừa* (giữ bản sao quyền hiện tại hoặc bắt đầu trống) và *Khôi phục kế thừa* (cho riêng mục này hoặc cả các mục con).
  - **Chia sẻ qua liên kết** (giống Google Drive): mở tab là có ngay liên kết của tài liệu — bấm **Sao chép** hoặc **mã QR**. Mục **Quyền truy cập chung** có 4 loại: **Hạn chế (người đã có quyền)** (mặc định) — chỉ người đã đăng nhập *và có quyền* với tư liệu mới mở được, liên kết không cấp thêm quyền; **Mọi người trong tổ chức** — ai đã đăng nhập mở liên kết là được cấp quyền đã chọn; **Người cụ thể** — chỉ những người được chọn (họ nhận thông báo kèm liên kết); **Bất kỳ ai có liên kết** — không cần đăng nhập. Với ba loại sau, chọn **Cấp quyền** (*Người xem*, *Người nhận xét*, *Biên tập*, *Chỉ xem (không tải)*), đặt thêm ngày hết hạn, số lượt truy cập tối đa nếu cần. Thu hồi liên kết là mọi quyền nó cấp mất ngay. Hệ thống chỉ lưu mã băm của liên kết; liên kết tạo trước bản cập nhật này vẫn mở được nhưng không hiển thị lại địa chỉ — bấm **Tạo liên kết mới** để có địa chỉ sao chép được. Bật/tắt **Cho phép tải xuống** rồi bấm **Lưu cài đặt**; **thu hồi** khi không cần nữa (liên kết cũ còn hiệu lực hiện thành một dòng nhắc để thu hồi). Với *Biên tập*, mở liên kết là vào ngay trình soạn thảo đầy đủ (không phải bấm thêm nút) và cùng soạn trực tiếp với mọi người đang mở tài liệu Word, Excel, PowerPoint. Người đã đăng nhập sửa bằng chính tài khoản của mình (phiên bản mang tên họ); người không đăng nhập hiện là "Khách", phiên bản họ lưu mang tên người tạo liên kết, nhật ký ghi rõ liên kết. Chỉ người có quyền sửa tài liệu mới chọn được *Biên tập*; nếu người đó mất quyền sửa thì liên kết tự về chỉ xem. Quản trị hệ thống tắt được chỉnh sửa qua liên kết ở **Quản trị → Cài đặt hệ thống → Chia sẻ**.
- Mục **Được chia sẻ**: tư liệu người khác chia sẻ với bạn, lọc theo người chia sẻ, thời gian; tab *Tệp đính kèm* liệt kê tệp đính kèm trong các thảo luận.

![Được chia sẻ](images/duoc-chia-se.png)

## 6. Gần đây, Quan trọng, Thùng rác (#18, #23, #61–62)

- **Gần đây**: tài liệu bạn vừa mở, sửa, tải lên.
- **Quan trọng**: tài liệu/thư mục đã đánh dấu sao.
- **Thùng rác**: chọn không gian (cá nhân hoặc nhóm), xem người xóa, thời điểm, vị trí gốc, ngày tự xóa. **Khôi phục** về vị trí cũ (nếu thư mục cũ không còn, khôi phục về thư mục gốc), **Xóa vĩnh viễn**, **Dọn sạch thùng rác**. Dung lượng chỉ được trả lại khi xóa vĩnh viễn; tệp tự xóa vĩnh viễn sau 30 ngày.

![Thùng rác](images/thung-rac.png)

## 7. Nhóm của tôi — tư liệu dùng chung (#28–38, #57–58)

![Nhóm của tôi](images/nhom-cua-toi.png)

- Tạo nhóm tài nguyên của nhóm/đơn vị, mời thành viên với vai trò: *Trưởng nhóm, Quản lý, Biên tập, Tải lên, Phê duyệt, Chỉ xem*. Trưởng nhóm/quản lý có thể **khóa nhóm** (chỉ xem, không ghi).
- Trong nhóm có đầy đủ thao tác như Tài liệu của tôi (tạo mới, tải lên, tìm kiếm, chia sẻ, phiên bản, thảo luận).
- Tab **Tổng hợp tư liệu**: số lượng, dung lượng tư liệu theo từng thành viên; lọc tư liệu theo thành viên.
- Tab **Lịch sử chỉnh sửa**: dòng thời gian thao tác của các thành viên.
- **Quản lý quyền truy cập** (chuột phải thư mục/tệp): cùng màn hình *Người có quyền truy cập* như trên; quyền của thư mục được kế thừa xuống mọi thư mục con và tệp bên trong, có thể **ngừng kế thừa** tại một thư mục con.
- **Thư viện tài liệu**: ở tab **Cài đặt** của Space, mục *Thư viện tài liệu*: tạo thư viện mới và mở *Cài đặt* từng thư viện — *Phiên bản chính và phụ* (mỗi lần lưu là bản nháp 1.1, 1.2…; chỉ người sửa hoặc người được xem bản nháp thấy; bấm **Xuất bản** để thành phiên bản chính cho mọi người), số phiên bản giữ lại, *Bắt buộc check-out*, *Phê duyệt nội dung*, định dạng và dung lượng cho phép. Trong bảng thông tin của tệp: **Check-out** (người khác chỉ xem tới khi bạn **Check-in**, có thể ghi chú và xuất bản khi check-in), *Bỏ check-out*; Quản lý Space *Ép check-in* được.
- **Cột thông tin, dạng xem, mẫu**: trong *Cài đặt* thư viện thêm **cột thông tin** (số hiệu, lĩnh vực, kinh phí…: kiểu, bắt buộc, không trùng, giá trị mặc định, kiểm tra hợp lệ) và **mẫu tài liệu**. Tab **Thư viện** của Space hiển thị tư liệu theo **dạng xem**: chọn cột, sắp xếp, lọc, nhóm, hiển thị phẳng; *Dạng xem mới* (cá nhân, hoặc công khai nếu bạn quản lý thư viện); bật **Sửa nhanh** để sửa giá trị ngay trên bảng; *Mới theo mẫu* tạo tài liệu từ mẫu. Giá trị cột của một tệp xem / sửa ở mục *Cột thông tin* trong bảng thông tin.
- **Trang chủ Space**: tab *Trang chủ* của Space cho thấy mô tả, ảnh bìa, thành viên, tài liệu gần đây, dung lượng, thư viện và hoạt động gần đây. Quản lý Space có thêm tab **Quyền của Space** liệt kê các thư mục, tệp có quyền riêng (ai được cấp trực tiếp, có liên kết không), mở để xem / sửa quyền hoặc *Khôi phục kế thừa*.
- **Chuyển quyền sở hữu**: chuột phải tư liệu trong Drive của tôi → *Chuyển quyền sở hữu* → chọn người nhận. Người nhận nhận thông báo, mở và bấm *Chấp nhận* (tư liệu chuyển sang Drive của họ, bạn vẫn có quyền Biên tập) hoặc *Từ chối*. Tư liệu trong Space thuộc về Space nên không chuyển quyền sở hữu.
- **Di chuyển giữa Drive của tôi và Space**: chỉ chủ sở hữu chuyển được tư liệu ra khỏi Drive của mình (vào Space thì tư liệu thuộc Space); chỉ Quản lý Space chuyển được tư liệu ra khỏi Space (và Space có thể cấm). Trước khi di chuyển, hệ thống hiện danh sách ai sẽ mất quyền (quyền có từ vị trí cũ) và ai được thêm quyền (từ vị trí mới); người được cấp quyền trực tiếp vẫn giữ quyền.
- **Lối tắt**: chuột phải một tệp/thư mục → *Thêm lối tắt vào Drive của tôi*. Lối tắt có nhãn **Lối tắt**, mở là mở tư liệu gốc với quyền của bạn trên tư liệu đó; khi bạn không còn quyền, mở lối tắt sẽ hiện trang yêu cầu quyền; khi tư liệu gốc bị xóa, lối tắt hiện "đích đã bị xóa". *Xóa lối tắt* không ảnh hưởng tư liệu gốc.
- **Tôi đã chia sẻ** (menu trái): mọi tư liệu bạn đã cấp quyền cho người khác hoặc tạo liên kết còn hiệu lực. **Bộ nhớ**: tệp trong Drive của tôi theo dung lượng (kể cả các phiên bản được giữ) để dọn dẹp khi gần hết dung lượng. Ở **Được chia sẻ với tôi** bật *Nhóm theo người chia sẻ* để xem theo từng người. Khi mở một thư mục được chia sẻ nằm sâu trong cây, đường dẫn chỉ bắt đầu từ thư mục bạn được chia sẻ (không hiện tên các thư mục phía trên).
- **Space (nhóm tài nguyên)**: khi tạo chọn **Mẫu Space** (Phòng ban, Dự án, Chiến dịch truyền thông có sẵn thư mục). Tab **Cài đặt** của Space (Quản lý Space): thêm cả *nhóm người dùng* hoặc *đơn vị* làm thành viên với vai trò; bật/tắt tải xuống, đồng bộ máy tính, tab Hoạt động cho thành viên; số ngày giữ thùng rác; chính sách chia sẻ; **Lưu trữ Space** (chỉ đọc, ẩn khỏi danh sách) hoặc **Xóa Space** (vào thùng rác hệ thống 93 ngày, quản trị khôi phục được ở **Quản trị → Thùng rác Space**).
- **Chia sẻ cho khách bên ngoài**: trong hộp thoại Chia sẻ, nhập email vào ô *Hoặc nhập email*. Email của cán bộ trong đơn vị là chính người đó; email khác trở thành **khách** (nếu chính sách chia sẻ cho phép) và nhận thư mời. Khách vào **trang đăng nhập → "Khách bên ngoài? Đăng nhập bằng mã email"**, nhập email để nhận mã 6 chữ số (hiệu lực 10 phút), không cần mật khẩu. Khách chỉ thấy tư liệu được chia sẻ trực tiếp, không tìm được người dùng nội bộ và không chia sẻ tiếp được. Quản trị bật/tắt khách, giới hạn tên miền và số ngày tự hết hạn ở **Cài đặt hệ thống → Khách bên ngoài**, quản lý danh sách khách ở **Quản trị → Khách bên ngoài**.
- **Nhắc hạn & gia hạn**: quyền có ngày hết hạn được báo trước 3 ngày (cho người được cấp và người cấp) và báo lại khi đã hết hạn; liên kết sắp hết hạn được báo cho người tạo. Bấm thông báo để mở trang **Gia hạn**: người cấp hoặc người quản lý mục chọn số ngày và bấm *Gia hạn*; người được cấp bấm *Đề nghị gia hạn* để gửi yêu cầu tới người quản lý.
- **Chính sách chia sẻ** (tầng chặt nhất thắng): quản trị đặt mức *Chia sẻ ra ngoài* và thời hạn tối đa của liên kết cho bất kỳ ai ở **Quản trị → Cài đặt hệ thống → Chia sẻ**; Quản lý Space đặt chặt hơn ở tab **Chia sẻ** của Space (kèm: thành viên có được chia sẻ cho người ngoài Space, có được chia sẻ riêng từng tệp/thư mục, ai duyệt yêu cầu truy cập); người quản lý quyền của thư mục/tệp đặt chặt hơn nữa ở mục *Chia sẻ ra ngoài* trong hộp thoại Chia sẻ. Hộp thoại cho biết mức đang áp dụng và tầng nào quyết định; lựa chọn *Bất kỳ ai có liên kết* bị tắt khi chính sách không cho phép.
- **Yêu cầu quyền truy cập**: mở địa chỉ một tệp chưa được cấp quyền sẽ hiện *Bạn cần quyền truy cập* — chọn cấp quyền cần, ghi lý do và *Gửi yêu cầu*. Người quản lý mục nhận thông báo, vào menu **Yêu cầu quyền truy cập** để *Duyệt* (có thể chọn cấp khác) hoặc *Từ chối*; yêu cầu không được trả lời hết hạn sau 30 ngày. Tab *Yêu cầu của tôi* cho biết kết quả các yêu cầu đã gửi. Bật **Cần phê duyệt khi tải lên** cho thư mục: tư liệu tải lên chờ người có vai trò Phê duyệt duyệt trước khi hiển thị cho người chỉ xem.

## 8. Phê duyệt (#43, #54)
Người có vai trò **Phê duyệt** vào mục *Phê duyệt*: xem trước tư liệu chờ duyệt, **Duyệt** hoặc **Từ chối** (nhập lý do). Người tải lên nhận thông báo kết quả.

![Phê duyệt](images/phe-duyet.png)

## 9. Tìm kiếm nâng cao (#20, #32, #50–51)

![Tìm kiếm](images/tim-kiem.png)

- Ô tìm kiếm trên đầu trang hoặc mục **Tìm kiếm nâng cao**. Gõ **có dấu hoặc không dấu** ("dan so" tìm được "Dân số"); tìm theo tên, tiêu đề, mô tả, thẻ và **nội dung bên trong tài liệu** (Word, Excel, PowerPoint, OpenDocument, PDF có lớp chữ, tệp văn bản): kết quả hiện đoạn trích có từ khóa được tô sáng. Tệp mới tải lên tìm được theo nội dung sau khi quét virus xong (vài giây); tệp đã có từ trước được hệ thống đọc dần trong nền. PDF chỉ là ảnh quét (không có lớp chữ) và định dạng cũ `.doc/.xls/.ppt` chưa tìm được theo nội dung.
- Bộ lọc: loại tư liệu, định dạng, danh mục (loại sản phẩm, chủ đề, chiến dịch, đối tượng...), **đơn vị đăng tải** (gồm đơn vị con), người đăng, khoảng thời gian, dung lượng, thẻ, phạm vi (toàn hệ thống hoặc một nhóm).
- Kết quả tô sáng từ khóa, hiển thị vị trí và đơn vị đăng tải; chỉ gồm tư liệu bạn có quyền xem. **Lưu bộ lọc** để dùng lại.

## 10. Hỗ trợ (#7 — mục 5.1 chất lượng dịch vụ)
Menu tài khoản > **Hỗ trợ**: gửi yêu cầu hỗ trợ/sự cố (mức độ *Khẩn cấp* hoặc *Không khẩn cấp*, mô tả, ảnh chụp màn hình), theo dõi trạng thái và kết quả xử lý. Thời hạn xử lý: khẩn cấp 4 giờ, không khẩn cấp 24 giờ.

![Hỗ trợ](images/ho-tro.png)

## 11. Báo cáo của tôi
Các báo cáo xuất Excel/PDF có khối lượng lớn được xử lý nền; khi xong bạn nhận thông báo, tải tệp tại *Báo cáo của tôi*.

## 12. Sử dụng trên điện thoại (#52–53, #71–84)

- **Trình duyệt điện thoại**: giao diện tự co giãn; menu trái mở bằng nút ☰.

  ![Menu trên điện thoại](images/mobile-menu.png)

- **Ứng dụng BlueDrive** (Android/iOS): nhập địa chỉ máy chủ và đăng nhập (có thể bật mở khóa bằng vân tay/khuôn mặt). Các mục: Trang chủ, Tài liệu của tôi, Được chia sẻ, Gần đây, Nhóm, Tìm kiếm. Với mỗi tệp: mở (ảnh, video, âm thanh, PDF, Office), đổi tên, đánh dấu quan trọng, sao chép/di chuyển (chọn thư mục đích trên cây thư mục), xóa, chia sẻ liên kết, xem chi tiết, **tải xuống** về máy (ảnh/video lưu vào Thư viện). Tải ảnh/video lên từ thư viện hoặc máy ảnh. Hướng dẫn cài đặt ứng dụng: `mobile/README.md`.

  | Trang chủ | Tìm kiếm không dấu | Xem PDF/Office | Chia sẻ liên kết |
  |---|---|---|---|
  | ![Trang chủ ứng dụng](images/mobile-trang-chu.png) | ![Tìm kiếm](images/mobile-tim-kiem.png) | ![Xem PDF](images/mobile-xem-pdf.png) | ![Chia sẻ liên kết](images/mobile-chia-se-lien-ket.png) |
  | **Nhóm và đơn vị** | **Được chia sẻ** | **Chi tiết tệp** | **Nghe âm thanh** |
  | ![Nhóm](images/mobile-nhom.png) | ![Được chia sẻ](images/mobile-duoc-chia-se.png) | ![Chi tiết tệp](images/mobile-chi-tiet.png) | ![Âm thanh](images/mobile-am-thanh.png) |
