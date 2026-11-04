# Báo cáo rà soát: tải đồng thời, tệp lớn, tranh chấp dữ liệu, hiệu năng request

- Ngày: 2026-11-04 · Commit gốc: `745e6d7` (cây làm việc có thay đổi chưa commit ở `desktop/`, `docs/`, `deploy/`)
- Phạm vi: máy chủ (`src/`), cấu hình triển khai (`deploy/`). Không rà ứng dụng desktop/mobile.
- Bối cảnh vận hành (HSMT mục 3.1, 3.4): 1 máy chủ 8 lõi / 16 GB RAM / SSD 520 GB / 300 Mbps; ~100 người dùng đồng thời; mỗi giao dịch phải < 10 giây (trừ báo cáo lớn có thông báo); "không mất dữ liệu hoặc ngắt kết nối bất thường".
- Không có kết quả chạy tải thật (`tests/load/t5-large-files.js` chưa được chạy/lưu kết quả). Các mức độ dưới đây suy ra từ đọc mã.

## Dành cho AI đánh giá/sửa

Mỗi mục có nhãn độ tin cậy:
- **Đã kiểm lại**: đã đọc lại đúng dòng mã và xác nhận.
- **Chưa kiểm lại**: do agent rà soát báo, có trích mã nhưng chưa được đối chiếu lần hai — hãy đọc mã trước khi sửa.
- **Nghi vấn**: hợp lý nhưng chưa chứng minh được kịch bản xảy ra.

Khi sửa, tuân theo `CLAUDE.md`: thông báo lỗi tiếng Việt (`AppException`/ProblemDetails), không để TODO, thêm test (unit hoặc integration với Testcontainers), migration bằng lệnh trong `CLAUDE.md`, chạy `scripts/ci.ps1` xanh trước khi commit. Mục "Đã loại" ở cuối liệt kê các báo cáo sai/được thiết kế như vậy — đừng sửa lại các mục đó.

## Tóm tắt theo mức độ

| # | Mức | Tin cậy | Vấn đề |
|---|---|---|---|
| A1 | Cao | Đã kiểm lại | Hoàn tất tus lỗi → xóa luôn tệp đã tải, phải tải lại từ đầu |
| B1 | Cao | Đã kiểm lại | Mã hóa + băm toàn bộ tệp đồng bộ trong request "hoàn tất" |
| B2 | Cao | Đã kiểm lại | Không kiểm tra dung lượng đĩa trống; mỗi tệp lớn chiếm 2–3 bản tạm |
| B3 | Cao | Đã kiểm lại | Sao lưu đầy đủ ghi `files.tar` lên cùng đĩa, không kiểm tra chỗ trống |
| B4 | Cao | Đã kiểm lại | Tạo ảnh thu nhỏ đọc cả tệp ảnh vào RAM, không giới hạn số điểm ảnh → Worker OOM |
| C1 | Cao | Đã kiểm lại | Không có ràng buộc duy nhất tên tệp/thư mục → trùng tên khi thao tác đồng thời |
| C2 | Cao | Đã kiểm lại | Kiểm tra `BaseETag` của sync là check-then-act → hai máy đẩy cùng lúc, bản sau ghi đè không tạo bản xung đột |
| D1 | Cao | Đã kiểm lại | N+1 kiểm tra quyền trong tìm kiếm / gần đây / gắn sao |
| D2 | Cao | Chưa kiểm lại | Sao chép thư mục đệ quy, `SaveChanges` từng nút, trong request |
| D3 | Cao | Chưa kiểm lại | Dọn thùng rác xóa từng mục tuần tự trong request |
| C3 | TB | Đã kiểm lại | `VersionNo = max+1` đua nhau → request thua nhận lỗi 500 (không map `DbUpdateException`) |
| C4 | TB | Đã kiểm lại | Quota kiểm tra trước, cộng sau, không giữ chỗ |
| D4 | TB | Đã kiểm lại | Cây thư mục tải toàn bộ thư mục, không lọc `BreakInheritance` (nghi lộ tên thư mục bị hạn chế) |
| B5 | TB | Đã kiểm lại | `pg_dump`/`pg_restore` không có timeout |
| B6 | TB | Đã kiểm lại | Sao lưu định kỳ không chặn chạy chồng |
| B7 | TB | Chưa kiểm lại | Thư mục `.tmp` (bản giải mã cho ffmpeg/ClamAV) không được dọn khi tiến trình chết |
| B8 | TB | Chưa kiểm lại | Xuất dữ liệu / báo cáo 200.000 dòng nạp toàn bộ vào RAM (Worker giới hạn 2 GB) |
| B9 | TB | Chưa kiểm lại | Callback ONLYOFFICE tải tệp không giới hạn kích thước; URL không khớp `InternalUrl` vẫn được gọi |
| D5 | TB | Chưa kiểm lại | Lô sync 500 thao tác, nạp lại principal sau mỗi thao tác |
| D6 | TB | Chưa kiểm lại | Các truy vấn tổng hợp trong bộ nhớ / subquery tương quan (được chia sẻ, dashboard, thống kê, thùng rác, xác minh chuỗi nhật ký) |
| C5 | Thấp | Chưa kiểm lại | Phê duyệt hai lần đồng thời → nhật ký/thông báo đôi |
| C6 | Thấp | Nghi vấn | Hai thao tác di chuyển chéo thư mục đồng thời → vòng lặp đường dẫn |
| C7 | Thấp | Chưa kiểm lại | Bộ đếm đăng nhập sai bị mất cập nhật |
| A2 | Thấp | Đã kiểm lại | Endpoint delta của sync xóa tệp tạm khi hoàn tất lỗi |
| B10 | Thấp | Chưa kiểm lại | Callback ONLYOFFICE lưu blob rồi `SaveChanges` lỗi → blob mồ côi |
| B11 | Thấp | Chưa kiểm lại | Tải zip thư mục: nạp toàn bộ danh sách tệp, không giới hạn số tệp/tổng dung lượng |

