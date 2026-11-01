using System.Security.Cryptography;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Auth;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Users;

public sealed record RefDto(Guid Id, string Name);

public sealed record UserDto(Guid Id, string Username, string FullName, string? Email, string? Phone, Guid? OrganizationId, string? OrganizationName,
    string? Position, bool IsActive, bool IsLocked, DateTime? LockoutEnd, bool MustChangePassword, DateTime? LastLoginAt, long QuotaBytes, long UsedBytes,
    IReadOnlyList<RefDto> Roles, IReadOnlyList<RefDto> Groups, DateTime CreatedAt)
{
    public string Status => !IsActive ? "inactive" : IsLocked ? "locked" : "active";
}

public sealed record UserLookupDto(Guid Id, string Username, string FullName, string? OrganizationName, Guid? AvatarFileId);

public static class UserStatusNames
{
    public static string Of(UserDto u) => u.Status switch { "inactive" => "Ngừng hoạt động", "locked" => "Đã khóa", _ => "Hoạt động" };
}

// ---------- Queries ----------
public sealed record ListUsersQuery(Guid? OrganizationId, bool IncludeSubOrganizations, Guid? RoleId, Guid? GroupId, string? Status)
    : PageQuery, IRequest<PagedResult<UserDto>>;

public static class UserQueries
{
    public static async Task<IQueryable<User>> FilterAsync(IAppDbContext db, ListUsersQuery q, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (q.OrganizationId.HasValue)
        {
            if (q.IncludeSubOrganizations)
            {
                var path = await db.Organizations.Where(o => o.Id == q.OrganizationId).Select(o => o.Path).FirstOrDefaultAsync(ct) ?? "/none/";
                query = query.Where(u => u.Organization != null && u.Organization.Path.StartsWith(path));
            }
            else query = query.Where(u => u.OrganizationId == q.OrganizationId);
        }
        if (q.RoleId.HasValue) query = query.Where(u => u.UserRoles.Any(r => r.RoleId == q.RoleId));
        if (q.GroupId.HasValue) query = query.Where(u => u.GroupMemberships.Any(g => g.GroupId == q.GroupId));
        query = q.Status switch
        {
            "active" => query.Where(u => u.IsActive && !u.IsLocked),
            "locked" => query.Where(u => u.IsLocked),
            "inactive" => query.Where(u => !u.IsActive),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(u => u.Username.Contains(term) || DbFn.Unaccent(u.FullName).Contains(term) || (u.Position != null && DbFn.Unaccent(u.Position).Contains(term)));
        }
        return q.Sort switch
        {
            "username" => query.OrderBy(u => u.Username),
            "-username" => query.OrderByDescending(u => u.Username),
            "-lastLoginAt" => query.OrderByDescending(u => u.LastLoginAt),
            "-usedBytes" => query.OrderByDescending(u => u.UsedBytes),
            "-createdAt" => query.OrderByDescending(u => u.CreatedAt),
            _ => query.OrderBy(u => u.FullName),
        };
    }

    public static IQueryable<UserDto> Project(IQueryable<User> q) => q.Select(u => new UserDto(u.Id, u.Username, u.FullName, u.Email, u.Phone,
        u.OrganizationId, u.Organization != null ? u.Organization.Name : null, u.Position, u.IsActive, u.IsLocked, u.LockoutEnd, u.MustChangePassword,
        u.LastLoginAt, u.QuotaBytes, u.UsedBytes,
        u.UserRoles.Select(r => new RefDto(r.RoleId, r.Role!.Name)).ToList(),
        u.GroupMemberships.Select(g => new RefDto(g.GroupId, g.Group!.Name)).ToList(), u.CreatedAt));

    public static UserDto Decrypt(UserDto u, ISensitiveDataProtector pii) => u with { Email = pii.Unprotect(u.Email), Phone = pii.Unprotect(u.Phone) };
}

public sealed class ListUsersHandler(IAppDbContext db, ISensitiveDataProtector pii) : IRequestHandler<ListUsersQuery, PagedResult<UserDto>>
{
    public async Task<PagedResult<UserDto>> Handle(ListUsersQuery q, CancellationToken ct)
    {
        var page = await UserQueries.Project(await UserQueries.FilterAsync(db, q, ct)).ToPagedAsync(q, ct);
        return page with { Items = page.Items.Select(u => UserQueries.Decrypt(u, pii)).ToList() };
    }
}

public sealed record GetUserQuery(Guid Id) : IRequest<UserDto>;

