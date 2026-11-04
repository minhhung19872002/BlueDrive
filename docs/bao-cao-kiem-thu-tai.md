# Báo cáo kiểm thử tải BlueDrive (Đợt 5)

Tài liệu ghi lại phương pháp, số liệu đo và các thay đổi đã thực hiện sau khi kiểm thử tải hệ thống BlueDrive trên môi trường giả lập cấu hình máy chủ của gói thầu. Kết quả dùng làm căn cứ cho yêu cầu phi chức năng mục 2.1 của HSMT ("thời gian xử lý tối đa cho một giao dịch là dưới 10 giây") và cho hồ sơ vận hành thử.

> Kiểm thử tải **không** chạy trên máy chủ thật (VM dùng chung 14.225.83.93): toàn bộ chạy trên máy phát triển bằng một bộ container riêng (`bluedrive-perf`), không dùng chung dữ liệu, mạng hay volume với môi trường nào khác.

## 1. Mục tiêu và ngưỡng đánh giá

| Ngưỡng | Giá trị | Căn cứ |
|---|---|---|
| Thời gian tối đa một giao dịch | < 10 giây (mọi yêu cầu đọc/ghi) | HSMT mục 2.1 |
| Thời gian phản hồi p95 (đọc, ghi) | < 2 giây | Mục tiêu nội bộ để có biên an toàn cho mục 2.1 |
| Tỷ lệ lỗi HTTP | < 1 % | HSMT mục 2.1 "không phát sinh lỗi chức năng" |
| Tỷ lệ kiểm tra (check) đạt | > 99 % | Như trên |
| Đăng nhập đầu giờ (T3) | 100 % thành công, không bị chặn 429 | Cả cơ quan dùng chung một địa chỉ NAT |
| Tải đồng thời mục tiêu | 100 người dùng làm việc liên tục; kiểm thử quá tải đến 400 | Quy mô Trung tâm + các đơn vị |

Các ngưỡng được khai báo trong `tests/load/lib.js` (`HSMT_THRESHOLDS`); k6 báo "KHÔNG ĐẠT ngưỡng" nếu một ngưỡng bị vượt.

## 2. Môi trường giả lập

| Thành phần | Cấu hình |
|---|---|
| Máy chạy | Windows 11, 32 luồng CPU, Docker Desktop (WSL2) |
| Giới hạn tài nguyên | Mọi dịch vụ ghim vào 8 lõi CPU (`cpuset: 0-7`), giữ nguyên giới hạn bộ nhớ của `deploy/docker-compose.yml` (tương đương máy chủ 8 lõi / 16 GB của HSMT) |
| Ngăn xếp | Đúng `deploy/docker-compose.yml` dùng khi triển khai (Caddy, web, API, Worker, PostgreSQL 16, ONLYOFFICE, ClamAV) + lớp phủ `deploy/docker-compose.perf.yml` |
| Đường mạng | Máy kiểm thử đi qua dịch vụ `edge` (Toxiproxy, cổng 8091), có `tc netem`: trễ 12 ms mỗi chiều (≈25 ms khứ hồi) và băng thông 300 Mbit/s dùng chung |
| PostgreSQL | `shared_buffers=768MB`, `work_mem=16MB`, `max_connections=200`, bật `pg_stat_statements` và ghi log câu lệnh > 1 giây |
| Bí mật | `deploy/.env.perf` sinh ngẫu nhiên bởi `scripts/perf-up.ps1` (không đưa lên git) |

Dữ liệu nạp sẵn (`tests/load/seed/PerfSeed.cs`, dùng chính mô hình dữ liệu và mã hóa của ứng dụng):

| Loại | Số lượng |
|---|---|
| Người dùng kiểm thử (`perf001`–`perf100`) | 100 (1–3 quản trị, 4–13 phê duyệt, 14–33 chỉ xem, còn lại biên tập) + quản trị mặc định |
| Tệp | 200 007 |
| Thư mục | 1 300 |
| Chia sẻ | 2 960 |
| Dòng nhật ký (chuỗi băm) | 2 000 000 |
| Dung lượng CSDL | 1 652 MB |

Kiểm tra toàn vẹn chuỗi nhật ký 200 000 dòng: 1,3 giây, kết quả hợp lệ.

## 3. Cách chạy

```powershell
powershell -File scripts/perf-up.ps1                       # tạo .env.perf, dựng ngăn xếp, nạp dữ liệu (~2 giờ)
powershell -File tests/load/run.ps1 -Test t1               # mỗi kịch bản; kết quả ở tests/load/results/<tên>.*
py tests/load/analyze.py tests/load/results/t1.csv.gz      # bảng theo phút và các điểm cuối chậm nhất
powershell -File scripts/perf-up.ps1 -Down                 # dừng (giữ dữ liệu); thêm -Purge để xóa volume
```