Thứ tự sửa đề xuất: A1 → B2 → C1 (+C3) → B1 → C2 → B4 → B3/B5/B6 → D1 → D2/D3 → phần còn lại.

---

## A. Mất dữ liệu đã tải

### A1. Hoàn tất tus lỗi thì xóa luôn tệp đã nhận — Cao · Đã kiểm lại
- `src/BlueDrive.Api/Controllers/StorageControllers.cs:202-210`
  ```csharp
  try { return await Mediator.Send(new CompleteUploadCommand(Path.Combine(dir, tusId), meta)); }
  finally { await store.DeleteFileAsync(tusId, CancellationToken.None); System.IO.File.Delete(ownerFile); }
  ```
- Kịch bản: người dùng tải xong 5 GB; `CompleteUploadHandler` ném lỗi (hết quota vì người khác vừa tải vào cùng nhóm — xem C4, lỗi DB tạm thời, 415, trùng `VersionNo` — xem C3). Tệp tus bị xóa → phải tải lại toàn bộ 5 GB. Web (`web/src/stores/uploads.ts`, `finishComplete`) không còn gì để thử lại.
- Sửa: chỉ xóa tệp tus + `.owner` khi hoàn tất **thành công**. Lỗi "vĩnh viễn" (415, 403) có thể xóa ngay; lỗi có thể khắc phục (413 quota, 409, 5xx) giữ lại để `TusCleanupService` dọn sau 24 giờ. Phía web: khi hoàn tất lỗi có thể thử lại, cho nút "Thử lại" gọi lại `complete` thay vì tải lại. Cần test integration: hoàn tất lỗi quota → nâng quota → gọi lại `complete` thành công.

### A2. Endpoint delta của sync cũng xóa trong `finally` — Thấp · Đã kiểm lại
- `src/BlueDrive.Api/Controllers/SyncControllers.cs:93-107`. Chỉ mất các khối thay đổi (thường nhỏ); client desktop tự gửi lại. Có thể giữ nguyên, ghi nhận để biết.

## B. Tài nguyên máy chủ khi tệp lớn / nhiều người

