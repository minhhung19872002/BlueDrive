using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Users;

public static class UserImportTemplate
{
    public const string Username = "Tên đăng nhập", FullName = "Họ và tên", Email = "Email", Phone = "Điện thoại", OrgCode = "Mã đơn vị",
        Position = "Chức vụ", Roles = "Mã vai trò", Groups = "Nhóm người dùng", Quota = "Dung lượng (GB)", Password = "Mật khẩu";

    public static readonly string[] Headers = [Username, FullName, Email, Phone, OrgCode, Position, Roles, Groups, Quota, Password];
}

public sealed record GetUserImportTemplateQuery : IRequest<ExportedFile>;

public sealed class GetUserImportTemplateHandler(IAppDbContext db, IExcelReader excel) : IRequestHandler<GetUserImportTemplateQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(GetUserImportTemplateQuery q, CancellationToken ct)
    {
        var orgs = await db.Organizations.Where(o => o.IsActive).OrderBy(o => o.Path).Select(o => o.Code + " = " + o.Name).ToListAsync(ct);
        var roles = await db.Roles.OrderBy(r => r.Name).Select(r => r.Code + " = " + r.Name).ToListAsync(ct);
        var sampleOrg = await db.Organizations.Where(o => o.ParentId != null).Select(o => o.Code).FirstOrDefaultAsync(ct) ?? "TTTT";
        var notes = new List<string>
        {
            "HƯỚNG DẪN NHẬP NGƯỜI DÙNG HÀNG LOẠT",
            "- Cột bắt buộc: Tên đăng nhập (3-50 ký tự, chữ không dấu/số/. _ -), Họ và tên.",
            "- Mã vai trò, Nhóm người dùng: nhiều giá trị cách nhau bằng dấu phẩy.",
            "- Để trống Mật khẩu: hệ thống sinh mật khẩu tạm và hiển thị sau khi nhập. Người dùng phải đổi mật khẩu khi đăng nhập lần đầu.",
            "- Để trống Dung lượng: dùng dung lượng mặc định trong Cấu hình hệ thống.",
            "- Nếu có bất kỳ dòng lỗi, toàn bộ tệp sẽ không được nhập; hệ thống trả về danh sách lỗi theo từng dòng.",
            "",
            "DANH SÁCH MÃ ĐƠN VỊ:",
        };
        notes.AddRange(orgs);
        notes.Add("");
        notes.Add("DANH SÁCH MÃ VAI TRÒ:");
        notes.AddRange(roles);
        var bytes = excel.CreateTemplate("Người dùng", UserImportTemplate.Headers,
            [["nguyenvana", "Nguyễn Văn A", "nguyenvana@example.gov.vn", "0912345678", sampleOrg, "Chuyên viên", Perm.Roles.Editor, "", "10", ""]], notes);
        return new ExportedFile(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "mau_nhap_nguoi_dung.xlsx");
    }
}

public sealed record ImportRowError(int Row, string Message);

public sealed record ImportedUser(int Row, string Username, string FullName, string? TemporaryPassword);

public sealed record ImportUsersResult(int Total, int Imported, IReadOnlyList<ImportRowError> Errors, IReadOnlyList<ImportedUser> Users);

public sealed record ImportUsersCommand(Stream File, bool DryRun) : IRequest<ImportUsersResult>;

