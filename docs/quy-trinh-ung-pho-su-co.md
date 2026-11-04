# Quy trình quản lý lỗ hổng và ứng phó sự cố an toàn thông tin BlueDrive

Đáp ứng yêu cầu HSMT mục 3.4 "Quản lý lỗ hổng và sự cố an toàn thông tin". Tài liệu dùng cho đơn vị vận hành (Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số) và nhà thầu hỗ trợ kỹ thuật.

Căn cứ áp dụng:
- Luật An toàn thông tin mạng 2015.
- Quyết định 05/2017/QĐ-TTg về hệ thống phương án ứng cứu khẩn cấp bảo đảm an toàn thông tin mạng quốc gia.
- Thông tư 20/2017/TT-BTTTT quy định về điều phối, ứng cứu sự cố an toàn thông tin mạng trên toàn quốc.
- Nghị định 85/2016/NĐ-CP về bảo đảm an toàn hệ thống thông tin theo cấp độ.

Từ năm 2025, chức năng điều phối ứng cứu sự cố quốc gia (trước đây là VNCERT/CC) do Cục An toàn thông tin thực hiện. Khi áp dụng, đơn vị vận hành kiểm tra lại văn bản và đầu mối tiếp nhận hiện hành.

## 1. Vai trò

| Vai trò | Trách nhiệm | Quyền trong BlueDrive |
|---|---|---|
| Lãnh đạo đơn vị (chủ quản hệ thống) | Phê duyệt phương án xử lý sự cố mức Cao/Nghiêm trọng, ký báo cáo gửi cơ quan cấp trên và cơ quan điều phối | `incident.view` |
| Cán bộ phụ trách an toàn thông tin | Tiếp nhận, phân loại, điều phối xử lý, giao người xử lý, kết luận và đóng hồ sơ | `incident.view`, `incident.upload`, `incident.edit`, `incident.admin` |
| Quản trị hệ thống | Theo dõi cảnh báo, ngăn chặn, khắc phục, khôi phục, cập nhật tiến độ vào hồ sơ | `incident.view`, `incident.upload`, `incident.edit` |
| Nhà thầu hỗ trợ kỹ thuật | Phân tích kỹ thuật, vá lỗi, phát hành bản cập nhật, phối hợp theo yêu cầu | Theo vai trò được cấp |
| Người dùng | Báo ngay khi thấy dấu hiệu bất thường (qua Hỗ trợ người dùng hoặc trực tiếp cán bộ ATTT) | — |

Quyền được gán ở **Quản trị → Vai trò & phân quyền** (dòng "Sự cố an toàn thông tin"). Chỉ người có `incident.admin` mới giao người xử lý, đóng hồ sơ và sửa hồ sơ đã đóng.

## 2. Phân loại và mức độ

Loại sự cố: mã độc; xâm nhập trái phép; khai thác lỗ hổng; lộ, lọt dữ liệu; tấn công từ chối dịch vụ; chiếm đoạt tài khoản; khác.

| Mức độ | Dấu hiệu | Thời hạn bắt đầu xử lý | Báo cáo |
|---|---|---|---|
| Nghiêm trọng | Hệ thống ngừng hoạt động, dữ liệu bị lộ ra ngoài hoặc bị mã hóa tống tiền, kẻ tấn công còn trong hệ thống | Ngay, kể cả ngoài giờ | Báo lãnh đạo ngay; báo cáo ban đầu tới cơ quan điều phối (mục 5) |
| Cao | Một chức năng chính không dùng được; tài khoản quản trị bị lộ; mã độc lây trên nhiều tệp | Trong 4 giờ | Báo lãnh đạo trong ngày; báo cáo cơ quan điều phối nếu thuộc diện phải báo cáo |
| Trung bình | Ảnh hưởng một nhóm người dùng; tài khoản thường bị dò mật khẩu hoặc bị chiếm | Trong 1 ngày làm việc | Ghi hồ sơ, tổng hợp trong báo cáo định kỳ |
| Thấp | Cảnh báo đơn lẻ, đã tự chặn (ví dụ một tệp nhiễm đã bị cách ly) | Trong 3 ngày làm việc | Ghi hồ sơ |

## 3. Quy trình xử lý

Mỗi bước tương ứng một trạng thái của hồ sơ trên màn hình **Quản trị → Sự cố an toàn thông tin** (`/admin/incidents`). Mọi thay đổi được ghi vào dòng thời gian của hồ sơ và nhật ký hệ thống.

1. **Phát hiện.** Nguồn phát hiện:
   - Cảnh báo tự động ở **Quản trị → Cảnh báo**: phát hiện mã độc, đăng nhập sai nhiều lần, tải xuống bất thường, máy quét lỗi.
   - Chuông thông báo.
   - Phản ánh của người dùng, thông báo của cơ quan chức năng, kết quả quét lỗ hổng (mục 6).
