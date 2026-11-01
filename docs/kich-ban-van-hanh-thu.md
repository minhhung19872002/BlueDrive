# Kịch bản vận hành thử BlueDrive

Dùng cho Chủ đầu tư khi vận hành thử (HSMT mục 3.5) và nghiệm thu theo từng dòng chức năng mục 3.2 (HSMT không có mục 25).

**Chuẩn bị**: hệ thống đã cài đặt (`docs/huong-dan-cai-dat.md`), đã nạp dữ liệu mẫu (`scripts/seed.ps1`): tài khoản `canbo1`, `canbo2` (Biên tập/Tải lên), `canbo3` (Phê duyệt), `canbo4` (Chỉ xem), `canbo5` (Biên tập), mật khẩu ban đầu `Demo@12345` (đổi khi đăng nhập lần đầu); 2 nhóm tài nguyên; tệp mẫu các loại. Cấu hình SMTP để kiểm tra email.

Cột "Kết quả mong đợi" ghi "Thành công" nghĩa là thao tác thực hiện được, dữ liệu hiển thị đúng, có thông báo tiếng Việt.

## A. Quản trị hệ thống

| STT HSMT | Chức năng | Các bước thực hiện | Dữ liệu thử | Kết quả mong đợi | Đạt | Không đạt | Ghi chú |
|---|---|---|---|---|---|---|---|
| 1 | Thêm, sửa, xóa đơn vị/phòng ban | Quản trị > Cơ cấu tổ chức > Thêm; sửa tên; kéo thả sang đơn vị cha khác; xóa đơn vị còn người dùng; xóa đơn vị trống | Mã `PB-TEST`, tên "Phòng Thử nghiệm" | Thêm/sửa thành công; trùng mã báo lỗi; đơn vị còn người dùng không xóa được và đề nghị Ngừng hoạt động; đơn vị trống xóa được | ☐ | ☐ | |
| 2 | Xem danh sách, tra cứu tổ chức | Chuyển dạng cây/bảng; tìm "phong thu nghiem" (không dấu); lọc trạng thái, đơn vị cha | Từ khóa không dấu | Hiển thị đúng; tìm không dấu ra "Phòng Thử nghiệm" | ☐ | ☐ | |
| 3 | In, xuất Excel, PDF danh sách tổ chức | Lọc rồi bấm Xuất Excel, Xuất PDF, In | Bộ lọc đang áp dụng | Tệp có tiêu đề, ngày xuất, người xuất, đúng dữ liệu đã lọc; bản in có Quốc hiệu | ☐ | ☐ | |
| 4 | Thêm, sửa, xóa tài khoản và nhóm người dùng | Thêm người dùng (để trống mật khẩu); sửa; khóa/mở khóa; đặt lại mật khẩu; nhập từ Excel theo mẫu; tạo nhóm người dùng, thêm thành viên; xóa | Tệp mẫu Excel có 1 dòng lỗi | Hiện mật khẩu tạm; dòng lỗi báo đúng số dòng và không nhập; người bị khóa không đăng nhập được | ☐ | ☐ | |
| 5 | Gán người dùng vào đơn vị và vai trò | Chọn nhiều người dùng > Gán hàng loạt đơn vị + vai trò | 2 người dùng, vai trò Biên tập | Người dùng nhận đơn vị và quyền mới ngay | ☐ | ☐ | |
| 6 | Tra cứu, xuất danh sách người dùng | Lọc theo đơn vị (gồm đơn vị con), vai trò, trạng thái, nhóm; xuất Excel/PDF | | Danh sách và tệp xuất đúng bộ lọc | ☐ | ☐ | |
| 7 | Thêm, sửa, xóa vai trò | Tạo vai trò "Thử nghiệm" (sao chép từ Chỉ xem); sửa; xóa; thử xóa vai trò hệ thống | | Vai trò hệ thống, vai trò đang gán không xóa được | ☐ | ☐ | |
| 8 | Cấu hình phân quyền chi tiết theo vai trò | Mở ma trận quyền, chọn cả dòng/cột, Lưu; đăng nhập tài khoản có vai trò đó | Quyền Cơ cấu tổ chức - Xem | Người dùng thấy/không thấy chức năng theo ma trận; nhật ký ghi Đổi quyền | ☐ | ☐ | |
| 9 | Hiển thị toàn bộ chức năng và vai trò để đối chiếu | Mở Bảng đối chiếu quyền, lọc theo phân hệ, xuất Excel | | Bảng chức năng × vai trò khớp ma trận | ☐ | ☐ | |
| 10 | Danh mục dùng chung | Thêm loại danh mục và mục (có mục con), tắt mục, nhập/xuất Excel | Danh mục "Chủ đề" | Mục mới có ở bộ lọc tìm kiếm và khi gắn thuộc tính tư liệu | ☐ | ☐ | |
| 11 | Ghi nhật ký thao tác | Đăng nhập, tải lên, tải xuống, sửa, chia sẻ, hủy chia sẻ, đổi tên, di chuyển, sao chép, xóa, khôi phục, xóa vĩnh viễn, đổi quyền; đăng xuất | Tài khoản `canbo1` | Mọi thao tác có trong Nhật ký hoạt động với đúng người, thời gian, IP, đối tượng | ☐ | ☐ | |
| 12 | Tra cứu, lọc nhật ký | Lọc theo người dùng, thời gian, hành động, tên tư liệu, IP; xem chi tiết; bấm Kiểm tra toàn vẹn | | Kết quả lọc đúng; kiểm tra toàn vẹn báo chuỗi hợp lệ | ☐ | ☐ | |
| 13 | Thống kê dung lượng theo tài khoản, đơn vị | Mở Thống kê dung lượng, xem các tab, sắp xếp theo mức dùng | | Khớp dung lượng thực tế; tô màu khi ≥ 80% / ≥ 100% | ☐ | ☐ | |
| 14 | Cảnh báo bất thường | Đăng nhập sai 5 lần; đặt hạn mức nhỏ rồi tải tệp vượt ngưỡng; đặt ngưỡng tải xuống thấp rồi tải nhiều tệp | Tài khoản thử | Tài khoản bị khóa tạm; quản trị nhận cảnh báo thời gian thực (chuông), trên trang Cảnh báo và email; xác nhận đã xử lý được | ☐ | ☐ | |
| 15 | In, xuất báo cáo giám sát | Xuất báo cáo nhật ký, dung lượng, cảnh báo ra Excel/PDF; In | | Thông báo "đang xử lý"; nhận thông báo khi xong; tệp đúng dữ liệu | ☐ | ☐ | |
| 16 | Đăng nhập, đổi mật khẩu, khôi phục mật khẩu | Đăng nhập lần đầu (bắt buộc đổi); đổi mật khẩu sai chính sách; Quên mật khẩu qua email; dùng lại liên kết lần 2 | Email thật | Bắt buộc đổi mật khẩu; lỗi chính sách bằng tiếng Việt; liên kết dùng một lần, hết hạn sau 30 phút | ☐ | ☐ | |
| 17 | Ghi nhận, kiểm soát phiên, thu hồi truy cập | Đăng nhập 2 thiết bị; xem Phiên đăng nhập; đăng xuất thiết bị khác; quản trị thu hồi toàn bộ phiên | | Thiết bị bị thu hồi phải đăng nhập lại ngay | ☐ | ☐ | |