### B1. Mã hóa + băm cả tệp trong request "hoàn tất" — Cao · Đã kiểm lại
- `src/BlueDrive.Application/Features/Storage/UploadFeatures.cs:97-99` → `src/BlueDrive.Infrastructure/Storage/FileContentService.cs:47-75` (`ChunkedAesGcm.EncryptAsync` đọc toàn bộ bản rõ, ghi bản mã ra tệp tạm, rồi `ImportFileAsync`/`WriteAsync`).
- Kịch bản: tệp 2 GB ⇒ đọc 2 GB + ghi 2 GB (+ sao chép nếu storage không hỗ trợ import/rename) trong request. 10 người hoàn tất cùng lúc tranh nhau I/O SSD ⇒ mỗi request hàng chục giây đến vài phút, vượt yêu cầu < 10 giây; cũng là cửa sổ đua cho C2/C4.
- Cũng gặp ở: callback ONLYOFFICE (`src/BlueDrive.Application/Features/Office/OfficeFeatures.cs:115`), delta sync.
- Hướng sửa (chọn một):
  1. Hoàn tất bất đồng bộ: request tạo bản ghi tệp ở trạng thái "đang xử lý" và trả `202`, Hangfire job mã hóa + chuyển vào storage, đẩy kết quả qua SignalR; hoặc
  2. Mã hóa ngay khi nhận từng chunk (store tus tùy biến ghi thẳng định dạng `ChunkedAesGcm`; cần biết độ dài trước — tus có `Upload-Length`), để bước hoàn tất chỉ là đổi tên tệp + ghi DB.
  Phương án 2 tốt hơn về đĩa (B2) nhưng phức tạp hơn; cần giữ nguyên định dạng và khả năng giải mã/Range hiện tại (`DecryptingStream`).

### B2. Không kiểm tra dung lượng đĩa trống; một tệp chiếm nhiều bản — Cao · Đã kiểm lại
- Không có kiểm tra chỗ trống trước khi nhận tệp (`TusSetup.OnBeforeCreateAsync`, `ValidateUploadHandler`). `FreeBytes()` (`src/BlueDrive.Infrastructure/Auditing/AuditMonitoring.cs:61`) chỉ dùng để cảnh báo. Quota là theo nhóm, không phản ánh đĩa thật.
- Trong lúc xử lý, một tệp có thể tồn tại đồng thời: tệp tus (`.tus/`), tệp mã hóa tạm (`TempDirectory`), tệp đích, và bản giải mã Worker tạo cho ffmpeg/ClamAV (`MediaSteps.cs:29-35`). Phiên tus dở dang giữ 24 giờ.
- Kịch bản: SSD 520 GB còn 30 GB, 10 người tải tệp 2–3 GB ⇒ đĩa đầy ⇒ PostgreSQL ngừng ghi (WAL), toàn hệ thống lỗi.
- Sửa: cấu hình ngưỡng (vd. `Storage:MinFreeBytes`, mặc định ~10% đĩa hoặc 20 GB); trong `OnBeforeCreateAsync` và trước bước mã hóa từ chối khi `free - (UploadLength × hệ số) < ngưỡng`, trả 507 với thông báo tiếng Việt; phát cảnh báo quản trị. Lưu ý Docker: đo đúng ổ của `Storage:LocalRoot` trong container.

### B3. Sao lưu ghi lên cùng đĩa, không kiểm tra chỗ trống — Cao · Đã kiểm lại
- `src/BlueDrive.Infrastructure/Operations/BackupRunner.cs:50-73`: tạo thư mục rồi `pg_dump` + `ArchiveFilesAsync` (tar toàn bộ tệp khi full) ngay. `deploy/docker-compose.yml:25,79,103,181`: `Backup__Root: /data/backups` là volume Docker trên cùng máy, mặc định `BACKUP_S3_ENABLED=false`.
- Hệ quả: bản full chiếm ≈ toàn bộ dữ liệu ⇒ với đĩa 520 GB, dữ liệu thật chỉ dùng được dưới ~1/2 (ít hơn nếu giữ nhiều bản full). Khi đĩa không đủ, sao lưu làm đầy đĩa giữa chừng (phần dọn dẹp chỉ chạy sau khi lỗi).
- Sửa: trước khi chạy, ước tính dung lượng cần (full: tổng `UsedBytes`/dung lượng storage; incremental: tổng tệp mới từ `since`) + dump DB lần trước, từ chối và báo `BackupFailed` khi không đủ. Ghi rõ trong `docs/huong-dan-quan-tri.md` khuyến nghị đích sao lưu là S3/ổ khác. Nếu bật S3, cân nhắc stream thẳng lên S3 thay vì dựng bản cục bộ.

### B4. Ảnh thu nhỏ: đọc cả tệp vào RAM, không giới hạn điểm ảnh — Cao · Đã kiểm lại
- `src/BlueDrive.Infrastructure/Media/MediaSteps.cs:183-190`
  ```csharp
  using var ms = new MemoryStream(); await stream.CopyToAsync(ms, ct);
  using var codec = SKCodec.Create(ms); SKBitmap? bmp = codec is null ? null : SKBitmap.Decode(codec);
  ```
