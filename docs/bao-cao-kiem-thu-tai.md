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

Các kịch bản đã chạy: T1, T2, T3. Kết quả T4–T9 sẽ được bổ sung vào mục này sau khi chạy.

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

## 7. Giới hạn của môi trường giả lập và khuyến nghị

Giới hạn của phép đo:

- Băng thông 300 Mbit/s được áp chung cho chiều ra của dịch vụ `edge`, không tách theo từng người dùng.
- Docker Desktop trên WSL2 không hỗ trợ giới hạn tốc độ đĩa (blkio), nên đĩa SSD của máy phát triển nhanh hơn hoặc bằng máy chủ thật.
- Máy phát triển cùng lúc chạy các container của dự án khác (ít tải trong lúc đo).
- ONLYOFFICE tạo ảnh thu nhỏ cho mỗi tệp tải lên, chiếm khoảng một lõi trong T1/T2; đây là tải thật nhưng phụ thuộc tỷ lệ tệp văn phòng trong dữ liệu tải lên.

Khuyến nghị cho vận hành:

1. Giữ cấu hình tối thiểu 8 lõi / 16 GB của HSMT cho khoảng 100 người dùng đồng thời; theo dõi chỉ số "Phiên đang hoạt động (30 phút)" trên bảng điều khiển quản trị.
2. Khi số người dùng làm việc đồng thời vượt khoảng 150, cần giảm tranh chấp trên chuỗi băm nhật ký (ghi nhật ký theo lô bằng một tiến trình nền thay vì khóa trong từng giao dịch), sau đó đo lại T2.
3. Các tối ưu khác đã xác định nhưng chưa cần ở mức 100 người dùng: gom kiểm tra quyền theo lô cho "Gần đây" và trang kết quả tìm kiếm; giảm chi phí tải danh sách không gian làm việc lúc đăng nhập (p95 khoảng 1,9 giây khi 100 người dùng đăng nhập cùng một lúc).
