# Ma trận truy vết chức năng HSMT (mục 3.2)

Mỗi dòng ánh xạ 1 chức năng trong HSMT với API, màn hình, quyền (permission) và test kiểm chứng. HSMT không có mục 25.

| STT HSMT | Nhóm | Chức năng | API | Màn hình | Permission | Test | Phase |
|---|---|---|---|---|---|---|---|
| 1 | Quản lý cơ cấu tổ chức (đơn vị, phòng/ban) | Thêm mới, sửa, xóa thông tin đơn vị/phòng ban. | _(phase 2)_ | | | | 2 |
| 2 | Quản lý cơ cấu tổ chức (đơn vị, phòng/ban) | Xem danh sách, tra cứu, tìm kiếm thông tin tổ chức. | _(phase 2)_ | | | | 2 |
| 3 | Quản lý cơ cấu tổ chức (đơn vị, phòng/ban) | In, kết xuất danh sách ra Excel, PDF. | _(phase 2)_ | | | | 2 |
| 4 | Quản lý người dùng, nhóm người dùng | Thêm mới, sửa, xóa tài khoản người dùng và nhóm người dùng. | _(phase 2)_ | | | | 2 |
| 5 | Quản lý người dùng, nhóm người dùng | Gán người dùng vào đơn vị/phòng ban và vai trò cụ thể. | _(phase 2)_ | | | | 2 |
| 6 | Quản lý người dùng, nhóm người dùng | Tra cứu, tìm kiếm, xuất danh sách ra Excel, PDF. | _(phase 2)_ | | | | 2 |
| 7 | Thiết lập vai trò, phân quyền chức năng | Thêm mới, sửa, xóa vai trò trong hệ thống. | _(phase 2)_ | | | | 2 |
| 8 | Thiết lập vai trò, phân quyền chức năng | Cấu hình phân quyền chi tiết theo từng vai trò (xem, tải lên, chỉnh sửa, xóa, chia sẻ, phê duyệt, quản trị). | _(phase 2)_ | | | | 2 |
| 9 | Thiết lập vai trò, phân quyền chức năng | Hiển thị toàn bộ chức năng và vai trò hiện có trên hệ thống để đối chiếu khi phân quyền. | _(phase 2)_ | | | | 2 |
| 10 | Danh mục dùng chung | Quản lý các danh mục động phục vụ quản lý sản phẩm truyền thông (hình ảnh, video, âm thanh, tài liệu, infographic, ấn phẩm điện tử)... | _(phase 2)_ | | | | 2 |
| 11 | Giám sát hoạt động của hệ thống | Ghi nhật ký (log) đăng nhập/đăng xuất và các thao tác tải lên, tải xuống, chỉnh sửa, chia sẻ, xóa tư liệu. | _(phase 7)_ | | | | 7 |
| 12 | Giám sát hoạt động của hệ thống | Tra cứu, lọc tìm kiếm nhật ký theo người dùng, thời gian, hành động. | _(phase 7)_ | | | | 7 |
| 13 | Giám sát hoạt động của hệ thống | Thống kê dung lượng lưu trữ đã sử dụng/còn lại theo tài khoản, đơn vị. | _(phase 7)_ | | | | 7 |
| 14 | Giám sát hoạt động của hệ thống | Cảnh báo bất thường (đăng nhập sai nhiều lần, dung lượng vượt ngưỡng cho phép). | _(phase 7)_ | | | | 7 |
| 15 | Giám sát hoạt động của hệ thống | In, kết xuất báo cáo giám sát ra Excel, PDF. | _(phase 7)_ | | | | 7 |
| 16 | Xác thực và kiểm soát truy cập | Đăng nhập bằng tài khoản/mật khẩu; hỗ trợ đổi mật khẩu, khôi phục mật khẩu. | _(phase 2)_ | | | | 2 |
| 17 | Xác thực và kiểm soát truy cập | Ghi nhận, kiểm soát phiên đăng nhập; cho phép thu hồi quyền truy cập khi cần. | _(phase 2)_ | | | | 2 |
| 18 | Quản lý tư liệu theo cá nhân | Xem tài liệu của tôi, tài liệu gần đây. | _(phase 3)_ | | | | 3 |
| 19 | Quản lý tư liệu theo cá nhân | Xem tài liệu được chia sẻ, tệp đính kèm. | _(phase 3)_ | | | | 3 |
| 20 | Quản lý tư liệu theo cá nhân | Tìm kiếm, tra cứu, lọc tài liệu theo từ khóa, loại tài liệu, thời gian. | _(phase 6)_ | | | | 6 |
| 21 | Quản lý tư liệu theo cá nhân | Tạo mới tài liệu (Word, Excel, PowerPoint, văn bản). | _(phase 5)_ | | | | 5 |
| 22 | Quản lý tư liệu theo cá nhân | Tải lên tệp/thư mục từ máy tính. | _(phase 3)_ | | | | 3 |
| 23 | Quản lý tư liệu theo cá nhân | Mở, chia sẻ, tải xuống, đánh dấu quan trọng tài liệu. | _(phase 3)_ | | | | 3 |
| 24 | Quản lý tư liệu theo cá nhân | Đổi tên, sao chép, cắt, sao chép tới, xem thông tin, xóa tài liệu. | _(phase 3)_ | | | | 3 |
| 26 | Quản lý tư liệu theo cá nhân | Quản lý, cập nhật, theo dõi phiên bản của tài liệu. | _(phase 5)_ | | | | 5 |
| 27 | Quản lý tư liệu theo cá nhân | Trao đổi thảo luận trong màn hình chi tiết tài liệu ngay khi được chia sẻ | _(phase 5)_ | | | | 5 |
| 28 | Quản lý tư liệu theo nhóm/đơn vị | Tạo, quản lý thư mục/tư liệu dùng chung theo nhóm, đơn vị. | _(phase 3)_ | | | | 3 |
| 29 | Quản lý tư liệu theo nhóm/đơn vị | Tổng hợp tư liệu của các thành viên trong nhóm. | _(phase 5)_ | | | | 5 |
| 30 | Quản lý tư liệu theo nhóm/đơn vị | Phân quyền tư liệu cho từng thành viên trong nhóm. | _(phase 4)_ | | | | 4 |
| 31 | Quản lý tư liệu theo nhóm/đơn vị | Ghi nhận lịch sử chỉnh sửa tư liệu theo từng thành viên. | _(phase 5)_ | | | | 5 |
| 32 | Quản lý tư liệu theo nhóm/đơn vị | Tìm kiếm, tra cứu, lọc tài liệu theo từ khóa, loại tài liệu, thời gian. | _(phase 6)_ | | | | 6 |
| 33 | Quản lý tư liệu theo nhóm/đơn vị | Tạo mới tài liệu (Word, Excel, PowerPoint, văn bản). | _(phase 5)_ | | | | 5 |
| 34 | Quản lý tư liệu theo nhóm/đơn vị | Tải lên tệp/thư mục từ máy tính. | _(phase 5)_ | | | | 5 |
| 35 | Quản lý tư liệu theo nhóm/đơn vị | Mở, chia sẻ, tải xuống, đánh dấu quan trọng tài liệu. | _(phase 5)_ | | | | 5 |
| 36 | Quản lý tư liệu theo nhóm/đơn vị | Đổi tên, sao chép, cắt, sao chép tới, xem thông tin, xóa tài liệu. | _(phase 5)_ | | | | 5 |
| 37 | Quản lý tư liệu theo nhóm/đơn vị | Quản lý, cập nhật, theo dõi phiên bản của tài liệu. | _(phase 5)_ | | | | 5 |
| 38 | Quản lý tư liệu theo nhóm/đơn vị | Trao đổi thảo luận trong màn hình chi tiết tài liệu ngay khi được chia sẻ | _(phase 5)_ | | | | 5 |
| 39 | Tải lên, biên tập tư liệu | Tải lên theo từng tệp từ máy tính, hỗ trợ định dạng: hình ảnh, video, âm thanh, văn bản, infographic, ấn phẩm điện tử. | _(phase 3)_ | | | | 3 |
| 40 | Tải lên, biên tập tư liệu | Tải lên cả thư mục từ máy tính. | _(phase 3)_ | | | | 3 |
| 41 | Tải lên, biên tập tư liệu | Tạo mới tài liệu văn bản/bảng tính/trình chiếu. | _(phase 5)_ | | | | 5 |
| 42 | Tải lên, biên tập tư liệu | Chỉnh sửa tài liệu trực tuyến (online). | _(phase 5)_ | | | | 5 |
| 43 | Tải lên, biên tập tư liệu | Quản lý phiên bản (version), cho phép cập nhật phiên bản mới đối với tài liệu văn bản. | _(phase 5)_ | | | | 5 |
| 44 | Thao tác trên thư mục/tệp tư liệu | Xem, mở tài liệu. | _(phase 3)_ | | | | 3 |
| 45 | Thao tác trên thư mục/tệp tư liệu | Chia sẻ, tải xuống tài liệu. | _(phase 3)_ | | | | 3 |
| 46 | Thao tác trên thư mục/tệp tư liệu | Đổi tên, sao chép, cắt/di chuyển tài liệu. | _(phase 3)_ | | | | 3 |
| 47 | Thao tác trên thư mục/tệp tư liệu | Đánh dấu quan trọng, xem thông tin tài liệu. | _(phase 3)_ | | | | 3 |
| 48 | Thao tác trên thư mục/tệp tư liệu | Xóa tài liệu. | _(phase 3)_ | | | | 3 |
| 49 | Thao tác trên thư mục/tệp tư liệu | Chia sẻ nội bộ hoặc chia sẻ ra ngoài qua liên kết (link), có thể giới hạn quyền xem/tải xuống. | _(phase 4)_ | | | | 4 |
| 50 | Tra cứu, tìm kiếm, khai thác tư liệu | Tra cứu, tìm kiếm tư liệu theo từ khóa, loại tư liệu. | _(phase 6)_ | | | | 6 |
| 51 | Tra cứu, tìm kiếm, khai thác tư liệu | Lọc tư liệu theo đơn vị đăng tải, khoảng thời gian. | _(phase 6)_ | | | | 6 |
| 52 | Tra cứu, tìm kiếm, khai thác tư liệu | Truy cập, khai thác tư liệu từ nhiều thiết bị, nhiều địa điểm khác nhau (đăng nhập từ xa qua Internet). | _(phase 6)_ | | | | 6 |
| 53 | Tra cứu, tìm kiếm, khai thác tư liệu | Chia sẻ tư liệu khi truy cập từ xa. | _(phase 4)_ | | | | 4 |
| 54 | Phân quyền theo vai trò | Phân quyền chi tiết theo vai trò: quản trị, biên tập/tải lên, phê duyệt, chỉ xem. | _(phase 4)_ | | | | 4 |
| 55 | Phân quyền theo vai trò | Phân quyền truy cập tư liệu theo đơn vị hoặc nhóm. | _(phase 4)_ | | | | 4 |
| 56 | Phân quyền theo vai trò | Phân quyền truy cập tư liệu theo từng thư mục/tệp cụ thể. | _(phase 4)_ | | | | 4 |
| 57 | Cấu hình | Thêm, xóa thành viên nhóm tài nguyên. | _(phase 3)_ | | | | 3 |
| 58 | Cấu hình | Phân quyền sử dụng, khóa nhóm tài nguyên. | _(phase 3)_ | | | | 3 |
| 59 | Cấu hình | Cấu hình dung lượng cá nhân. | _(phase 3)_ | | | | 3 |
| 60 | Cấu hình | Cấu hình dung lượng nhóm. | _(phase 3)_ | | | | 3 |
| 61 | Cấu hình | Xem thùng rác theo nhóm. | _(phase 3)_ | | | | 3 |
| 62 | Cấu hình | Khôi phục, xóa vĩnh viễn tệp tại thùng rác. | _(phase 3)_ | | | | 3 |
| 63 | Tổng quan dung lượng cá nhân | Hiển thị dung lượng cá nhân đã sử dụng trên tổng dung lượng được cấp, kèm liên kết nhanh. | _(phase 8)_ | | | | 8 |
| 64 | Biểu đồ phân bổ dung lượng theo loại file cá nhân | Biểu đồ tròn thể hiện tỷ lệ dung lượng theo từng loại file. | _(phase 8)_ | | | | 8 |
| 65 | Biểu đồ top thư mục cá nhân chiếm nhiều dung lượng nhất | Biểu đồ xếp hạng các thư mục cá nhân theo dung lượng sử dụng. | _(phase 8)_ | | | | 8 |
| 66 | Hiển thị danh sách tài liệu cá nhân gần đây | Hiển thị tên tài liệu, người sở hữu, dung lượng, thời gian sửa gần nhất. | _(phase 8)_ | | | | 8 |
| 67 | Tổng quan nhóm tài liệu | Hiển thị số nhóm đang là thành viên, số nhóm đang làm trưởng nhóm, kèm liên kết nhanh. | _(phase 8)_ | | | | 8 |
| 68 | Biểu đồ dung lượng sử dụng theo nhóm | Biểu đồ cột ngang thể hiện dung lượng sử dụng của từng nhóm. | _(phase 8)_ | | | | 8 |
| 69 | Biểu đồ phân bổ loại file trong từng nhóm | Biểu đồ thể hiện tỷ lệ % các loại file trong từng nhóm. | _(phase 8)_ | | | | 8 |
| 70 | Hiển thị danh sách tài liệu nhóm gần đây | Hiển thị tên tài liệu, tên nhóm, dung lượng, thời gian sửa gần nhất. | _(phase 8)_ | | | | 8 |
| 71 | Xem tài liệu của tôi | Xem danh sách các tài liệu do người dùng tạo, tải lên hoặc đang được quản lý. | _(phase 10)_ | | | | 10 |
| 72 | Xem tài liệu được chia sẻ. | Xem các tài liệu được người khác chia sẻ cho người dùng. | _(phase 10)_ | | | | 10 |
| 73 | Xem tài liệu gần đây | Xem danh sách các tài liệu được truy cập, cập nhật hoặc thao tác gần đây. | _(phase 10)_ | | | | 10 |
| 74 | Xem tài liệu nhóm | Xem các tài liệu thuộc nhóm mà người dùng tham gia hoặc được phân quyền truy cập. | _(phase 10)_ | | | | 10 |
| 75 | Tìm kiếm tài liệu | Tìm kiếm tài liệu theo tên, từ khóa hoặc các thông tin liên quan. | _(phase 10)_ | | | | 10 |
| 76 | Mở tài liệu | Mở và xem nội dung tài liệu trên hệ thống. | _(phase 10)_ | | | | 10 |
| 77 | Đổi tên tài liệu | Thay đổi tên tài liệu theo nhu cầu của người dùng. | _(phase 10)_ | | | | 10 |
| 78 | Đánh dấu quan trọng | Đánh dấu tài liệu là quan trọng để dễ nhận biết và truy cập nhanh. | _(phase 10)_ | | | | 10 |
| 79 | Sao chép | Tạo một bản sao của tài liệu và lưu tại vị trí được lựa chọn. | _(phase 10)_ | | | | 10 |
| 80 | Di chuyển | Di chuyển tài liệu sang thư mục hoặc vị trí lưu trữ khác. | _(phase 10)_ | | | | 10 |
| 81 | Xóa | Xóa tài liệu khỏi vị trí lưu trữ hiện tại. | _(phase 10)_ | | | | 10 |
| 82 | Xem cây thư mục | Xem cấu trúc thư mục/tư liệu theo phân quyền được cấp | _(phase 10)_ | | | | 10 |
| 83 | Xem chi tiết tệp | Xem thông tin, nội dung chi tiết của tệp tư liệu | _(phase 10)_ | | | | 10 |
| 84 | Tải xuống tư liệu | Tải xuống tệp tư liệu về thiết bị di động. | _(phase 10)_ | | | | 10 |