- Kịch bản: tệp TIFF/PNG 800 MB, hoặc PNG nhỏ khai báo 30.000×30.000 (decompression bomb ⇒ ~3,6 GB RAM). Worker giới hạn 2 GB (`deploy/docker-compose.yml:110`), chạy 4 job song song (`HangfireSetup.cs:32`) ⇒ OOM, container khởi động lại, các job khác dừng giữa chừng.
- Sửa: dùng `SKCodec.Create` trên stream có thể tua (giải mã ra tệp tạm như nhánh ffmpeg) thay cho `MemoryStream`; kiểm tra `codec.Info.Width*Height` ≤ ngưỡng (vd. 100 MP) trước `Decode`, quá ngưỡng thì bỏ qua thumbnail; giải mã ở kích thước thu nhỏ (`codec.GetScaledDimensions`/`SKImageInfo` nhỏ) thay vì full resolution.

### B5. `pg_dump`/`pg_restore` không có timeout — TB · Đã kiểm lại
- `src/BlueDrive.Infrastructure/Operations/PgTools.cs:31-41`: chỉ chờ theo token của Hangfire. Treo (khóa, mạng) ⇒ chiếm một worker hàng đợi `maintenance` vô thời hạn, sao lưu kế tiếp không chạy.
- Sửa: `CancelAfter` (cấu hình, vd. 2 giờ) + `p.Kill(entireProcessTree: true)` khi hủy, giống `MediaSteps.RunAsync`.

### B6. Sao lưu định kỳ không chặn chạy chồng — TB · Đã kiểm lại
- `src/BlueDrive.Infrastructure/Operations/BackupRunner.cs:295-302` (`RunScheduledAsync`) tạo job và chạy ngay, không kiểm tra job `Queued/Running` như đường sao lưu đột xuất (agent báo `BackupFeatures.cs:87` có kiểm tra — chưa kiểm lại). Sao lưu đột xuất đang chạy lúc 2 giờ sáng ⇒ hai bản chạy song song, gấp đôi đĩa/I/O.
- Sửa: `[DisableConcurrentExecution]` hoặc dùng chung kiểm tra `AnyAsync(Queued||Running)`; nếu đang chạy thì bỏ lượt định kỳ và ghi log.

### B7. `.tmp` không được dọn khi Worker/API chết — TB · Chưa kiểm lại
- Bản giải mã (`proc-*`) cho ffmpeg/ClamAV và tệp tạm ONLYOFFICE nằm ở `TempDirectory`; chỉ xóa trong `finally`. OOM/khởi động lại ⇒ để lại **bản rõ chưa mã hóa** trên đĩa (cũng là vấn đề ATTT với mã hóa lưu trữ) và chiếm chỗ.
- Sửa: dọn tệp trong `TempDirectory` cũ hơn N giờ khi khởi động và định kỳ (gộp vào `TusCleanupService` hoặc recurring job).

### B8. Xuất dữ liệu / báo cáo nạp toàn bộ vào RAM — TB · Chưa kiểm lại
- `src/BlueDrive.Infrastructure/Operations/DataExporter.cs:41-52` (`ToListAsync` phiên bản, bình luận, ACL…); `src/BlueDrive.Application/Features/Monitoring/ReportFeatures.cs:173` (`Take(200_000)` rồi ClosedXML dựng workbook trong RAM).
- Sửa: duyệt theo lô / `AsAsyncEnumerable`; với Excel lớn dùng ghi luồng (OpenXML SAX) hoặc hạ trần và báo người dùng.

### B9. Callback ONLYOFFICE tải tệp không giới hạn — TB · Chưa kiểm lại
- `src/BlueDrive.Application/Features/Office/OfficeFeatures.cs:112-115`; `Internalize()` trong `src/BlueDrive.Infrastructure/Office/OnlyOfficeProvider.cs`. Callback đã xác thực JWT nên rủi ro thấp hơn, nhưng nên: giới hạn kích thước theo `Content-Length` và khi sao chép; từ chối URL không thuộc gốc ONLYOFFICE đã cấu hình.

