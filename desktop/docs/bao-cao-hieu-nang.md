# Báo cáo hiệu năng BlueDrive Desktop

Đo theo mục 10 của đặc tả (Sync-Phase 9), ngày 03/11/2026, bằng công cụ `desktop/tools/BlueDrive.Sync.Perf` (`bluedrive-perf`).
Số liệu gốc: [`perf/ket-qua.json`](perf/ket-qua.json).

**Máy đo:** AMD Ryzen 7 7435HS (16 luồng), RAM 32 GB, SSD NVMe, Windows 11 (build 26300), .NET 10.
**Máy chủ:** stack Docker đầy đủ trên cùng máy (`deploy/docker-compose.yml`: API, PostgreSQL 16, nginx, Caddy, ONLYOFFICE).

## 1. Kết quả

| Chỉ số (mục 10) | Mục tiêu | Đo được | Kết quả |
|---|---|---|---|
| Tạo tệp giữ chỗ lần đầu, 100 000 tệp / 5 000 thư mục | < 5 phút | **210 giây** (105 000 mục) | Đạt |
| Quét khởi động khi không có thay đổi (100 000 tệp) | < 30 giây | **4,5 giây** | Đạt |
| RAM nền sau khi đồng bộ 100 000 tệp | < 200 MB | **105 MB** (tác tử trên máy chủ thật: 118 MB) | Đạt |
| CPU nền khi rảnh | gần 0% | **0%** một lõi (máy chủ thật, 60 giây: 0,16%) | Đạt |
| Thay đổi trên web xuất hiện trên máy (SignalR) | < 5 giây | trung vị **0,73 giây**, p95 0,85 giây (10 lượt) | Đạt |
| Lưu tệp Office (2 MB) trên máy → có trên máy chủ | < 10 giây | trung vị **3,25 giây**, p95 3,32 giây (10 lượt) | Đạt |
| Tải lên / tải xuống 5 GB qua mạng chập chờn: tiếp tục đúng, mã băm khớp | — | Kiểm thử tự động (mục 3) | Đạt |
| Dừng tiến trình bất kỳ lúc nào: không mất dữ liệu, không trùng tệp | — | Kiểm thử tự động (mục 3) | Đạt |

"Lưu trên máy → máy chủ" gồm 3 giây chờ thư mục yên tĩnh (debounce, tránh tải lên tệp đang ghi dở — đặc tả 6.1) và thời gian tải lên.

## 2. Cách đo

```powershell
dotnet build desktop/tools/BlueDrive.Sync.Perf -c Release
$perf = 'desktop/tools/BlueDrive.Sync.Perf/bin/Release/net10.0-windows10.0.19041.0/bluedrive-perf.dll'
# Quy mô: máy chủ giả lập trong bộ nhớ (không tính mạng), Cloud Files API thật, SQLite thật, thư mục dưới %USERPROFILE%.
dotnet $perf scale --files 100000 --folders 5000 --out desktop/docs/perf/ket-qua.json
# Độ trễ: máy chủ thật, đăng nhập thiết bị (PKCE) như ứng dụng, tác tử đồng bộ đầy đủ với SignalR.
dotnet $perf latency --server http://localhost:8088 --user admin --password '<mật khẩu>' --rounds 10 --out desktop/docs/perf/ket-qua.json
```

- **scale**: cây 50 thư mục cấp 1 × 99 thư mục con, 20 tệp `.docx` mỗi thư mục. Lần đồng bộ đầu tạo toàn bộ tệp giữ chỗ (Files On-Demand);
  lần thứ hai không có thay đổi (quét thư mục + đối chiếu cơ sở dữ liệu trạng thái). RAM đo sau khi đồng bộ (working set), CPU trung bình 30 giây
  khi rảnh. Công cụ tự gỡ sync root và xóa thư mục thử khi xong.
- **latency**: mỗi lượt tạo một thư mục trên web (API) và chờ nó xuất hiện trong thư mục đồng bộ; rồi ghi một tệp Word 2 MB vào thư mục
  đồng bộ và chờ tệp có trên máy chủ (API). Công cụ xóa các mục thử khi xong.

## 3. Độ tin cậy (kiểm thử tự động, chạy trong `scripts/ci.ps1`)

| Kịch bản | Kiểm thử |
|---|---|
| Tệp 5 GB: tải lên chụp nội dung ra đĩa (không nạp RAM), tải xuống ghi thẳng tệp tạm, sai mã băm thì giữ tệp cũ | `LargeFileTests` (Sync.Tests) |
| Mất mạng giữa lúc tải lên → tiếp tục phiên tus sau khi khởi động lại; tải xuống tiếp bằng HTTP Range | IT `DesktopClientTests.An_interrupted_upload_continues_after_a_restart_without_sending_the_start_again`, `Download_resumes_with_range_after_a_dropped_connection` |
| Dừng tác tử ngẫu nhiên giữa lượt, khởi động lại: đủ tệp, không trùng, không bản xung đột thừa | IT `SyncAgentTests.Killing_the_agent_in_the_middle_loses_nothing_and_duplicates_nothing`; mô phỏng FsCheck 10 000 chuỗi có crash ngẫu nhiên (`SimulationTests`) |

## 4. Tối ưu đã làm để đạt mục tiêu

| Trước | Sau | Thay đổi |
|---|---|---|
| Quét 10 500 mục: 3,7 giây (≈ 37 giây cho 105 000) | 0,6 giây (4,5 giây cho 105 000) | Quét thư mục bằng `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)`: một lần gọi cho mỗi thư mục trả về mã tệp NTFS, kích thước, ngày sửa, thuộc tính — thay vì mở một handle cho mỗi tệp (ổ không hỗ trợ: quay về cách cũ). |
| Lần đầu 10 500 mục: 32 giây (≈ 5 phút 20 giây cho 105 000) | 210 giây cho 105 000 | `Stat` một handle (mã tệp + thuộc tính + loại reparse); kiểm tra tồn tại chỉ đọc thuộc tính. (Bỏ qua bước đánh dấu lại "đã đồng bộ" tệp giữ chỗ vừa tạo nhanh hơn — 150 giây — nhưng làm "Giải phóng dung lượng" thỉnh thoảng lỗi khi thư mục cha chưa là tệp giữ chỗ, nên giữ bước này.) |
| RAM sau lần đầu: 300 MB | 105 MB | Sau lượt đồng bộ lớn (≥ 2 000 thao tác) tác tử trả bộ nhớ trống của GC cho Windows; nhật ký kế hoạch của lượt (chẩn đoán) giới hạn 2 000 dòng. |