`run.ps1` đặt lại `pg_stat_statements` trước mỗi lượt, lấy mẫu CPU/RAM container mỗi 10 giây và ghi 25 câu SQL tốn thời gian nhất sau lượt chạy.

## 4. Kịch bản

| Mã | Nội dung | Thời lượng |
|---|---|---|
| T1 | 100 người dùng làm việc văn phòng hỗn hợp: ~70 % đọc (duyệt thư mục, gần đây, tìm kiếm, xem chi tiết, bình luận, xem trước), ~30 % ghi (tải lên, đổi tên, sửa thông tin, di chuyển, xóa, bình luận); mỗi người dùng một phiên và quyền theo vai trò | 15 phút |
| T2 | Như T1, tăng dần 100 → 200 → 300 → 400 người dùng (3 phút mỗi bậc, giữ 400 trong 5 phút) để tìm điểm p95 vượt 2 giây | ~22 phút |
| T3 | Đầu giờ làm việc: 200 lượt đăng nhập trong 1 phút từ cùng một địa chỉ IP (cơ quan sau NAT) | 1 phút |
| T4 | Chạy bền: T1 trong 4 giờ | 4 giờ |
| T5 | Tệp lớn: 10 người tải lên tệp 200 MB–2 GB qua tus (khối 50 MB) trong khi 20 người xem video 150 MB (tua ngẫu nhiên) | ~20 phút |
| T6 | ONLYOFFICE: 25 người mở tài liệu Word cùng lúc bằng trình duyệt thật (bản Community giới hạn 20 kết nối soạn thảo) | ~5 phút |
| T7 | Màn hình quản trị trên CSDL đầy: tra cứu nhật ký, thống kê dung lượng, bảng điều khiển, kiểm tra chuỗi băm; chạy song song T1 | 10 phút |
| T8 | Sao lưu đầy đủ trong khi T1 đang chạy, sau đó đo thời gian phục hồi (RTO) | ~25 phút |
| T9 | 50 ứng dụng BlueDrive Desktop giữ kết nối SignalR; đo thời gian từ khi tệp đổi trên máy khác đến khi máy khách nhận được | ~10 phút |

## 5. Kết quả

Các kịch bản đã chạy: T1, T2, T3, T5, T6, T7, T8, T9. Chưa chạy T4 (chạy bền 4 giờ). T4 không bắt buộc cho đợt này; nên chạy một lần trên máy chủ thật trước khi nghiệm thu (khuyến nghị 6, mục 7).

### 5.1. T1 — 100 người dùng đồng thời (15 phút)

T1 chạy ba lượt: lượt 1 trên mã ban đầu, lượt 2 và 3 sau từng nhóm sửa đổi ở mục 6. Đơn vị: mili giây.

| Chỉ số | Lượt 1 (ban đầu) | Lượt 2 (tắt JIT) | Lượt 3 (giới hạn đếm + nhớ quyền) |
|---|---|---|---|
| Đọc p95 / p99 / max | 3 068 / – / **13 980** | 985 / 2 226 / **10 151** | **345 / 1 046 / 7 613** |
| Ghi p95 / max | 686 / – | 383 / – | **197 / 3 972** |
| Tìm kiếm (`POST /search`) p95 | 6 723 | 2 701 | **1 230** (p50 298) |
| Gần đây (`GET /recent`) p95 | 1 952 | 1 441 | **517** |
| Tỷ lệ lỗi HTTP | 0 % | 0 % | **0 %** (kiểm tra đạt 100 %) |
| Số yêu cầu | – | 65 585 (64/s) | 69 654 (68/s) |
| CPU container (trung bình) | PostgreSQL 170 % | PostgreSQL 159 %, ONLYOFFICE 103 %, API 66 % | PostgreSQL 126 %, ONLYOFFICE 106 %, API 72 % |
| Kết luận | KHÔNG ĐẠT (p95 > 2 s, max > 10 s) | KHÔNG ĐẠT (max > 10 s) | **ĐẠT tất cả ngưỡng** |

Lượt 3 theo từng phút: phút thứ 2 (cả 100 người dùng bắt đầu cùng lúc, mỗi người tải danh sách không gian làm việc và thư mục gốc) có p95 1 366 ms, max 7 613 ms; các phút còn lại p95 250–430 ms. Các điểm cuối khác ngoài tìm kiếm, gần đây và danh sách không gian làm việc lúc khởi động đều có p95 < 350 ms (tải lên p95 330 ms).

Câu SQL tốn thời gian nhất ở lượt 3 (`pg_stat_statements`): trang kết quả tìm kiếm trung bình 147 ms (5 663 lần; lượt 1 là 874 ms), đếm kết quả có giới hạn 67 ms (lượt 2 không giới hạn là 221 ms).