public sealed class ImportUsersHandler(
    IAppDbContext db, IExcelReader excel, IPasswordService passwords, ISettingsService settings, ISensitiveDataProtector pii,
    IPermissionService perms, IAuditLogger audit) : IRequestHandler<ImportUsersCommand, ImportUsersResult>
{
    public async Task<ImportUsersResult> Handle(ImportUsersCommand cmd, CancellationToken ct)
    {
        var rows = excel.Read(cmd.File);
        if (rows.Count == 0) throw new AppException("Tệp không có dữ liệu. Vui lòng nhập từ dòng thứ 2 theo đúng tệp mẫu.");
        if (rows.Count > 5000) throw new AppException("Mỗi lần nhập tối đa 5.000 người dùng.");

        var orgs = await db.Organizations.ToDictionaryAsync(o => o.Code.ToUpperInvariant(), o => o.Id, ct);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Code.ToLowerInvariant(), r => r.Id, ct);
        var groups = await db.UserGroups.ToListAsync(ct);
        var existingUsernames = (await db.Users.Select(u => u.Username).ToListAsync(ct)).ToHashSet();
        var existingEmails = (await db.Users.Where(u => u.EmailHash != null).Select(u => u.EmailHash!).ToListAsync(ct)).ToHashSet();
        var seenUsernames = new HashSet<string>();
        var errors = new List<ImportRowError>();
        var pending = new List<(ExcelRow Row, User User, string Password, bool Generated, List<Guid> Roles, List<Guid> Groups)>();

        foreach (var row in rows)
        {
            var rowErrors = new List<string>();
            var username = row.Get(UserImportTemplate.Username).ToLowerInvariant();
            var fullName = row.Get(UserImportTemplate.FullName);
            var email = row.Get(UserImportTemplate.Email);
            var phone = row.Get(UserImportTemplate.Phone);
            if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-z0-9._-]{3,50}$")) rowErrors.Add("Tên đăng nhập không hợp lệ (3-50 ký tự, chữ không dấu, số, . _ -).");
            else if (existingUsernames.Contains(username)) rowErrors.Add($"Tên đăng nhập '{username}' đã tồn tại trong hệ thống.");
            else if (!seenUsernames.Add(username)) rowErrors.Add($"Tên đăng nhập '{username}' bị trùng trong tệp.");
            if (string.IsNullOrWhiteSpace(fullName)) rowErrors.Add("Thiếu Họ và tên.");
            if (email.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(email, out _)) rowErrors.Add("Email không đúng định dạng.");
            else if (email.Length > 0 && !existingEmails.Add(pii.Hash(email)!)) rowErrors.Add("Email đã được sử dụng (trong hệ thống hoặc trong tệp).");
            if (phone.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^[0-9+()\s.-]{6,20}$")) rowErrors.Add("Số điện thoại không hợp lệ.");

            Guid? orgId = null;
            var orgCode = row.Get(UserImportTemplate.OrgCode).ToUpperInvariant();
            if (orgCode.Length > 0)
            {
                if (orgs.TryGetValue(orgCode, out var oid)) orgId = oid; else rowErrors.Add($"Mã đơn vị '{orgCode}' không tồn tại.");
            }
            var roleIds = new List<Guid>();
            foreach (var code in Split(row.Get(UserImportTemplate.Roles)))
                if (roles.TryGetValue(code.ToLowerInvariant(), out var rid)) roleIds.Add(rid); else rowErrors.Add($"Mã vai trò '{code}' không tồn tại.");
            var groupIds = new List<Guid>();
            foreach (var name in Split(row.Get(UserImportTemplate.Groups)))
            {
                var g = groups.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
                if (g is null) rowErrors.Add($"Nhóm người dùng '{name}' không tồn tại."); else groupIds.Add(g.Id);
            }
            long quota = 0;
            var quotaText = row.Get(UserImportTemplate.Quota).Replace(',', '.');
            if (quotaText.Length > 0)
            {
                if (double.TryParse(quotaText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb >= 0)
                    quota = (long)(gb * WorkspaceProvisioning.Gb);
                else rowErrors.Add("Dung lượng (GB) phải là số không âm.");
            }
            var password = row.Get(UserImportTemplate.Password);
            var generated = password.Length == 0;
            if (generated) password = PasswordGenerator.Generate();
            else rowErrors.AddRange(await passwords.ValidatePolicyAsync(password, null, ct));

            if (rowErrors.Count > 0)
            {
                errors.AddRange(rowErrors.Select(e => new ImportRowError(row.RowNumber, e)));
                continue;
            }
            pending.Add((row, new User
            {
                Username = username, FullName = fullName, Email = pii.Protect(email), EmailHash = pii.Hash(email), Phone = pii.Protect(phone),
                OrganizationId = orgId, Position = row.Get(UserImportTemplate.Position), QuotaBytes = quota,
            }, password, generated, roleIds, groupIds));
        }

        var imported = new List<ImportedUser>();
        if (errors.Count == 0 && !cmd.DryRun)
        {
            foreach (var p in pending)
            {
                await passwords.SetPasswordAsync(p.User, p.Password, mustChange: true, ct);
                db.Users.Add(p.User);
                foreach (var r in p.Roles.Distinct()) p.User.UserRoles.Add(new UserRole { UserId = p.User.Id, RoleId = r });
                foreach (var g in p.Groups.Distinct()) p.User.GroupMemberships.Add(new UserGroupMember { UserId = p.User.Id, GroupId = g, AddedAt = DateTime.UtcNow });
                await WorkspaceProvisioning.EnsurePersonalWorkspaceAsync(db, settings, p.User, ct);
                imported.Add(new ImportedUser(p.Row.RowNumber, p.User.Username, p.User.FullName, p.Generated ? p.Password : null));
            }
            await db.SaveChangesAsync(ct);
            perms.Invalidate();
            await audit.LogAsync(new AuditEntry(AuditAction.Import, ResourceName: "Nhập người dùng từ Excel", Detail: new { entity = "user", count = imported.Count, usernames = imported.Select(i => i.Username) }), ct);
        }
        return new ImportUsersResult(rows.Count, imported.Count, errors, imported);
    }

    private static IEnumerable<string> Split(string s) => s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