## B. Lưu trữ sản phẩm truyền thông

| STT HSMT | Chức năng | Các bước thực hiện | Dữ liệu thử | Kết quả mong đợi | Đạt | Không đạt | Ghi chú |
|---|---|---|---|---|---|---|---|
| 18 | Xem tài liệu của tôi, gần đây | Mở Tài liệu của tôi; mở/sửa vài tệp rồi mở Gần đây | | Gần đây sắp theo thời gian mới nhất | ☐ | ☐ | |
| 19 | Xem tài liệu được chia sẻ, tệp đính kèm | `canbo1` chia sẻ cho `canbo2`; `canbo2` mở Được chia sẻ, lọc theo người chia sẻ; tab Tệp đính kèm | | Thấy tệp và lời nhắn; mở được tệp đính kèm trong thảo luận | ☐ | ☐ | |
| 20 | Tìm kiếm, lọc tài liệu cá nhân | Trong Tài liệu của tôi tìm "dan so", lọc loại, thời gian | Tệp "Báo cáo Dân số" | Gõ không dấu ra tệp có dấu | ☐ | ☐ | |
| 21 | Tạo mới Word, Excel, PowerPoint, văn bản | Mới > Tài liệu Word / Bảng tính / Trình chiếu / Văn bản | | Tệp được tạo và mở ngay trong trình soạn thảo trực tuyến | ☐ | ☐ | |
| 22 | Tải lên tệp/thư mục | Kéo thả nhiều tệp; tải lên thư mục có thư mục con; tạm dừng/tiếp tục tệp lớn | Video ≥ 1 GB, thư mục 2 cấp | Giữ nguyên cây; tiếp tục được sau khi tạm dừng; vượt dung lượng có thông báo | ☐ | ☐ | |
| 23 | Mở, chia sẻ, tải xuống, đánh dấu quan trọng | Mở ảnh/video/PDF; tải 1 tệp và nhiều tệp (zip); đánh dấu sao | | Xem được theo loại; zip đúng cấu trúc; tệp có sao ở mục Quan trọng | ☐ | ☐ | |
| 24 | Đổi tên, sao chép, cắt, sao chép tới, xem thông tin, xóa | F2 đổi tên; Ctrl+C/Ctrl+V; Ctrl+X/Ctrl+V; Sao chép tới nhóm khác; panel thông tin; Del | | Đúng; panel có kích thước, người tạo, vị trí, phiên bản, ai có quyền | ☐ | ☐ | |
| 26 | Quản lý, cập nhật, theo dõi phiên bản | Sửa trực tuyến, đóng; tải lên phiên bản mới; xem/tải phiên bản cũ; khôi phục phiên bản 1 | | Mỗi lần lưu tạo 1 phiên bản; khôi phục tạo phiên bản mới | ☐ | ☐ | |
| 27 | Thảo luận trong chi tiết tài liệu | Tab Thảo luận: bình luận, trả lời, @nhắc tên, đính kèm, sửa/xóa bình luận của mình | 2 người cùng mở | Hiện thời gian thực; người được nhắc nhận thông báo; người không có quyền xem không thấy | ☐ | ☐ | |
| 28 | Thư mục/tư liệu dùng chung theo nhóm, đơn vị | Nhóm của tôi > Tạo nhóm (nhóm/đơn vị); tạo thư mục dùng chung | | Nhóm hiện ở menu trái của thành viên | ☐ | ☐ | |
| 29 | Tổng hợp tư liệu thành viên | Mở nhóm > Tổng hợp tư liệu; lọc theo thành viên | | Số lượng, dung lượng theo thành viên đúng | ☐ | ☐ | |
| 30 | Phân quyền tư liệu cho thành viên | Quản lý quyền truy cập thư mục: Xem cho 1 thành viên, Chỉnh sửa cho người khác | | Thao tác đúng mức quyền | ☐ | ☐ | |
| 31 | Lịch sử chỉnh sửa theo thành viên | Nhóm > Lịch sử chỉnh sửa, lọc thành viên | | Đúng người, đúng thời điểm | ☐ | ☐ | |
| 32 | Tìm kiếm, lọc tài liệu nhóm | Trong nhóm tìm theo từ khóa, loại, thời gian | | Chỉ ra kết quả trong nhóm | ☐ | ☐ | |
| 33 | Tạo mới tài liệu trong nhóm | Trong nhóm: Mới > Tài liệu Word | | Tạo và mở soạn thảo trong thư mục nhóm | ☐ | ☐ | |
| 34 | Tải lên tệp/thư mục trong nhóm | Kéo thả thư mục vào nhóm | | Như #22, dung lượng tính vào nhóm | ☐ | ☐ | |
| 35 | Mở, chia sẻ, tải xuống, quan trọng (nhóm) | Như #23 trong nhóm | | Như #23 | ☐ | ☐ | |
| 36 | Đổi tên, sao chép, cắt, sao chép tới, thông tin, xóa (nhóm) | Như #24 trong nhóm; xóa vào thùng rác nhóm | | Như #24 | ☐ | ☐ | |
| 37 | Phiên bản tài liệu nhóm | Như #26 với tài liệu nhóm | | Như #26 | ☐ | ☐ | |
| 38 | Thảo luận tài liệu nhóm | Như #27 với tài liệu nhóm | | Như #27 | ☐ | ☐ | |
| 39 | Tải lên từng tệp các định dạng | Tải ảnh (jpg, png, heic), video (mp4, mov), âm thanh (mp3, wav), văn bản (docx, pdf), infographic, ấn phẩm (epub); nhập tiêu đề, mô tả, danh mục, thẻ áp dụng hàng loạt; thử tệp .exe đổi tên .jpg | Bộ tệp mẫu | Thành công, có ảnh thu nhỏ; tệp giả mạo bị chặn với thông báo rõ | ☐ | ☐ | |
| 40 | Tải lên cả thư mục | Tải lên thư mục nhiều cấp | | Giữ nguyên cây | ☐ | ☐ | |
| 41 | Tạo mới văn bản/bảng tính/trình chiếu | Như #21 | | Thành công | ☐ | ☐ | |
| 42 | Chỉnh sửa trực tuyến | 2 người cùng mở một tài liệu Word và cùng gõ | | Đồng soạn thảo thời gian thực; lưu tạo phiên bản mới | ☐ | ☐ | |
| 43 | Quản lý phiên bản; luồng phê duyệt | Như #26; thư mục bật "Cần phê duyệt", `canbo1` tải lên, `canbo3` duyệt/từ chối | | Chờ duyệt không hiện với `canbo4` (Chỉ xem); duyệt xong mới hiện; từ chối có lý do, người tải lên nhận thông báo | ☐ | ☐ | |
| 44 | Xem, mở tài liệu | Chế độ lưới/danh sách; mở ảnh, video (tua), PDF, Office | | Thành công | ☐ | ☐ | |
| 45 | Chia sẻ, tải xuống | Từ menu chuột phải và thanh công cụ | | Thành công | ☐ | ☐ | |
| 46 | Đổi tên, sao chép, cắt/di chuyển | Kéo thả tệp vào thư mục khác; đổi tên; sao chép | | Thành công | ☐ | ☐ | |
| 47 | Đánh dấu quan trọng, xem thông tin | Đánh dấu sao; mở panel thông tin | | Thành công | ☐ | ☐ | |
| 48 | Xóa tài liệu | Chọn nhiều, Del, xác nhận | | Vào thùng rác | ☐ | ☐ | |
| 49 | Chia sẻ nội bộ hoặc qua liên kết | Chia sẻ cho người/nhóm/đơn vị (Xem; Xem + Tải; Chỉnh sửa); tạo liên kết có mật khẩu, hạn dùng, giới hạn 2 lượt, không cho tải; quét QR; thu hồi | | Mở `/s/...` trên máy khác: đúng mật khẩu mới xem; lượt thứ 3 bị từ chối; không có nút tải; thu hồi xong không mở được; lượt truy cập có trong nhật ký | ☐ | ☐ | |
| 50 | Tra cứu theo từ khóa, loại tư liệu | Tìm kiếm nâng cao: từ khóa không dấu, loại tư liệu, danh mục | | Có tô sáng từ khóa; chỉ gồm tư liệu có quyền | ☐ | ☐ | |
| 51 | Lọc theo đơn vị đăng tải, khoảng thời gian | Lọc đơn vị đăng tải (gồm đơn vị con), khoảng ngày; lưu bộ lọc | | Đúng kết quả; dùng lại bộ lọc đã lưu | ☐ | ☐ | |
| 52 | Truy cập từ nhiều thiết bị, địa điểm | Đăng nhập qua Internet trên máy tính, máy tính bảng, điện thoại (trình duyệt và ứng dụng) | Mạng 4G | Giao diện co giãn, thao tác được | ☐ | ☐ | |
| 53 | Chia sẻ khi truy cập từ xa | Trên điện thoại chia sẻ liên kết tư liệu | | Nhận liên kết qua ứng dụng nhắn tin, mở được | ☐ | ☐ | |
| 54 | Phân quyền theo vai trò | Đăng nhập lần lượt 4 vai trò | `canbo1`..`canbo4` | Mỗi vai trò chỉ thực hiện đúng chức năng | ☐ | ☐ | |
| 55 | Phân quyền theo đơn vị hoặc nhóm | Cấp quyền thư mục cho một đơn vị, một nhóm người dùng | | Thành viên đơn vị/nhóm truy cập được | ☐ | ☐ | |
| 56 | Phân quyền theo thư mục/tệp | Quản lý quyền truy cập: thêm người, mức quyền, ngắt kế thừa ở thư mục con | | Thư mục con ngắt kế thừa không còn cho người được cấp ở thư mục cha | ☐ | ☐ | |
| 57 | Thêm, xóa thành viên nhóm tài nguyên | Nhóm > Thành viên: thêm, đổi vai trò, xóa | | Thành công | ☐ | ☐ | |
| 58 | Phân quyền sử dụng, khóa nhóm | Khóa nhóm | | Thành viên chỉ xem, không ghi được | ☐ | ☐ | |
| 59 | Cấu hình dung lượng cá nhân | Đặt dung lượng cho 1 người và cả đơn vị | | Cập nhật ngay; vượt hạn mức không tải lên được | ☐ | ☐ | |
| 60 | Cấu hình dung lượng nhóm | Đặt hạn mức nhóm | | Thành công | ☐ | ☐ | |
| 61 | Thùng rác theo nhóm | Mở Thùng rác, chọn nhóm | | Người xóa, thời điểm, vị trí gốc, ngày tự xóa | ☐ | ☐ | |
| 62 | Khôi phục, xóa vĩnh viễn | Khôi phục tệp (thư mục gốc đã bị xóa); xóa vĩnh viễn; Dọn sạch thùng rác | | Khôi phục về vị trí gốc hoặc về gốc; dung lượng giảm sau xóa vĩnh viễn | ☐ | ☐ | |