Tổng hợp CPU: cả ngăn xếp dùng khoảng 3,4 trên 8 lõi, nên độ trễ còn lại đến từ thời gian xử lý tuần tự của từng câu truy vấn chứ không phải do thiếu CPU. ONLYOFFICE chiếm khoảng một lõi vì mỗi tệp tải lên được tạo ảnh thu nhỏ qua dịch vụ chuyển đổi; đây là tải thật của hệ thống.

### 5.2. T2 — tăng dần đến 400 người dùng (sau các sửa đổi ở mục 6)

| Người dùng ảo | Yêu cầu/giây | p95 (ms) | Lâu nhất (ms) | Nhận xét |
|---|---|---|---|---|
| 100 | 64–78 | 221–294 | 1 018–1 711 | Đạt, như T1 |
| 200 | ~105 | 2 249–2 843 | 6 193–6 867 | p95 vượt nhẹ 2 s, mọi yêu cầu vẫn < 10 s |
| 300 | ~105 | 4 833–6 627 | 8 645–11 131 | Bắt đầu có yêu cầu > 10 s |
| 400 | 103–117 | 8 060–10 648 | 12 710–15 560 | Quá tải |

Tổng cả bài: 133 152 yêu cầu, lỗi HTTP 0,08 % (toàn bộ là 404: đổi tên, di chuyển, sửa thông tin một tệp mà người dùng ảo khác vừa xóa; đúng nghiệp vụ), kiểm tra đạt 99,92 %. k6 báo KHÔNG ĐẠT vì p95 và max của cả bài, đúng mục đích của bài này là tìm điểm quá tải.

Nhận xét:

- Thông lượng dừng ở khoảng 105 yêu cầu/giây từ 200 người dùng ảo, trong khi cả ngăn xếp chỉ dùng khoảng 3,5 trên 8 lõi (PostgreSQL 167 %, ONLYOFFICE 100 %, API 65 %). Điểm nghẽn là tuần tự hóa, không phải CPU: câu `pg_advisory_xact_lock` (khóa giữ thứ tự chuỗi băm nhật ký, mỗi thao tác ghi đi qua một lần) chờ trung bình 1,7 giây ở mức tải này. Các yêu cầu ghi xếp hàng sau khóa giữ kết nối CSDL, làm chậm cả tìm kiếm và "Gần đây".
- Mỗi người dùng ảo thao tác liên tục (khoảng 2 yêu cầu mỗi 5 giây, không có thời gian đọc nội dung), nặng hơn nhiều so với một người dùng văn phòng thật. 100 người dùng ảo đã tạo 68 yêu cầu/giây.
- Kết luận: cấu hình 8 lõi / 16 GB đáp ứng mục tiêu 100 người dùng đồng thời với biên an toàn; khoảng 150–200 người dùng thao tác liên tục là ngưỡng trước khi p95 vượt 2 giây. Muốn vượt ngưỡng này cần giảm tranh chấp trên chuỗi băm nhật ký (ví dụ ghi nhật ký theo lô bởi một tiến trình nền), xem mục 7.

### 5.3. T3 — đăng nhập đầu giờ từ một địa chỉ IP

| Chỉ số | Trước khi sửa | Sau khi sửa |
|---|---|---|
| Đăng nhập thành công | 29,85 % (141 lượt bị trả 429) | **100 % (201/201)**, không có 429 |
| Thời gian đăng nhập p95 / max | – | 134 / 379 ms |
| Kết luận | KHÔNG ĐẠT | **ĐẠT** |

Nguyên nhân ban đầu: đăng nhập, quên mật khẩu và đặt lại mật khẩu dùng chung một giới hạn tần suất 60 lượt/phút cho mỗi địa chỉ IP, trong khi cả cơ quan đi ra Internet qua một địa chỉ NAT. Cách sửa ở mục 6.4.

### 5.4. T5 — tệp lớn và phát video

10 người tải lên tệp video 200 MB–2 GB (2 tệp mỗi người, khối 50 MB qua tus) trong khi 20 người khác xem một video 150 MB được chia sẻ cho họ. Mỗi lượt xem lấy 1 MB đầu rồi tua ngẫu nhiên 4 lần.