### B10. Blob mồ côi khi lưu phiên bản ONLYOFFICE lỗi DB — Thấp · Chưa kiểm lại
- `OfficeFeatures.cs:114-148`: `content.SaveAsync` trước `SaveChangesAsync`; lỗi DB không dọn blob. Sửa: bắt lỗi sau khi lưu blob và `content.DeleteAsync`. (Cùng mẫu có thể có ở `CompleteUploadHandler` — kiểm tra luôn.)

### B11. Tải zip thư mục không giới hạn — Thấp · Chưa kiểm lại
- `src/BlueDrive.Application/Features/Storage/DownloadFeatures.cs:114-122` nạp toàn bộ tệp của cây thư mục; không giới hạn số tệp/tổng dung lượng. Truyền zip đã stream đúng (không đệm). Sửa: đặt trần số tệp và tổng dung lượng, báo lỗi tiếng Việt khi vượt.

## C. Tranh chấp khi thao tác đồng thời

### C1. Không có ràng buộc duy nhất tên tệp/thư mục — Cao · Đã kiểm lại
- `src/BlueDrive.Infrastructure/Persistence/AppDbContext.cs`: chỉ `(FileId, VersionNo)` và các mã danh mục là `IsUnique`; tên tệp/thư mục chỉ có index trigram để tìm kiếm.
- Tạo thư mục: `src/BlueDrive.Application/Features/Storage/ItemOperations.cs:46-51` (`AnyAsync` rồi `Add`). Đặt tên tệp khi tải lên: `UniqueFileNameAsync` (`StorageCore.cs:56-80`) đọc tên anh em rồi chọn tên "(1)". Riêng tải lên thư mục có advisory lock theo workspace (`FolderChain.EnsureAsync`) — chỉ bảo vệ đường đó.
- Kịch bản: hai người tải `Báo cáo.docx` vào cùng thư mục cùng lúc ⇒ hai tệp cùng tên; hai cú "Tạo thư mục" ⇒ hai thư mục "Tài liệu". Desktop sync ánh xạ theo tên ⇒ khó xử lý (xem `desktop/src/BlueDrive.Sync.Core/NameMapper.cs`).
- Sửa: migration thêm partial unique index, ví dụ
  `files (workspace_id, folder_id, lower(name)) WHERE NOT is_deleted` với `NULLS NOT DISTINCT` (PG16 hỗ trợ; `folder_id` null = gốc), tương tự `folders (workspace_id, parent_id, lower(name)) WHERE NOT is_deleted`. **Trước khi thêm**, migration phải xử lý dữ liệu trùng sẵn có (đổi tên kèm hậu tố). Bắt `DbUpdateException` có `PostgresException.SqlState == "23505"`: với tải lên thì thử lại tên kế tiếp; với tạo/đổi tên thì trả 409 tiếng Việt. Kiểm tra lại quy tắc so sánh tên hiện có (`ToLower()`) để index khớp đúng ngữ nghĩa, và quy tắc tên trong thùng rác khi khôi phục.

### C2. Kiểm tra `BaseETag` của sync không nguyên tử — Cao · Đã kiểm lại
- `src/BlueDrive.Application/Features/Storage/UploadFeatures.cs:55`: so `meta.BaseETag` với `f.Revision` trong `UploadRules.CheckAsync`, sau đó mới mã hóa (B1, có thể hàng chục giây) rồi ghi phiên bản. `Revision` được tăng trong bộ nhớ (`src/BlueDrive.Infrastructure/Persistence/ChangeJournalInterceptor.cs:61,90`) và **không** là concurrency token (không có `IsConcurrencyToken`/xmin trong `AppDbContext`).
- Kịch bản: máy A và máy B cùng sửa một tệp, cùng `BaseETag = r5`, cùng đẩy. Cả hai qua kiểm tra; cả hai ghi phiên bản; bản sau trở thành hiện hành, bản trước chỉ còn là phiên bản cũ — **không tạo "bản xung đột"** như đặc tả sync yêu cầu (xem `docs/kich-ban-kiem-thu-desktop.md` dòng 51), người dùng A không biết thay đổi của mình bị đè.
- Sửa: đánh dấu `Revision` là concurrency token (EF sinh `WHERE revision = @old`), bắt `DbUpdateConcurrencyException` trong đường tải phiên bản và đổi thành `PreconditionFailedException` (412) để client tạo bản xung đột; hoặc khóa dòng tệp (`SELECT ... FOR UPDATE`) và kiểm tra lại ETag ngay trước khi ghi. Lưu ý đổi sang concurrency token ảnh hưởng mọi chỗ `SaveChanges` sửa `StoredFile`/`Folder` — `GlobalExceptionHandler` đã map `DbUpdateConcurrencyException` → 409; cần rà các handler khác.