## C. Dashboard

| STT HSMT | Chức năng | Các bước thực hiện | Dữ liệu thử | Kết quả mong đợi | Đạt | Không đạt | Ghi chú |
|---|---|---|---|---|---|---|---|
| 63 | Tổng quan dung lượng cá nhân | Mở Trang chủ | | Đã dùng/tổng, %, liên kết nhanh | ☐ | ☐ | |
| 64 | Biểu đồ phân bổ theo loại file | Biểu đồ tròn | | Tỷ lệ đúng | ☐ | ☐ | |
| 65 | Top thư mục cá nhân | Biểu đồ cột; bấm một cột | | Mở đúng thư mục | ☐ | ☐ | |
| 66 | Tài liệu cá nhân gần đây | Bảng gần đây | | Tên, người sở hữu, dung lượng, thời gian sửa | ☐ | ☐ | |
| 67 | Tổng quan nhóm | Phần nhóm ở Trang chủ | | Số nhóm tham gia, làm trưởng nhóm, liên kết nhanh | ☐ | ☐ | |
| 68 | Dung lượng theo nhóm | Biểu đồ cột ngang | | Dung lượng so với hạn mức | ☐ | ☐ | |
| 69 | Phân bổ loại file trong nhóm | Biểu đồ tỷ lệ % | | Đúng | ☐ | ☐ | |
| 70 | Tài liệu nhóm gần đây | Bảng tài liệu nhóm gần đây | | Tên, nhóm, dung lượng, thời gian sửa | ☐ | ☐ | |