| Chỉ số | Lượt 1 (ban đầu) | Lượt 2 (sau sửa đổi 6.5) | Đối chứng: không qua giả lập mạng |
|---|---|---|---|
| Tải lên | 21 tệp, 22 GB, 0 % lỗi | 21 tệp, 24 GB, 0 % lỗi | 11 tệp, 12,5 GB, 0 % lỗi |
| Tốc độ tải lên mỗi người (trung bình) | 39,7 Mbit/s | 33,0 Mbit/s | 346 Mbit/s |
| Thời gian tải một tệp, trung bình / lâu nhất | 246 / 498 s | 302 / 513 s | 29 / 44 s |
| Video 150 MB quét xong virus | **Không xong sau hơn 20 phút** | **8 giây** | – |
| Người xem nhận được video | 0 % (mọi lượt bị 423 "đang quét") | 90 % lượt (80 lượt 423 trong 8 giây đầu) | 96 % lượt (20 lượt 423 đầu bài) |
| Bắt đầu phát p50 / p95 | – | 122 / 5 202 ms | 7 / 62 ms |
| Tua p50 / p95 | – | 120 / 6 016 ms | 8 / 37 ms |
| Kết luận | KHÔNG ĐẠT | KHÔNG ĐẠT ngưỡng p95 video (xem nhận xét) | **ĐẠT** |

Nhận xét:

- **Lượt 1 phát hiện lỗi thật của sản phẩm.** Quét virus và tạo ảnh xem trước chạy trong cùng một job, cùng hàng đợi `media` với 2 worker. Mỗi job mất khoảng 6 giây, chủ yếu do chuyển đổi tài liệu qua ONLYOFFICE để làm ảnh thu nhỏ. Sau T1/T2, hàng đợi còn 8 534 job, nên tệp mới phải chờ hàng giờ mới được quét. Trong thời gian đó người khác không tải được tệp (cơ chế chặn tải tệp chưa quét, mã 423). Cách sửa ở mục 6.5. Lượt 2 vẫn giữ nguyên 8 534 job tồn đọng ấy mà video vẫn quét xong sau 8 giây.
- Tệp trên 200 MB (`CLAMAV_MAX_SCAN_MB`) được đánh dấu "Bỏ qua quét" ngay (trung bình 1,1 giây sau khi tải xong) và tải về kèm cảnh báo, đúng thiết kế.
- Lượt 2: video chỉ chậm trong 12 phút đầu, khi các tệp lớn đang được tải lên (trung bình 4–6 giây mỗi yêu cầu 1 MB). Sau đó, cùng 20 người xem, thời gian giảm còn 112 ms. Nguyên nhân là giả lập mạng: `tc netem` được áp lên giao diện duy nhất của dịch vụ `edge`, nên chiều tải lên (edge → máy chủ) và chiều xem video (edge → người dùng) cùng xếp một hàng đợi 300 Mbit/s. 10 luồng tải lên chiếm gần hết hàng đợi đó. Đường truyền thật là song công (chiều lên và chiều xuống có băng thông riêng), nên không có hiện tượng này.
- Lượt đối chứng gọi thẳng vào ngăn xếp: máy chủ nhận khoảng 3,4 Gbit/s tải lên tổng cộng mà vẫn phục vụ video trung bình 203 ms trong cùng phút đó, các phút sau 7 ms. API dùng tối đa 1,6 lõi. Máy chủ không phải điểm nghẽn; giới hạn thực tế là băng thông đường truyền của Trung tâm (xem khuyến nghị 4, mục 7).

### 5.5. T6 — 25 người mở tài liệu Word cùng lúc

25 người dùng biên tập, mỗi người dùng một trình duyệt Chromium thật, mở một tệp `.docx` riêng trong trình soạn thảo, giữ phiên 120 giây (ảnh chụp từng phiên ở `tests/load/results/t6-onlyoffice/`). Trước bài này đã xóa 7 855 job ảnh xem trước còn tồn trong hàng đợi `media` từ T1/T2, để ONLYOFFICE chỉ phục vụ phiên soạn thảo và số đo không bị lẫn tải nền.

| Chỉ số | Kết quả |
|---|---|
| Phiên 1–20 | Mở và soạn thảo bình thường; thời gian từ lúc bấm mở đến khi trình soạn thảo sẵn sàng: trung vị 1 948 ms, lâu nhất 2 366 ms |
| Phiên 21–25 | Trình soạn thảo vẫn tải nhưng thanh công cụ bị khóa. ONLYOFFICE hiện hộp thoại "Bạn đang sử dụng phiên bản nguồn mở… giới hạn 20 kết nối cùng một lúc" kèm nút liên hệ mua giấy phép |
| CPU | ONLYOFFICE tối đa 36 %, API tối đa 7 % |

Nhận xét:

- Hiệu năng đạt: 20 phiên soạn thảo đồng thời chỉ dùng chưa đến nửa lõi CPU, và mở tài liệu dưới 2,5 giây.
- Giới hạn là **giấy phép**, không phải tài nguyên. ONLYOFFICE Docs Community Edition (AGPL, miễn phí) cho phép tối đa 20 kết nối soạn thảo cùng lúc trên toàn máy chủ. Từ người thứ 21, người dùng chỉ xem được và thấy thông báo của nhà sản xuất. HSMT không nêu số người soạn thảo đồng thời. Nếu Trung tâm dự kiến hơn 20 người mở tài liệu Office cùng lúc thì cần mua giấy phép ONLYOFFICE Docs Enterprise; giấy phép được tính theo số kết nối đồng thời, BlueDrive không phải sửa gì.