### C3. `VersionNo = max+1` → request thua nhận 500 — TB · Đã kiểm lại
- `UploadFeatures.cs:112`, `OfficeFeatures.cs:121` (callback), `OfficeFeatures.cs:182` (khôi phục phiên bản). Unique index `(file_id, version_no)` chặn được dữ liệu sai, nhưng `src/BlueDrive.Api/Infrastructure/GlobalExceptionHandler.cs:26-28` chỉ map `DbUpdateConcurrencyException`; `DbUpdateException` (23505) rơi vào nhánh 500.
- Kịch bản: ONLYOFFICE lưu cưỡng bức (status 6) và desktop đẩy phiên bản cùng lúc ⇒ một bên lỗi 500 (kết hợp A1 ⇒ mất tệp tus).
- Sửa: khóa dòng tệp/advisory lock theo `file_id` khi tạo phiên bản, hoặc thử lại khi gặp 23505; thêm map 23505 → 409 tiếng Việt trong `GlobalExceptionHandler` làm lưới an toàn chung.

### C4. Quota kiểm tra trước, cộng sau — TB · Đã kiểm lại
- `src/BlueDrive.Application/Features/Storage/StorageCore.cs:14-25` (đọc `UsedBytes`, so sánh) và `:28-32` (`ExecuteUpdateAsync` cộng nguyên tử). Giữa hai bước là cả quá trình tải + mã hóa.
- Kịch bản: nhóm còn 3 GB, 3 người cùng tải 2 GB ⇒ cả ba qua kiểm tra lúc tạo phiên; ở bước hoàn tất ai kiểm tra trước cộng trước — có thể vượt quota nếu các bước hoàn tất trùng nhau; người cuối bị từ chối **sau khi** đã tải xong (kết hợp A1 ⇒ mất phần đã tải).
- Sửa: kiểm tra + cộng trong một câu lệnh có điều kiện (`UPDATE workspaces SET used_bytes = used_bytes + @d WHERE id=@id AND (quota_bytes=0 OR used_bytes+@d<=quota_bytes)`), hoàn trả nếu bước sau lỗi; hoặc giữ chỗ (reserved_bytes) từ lúc tạo phiên tus và giải phóng khi hoàn tất/hết hạn.

### C5. Phê duyệt hai lần đồng thời — Thấp · Chưa kiểm lại
- `src/BlueDrive.Application/Features/Sharing/ShareLinkFeatures.cs:294-296` (và `SubmitForApproval` ~:320): đọc trạng thái `Pending` rồi ghi. Hai người duyệt cùng lúc ⇒ nhật ký và thông báo đôi. Sửa: `ExecuteUpdateAsync(... WHERE approval_status = 'pending')`, kiểm tra số dòng = 1, ngược lại 409.

### C6. Di chuyển chéo thư mục đồng thời → vòng lặp — Thấp · Nghi vấn
- `src/BlueDrive.Application/Features/Storage/ItemOperations.cs:234-248`: kiểm tra `target.Path.StartsWith(folder.Path)` không khóa. A chuyển `/a` vào `/b`, B chuyển `/b` vào `/a` cùng lúc ⇒ có thể tạo vòng trong `path`. Sửa: advisory lock theo workspace khi di chuyển thư mục (như `FolderChain`) và kiểm tra lại sau khi khóa.

### C7. Bộ đếm đăng nhập sai mất cập nhật — Thấp · Chưa kiểm lại
- `src/BlueDrive.Application/Features/Auth/AuthCommands.cs:88-95`: `FailedLoginCount++` trong bộ nhớ. Đăng nhập dồn dập song song có thể đếm thiếu, khóa tài khoản muộn hơn ngưỡng (đã có rate limit đăng nhập nên mức thấp). Sửa: tăng bằng `ExecuteUpdateAsync(u => u.FailedLoginCount + 1)` rồi đọc lại.

## D. Hiệu năng trong request (yêu cầu < 10 giây)