public sealed class GetUserHandler(IAppDbContext db, ISensitiveDataProtector pii) : IRequestHandler<GetUserQuery, UserDto>
{
    public async Task<UserDto> Handle(GetUserQuery q, CancellationToken ct)
        => UserQueries.Decrypt(await UserQueries.Project(db.Users.AsNoTracking().Where(u => u.Id == q.Id)).FirstOrDefaultAsync(ct)
                               ?? throw new NotFoundException("Không tìm thấy người dùng."), pii);
}

/// <summary>Lightweight directory search for pickers (share dialog, @mentions). Available to every signed-in user.</summary>
public sealed record LookupUsersQuery(string? Q, Guid? OrganizationId, int Take = 20) : IRequest<IReadOnlyList<UserLookupDto>>;

public sealed class LookupUsersHandler(IAppDbContext db) : IRequestHandler<LookupUsersQuery, IReadOnlyList<UserLookupDto>>
{
    public async Task<IReadOnlyList<UserLookupDto>> Handle(LookupUsersQuery q, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().Where(u => u.IsActive);
        if (q.OrganizationId.HasValue) query = query.Where(u => u.OrganizationId == q.OrganizationId);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(u => u.Username.Contains(term) || DbFn.Unaccent(u.FullName).Contains(term));
        }
        return await query.OrderBy(u => u.FullName).Take(Math.Clamp(q.Take, 1, 50))
            .Select(u => new UserLookupDto(u.Id, u.Username, u.FullName, u.Organization != null ? u.Organization.Name : null, u.AvatarFileId)).ToListAsync(ct);
    }
}

// ---------- Export (#6) ----------
public sealed record ExportUsersQuery(ListUsersQuery Filter, string Format, string ExportedBy) : IRequest<ExportedFile>;

public sealed class ExportUsersHandler(IAppDbContext db, ITableExporter exporter, ISensitiveDataProtector pii, IAuditLogger audit) : IRequestHandler<ExportUsersQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(ExportUsersQuery q, CancellationToken ct)
    {
        var rows = (await UserQueries.Project(await UserQueries.FilterAsync(db, q.Filter, ct)).Take(50_000).ToListAsync(ct)).Select(u => UserQueries.Decrypt(u, pii)).ToList();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.Filter.Q)) filters.Add($"Từ khóa \"{q.Filter.Q}\"");
        if (q.Filter.OrganizationId.HasValue) filters.Add($"Đơn vị: {await db.Organizations.Where(o => o.Id == q.Filter.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync(ct)}");
        if (q.Filter.RoleId.HasValue) filters.Add($"Vai trò: {await db.Roles.Where(o => o.Id == q.Filter.RoleId).Select(o => o.Name).FirstOrDefaultAsync(ct)}");
        if (q.Filter.GroupId.HasValue) filters.Add($"Nhóm: {await db.UserGroups.Where(o => o.Id == q.Filter.GroupId).Select(o => o.Name).FirstOrDefaultAsync(ct)}");
        if (!string.IsNullOrEmpty(q.Filter.Status)) filters.Add($"Trạng thái: {q.Filter.Status switch { "locked" => "Đã khóa", "inactive" => "Ngừng hoạt động", _ => "Hoạt động" }}");
        var file = await exporter.ExportAsync(new TableExport("Danh sách người dùng",
            [new("Tên đăng nhập", 1.4f), new("Họ và tên", 2), new("Email", 2), new("Điện thoại", 1.2f), new("Đơn vị", 2.2f), new("Chức vụ", 1.4f),
             new("Vai trò", 1.8f), new("Dung lượng (đã dùng/cấp)", 1.6f), new("Trạng thái", 1.1f), new("Đăng nhập gần nhất", 1.4f)],
            rows.Select(u => (IReadOnlyList<string?>)[u.Username, u.FullName, u.Email, u.Phone, u.OrganizationName, u.Position,
                string.Join(", ", u.Roles.Select(r => r.Name)), $"{TextUtil.FormatBytes(u.UsedBytes)} / {TextUtil.FormatBytes(u.QuotaBytes)}",
                UserStatusNames.Of(u), u.LastLoginAt is { } l ? l.AddHours(7).ToString("dd/MM/yyyy HH:mm") : ""]).ToList(),
            filters.Count > 0 ? string.Join("; ", filters) : null, q.ExportedBy), q.Format, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: file.FileName, Detail: new { report = "users", rows = rows.Count }), ct);
        return file;
    }
}

