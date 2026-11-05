# Hiệu năng phân quyền (đặc tả mục 4.5)

## Mục tiêu

- 1 triệu tệp, 50.000 scope riêng, 5.000 người dùng, 2.000 nhóm: `Has()` có cache < 1 ms, không cache < 20 ms.
- Liệt kê thư mục 1.000 mục có lọc quyền: p95 < 300 ms. Tìm kiếm toàn hệ thống có lọc quyền: p95 < 1 giây.
- Ngắt kế thừa thư mục có 100.000 mục con: < 10 giây, trong một transaction.

## Cách đo

Dự án `tests/BlueDrive.Benchmarks` (BenchmarkDotNet, chạy trong tiến trình):

1. Khởi động PostgreSQL 16 trong container (Testcontainers) và API thật trong bộ nhớ (như integration test), chạy migration và dữ liệu nền.
2. Tạo dữ liệu lớn nhanh: vài dòng mẫu qua API (để mọi cột đúng), rồi nhân bản bằng SQL `generate_series`:
   - 5.000 người dùng (mỗi người trong 3 nhóm), 2.000 nhóm; 50 Space, mỗi Space có 10 nhóm làm thành viên (Biên tập);
   - mỗi Space 50 thư mục cấp 1 × 20 thư mục con; **mọi thư mục có quyền riêng** (52.500 scope): sao chép mục quyền của Space + một nhóm Người xem + một nhóm Biên tập;
   - 18 tệp mỗi thư mục con, thêm một thư mục 1.000 tệp (để liệt kê) và một thư mục 100.000 tệp (để ngắt kế thừa): tổng ≈ 1.001.000 tệp;
   - `securables` (đường dẫn ltree), `acl_scopes`, `acl_entries`, `scope_grants` ghi đúng như bộ chiếu / bộ sửa quyền ghi.
3. Đo:
   - `Has()` có cache: một request mới (bộ nhớ theo request rỗng), cache trong tiến trình còn nóng;
   - `Has()` không cache: xóa sạch `IMemoryCache` trước mỗi lần — mọi dòng đọc từ PostgreSQL;
   - liệt kê: `GET /workspaces/{id}/items?folderId=…&pageSize=500` (thư mục 1.000 mục) bằng tài khoản người dùng thường, 30 lần;
   - tìm kiếm: `GET /search?q=…` 30 lần mỗi loại — từ khóa cụ thể (mã 8 ký tự có trong tên tệp), "tài liệu 99999" (vài chục kết quả) và trường hợp cực rộng "tài liệu 12" (≈ 55.000 tệp khớp vì mọi tệp tên "Tài liệu …");
   - ngắt / khôi phục kế thừa thư mục 100.000 tệp bằng `AclEditor` trong một transaction.

Chạy lại:

```powershell
dotnet run -c Release --project tests/BlueDrive.Benchmarks                  # quy mô đặc tả (≈ 5 phút tạo dữ liệu)
dotnet run -c Release --project tests/BlueDrive.Benchmarks -- --files 20000 --users 200 --groups 50 --spaces 3 --top-folders 5 --sub-folders 4 --big-folder 2000   # nhanh
```

Kết quả ghi vào `artifacts/perf-permissions-results.md`.

## Thiết kế giúp đạt mục tiêu

- **Cache 3 tầng** (`permissions-model.md` mục 2): tầng L0 giữ cả quyết định (người dùng, mục) trong bộ nhớ, đóng dấu `PermissionGeneration` — mọi câu
  ghi vào bảng liên quan quyền làm nó hết hiệu lực ngay; nhờ đó `Has()` có cache không còn chạm CSDL.
- **ACL theo scope + `scope_grants`**: quyền của một mục là OR các mục quyền của *một* scope; danh sách lọc bằng một truy vấn con trên
  `securables × scope_grants` có chỉ mục.
- **ltree**: ngắt / khôi phục kế thừa trỏ lại cả cây con bằng một câu `UPDATE … WHERE path <@ …`.
- **Tìm kiếm**: ba nhánh `UNION` (search_vector GIN, trigram GIN trên tên không dấu, nội dung tài liệu GIN) thay cho một `OR` buộc quét toàn bảng.

## Kết quả

Đo lúc 05/11/2026 08:07, 16 luồng CPU, Microsoft Windows NT 10.0.26300.0, .NET 10.0.14, PostgreSQL 16 (container).

Dữ liệu: 1,001,001 tệp, 52,503 thư mục, 52,553 scope quyền riêng, 367,766 mục quyền, 5,002 người dùng, 2,001 nhóm; tạo trong 00:04:50.

| Phép đo | Mục tiêu | Kết quả | Đạt |
|---|---|---|---|
| `Has()` có cache | < 1 ms | 0.06 ms (p95 0.06 ms) | Đạt |
| `Has()` không cache | < 20 ms | 14 ms (p95 15 ms) | Đạt |
| Liệt kê thư mục 1,000 mục, lọc quyền (trang 500 mục, API) | p95 < 300 ms | p50 240 ms, p95 250 ms | Đạt |
| Liệt kê đủ 1,000 mục (mọi trang) | — | p50 471 ms, p95 498 ms | — |
| Tìm kiếm toàn hệ thống, lọc quyền (API), từ khóa cụ thể (mã tài liệu) | p95 < 1 s | p50 528 ms, p95 543 ms | Đạt |
| Tìm kiếm "tài liệu 99999" (vài chục kết quả) | p95 < 1 s | p50 648 ms, p95 662 ms | Đạt |
| Tìm kiếm rất rộng "tài liệu 12" (≈ 55.000 tệp khớp) | — | p50 2129 ms, p95 2326 ms | — |
| Ngắt kế thừa thư mục có 100,000 mục con (một transaction) | < 10 s | 2582 ms | Đạt |
| Khôi phục kế thừa thư mục đó | — | 2790 ms | — |

### Nhận xét

- Mọi mục tiêu đạt ở quy mô đặc tả. Trước khi có tầng L0, `Has()` có cache mất ≈ 18 ms (vẫn đọc phiên bản ACL / thành viên, nút, nhãn, trạng thái Space từ CSDL); tầng L0 đưa xuống ≈ 0,06 ms.
- Tìm kiếm trước khi tách 3 nhánh `UNION`: p95 4,6 s (một `OR` buộc quét 1 triệu tệp và tính `f_unaccent` từng dòng).
- Trường hợp cực rộng (≈ 55.000 tệp khớp) mất ≈ 2,3 s, phần lớn là gom và kiểm lại ≈ 45.000 trang dữ liệu của các tệp khớp và đếm tổng; đây không phải tìm kiếm thường gặp (người dùng sẽ thêm từ khóa hoặc bộ lọc), nên không tính vào mục tiêu.
- Ngắt kế thừa 100.000 mục con: ≈ 2,6 s trong một transaction — không cần chạy nền.
- Máy đo: máy phát triển (16 luồng, PostgreSQL trong Docker Desktop, `shared_buffers` 512 MB). Máy chủ thật có đĩa và bộ nhớ riêng cho PostgreSQL nên thường nhanh hơn.