2. **Tiếp nhận — trạng thái "Mới phát hiện".** Bấm **Lập hồ sơ sự cố**:
   - Từ màn hình Cảnh báo, hồ sơ được điền sẵn tiêu đề, loại, mức độ, thời điểm phát hiện và gắn với cảnh báo gốc. Cảnh báo được đánh dấu đã xử lý, cột trạng thái hiện mã hồ sơ.
   - Khi tạo trực tiếp, nhập tiêu đề, loại, mức độ, thời điểm phát hiện, mô tả và hệ thống bị ảnh hưởng.
   - Hệ thống cấp mã `SC-yyyyMMdd-nnn`.
3. **Phân loại — "Đang điều tra".** Cán bộ ATTT xác định loại, mức độ (điều chỉnh nếu cần), phạm vi ảnh hưởng và giao người xử lý. Bằng chứng được thu thập trước khi thay đổi hệ thống:
   - Nhật ký hệ thống: **Quản trị → Nhật ký hoạt động**, kiểm tra toàn vẹn chuỗi băm.
   - Nhật ký container: `docker compose logs`.
   - Bản sao tệp nghi nhiễm, đang được cách ly.
4. **Ngăn chặn — "Đang ngăn chặn".** Tùy loại sự cố:
   - **Tài khoản:** khóa tài khoản (**Quản trị → Người dùng**), thu hồi phiên đăng nhập (**Phiên đăng nhập**), thu hồi thiết bị đồng bộ (**Thiết bị đồng bộ**).
   - **Lộ dữ liệu:** thu hồi liên kết chia sẻ công khai liên quan.
   - **Mã độc:** tệp nhiễm đã tự động bị chặn tải ở mọi đường. Kiểm tra người đã tải lên và các tệp khác của người đó.
   - **Xâm nhập hoặc tấn công:** chặn địa chỉ nguồn ở tường lửa hoặc reverse proxy. Nếu cần, đặt hệ thống ở chế độ bảo trì và đăng **Thông báo hệ thống** mức "Bảo trì" cho người dùng.
   - **Lộ khóa bí mật:** đổi `JWT_SECRET`, `ONLYOFFICE_JWT_SECRET` hoặc mật khẩu CSDL trong `deploy/.env`, rồi khởi động lại (`docker compose up -d`).
5. **Khắc phục — "Đang khắc phục".** Loại bỏ nguyên nhân:
   - Vá lỗ hổng, nâng cấp gói (mục 6).
   - Xóa mã độc.
   - Đặt lại mật khẩu.
   - Rà soát quyền chia sẻ.
6. **Khôi phục — vẫn "Đang khắc phục".** Khi dữ liệu bị hỏng hoặc mất, phục hồi từ bản sao lưu gần nhất trước thời điểm sự cố (`scripts/restore.ps1`, xem `huong-dan-quan-tri.md` mục 9). Sau đó:
   - Kiểm tra chuỗi nhật ký toàn vẹn và trạng thái hệ thống (trang **Trạng thái hệ thống**, `/admin/system-status`).
   - Gỡ thông báo bảo trì.
7. **Kết luận — "Đã khắc phục".** Ghi vào hồ sơ (trạng thái này yêu cầu đã nhập biện pháp khắc phục; "Đã đóng" yêu cầu thêm nguyên nhân và kết luận):
   - Nguyên nhân.
   - Biện pháp khắc phục.
   - Các bên phối hợp.
   - Kết luận và bài học, kèm biện pháp phòng ngừa.
8. **Đóng hồ sơ — "Đã đóng".** Người có `incident.admin` đóng hồ sơ sau khi lãnh đạo chấp thuận. Hồ sơ đã đóng chỉ người có `incident.admin` sửa được.

**Xuất hồ sơ.** Từ trang chi tiết hồ sơ, dùng nút xuất (PDF/Excel) để lấy báo cáo của một sự cố, dùng làm phụ lục báo cáo gửi cấp trên. Nút xuất ở đầu danh sách cho sổ theo dõi sự cố theo bộ lọc.

## 4. Thông báo cho người dùng

Khi sự cố hoặc bảo trì ảnh hưởng tới người dùng, quản trị viên đăng thông báo ở **Quản trị → Thông báo hệ thống**:
- Chọn loại "Thông tin", "Lưu ý" hoặc "Bảo trì", và đặt thời gian hiển thị.
- Thông báo hiện ngay (thời gian thực) ở đầu mọi trang cho mọi người dùng đang đăng nhập.
- Người dùng có thể ẩn thông báo. Nếu thông báo được sửa, nó hiện lại.

Không đưa chi tiết kỹ thuật có thể bị lợi dụng (địa chỉ, lỗ hổng chưa vá) vào thông báo.

## 5. Báo cáo và phối hợp với cơ quan chức năng

Theo Thông tư 20/2017/TT-BTTTT:
- Sự cố thuộc diện phải báo cáo được **báo cáo ban đầu** tới cơ quan điều phối quốc gia (Cục An toàn thông tin) và đơn vị chuyên trách ứng cứu sự cố của Bộ Y tế. Hạn báo cáo là 05 ngày kể từ khi phát hiện.
- Sự cố nghiêm trọng phải được báo cáo **ngay**.
- Khi kết thúc ứng cứu, gửi **báo cáo kết thúc**.