### D1. N+1 kiểm tra quyền — Cao · Đã kiểm lại
- `src/BlueDrive.Application/Features/Search/SearchFeatures.cs:82-84`: mỗi kết quả gọi `auth.CanAsync` và `FileLocation.BuildAsync`.
- `src/BlueDrive.Application/Features/Storage/DriveQueries.cs:262-266` (gần đây, tới 400 ứng viên) và `:284-286` (gắn sao, **không giới hạn số lượng** — `:278`).
- `ResourceAuthorizationService` (`src/BlueDrive.Application/Security/ResourceAuthorization.cs:57+`) không có bộ nhớ đệm trong request; mỗi lần gọi nhiều truy vấn (principal, workspace, ACL, chuỗi thư mục…).
- Kịch bản: trang tìm kiếm 100 kết quả ⇒ hàng trăm truy vấn tuần tự; với 100 người dùng đồng thời dễ vượt 10 giây và cạn pool kết nối.
- Sửa: tính tập principal một lần cho mỗi request (cache scoped trong service), thêm API kiểm tra theo lô (`FilterVisibleAsync(ids)`) dùng truy vấn tập hợp như `ListItemsHandler` đang làm; dựng breadcrumb theo lô; giới hạn danh sách gắn sao (vd. 500) và phân trang.

### D2. Sao chép thư mục đệ quy trong request — Cao · Chưa kiểm lại
- `src/BlueDrive.Application/Features/Storage/ItemOperations.cs:343-354`: mỗi nút một `SaveChangesAsync` + một truy vấn con. Cây 500 thư mục / 2.000 tệp ⇒ hàng nghìn round-trip, không có điểm dừng; lỗi giữa chừng để lại bản sao dở (cần kiểm tra có transaction không).
- Sửa: thêm vào change tracker theo lô rồi lưu một lần (hoặc theo lô vài trăm), bọc transaction; với cây lớn hơn ngưỡng chuyển sang job nền trả `202` + thông báo khi xong.

### D3. Dọn thùng rác tuần tự trong request — Cao · Chưa kiểm lại
- `src/BlueDrive.Application/Features/Storage/TrashFeatures.cs:141-149`: `foreach (item) await purger.PurgeAsync(item, ...)`, mỗi lần nhiều lệnh xóa. "Dọn sạch thùng rác" với hàng nghìn mục ⇒ vượt xa 10 giây.
- Sửa: chuyển sang job nền theo lô, trả `202` và cập nhật giao diện khi xong (hoặc xóa theo lô bằng `ExecuteDeleteAsync` theo tập id).

### D4. Cây thư mục: tải toàn bộ và không lọc quyền thư mục — TB · Đã kiểm lại (phần bảo mật: Nghi vấn)
- `src/BlueDrive.Application/Features/Storage/DriveQueries.cs:178-185`: chỉ `EnsureAsync(Workspace, View)` rồi trả **mọi** thư mục chưa xóa của workspace. Thư mục có `BreakInheritance` (`ResourceAuthorization.cs:181-182`) nhưng không được lọc ở đây ⇒ thành viên không có quyền với thư mục bị hạn chế vẫn thấy **tên** thư mục đó trong hộp chọn "Sao chép tới / Di chuyển tới".
- Cần xác minh: cách `ListItemsHandler` lọc thư mục `BreakInheritance` và liệu đây có phải hành vi mong muốn không. Nếu không: lọc theo cùng scope SQL; cân nhắc tải cây theo từng cấp (lazy) cho workspace lớn.

### D5. Lô sync 500 thao tác — TB · Chưa kiểm lại
- `src/BlueDrive.Application/Features/Sync/SyncOperations.cs:108,146`: tối đa 500 thao tác/lô, `access.InitAsync` gọi lại sau mỗi thao tác. Sửa: chỉ khởi tạo khi principal thực sự đổi, hạ trần lô (vd. 100) — nhớ chỉnh client desktop tương ứng (`desktop/src/BlueDrive.Sync.Client/HttpRemoteStore.cs`).