### 5.6. T7 — màn hình quản trị trên CSDL đầy, song song T1

3 quản trị viên dùng liên tục các màn hình Nhật ký (lọc theo thao tác, khoảng ngày, từ khóa, người dùng, tiền tố IP; trang 1 rồi trang 2), Thống kê dung lượng, Bảng điều khiển, Cảnh báo, và chạy 3 lần kiểm tra toàn vẹn chuỗi băm (200 000 bản ghi mỗi lần). Dữ liệu: 2 084 003 bản ghi nhật ký, khoảng 211 000 tệp. Cùng lúc 100 người dùng chạy T1. Đơn vị: mili giây.

| Chỉ số | Lượt 1 (ban đầu) | Lượt 2 (chỉ mục từ khóa + giới hạn đếm) | Lượt 3 (thêm chỉ mục IP) |
|---|---|---|---|
| Màn hình quản trị: đọc p95 / max | 6 403 / **11 670** | 1 232 / **19 445** | **398 / 2 663** |
| Tra cứu nhật ký (`POST /audit-logs/search`) p95 / max | 7 017 / 11 670 | 1 914 / 19 445 | **375 / 2 663** |
| Kiểm tra chuỗi băm 200 000 bản ghi, trung bình / max | 4 285 / 10 127 | 4 815 / 11 619 | **1 550 / 1 964** |
| Thống kê dung lượng p95 | 518 | 766 | 682 |
| Bảng điều khiển quản trị p95 | 563 | 635 | 622 |
| T1 chạy song song: đọc p95 / max, lỗi | 821 / 5 491, 0 % | 755 / 13 512, 0 % | **481 / 4 294, 0 %** |
| Kết luận | KHÔNG ĐẠT | KHÔNG ĐẠT | **ĐẠT** |

Nhận xét:

- **Lượt 1 phát hiện lỗi thật của sản phẩm.** Lọc nhật ký theo từ khóa chạy `f_unaccent(resource_name) LIKE '%…%'` trên 2 triệu dòng mà không có chỉ mục, lại kèm một câu `COUNT(*)` trên toàn bộ kết quả: câu đếm trung bình 6,9 giây, lâu nhất 10,1 giây. Cách sửa ở mục 6.6.
- Lượt 2: lọc theo từ khóa còn tối đa 407 ms, nhưng lộ ra trường hợp thứ hai: lọc theo tiền tố IP không khớp dòng nào (`172.`) phải quét hết bảng, lâu nhất 19 giây. Lượt 3 thêm chỉ mục cho cột IP; đo riêng mọi tiền tố (khớp nhiều, khớp ít, không khớp) đều dưới 30 ms.
- Kiểm tra chuỗi băm đọc lần lượt 200 000 bản ghi từ đầu bảng. Ở lượt 1 và 2 nó chạy ngay sau khi khởi động lại container (bộ đệm CSDL còn trống) nên mất đến 10–11 giây; ở lượt 3 bộ đệm đã ấm, còn dưới 2 giây. Đây là thao tác quản trị viên chủ động bấm, không thuộc ngưỡng 2 giây của màn hình đọc, và không làm chậm người dùng khác.
- Các màn hình quản trị không ảnh hưởng đáng kể đến người dùng thường: T1 chạy song song vẫn đạt mọi ngưỡng.

### 5.7. T8 — sao lưu đầy đủ khi có tải, đo thời gian phục hồi (RTO)

100 người dùng chạy T1 trong 20 phút; sau 4 phút quản trị viên bấm sao lưu đầy đủ (gồm CSDL và toàn bộ tệp số hóa, HSMT mục 3.5). Sau đó dừng API/Worker, phục hồi đúng bản vừa sao lưu bằng lệnh `worker restore <mã>` và khởi động lại.

| Chỉ số | Kết quả |
|---|---|
| Dữ liệu sao lưu | 66,07 GB (CSDL chiếm khoảng 1,8 GB trên đĩa, còn lại là khoảng 64 GB tệp, gồm cả các video lớn của T5) |
| Thời gian sao lưu đầy đủ khi có tải | **384 giây** (khoảng 172 MB/s), thành công |
| 100 người dùng trong lúc sao lưu (phút 4–10) | p95 từng phút 1 125–1 989 ms, lâu nhất 6 127 ms |
| 100 người dùng cả bài (20 phút) | Đọc p95 774 ms, ghi p95 929 ms, lâu nhất 8 013 ms, lỗi 0 %, 66 yêu cầu/giây: **đạt mọi ngưỡng** |
| Phục hồi: kiểm tra checksum + CSDL | 77 giây |
| Phục hồi: giải nén 64 GB tệp (25 608 tệp) | 254 giây (khoảng 250 MB/s) |
| **RTO** (từ lúc dừng dịch vụ đến khi đăng nhập lại được) | **395 giây (6 phút 35 giây)** |
| Toàn vẹn sau phục hồi | 17 065/17 065 khóa lưu trữ trong CSDL đều có tệp trên đĩa; kiểm tra toàn bộ chuỗi băm nhật ký (2 097 381 bản ghi, 11 lần × 200 000 qua `GET /audit-logs/verify?fromId=…`, tổng 11,6 giây): hợp lệ |