// ---------- Create / update (#4, #5) ----------
public sealed record SaveUserCommand(Guid? Id, string Username, string FullName, string? Email, string? Phone, Guid? OrganizationId, string? Position,
    double? QuotaGb, bool IsActive, IReadOnlyList<Guid>? RoleIds, IReadOnlyList<Guid>? GroupIds, string? Password) : IRequest<SaveUserResult>;

public sealed record SaveUserResult(Guid Id, string? TemporaryPassword);

public sealed class SaveUserValidator : AbstractValidator<SaveUserCommand>
{
    public SaveUserValidator()
    {
        RuleFor(x => x.Username).NotEmpty().WithMessage("Vui lòng nhập tên đăng nhập.")
            .Matches("^[a-zA-Z0-9._-]{3,50}$").WithMessage("Tên đăng nhập dài 3-50 ký tự, chỉ gồm chữ không dấu, số và . _ -");
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Vui lòng nhập họ và tên.").MaximumLength(255);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("Email không đúng định dạng.");
        RuleFor(x => x.Phone).Matches(@"^[0-9+()\s.-]{6,20}$").When(x => !string.IsNullOrWhiteSpace(x.Phone)).WithMessage("Số điện thoại không hợp lệ.");
        RuleFor(x => x.QuotaGb).InclusiveBetween(0, 100_000).When(x => x.QuotaGb.HasValue).WithMessage("Dung lượng cấp phát không hợp lệ.");
        RuleFor(x => x.Position).MaximumLength(255);
    }
}

public sealed class SaveUserHandler(
    IAppDbContext db, IPasswordService passwords, ISettingsService settings, ISensitiveDataProtector pii, IPermissionService perms,
    SessionRevoker revoker, IAuditLogger audit) : IRequestHandler<SaveUserCommand, SaveUserResult>
{
    public async Task<SaveUserResult> Handle(SaveUserCommand cmd, CancellationToken ct)
    {
        var username = cmd.Username.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Username == username && u.Id != cmd.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["username"] = [$"Tên đăng nhập '{username}' đã tồn tại."] });
        var emailHash = pii.Hash(cmd.Email);
        if (emailHash is not null && await db.Users.AnyAsync(u => u.EmailHash == emailHash && u.Id != cmd.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["email"] = ["Email đã được sử dụng cho tài khoản khác."] });
        if (cmd.OrganizationId.HasValue && !await db.Organizations.AnyAsync(o => o.Id == cmd.OrganizationId, ct))
            throw new NotFoundException("Không tìm thấy đơn vị/phòng ban.");

        var isNew = cmd.Id is null;
        var user = isNew ? new User() : await db.Users.Include(u => u.UserRoles).Include(u => u.GroupMemberships).FirstOrDefaultAsync(u => u.Id == cmd.Id, ct)
                                        ?? throw new NotFoundException("Không tìm thấy người dùng.");
        if (!isNew && user.Username == "admin" && username != "admin") throw new AppException("Không được đổi tên đăng nhập của tài khoản quản trị mặc định.");
        var wasActive = user.IsActive;
        user.Username = username;
        user.FullName = cmd.FullName.Trim();
        user.Email = pii.Protect(cmd.Email);
        user.EmailHash = emailHash;
        user.Phone = pii.Protect(cmd.Phone);
        user.OrganizationId = cmd.OrganizationId;
        user.Position = cmd.Position?.Trim();
        user.IsActive = cmd.IsActive;
        if (cmd.QuotaGb.HasValue) user.QuotaBytes = (long)(cmd.QuotaGb.Value * WorkspaceProvisioning.Gb);

        string? tempPassword = null;
        if (isNew)
        {
            var pwd = string.IsNullOrWhiteSpace(cmd.Password) ? tempPassword = PasswordGenerator.Generate() : cmd.Password;
            var errors = await passwords.ValidatePolicyAsync(pwd, null, ct);
            if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { ["password"] = [.. errors] });
            await passwords.SetPasswordAsync(user, pwd, mustChange: true, ct);
            db.Users.Add(user);
        }
        if (cmd.RoleIds is not null) await UserAssignments.SetRolesAsync(db, user, cmd.RoleIds, ct);
        if (cmd.GroupIds is not null) await UserAssignments.SetGroupsAsync(db, user, cmd.GroupIds, ct);

        var ws = await WorkspaceProvisioning.EnsurePersonalWorkspaceAsync(db, settings, user, ct);
        ws.QuotaBytes = user.QuotaBytes;
        ws.OrganizationId = user.OrganizationId;
        ws.Name = $"Tài liệu của {user.FullName}";
        await db.SaveChangesAsync(ct);
        perms.Invalidate(user.Id);
        if (!isNew && wasActive && !user.IsActive) await revoker.RevokeAllForUserAsync(user.Id, null, ct);

        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceName: $"Người dùng {user.Username}",
            Detail: new { entity = "user", id = user.Id, user.FullName, user.OrganizationId, user.IsActive, quotaBytes = user.QuotaBytes, roles = cmd.RoleIds, groups = cmd.GroupIds }), ct);
        return new SaveUserResult(user.Id, tempPassword);
    }
}