## D. Ứng dụng di động (Android/iOS)

| STT HSMT | Chức năng | Các bước thực hiện | Dữ liệu thử | Kết quả mong đợi | Đạt | Không đạt | Ghi chú |
|---|---|---|---|---|---|---|---|
| 71 | Xem tài liệu của tôi | Mở tab Tài liệu | Điện thoại thật | Thành công | ☐ | ☐ | |
| 72 | Xem tài liệu được chia sẻ | Tab Chia sẻ | | Thành công | ☐ | ☐ | |
| 73 | Xem tài liệu gần đây | Mục Gần đây | | Thành công | ☐ | ☐ | |
| 74 | Xem tài liệu nhóm | Tab Nhóm | | Thành công | ☐ | ☐ | |
| 75 | Tìm kiếm | Tìm "dan so" | | Ra tài liệu có dấu | ☐ | ☐ | |
| 76 | Mở tài liệu | Mở ảnh, video, âm thanh, PDF, Word | | Thành công | ☐ | ☐ | |
| 77 | Đổi tên | Menu ⋯ > Đổi tên | | Thành công | ☐ | ☐ | |
| 78 | Đánh dấu quan trọng | Menu ⋯ > Đánh dấu quan trọng | | Thành công | ☐ | ☐ | |
| 79 | Sao chép | Sao chép tới thư mục khác (chọn bằng cây thư mục) | | Thành công | ☐ | ☐ | |
| 80 | Di chuyển | Di chuyển tới thư mục khác | | Thành công | ☐ | ☐ | |
| 81 | Xóa | Xóa, xác nhận | | Vào thùng rác | ☐ | ☐ | |
| 82 | Xem cây thư mục | Duyệt cây thư mục theo quyền | | Thành công | ☐ | ☐ | |
| 83 | Xem chi tiết tệp | Mở chi tiết | | Thông tin + xem trước | ☐ | ☐ | |
| 84 | Tải xuống | Tải ảnh/video vào Thư viện, tệp khác lưu/chia sẻ | | Thành công | ☐ | ☐ | |