Nhận xét:

- Sao lưu làm p95 tăng từ khoảng 0,3–0,6 giây lên 1,1–2,0 giây trong 6 phút sao lưu (đọc đĩa và nén song song với PostgreSQL), nhưng không có lỗi và không yêu cầu nào vượt 10 giây. Lịch sao lưu tự động mặc định chạy lúc 2 giờ sáng hằng ngày, ngoài giờ làm việc; nên giữ như vậy.
- Sau phục hồi CSDL có 217 065 tệp, trước khi phục hồi là 219 702: 2 637 tệp chênh lệch là các tệp T1 tải lên sau thời điểm chụp CSDL. Đây là hành vi đúng: dữ liệu mất tối đa (RPO) bằng khoảng thời gian từ lần sao lưu gần nhất, nên tần suất sao lưu do quản trị viên đặt quyết định RPO.
- RTO tăng gần tuyến tính theo dung lượng tệp (khoảng 4 phút cho mỗi 64 GB trên SSD). Với kho 1 TB, ước tính khoảng 70 phút trên đĩa cùng tốc độ; đĩa máy chủ thật chậm hơn thì lâu hơn. Phục hồi từng phần (một tệp, một thư mục) không cần dừng hệ thống: dùng thùng rác và lịch sử phiên bản.
- Hàng đợi công việc nền (Hangfire) nằm cùng CSDL nên được phục hồi theo: các job còn chờ ở thời điểm sao lưu (khoảng 4 600 job tạo ảnh xem trước do các lượt T1 chạy kèm T7, T8 tạo ra) chạy tiếp sau khi khởi động lại. Không job nào mất, nhưng ONLYOFFICE bận chuyển đổi tài liệu một thời gian sau phục hồi.

### 5.8. T9 — 50 máy BlueDrive Desktop đồng bộ cùng lúc

50 người dùng biên tập, mỗi người một máy khách giả lập đúng cách ứng dụng Desktop làm: lấy danh sách gốc đồng bộ, tải ảnh chụp thư mục cá nhân (lấy con trỏ), mở kết nối SignalR `/hubs/sync` và giữ trong 10 phút. Khoảng 30–40 giây một lần, chính người đó đổi tên một tệp của mình từ "máy khác" (qua API web). Đo từ lúc đổi tên đến khi máy khách nhận sự kiện `changes_available`, và đến khi thay đổi xuất hiện trong `GET /sync/changes`. Toàn bộ đi qua giả lập mạng. Ngưỡng theo yêu cầu: thay đổi đến máy khác trong 5 giây.

| Chỉ số | Kết quả |
|---|---|
| Kết nối SignalR | 50/50 mở thành công (thiết lập p95 93 ms), giữ đủ 10 phút |
| Số lần đổi tên | 842, không lỗi |
| Đổi tên → nhận sự kiện: trung bình / p95 / lâu nhất | 522 / 963 / 1 032 ms |
| Đổi tên → thấy trong `/sync/changes`: trung bình / p95 / lâu nhất | 764 / 1 214 / 1 480 ms |
| Sự kiện bị lỡ | 0 |
| Yêu cầu đọc p95 / lỗi | 458 ms / 0 % (`/sync/changes` p95 371 ms, ảnh chụp thư mục p95 1 389 ms) |
| CPU | API trung bình 16 %, tối đa 79 %; PostgreSQL trung bình 25 % |
| Kết luận | **ĐẠT** (p95 nhỏ hơn 1/4 ngưỡng 5 giây) |

Nhận xét:

- Độ trễ chủ yếu nằm ở hai chặng: ghi thay đổi rồi phát sự kiện qua SignalR (khoảng 0,5 giây, gồm độ trễ giả lập mạng), và một vòng `GET /sync/changes` (khoảng 0,25 giây). Không có máy khách nào phải chờ đến lượt kiểm tra định kỳ dự phòng.
- Bài chạy ngay sau phục hồi T8, khi hàng đợi `media` còn khoảng 4 600 job ảnh xem trước (ONLYOFFICE dùng trung bình 1,1 lõi). Đồng bộ vẫn đạt, vì job nền chạy trong Worker, tách khỏi API.

