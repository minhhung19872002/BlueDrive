---
name: bluedrive-hsmt-feature
description: Use this skill when adding or changing a BlueDrive HSMT function (chức năng HSMT #1–#84, mục 3.2) or any new API in the BlueDrive repo — MediatR command/query + FluentValidation + ACL (IResourceAuthorizationService) + audit (IAuditLogger) + HasPermission(Perm.X) controller + web/mobile screen + integration test + row in docs/traceability.md. Triggers: "thêm chức năng", "HSMT #", "traceability", "Perm.", "ResourceAuthorization", "AuditAction".
---

# BlueDrive — thêm/sửa một chức năng HSMT

## Khi nào dùng
- Thêm một chức năng mới hoặc mở rộng chức năng HSMT đã có (#1–#84, không có #25).
- Thêm endpoint `/api/v1/...` mới cần phân quyền + nhật ký.

## Quy trình (đủ cả 8 bước, thiếu bước nào là chưa xong)
1. **Đọc yêu cầu gốc** trong `docs/hsmt/yeu-cau-ky-thuat.txt` và dòng tương ứng trong `docs/traceability.md`.
2. **Quyền chức năng**: dùng/khai báo hằng trong `src/BlueDrive.Application/Security/Permissions.cs` (`function.action`, action ∈ view/upload/edit/delete/share/approve/admin). Quyền mới phải được seed (`Infrastructure/Persistence/Seed/ReferenceDataSeeder.cs`) và xuất hiện ở ma trận `/admin/roles`.
3. **Application** (`src/BlueDrive.Application/Features/<Module>/`): `record XxxCommand : IRequest<T>` + `XxxValidator : AbstractValidator<XxxCommand>` (thông báo tiếng Việt) + handler. Mẫu chuẩn: `RenameFileCommand` trong `Features/Storage/ItemOperations.cs`.
   - Tài nguyên (file/thư mục/không gian): gọi `auth.EnsureAsync(uid, ResourceType.X, id, AccessLevel.Y, ct)` **trước** khi đọc/sửa.
   - Lỗi nghiệp vụ: `throw new AppException("…tiếng Việt…")` → ProblemDetails.
   - Ghi nhật ký: `audit.LogAsync(new AuditEntry(AuditAction.X, ResourceType.Y, id, name, workspaceId, details), ct)` hoặc cho request implement `IAuditableRequest`.
   - Danh sách: trả `{ items, total, page, pageSize }`; `OrderBy` trước `Select` sang DTO; tìm không dấu dùng `DbFn.Unaccent`.
4. **Migration** nếu đổi schema: `dotnet ef migrations add <Name> -p src/BlueDrive.Infrastructure -s src/BlueDrive.Infrastructure -o Persistence/Migrations`. Enum mới lưu snake_case string.
5. **API** (`src/BlueDrive.Api/Controllers/`): action gắn `HasPermission(Perm.X)`, gọi `Mediator.Send`. Tham số complex đặt tên `query` (không đặt `q`).
6. **UI**: web `web/src/...` (AntD, chuỗi trong `web/src/locales/vi*.json`), mobile `mobile/src/...` nếu thuộc #71–#84 (chuỗi trong `mobile/src/i18n/vi.ts`). 100% tiếng Việt.
7. **Test**: integration test trong `tests/BlueDrive.IntegrationTests/<Module>Tests.cs` (dùng `ApiFactory`) kiểm cả trường hợp **bị từ chối quyền** và **có bản ghi audit**; unit test nếu là logic thuần.
8. **Truy vết**: cập nhật dòng trong `docs/traceability.md` (API | Màn hình | Permission | Test). Cập nhật `docs/huong-dan-su-dung.md`/`huong-dan-quan-tri.md` nếu người dùng thấy thay đổi.

Kết thúc: `powershell -File scripts/ci.ps1 -SkipDocker` xanh → commit `phase-N: ...` → push `origin main`.

## Lưu ý / Pitfalls
- Không TODO/stub, không hard-code secret (chỉ đọc `deploy/.env`/cấu hình).
- `DateTime` không Kind = UTC; lọc theo ngày dùng giờ VN (UTC+7, `VietnamTime`).
- Rate limit chỉ cho login/forgot/reset/refresh — không thêm vào endpoint thường dùng.
- Đổi tên tệp phải giữ phần mở rộng, chặn đuôi nguy hiểm (`IFileTypeInspector.IsBlockedExtension`).
- MediatR giữ 12.5.0, FluentAssertions 7.2.0 (bản sau là thương mại).