### D6. Tổng hợp trong bộ nhớ / subquery tương quan — TB · Chưa kiểm lại
- "Được chia sẻ với tôi": `src/BlueDrive.Application/Features/Sharing/ShareFeatures.cs:161-170` — nạp hết rồi `GroupBy`/lọc từ khóa trong C# trước khi phân trang.
- Dashboard quản trị: `src/BlueDrive.Application/Features/Dashboards/DashboardFeatures.cs:133` — nạp mọi tệp 30 ngày rồi đếm theo ngày trong C# (có cache 2 phút).
- Dashboard cá nhân: `DashboardFeatures.cs:55-60` — gộp theo thư mục O(n²) trong C# (cache 1 phút).
- Thống kê dung lượng theo người dùng: `src/BlueDrive.Application/Features/Monitoring/MonitoringFeatures.cs:170` — `COUNT` tương quan mỗi người dùng (kiểm tra có index `files.created_by`).
- Thùng rác: `TrashFeatures.cs:43` — tổng dung lượng thư mục bằng subquery `LIKE path%` mỗi thư mục; `:58` breadcrumb từng dòng.
- Xác minh chuỗi nhật ký: `MonitoringFeatures.cs:88` — `Max` lấy từ query string, mặc định 200.000, chạy đồng bộ.
- Tìm kiếm theo quyền thư mục: `src/BlueDrive.Infrastructure/Search/FileSearchService.cs:37` — `Path.Contains("/" + g + "/")` không dùng được index.
- Sửa chung: đẩy `GROUP BY`/`DISTINCT ON` xuống SQL, trần tham số từ client, việc dài chuyển job nền có thông báo.

---

## Đã loại (không phải lỗi / theo thiết kế — đừng sửa)

- **Rò tệp spool trong `ChunkedAesGcm.EncryptAsync`** (`ChunkedAesGcm.cs:72-77`): đúng là nếu `CopyToAsync` lỗi thì tệp spool ở lại, nhưng mọi nơi gọi (`FileContentService.SaveAsync` ← upload/ONLYOFFICE dùng `FileStream`, thumbnail/báo cáo/seed dùng `MemoryStream`) đều truyền stream tua được ⇒ nhánh spool không chạy. Chỉ cần sửa nếu sau này có nơi truyền stream mạng.
- **Khôi phục phiên bản / sao chép cộng quota dù dùng chung blob**: theo thiết kế "quota tính mọi phiên bản (kể cả thùng rác), chỉ giải phóng khi xóa vĩnh viễn" (`StorageCore.cs:11`; xóa vĩnh viễn trừ `versions.Sum(SizeBytes)` ở `TrashFeatures.cs:183,204`) ⇒ nhất quán.
- **Tải multipart (`files/upload`) xóa tệp tạm trong `finally`**: tệp ≤ 512 MB gửi một request, client gửi lại cả request — chấp nhận được.
- **ffmpeg đọc stdout/stderr**: hai lệnh đọc khởi động trước `WaitForExitAsync`, có timeout 5 phút và dọn tệp trong `finally` — ổn.

## Đã kiểm tra, ổn

- Tải lên tus qua nginx/Caddy không đệm (`proxy_request_buffering off`, `request_body max_size 0`), timeout 1 giờ; tusdotnet ghi thẳng đĩa.
- Tải xuống/Range trên tệp mã hóa: `DecryptingStream` tua được theo khối, không đệm cả tệp; zip stream thẳng ra response.
- Sao chép/khôi phục phiên bản dùng lại blob (không mã hóa lại); xóa vĩnh viễn chỉ xóa blob khi không còn phiên bản nào tham chiếu.
- Chuỗi băm nhật ký dùng `pg_advisory_xact_lock`; tải lên thư mục dùng advisory lock theo workspace (`FolderChain.EnsureAsync`).
- Giới hạn lượt truy cập liên kết chia sẻ dùng `ExecuteUpdateAsync` có điều kiện — nguyên tử.
- ClamAV: tệp > `CLAMAV_MAX_SCAN_MB` đánh dấu "quá lớn"; quét lỗi tối đa 3 lần rồi `Failed`, không chặn vĩnh viễn.
- Docker có giới hạn bộ nhớ cho mọi container (`deploy/docker-compose.yml`).
- Sync: luồng thay đổi giới hạn 1.000 mục/lần, snapshot phân trang con trỏ; tìm kiếm có GIN `search_vector` + trigram, `PageSize` ≤ 100.
- Báo cáo xuất Excel chạy ở Worker (trả `202`).

## Kiểm chứng sau khi sửa

- Integration test cho A1, C1, C2, C3, C4 (chạy song song bằng `Task.WhenAll` trên Testcontainers).
- Chạy `tests/load/t5-large-files.js` (10 người tải 200 MB–2 GB + 20 người xem video) trên cấu hình tương đương máy chủ thật, lưu kết quả vào hồ sơ vận hành thử; theo dõi `upload_total_s`, thời gian request `complete`, dung lượng đĩa trống, bộ nhớ Worker.