## 6. Thay đổi đã thực hiện sau kiểm thử

### 6.1. Tắt JIT của PostgreSQL

Với từ khóa phổ biến (ví dụ "dân số" có trong khoảng 40 % tệp), ước lượng chi phí câu tìm kiếm vượt `jit_above_cost`. PostgreSQL biên dịch JIT cho mọi lần chạy, mất khoảng 350 ms trong một câu khoảng 550 ms. Migration `DisableJit` đặt `ALTER DATABASE … SET jit = off`, nên câu tìm kiếm giảm còn khoảng 140–220 ms. Thiết lập này áp dụng cho kết nối mới và đi theo cơ sở dữ liệu khi sao lưu/phục hồi bằng migration.

### 6.2. Giới hạn đếm tổng số kết quả tìm kiếm

Trước đây mỗi lần tìm kiếm chạy thêm một câu `COUNT(*)` trên toàn bộ tập kết quả; với từ khóa phổ biến câu này tốn ngang câu lấy trang. Nay:

- Trang chưa đầy là trang cuối, nên tổng số được suy ra từ trang đó, không cần đếm lại.
- Nếu không, kết quả chỉ được đếm tối đa 1 000 (hoặc một trang sau trang hiện tại nếu người dùng đã đi xa hơn). Vượt quá thì API trả `totalCapped = true` và giao diện web, ứng dụng di động hiển thị "Tìm thấy hơn 1.000 kết quả".
- Người dùng vẫn chuyển được đến mọi trang: tổng số tăng dần theo trang đang xem cho đến khi đếm đủ.

HSMT không yêu cầu hiển thị số kết quả chính xác cho các chức năng tìm kiếm #20, #32, #50, #75. Kiểm thử tích hợp `SearchTests.Broad_terms_are_counted_up_to_the_limit_and_reported_as_more` dùng 1 050 tệp để kiểm tra cả hai trường hợp.

### 6.3. Nhớ tập chủ thể quyền trong một yêu cầu

`ResourceAuthorization` tính tập chủ thể của người dùng (người dùng, nhóm, đơn vị, vai trò) cho mỗi tài nguyên cần kiểm tra quyền. Một trang "Gần đây" hoặc một trang kết quả tìm kiếm 20 mục vì vậy chạy cùng một truy vấn 20 lần. Nay tập này được tính một lần trong mỗi yêu cầu HTTP (dịch vụ có phạm vi scoped), nên thay đổi quyền vẫn có hiệu lực ngay ở yêu cầu kế tiếp.

### 6.4. Đăng nhập sau NAT: chặn theo tài khoản, chặn IP khi dò mật khẩu

- `POST /auth/login` tách khỏi giới hạn chung, có giới hạn riêng 300 lượt/phút mỗi IP (`RateLimit:LoginPerMinute`). Mức này đủ cho đầu giờ làm việc của cả cơ quan mà vẫn chặn được công cụ dò hàng loạt. Quên/đặt lại mật khẩu giữ 60 lượt/phút.
- Khóa tài khoản sau nhiều lần sai mật khẩu vẫn giữ nguyên (theo tài khoản).
- Bổ sung `LoginIpGuard`: một IP sai mật khẩu `login.ip_max_failures` lần (mặc định 30) trong `login.ip_block_minutes` phút (mặc định 15) thì bị chặn đăng nhập thêm `login.ip_block_minutes` phút kể từ lần sai cuối. Đăng nhập đúng không bị tính, nên người dùng hợp lệ chung NAT không bị ảnh hưởng. Hai tham số chỉnh trong Quản trị → Cấu hình; mỗi lần chặn được ghi nhật ký. Kiểm thử: `LoginIpGuardTests`, `AuthTests.Many_failed_logins_from_one_address_block_that_address_only`.

### 6.5. Tách quét virus khỏi tạo ảnh xem trước

Quy trình sau tải lên được tách làm hai job:

- `FileProcessingJob` (hàng đợi `scan`) chỉ quét virus. Hàng đợi này có máy chủ Hangfire riêng với 2 worker (`Worker:ScanConcurrency`), nên không bao giờ phải chờ sau các job tạo ảnh xem trước. Tệp nhiễm virus bị cách ly và dừng ở đây.
- Quét xong mới đưa `FileDerivativesJob` vào hàng đợi `media`. Job này tạo siêu dữ liệu, ảnh thu nhỏ, ảnh xem trước, dùng các worker chung (`Worker:Concurrency`).

Job cũ còn trong hàng đợi `media` lúc nâng cấp vẫn chạy được: chúng quét rồi đưa phần ảnh xem trước vào hàng đợi mới. Kiểm thử tích hợp `CollaborationTests.Scan_stage_settles_the_file_before_and_without_the_thumbnail_stage` kiểm tra hai điều. Thứ nhất, sau giai đoạn quét, tệp đã có kết quả quét và tải về được dù chưa có ảnh thu nhỏ. Thứ hai, giai đoạn sau mới tạo ảnh thu nhỏ.