public static class PasswordGenerator
{
    /// <summary>12 chars satisfying the default policy (upper, lower, digit, special).</summary>
    public static string Generate()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789", special = "@#$%&*!?";
        var all = upper + lower + digits + special;
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digits), Pick(special) };
        while (chars.Count < 12) chars.Add(Pick(all));
        return new string([.. chars.OrderBy(_ => RandomNumberGenerator.GetInt32(1000))]);
        static char Pick(string s) => s[RandomNumberGenerator.GetInt32(s.Length)];
    }
}

public static class UserAssignments
{
    public static async Task SetRolesAsync(IAppDbContext db, User user, IReadOnlyList<Guid> roleIds, CancellationToken ct)
    {
        var valid = await db.Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        if (user.Username == "admin")
        {
            var adminRole = await db.Roles.Where(r => r.Code == Perm.Roles.Admin).Select(r => r.Id).FirstAsync(ct);
            if (!valid.Contains(adminRole)) throw new AppException("Không được gỡ vai trò Quản trị hệ thống khỏi tài khoản admin mặc định.");
        }
        user.UserRoles.RemoveAll(r => !valid.Contains(r.RoleId));
        foreach (var id in valid.Where(id => user.UserRoles.All(r => r.RoleId != id))) user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = id });
    }

    public static async Task SetGroupsAsync(IAppDbContext db, User user, IReadOnlyList<Guid> groupIds, CancellationToken ct)
    {
        var valid = await db.UserGroups.Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
        user.GroupMemberships.RemoveAll(m => !valid.Contains(m.GroupId));
        foreach (var id in valid.Where(id => user.GroupMemberships.All(m => m.GroupId != id)))
            user.GroupMemberships.Add(new UserGroupMember { UserId = user.Id, GroupId = id, AddedAt = DateTime.UtcNow });
    }
}

/// <summary>#5 bulk assignment: set organization and/or add/replace roles for many users at once.</summary>
public sealed record BulkAssignUsersCommand(IReadOnlyList<Guid> UserIds, Guid? OrganizationId, IReadOnlyList<Guid>? RoleIds, bool ReplaceRoles) : IRequest<int>;

public sealed class BulkAssignUsersValidator : AbstractValidator<BulkAssignUsersCommand>
{
    public BulkAssignUsersValidator()
    {
        RuleFor(x => x.UserIds).NotEmpty().WithMessage("Vui lòng chọn ít nhất một người dùng.");
        RuleFor(x => x).Must(x => x.OrganizationId.HasValue || x.RoleIds is { Count: > 0 }).WithMessage("Vui lòng chọn đơn vị hoặc vai trò cần gán.");
    }
}

public sealed class BulkAssignUsersHandler(IAppDbContext db, IPermissionService perms, IAuditLogger audit) : IRequestHandler<BulkAssignUsersCommand, int>
{
    public async Task<int> Handle(BulkAssignUsersCommand cmd, CancellationToken ct)
    {
        if (cmd.OrganizationId.HasValue && !await db.Organizations.AnyAsync(o => o.Id == cmd.OrganizationId, ct)) throw new NotFoundException("Không tìm thấy đơn vị/phòng ban.");
        var users = await db.Users.Include(u => u.UserRoles).Where(u => cmd.UserIds.Contains(u.Id)).ToListAsync(ct);
        foreach (var u in users)
        {
            if (cmd.OrganizationId.HasValue) u.OrganizationId = cmd.OrganizationId;
            if (cmd.RoleIds is { Count: > 0 })
            {
                var ids = cmd.ReplaceRoles ? cmd.RoleIds : u.UserRoles.Select(r => r.RoleId).Union(cmd.RoleIds).ToList();
                await UserAssignments.SetRolesAsync(db, u, ids, ct);
            }
        }
        if (cmd.OrganizationId.HasValue)
            await db.Workspaces.Where(w => cmd.UserIds.Contains(w.OwnerId) && w.Type == WorkspaceType.Personal)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.OrganizationId, cmd.OrganizationId), ct);
        await db.SaveChangesAsync(ct);
        perms.Invalidate();
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, ResourceName: $"Gán hàng loạt {users.Count} người dùng",
            Detail: new { entity = "user", userIds = users.Select(u => u.Id), cmd.OrganizationId, cmd.RoleIds, cmd.ReplaceRoles }), ct);
        return users.Count;
    }
}