## E. Kiểm tra phi chức năng

| Nội dung | Cách kiểm tra | Kết quả mong đợi | Đạt | Không đạt |
|---|---|---|---|---|
| Thời gian xử lý giao dịch | Mở thư mục, tìm kiếm, mở tệp | < 2 giây (HSMT: < 10 giây); báo cáo lớn có thông báo đang xử lý | ☐ | ☐ |
| Tải tệp lớn | `scripts/test-large-upload.ps1` (2 GB) | Tải lên có tạm dừng/tiếp tục, tua video được | ☐ | ☐ |
| Mã hóa khi truyền | Mở bằng https, kiểm tra chứng chỉ | TLS hợp lệ, http chuyển sang https | ☐ | ☐ |
| Sao lưu đột xuất + phục hồi | Sao lưu ngay → xóa vĩnh viễn 1 tệp → `scripts/restore.ps1 <mã>` | Tệp được khôi phục | ☐ | ☐ |
| Xuất toàn bộ dữ liệu | Quản trị > Sao lưu > Xuất toàn bộ dữ liệu | Gói ZIP mở được, có metadata | ☐ | ☐ |
| Trình duyệt | Chrome, Edge, Firefox, Safari bản mới | Hiển thị, thao tác đúng | ☐ | ☐ |
| Font chữ Unicode | Xem trên Windows, Linux, macOS | Tiếng Việt hiển thị đúng | ☐ | ☐ |

| Đại diện Chủ đầu tư | Đại diện Nhà thầu |
|---|---|
| (Ký, ghi rõ họ tên) | (Ký, ghi rõ họ tên) |