### 6.6. Chỉ mục và giới hạn đếm cho tra cứu nhật ký

- Migration `AuditLogTrigramIndexes` tạo ba chỉ mục GIN trigram trên `audit_logs`: tên tài nguyên không dấu (`f_unaccent(resource_name)`), tên đăng nhập và địa chỉ IP. Dùng GIN thay vì B-tree cho tiền tố IP vì tiền tố được truyền dưới dạng tham số `LIKE`, B-tree chỉ dùng được khi PostgreSQL lập kế hoạch riêng cho từng giá trị. Trên CSDL 2 triệu dòng, tạo cả ba chỉ mục mất khoảng 25 giây (một lần khi nâng cấp).
- Tra cứu nhật ký dùng chung cơ chế giới hạn đếm của tìm kiếm tệp (mục 6.2, nay là `PagingExtensions.CappedTotalAsync`): đếm tối đa 1 000 bản ghi, vượt quá thì API trả `totalCapped = true` và màn hình Nhật ký hiển thị "Hơn 1.000 bản ghi". Xuất báo cáo nhật ký ra Excel không đổi: vẫn lấy đến 200 000 bản ghi mới nhất khớp bộ lọc như trước.
- Kiểm thử tích hợp `MonitoringTests.Long_lists_count_only_up_to_the_limit` kiểm tra trang đầu (bị giới hạn), trang sâu (đếm tới trang kế tiếp), trang cuối thiếu (không đếm lại) và API tra cứu nhật ký.

## 7. Giới hạn của môi trường giả lập và khuyến nghị

Giới hạn của phép đo:

- Băng thông 300 Mbit/s được áp chung cho chiều ra của dịch vụ `edge`, không tách theo từng người dùng. `edge` chỉ có một giao diện mạng, nên lưu lượng tải lên và tải xuống dùng chung một hàng đợi (bán song công), khắt khe hơn đường truyền thật (xem T5).
- Docker Desktop trên WSL2 không hỗ trợ giới hạn tốc độ đĩa (blkio), nên đĩa SSD của máy phát triển nhanh hơn hoặc bằng máy chủ thật.
- Máy phát triển cùng lúc chạy các container của dự án khác (ít tải trong lúc đo).
- ONLYOFFICE tạo ảnh thu nhỏ cho mỗi tệp tải lên, chiếm khoảng một lõi trong T1/T2; đây là tải thật nhưng phụ thuộc tỷ lệ tệp văn phòng trong dữ liệu tải lên.

Khuyến nghị cho vận hành:

1. Giữ cấu hình tối thiểu 8 lõi / 16 GB của HSMT cho khoảng 100 người dùng đồng thời; theo dõi chỉ số "Phiên đang hoạt động (30 phút)" trên bảng điều khiển quản trị.
2. Khi số người dùng làm việc đồng thời vượt khoảng 150, cần giảm tranh chấp trên chuỗi băm nhật ký (ghi nhật ký theo lô bằng một tiến trình nền thay vì khóa trong từng giao dịch), sau đó đo lại T2.
3. Các tối ưu khác đã xác định nhưng chưa cần ở mức 100 người dùng: gom kiểm tra quyền theo lô cho "Gần đây" và trang kết quả tìm kiếm; giảm chi phí tải danh sách không gian làm việc lúc đăng nhập (p95 khoảng 1,9 giây khi 100 người dùng đăng nhập cùng một lúc).
4. Băng thông: mỗi người tải lên video lớn dùng được khoảng 30–40 Mbit/s qua đường 300 Mbit/s. Nếu đường truyền của Trung tâm hẹp hơn hoặc dùng chung với dịch vụ khác, nên hướng dẫn tải tệp rất lớn ngoài giờ cao điểm hoặc đặt giới hạn băng thông ở reverse proxy.
5. Theo dõi số job đang chờ trên trang Quản trị → Trạng thái hệ thống. Quét virus có hàng đợi và worker riêng nên không bị ảnh hưởng; nếu số job chờ (chủ yếu tạo ảnh xem trước) tăng kéo dài thì tăng `Worker:Concurrency` hoặc CPU cho ONLYOFFICE; việc này chỉ làm ảnh xem trước xuất hiện chậm hơn, không chặn người dùng tải tệp.
6. Trước nghiệm thu, chạy T4 (T1 liên tục 4 giờ) một lần trên máy chủ thật ngoài giờ làm việc, để kiểm tra rò rỉ bộ nhớ và độ trễ không tăng dần theo thời gian. Đợt này chưa chạy T4.