// ---------- Lock / unlock / reset password / delete ----------
public sealed record SetUserLockCommand(Guid Id, bool Locked) : IRequest<Unit>;

public sealed class SetUserLockHandler(IAppDbContext db, ICurrentUser me, SessionRevoker revoker, IAuditLogger audit) : IRequestHandler<SetUserLockCommand, Unit>
{
    public async Task<Unit> Handle(SetUserLockCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        if (cmd.Locked && user.Id == me.UserId) throw new AppException("Bạn không thể tự khóa tài khoản của mình.");
        user.IsLocked = cmd.Locked;
        if (!cmd.Locked) { user.LockoutEnd = null; user.FailedLoginCount = 0; }
        await db.SaveChangesAsync(ct);
        if (cmd.Locked) await revoker.RevokeAllForUserAsync(user.Id, null, ct);
        await audit.LogAsync(new AuditEntry(cmd.Locked ? AuditAction.UserLock : AuditAction.UserUnlock, ResourceName: $"Người dùng {user.Username}", Detail: new { userId = user.Id }), ct);
        return Unit.Value;
    }
}

public sealed record AdminResetPasswordCommand(Guid Id, string? NewPassword) : IRequest<SaveUserResult>;

public sealed class AdminResetPasswordHandler(IAppDbContext db, IPasswordService passwords, SessionRevoker revoker, IAuditLogger audit)
    : IRequestHandler<AdminResetPasswordCommand, SaveUserResult>
{
    public async Task<SaveUserResult> Handle(AdminResetPasswordCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        string? temp = null;
        var pwd = string.IsNullOrWhiteSpace(cmd.NewPassword) ? temp = PasswordGenerator.Generate() : cmd.NewPassword;
        var errors = await passwords.ValidatePolicyAsync(pwd, user.Id, ct);
        if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { ["newPassword"] = [.. errors] });
        await passwords.SetPasswordAsync(user, pwd, mustChange: true, ct);
        await db.SaveChangesAsync(ct);
        await revoker.RevokeAllForUserAsync(user.Id, null, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PasswordReset, ResourceName: $"Người dùng {user.Username}", Detail: new { step = "admin_reset", userId = user.Id }), ct);
        return new SaveUserResult(user.Id, temp);
    }
}

public sealed record DeleteUserCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteUserHandler(IAppDbContext db, ICurrentUser me, IAuditLogger audit) : IRequestHandler<DeleteUserCommand, Unit>
{
    public async Task<Unit> Handle(DeleteUserCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        if (user.Username == "admin") throw new AppException("Không được xóa tài khoản quản trị mặc định.");
        if (user.Id == me.UserId) throw new AppException("Bạn không thể tự xóa tài khoản của mình.");
        var personal = await db.Workspaces.Where(w => w.OwnerId == user.Id && w.Type == WorkspaceType.Personal).Select(w => w.Id).ToListAsync(ct);
        var hasFiles = await db.Files.AnyAsync(f => personal.Contains(f.WorkspaceId) || f.CreatedBy == user.Id, ct);
        var ownsGroups = await db.Workspaces.AnyAsync(w => w.OwnerId == user.Id && w.Type != WorkspaceType.Personal, ct);
        if (hasFiles || ownsGroups)
            throw new ConflictException("Không thể xóa người dùng đã có tư liệu hoặc đang làm trưởng nhóm tài nguyên. Hãy chuyển trạng thái \"Ngừng hoạt động\" để giữ lại dữ liệu và lịch sử.");

        await db.UserRoles.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.UserGroupMembers.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.WorkspaceMembers.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.UserSessions.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.PasswordHistories.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.PasswordResetTokens.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.UserFavorites.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.RecentItems.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.Folders.Where(f => personal.Contains(f.WorkspaceId)).ExecuteDeleteAsync(ct);
        await db.Workspaces.Where(w => personal.Contains(w.Id)).ExecuteDeleteAsync(ct);
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: $"Người dùng {user.Username}", Detail: new { entity = "user", id = user.Id, user.FullName }), ct);
        return Unit.Value;
    }
}