Biểu mẫu theo phụ lục của Thông tư. Nội dung lấy từ hồ sơ đã xuất: mã hồ sơ, thời điểm phát hiện, mô tả, hệ thống bị ảnh hưởng, biện pháp đã thực hiện, kết luận.

Trong quá trình ứng cứu:
- Ghi đơn vị và đầu mối đã liên hệ vào ô **Đơn vị phối hợp xử lý** của hồ sơ, gồm Cục An toàn thông tin, đơn vị chuyên trách của Bộ Y tế, nhà thầu và cơ quan công an khi có dấu hiệu tội phạm.
- Thực hiện yêu cầu điều phối của cơ quan chức năng.
- Giữ nguyên bằng chứng: không xóa nhật ký, và sao lưu nhật ký container trước khi tạo lại container.

Danh sách đầu mối liên hệ (tên, số điện thoại, thư điện tử của cán bộ ATTT, lãnh đạo, nhà thầu, đơn vị chuyên trách) được đơn vị vận hành lập khi tiếp nhận hệ thống. Danh sách này cần được rà soát 6 tháng một lần, cùng đợt diễn tập.

## 6. Quản lý lỗ hổng

| Lớp | Cách phát hiện | Khi nào |
|---|---|---|
| Thư viện .NET (NuGet) | `dotnet list package --vulnerable --include-transitive` trong `scripts/check-vulnerabilities.ps1` | Mỗi lần chạy `scripts/ci.ps1` và mỗi lần push (GitHub Actions) |
| Thư viện web, mobile (npm) | `npm audit --audit-level=high` | Mỗi lần push (GitHub Actions) và khi chạy `scripts/check-vulnerabilities.ps1` |
| Image Docker (api, worker, web: hệ điều hành và gói) | Trivy (`aquasec/trivy`, ghim theo digest) quét image vừa build, job "Docker images (Trivy)" | Mỗi lần push và **hằng tuần** (thứ Hai) để phát hiện CVE mới công bố |
| Cập nhật phụ thuộc | Dependabot (`.github/dependabot.yml`): NuGet, npm, Docker, GitHub Actions | Hằng tuần; bản vá bảo mật mở ngay khi có cảnh báo |
| Ứng dụng | Kiểm tra đối chiếu `docs/attt-742.md`, kiểm thử tự động phân quyền | Mỗi bản phát hành |

**Cổng chặn.** Lỗ hổng mức High hoặc Critical đã có bản vá làm CI thất bại, và bản đó **không được tự động triển khai** (job deploy phụ thuộc job quét). Chạy kiểm tra tại máy:

```powershell
powershell -File scripts/check-vulnerabilities.ps1      # NuGet + npm
```

**Thời hạn vá** kể từ khi phát hiện:

| Mức | Thời hạn |
|---|---|
| Critical | 7 ngày |
| High | 30 ngày |
| Medium | Gộp vào đợt cập nhật kế tiếp (tối đa 90 ngày) |
| Low | Theo kế hoạch nâng cấp |

Nếu lỗ hổng đang bị khai thác, lập hồ sơ sự cố loại "Khai thác lỗ hổng" và xử lý theo mục 3.

**Chấp nhận rủi ro.** Một lỗ hổng chỉ được bỏ qua khi chưa có bản vá, hoặc khi đoạn mã lỗi không được dùng. Khi đó:
- Ghi mã CVE vào `.trivyignore` kèm lý do và ngày rà soát.
- Lập hồ sơ sự cố loại "Khai thác lỗ hổng", mức "Thấp", ghi rõ biện pháp giảm thiểu.
- Rà soát lại mỗi tháng.

**Quy trình vá:**
1. Nâng phiên bản gói, hoặc nhận PR của Dependabot.
2. Chạy `scripts/ci.ps1`.
3. Merge vào `main`. GitHub Actions kiểm thử, quét lại, rồi triển khai.
4. Kiểm tra trạng thái hệ thống sau triển khai.

Image nền: `web/Dockerfile` chạy `apk upgrade` để lấy bản vá Alpine mới nhất, và các image .NET dùng bản `10.0` được Microsoft cập nhật định kỳ. Build lại và triển khai ít nhất mỗi tháng một lần, kể cả khi không có thay đổi mã.

## 7. Diễn tập và rà soát

- Diễn tập ứng phó **6 tháng một lần**. Kịch bản gợi ý: tải lên tệp mẫu EICAR, hoặc nhập sai mật khẩu 5 lần để sinh cảnh báo. Sau đó thực hiện đủ các bước của mục 3 trên một hồ sơ thử: lập hồ sơ từ cảnh báo, ngăn chặn, kết luận, xuất PDF.
- Hằng quý, xuất sổ theo dõi sự cố để báo cáo lãnh đạo. Rà soát `.trivyignore`, các hồ sơ chưa đóng và các PR Dependabot còn mở.
