using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Workspaces;

public sealed record WorkspaceDto(Guid Id, string Name, string? Description, WorkspaceType Type, Guid? OrganizationId, string? OrganizationName,
    Guid OwnerId, string OwnerName, long QuotaBytes, long UsedBytes, bool IsLocked, WorkspaceMemberRole? MyRole, int MemberCount, DateTime CreatedAt);

public sealed record WorkspaceMemberDto(Guid UserId, string Username, string FullName, string? OrganizationName, WorkspaceMemberRole MemberRole, DateTime AddedAt);

public static class WorkspaceQueries
{
    public static IQueryable<WorkspaceDto> Project(IAppDbContext db, IQueryable<Workspace> q, Guid userId) => q.Select(w => new WorkspaceDto(
        w.Id, w.Name, w.Description, w.Type, w.OrganizationId, w.Organization != null ? w.Organization.Name : null, w.OwnerId, w.Owner!.FullName,
        w.QuotaBytes, w.UsedBytes, w.IsLocked, w.Members.Where(m => m.UserId == userId).Select(m => (WorkspaceMemberRole?)m.MemberRole).FirstOrDefault(),
        w.Members.Count, w.CreatedAt));

    /// <summary>Workspaces the user belongs to or has been granted at workspace level (user/group/org/role ACL).</summary>
    public static async Task<IQueryable<Workspace>> AccessibleAsync(IAppDbContext db, IResourceAuthorizationService auth, Guid userId, CancellationToken ct)
    {
        var p = await auth.GetPrincipalsAsync(userId, ct);
        var principalIds = p.GroupIds.Concat(p.OrganizationIds).Concat(p.RoleIds).Append(userId).ToList();
        return db.Workspaces.AsNoTracking().Where(w => w.OwnerId == userId || w.Members.Any(m => m.UserId == userId) ||
            db.ResourcePermissions.Any(a => a.ResourceType == ResourceType.Workspace && a.ResourceId == w.Id && principalIds.Contains(a.PrincipalId)));
    }
}

/// <summary>
/// Who may change a group workspace (information, members): the owner, <c>workspace.admin</c>, or a manager of the
/// workspace (member role Manager, or a Manage grant on it) whose role holds <c>workspace.edit</c> ("Nhóm tài nguyên - Chỉnh sửa").
/// </summary>
public static class WorkspaceManagement
{
    public static async Task EnsureCanChangeAsync(IAppDbContext db, IResourceAuthorizationService auth, IPermissionService perms, Guid userId, Workspace ws, CancellationToken ct)
    {
        if (ws.OwnerId == userId || await perms.HasAsync(userId, Perm.WorkspaceAdmin, ct)) return;
        var manager = await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == ws.Id && m.UserId == userId
                                                              && (m.MemberRole == WorkspaceMemberRole.Manager || m.MemberRole == WorkspaceMemberRole.Owner), ct)
                      || (await auth.GetAccessAsync(userId, ResourceType.Workspace, ws.Id, ct)).Level >= AccessLevel.Manage;
        if (!manager) throw new ForbiddenException("Chỉ trưởng nhóm, quản lý nhóm hoặc quản trị viên được thay đổi nhóm tài nguyên.");
        if (!await perms.HasAsync(userId, Perm.WorkspaceEdit, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền chỉnh sửa nhóm tài nguyên.");
    }

    public static void EnsureNotLocked(Workspace ws)
    {
        if (ws.IsLocked) throw new AppException("Nhóm tài nguyên đang bị khóa, chỉ cho phép xem.", 423);
    }
}

public sealed record ListMyWorkspacesQuery(WorkspaceType? Type) : IRequest<IReadOnlyList<WorkspaceDto>>;

public sealed class ListMyWorkspacesHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms)
    : IRequestHandler<ListMyWorkspacesQuery, IReadOnlyList<WorkspaceDto>>
{
    public async Task<IReadOnlyList<WorkspaceDto>> Handle(ListMyWorkspacesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var query = await WorkspaceQueries.AccessibleAsync(db, auth, uid, ct);
        // Group / organization workspaces need "Nhóm tài nguyên - Xem"; the personal space is always listed.
        var seesGroups = await perms.HasAsync(uid, Perm.WorkspaceView, ct);
        query = query.Where(w => w.Type == WorkspaceType.Personal ? w.OwnerId == uid : seesGroups);
        if (q.Type.HasValue) query = query.Where(w => w.Type == q.Type);
        return await WorkspaceQueries.Project(db, query.OrderBy(w => w.Type).ThenBy(w => w.Name), uid).ToListAsync(ct);
    }
}

/// <summary>Admin screen "Cấu hình nhóm tài nguyên": every group/organization workspace.</summary>
public sealed record ListAllWorkspacesQuery(WorkspaceType? Type, Guid? OrganizationId, bool? IsLocked) : PageQuery, IRequest<PagedResult<WorkspaceDto>>;

public sealed class ListAllWorkspacesHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListAllWorkspacesQuery, PagedResult<WorkspaceDto>>
{
    public Task<PagedResult<WorkspaceDto>> Handle(ListAllWorkspacesQuery q, CancellationToken ct)
    {
        var query = db.Workspaces.AsNoTracking().Where(w => q.Type.HasValue ? w.Type == q.Type : w.Type != WorkspaceType.Personal);
        if (q.OrganizationId.HasValue) query = query.Where(w => w.OrganizationId == q.OrganizationId);
        if (q.IsLocked.HasValue) query = query.Where(w => w.IsLocked == q.IsLocked);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(w => DbFn.Unaccent(w.Name).Contains(term) || DbFn.Unaccent(w.Owner!.FullName).Contains(term));
        }
        query = q.Sort switch { "-usedBytes" => query.OrderByDescending(w => w.UsedBytes), "-createdAt" => query.OrderByDescending(w => w.CreatedAt), _ => query.OrderBy(w => w.Name) };
        return WorkspaceQueries.Project(db, query, me.RequireUserId()).ToPagedAsync(q, ct);
    }
}

public sealed record GetWorkspaceQuery(Guid Id) : IRequest<WorkspaceDto>;

public sealed class GetWorkspaceHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<GetWorkspaceQuery, WorkspaceDto>
{
    public async Task<WorkspaceDto> Handle(GetWorkspaceQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await auth.EnsureAsync(uid, ResourceType.Workspace, q.Id, AccessLevel.View, ct);
        return await WorkspaceQueries.Project(db, db.Workspaces.AsNoTracking().Where(w => w.Id == q.Id), uid).FirstAsync(ct);
    }
}

public sealed record SaveWorkspaceCommand(Guid? Id, string Name, string? Description, WorkspaceType Type, Guid? OrganizationId, double? QuotaGb,
    IReadOnlyList<Guid>? MemberIds, WorkspaceMemberRole MemberRole = WorkspaceMemberRole.Editor) : IRequest<Guid>;

public sealed class SaveWorkspaceValidator : AbstractValidator<SaveWorkspaceCommand>
{
    public SaveWorkspaceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên nhóm tài nguyên.").MaximumLength(255);
        RuleFor(x => x.Type).Must(t => t != WorkspaceType.Personal).WithMessage("Không thể tạo thêm không gian cá nhân.");
        RuleFor(x => x.OrganizationId).NotNull().When(x => x.Type == WorkspaceType.Organization).WithMessage("Nhóm tài nguyên của đơn vị phải chọn đơn vị.");
        RuleFor(x => x.QuotaGb).InclusiveBetween(0, 100_000).When(x => x.QuotaGb.HasValue).WithMessage("Dung lượng không hợp lệ.");
        RuleFor(x => x.MemberRole).Must(r => r != WorkspaceMemberRole.Owner).WithMessage("Không thể thêm thành viên với vai trò trưởng nhóm.");
    }
}

public sealed class SaveWorkspaceHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, ISettingsService settings,
    IResourceAuthorizationService auth, IAuditLogger audit, TimeProvider clock) : IRequestHandler<SaveWorkspaceCommand, Guid>
{
    public async Task<Guid> Handle(SaveWorkspaceCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var isAdmin = await perms.HasAsync(uid, Perm.WorkspaceAdmin, ct);
        var isNew = cmd.Id is null;
        Workspace ws;
        if (isNew)
        {
            if (!await perms.HasAsync(uid, Perm.WorkspaceCreate, ct)) throw new ForbiddenException("Bạn không có quyền tạo nhóm tài nguyên.");
            var defaultGb = await settings.GetAsync(SettingKeys.QuotaGroupDefaultGb, 50, ct);
            ws = new Workspace { OwnerId = uid, Type = cmd.Type, QuotaBytes = (long)((isAdmin && cmd.QuotaGb.HasValue ? cmd.QuotaGb.Value : defaultGb) * WorkspaceProvisioning.Gb) };
            ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = uid, MemberRole = WorkspaceMemberRole.Owner, AddedAt = clock.GetUtcNow().UtcDateTime });
            db.Workspaces.Add(ws);
        }
        else
        {
            ws = await db.Workspaces.Include(w => w.Members).FirstOrDefaultAsync(w => w.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
            if (ws.Type == WorkspaceType.Personal) throw new AppException("Không gian cá nhân không chỉnh sửa tại đây.");
            await WorkspaceManagement.EnsureCanChangeAsync(db, auth, perms, uid, ws, ct);
            WorkspaceManagement.EnsureNotLocked(ws);
            if (cmd.QuotaGb.HasValue && isAdmin) ws.QuotaBytes = (long)(cmd.QuotaGb.Value * WorkspaceProvisioning.Gb);
            ws.Type = cmd.Type;
        }
        if (cmd.OrganizationId.HasValue && !await db.Organizations.AnyAsync(o => o.Id == cmd.OrganizationId, ct)) throw new NotFoundException("Không tìm thấy đơn vị.");
        ws.Name = cmd.Name.Trim();
        ws.Description = cmd.Description?.Trim();
        ws.OrganizationId = cmd.OrganizationId;
        if (cmd.MemberIds is { Count: > 0 })
        {
            var valid = await db.Users.Where(u => cmd.MemberIds.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
            foreach (var id in valid.Where(id => ws.Members.All(m => m.UserId != id)))
                ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = id, MemberRole = cmd.MemberRole, AddedAt = clock.GetUtcNow().UtcDateTime });
        }
        // Organization workspaces are readable by everyone in the unit (#55) unless an admin changes the grant.
        if (isNew && cmd.Type == WorkspaceType.Organization && cmd.OrganizationId.HasValue)
            db.ResourcePermissions.Add(new ResourcePermission { ResourceType = ResourceType.Workspace, ResourceId = ws.Id, PrincipalType = PrincipalType.Organization, PrincipalId = cmd.OrganizationId.Value, AccessLevel = AccessLevel.Download });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceType.Workspace, ws.Id, ws.Name, ws.Id,
            new { ws.Type, ws.OrganizationId, quotaBytes = ws.QuotaBytes, members = cmd.MemberIds }), ct);
        return ws.Id;
    }
}

public sealed record DeleteWorkspaceCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteWorkspaceHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, IAuditLogger audit) : IRequestHandler<DeleteWorkspaceCommand, Unit>
{
    public async Task<Unit> Handle(DeleteWorkspaceCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        if (ws.Type == WorkspaceType.Personal) throw new AppException("Không thể xóa không gian cá nhân.");
        if (ws.OwnerId != uid && !await perms.HasAsync(uid, Perm.WorkspaceDelete, ct)) throw new ForbiddenException("Chỉ trưởng nhóm hoặc quản trị viên được xóa nhóm tài nguyên.");
        if (await db.Files.AnyAsync(f => f.WorkspaceId == ws.Id, ct))
            throw new ConflictException("Nhóm tài nguyên còn tư liệu (kể cả trong thùng rác). Hãy xóa vĩnh viễn hoặc di chuyển tư liệu trước khi xóa nhóm.");
        await db.ResourcePermissions.Where(p => p.ResourceType == ResourceType.Workspace && p.ResourceId == ws.Id).ExecuteDeleteAsync(ct);
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == ws.Id).ExecuteDeleteAsync(ct);
        await db.Folders.Where(f => f.WorkspaceId == ws.Id).ExecuteDeleteAsync(ct);
        db.Workspaces.Remove(ws);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceType.Workspace, ws.Id, ws.Name, ws.Id), ct);
        return Unit.Value;
    }
}

/// <summary>#58: lock = read-only workspace.</summary>
public sealed record SetWorkspaceLockCommand(Guid Id, bool Locked) : IRequest<Unit>;

public sealed class SetWorkspaceLockHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit)
    : IRequestHandler<SetWorkspaceLockCommand, Unit>
{
    public async Task<Unit> Handle(SetWorkspaceLockCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        await WorkspaceManagement.EnsureCanChangeAsync(db, auth, perms, uid, ws, ct);
        ws.IsLocked = cmd.Locked;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { locked = cmd.Locked }), ct);
        return Unit.Value;
    }
}

// ---------- Members (#57, #58) ----------
public sealed record ListWorkspaceMembersQuery(Guid WorkspaceId) : IRequest<IReadOnlyList<WorkspaceMemberDto>>;

public sealed class ListWorkspaceMembersHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms)
    : IRequestHandler<ListWorkspaceMembersQuery, IReadOnlyList<WorkspaceMemberDto>>
{
    public async Task<IReadOnlyList<WorkspaceMemberDto>> Handle(ListWorkspaceMembersQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.WorkspaceView, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền xem nhóm tài nguyên.");
        await auth.EnsureAsync(uid, ResourceType.Workspace, q.WorkspaceId, AccessLevel.View, ct);
        return await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == q.WorkspaceId).OrderBy(m => m.MemberRole).ThenBy(m => m.User!.FullName)
            .Select(m => new WorkspaceMemberDto(m.UserId, m.User!.Username, m.User.FullName, m.User.Organization != null ? m.User.Organization.Name : null, m.MemberRole, m.AddedAt))
            .ToListAsync(ct);
    }
}

public sealed record SetWorkspaceMembersCommand(Guid WorkspaceId, IReadOnlyList<Guid> UserIds, WorkspaceMemberRole Role) : IRequest<int>;

public sealed class SetWorkspaceMembersHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<SetWorkspaceMembersCommand, int>
{
    public async Task<int> Handle(SetWorkspaceMembersCommand cmd, CancellationToken ct)
    {
        var ws = await db.Workspaces.Include(w => w.Members).FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        if (ws.Type == WorkspaceType.Personal) throw new AppException("Không gian cá nhân không có thành viên.");
        await WorkspaceManagement.EnsureCanChangeAsync(db, auth, perms, me.RequireUserId(), ws, ct);
        WorkspaceManagement.EnsureNotLocked(ws);
        if (cmd.Role == WorkspaceMemberRole.Owner) throw new AppException("Dùng chức năng chuyển trưởng nhóm để đổi trưởng nhóm.");
        var valid = await db.Users.Where(u => cmd.UserIds.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        var count = 0;
        foreach (var id in valid)
        {
            if (id == ws.OwnerId) continue;
            var m = ws.Members.FirstOrDefault(x => x.UserId == id);
            if (m is null) ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = id, MemberRole = cmd.Role, AddedAt = clock.GetUtcNow().UtcDateTime });
            else m.MemberRole = cmd.Role;
            count++;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { action = "set_members", users = valid, role = cmd.Role }), ct);
        return count;
    }
}

public sealed record RemoveWorkspaceMemberCommand(Guid WorkspaceId, Guid UserId) : IRequest<Unit>;

public sealed class RemoveWorkspaceMemberHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit)
    : IRequestHandler<RemoveWorkspaceMemberCommand, Unit>
{
    public async Task<Unit> Handle(RemoveWorkspaceMemberCommand cmd, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        var uid = me.RequireUserId();
        // Members may leave on their own; otherwise managers only.
        if (cmd.UserId != uid)
        {
            await WorkspaceManagement.EnsureCanChangeAsync(db, auth, perms, uid, ws, ct);
            WorkspaceManagement.EnsureNotLocked(ws);
        }
        if (cmd.UserId == ws.OwnerId) throw new AppException("Không thể xóa trưởng nhóm khỏi nhóm. Hãy chuyển trưởng nhóm trước.");
        var removed = await db.WorkspaceMembers.Where(m => m.WorkspaceId == ws.Id && m.UserId == cmd.UserId).ExecuteDeleteAsync(ct);
        if (removed == 0) throw new NotFoundException("Người dùng không phải thành viên nhóm.");
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { action = "remove_member", userId = cmd.UserId }), ct);
        return Unit.Value;
    }
}

public sealed record TransferWorkspaceOwnerCommand(Guid WorkspaceId, Guid NewOwnerId) : IRequest<Unit>;

public sealed class TransferWorkspaceOwnerHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, IAuditLogger audit, TimeProvider clock) : IRequestHandler<TransferWorkspaceOwnerCommand, Unit>
{
    public async Task<Unit> Handle(TransferWorkspaceOwnerCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.Include(w => w.Members).FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        if (ws.Type == WorkspaceType.Personal) throw new AppException("Không thể chuyển chủ sở hữu không gian cá nhân.");
        if (ws.OwnerId != uid && !await perms.HasAsync(uid, Perm.WorkspaceAdmin, ct)) throw new ForbiddenException();
        if (!await db.Users.AnyAsync(u => u.Id == cmd.NewOwnerId && u.IsActive, ct)) throw new NotFoundException("Không tìm thấy người dùng.");
        foreach (var m in ws.Members.Where(m => m.MemberRole == WorkspaceMemberRole.Owner)) m.MemberRole = WorkspaceMemberRole.Manager;
        var target = ws.Members.FirstOrDefault(m => m.UserId == cmd.NewOwnerId);
        if (target is null) ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = cmd.NewOwnerId, MemberRole = WorkspaceMemberRole.Owner, AddedAt = clock.GetUtcNow().UtcDateTime });
        else target.MemberRole = WorkspaceMemberRole.Owner;
        ws.OwnerId = cmd.NewOwnerId;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { action = "transfer_owner", to = cmd.NewOwnerId }), ct);
        return Unit.Value;
    }
}

// ---------- Quota configuration (#59, #60) ----------
public sealed record SetWorkspaceQuotaCommand(Guid WorkspaceId, double QuotaGb) : IRequest<Unit>;

public sealed class SetWorkspaceQuotaHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<SetWorkspaceQuotaCommand, Unit>
{
    public async Task<Unit> Handle(SetWorkspaceQuotaCommand cmd, CancellationToken ct)
    {
        if (cmd.QuotaGb < 0) throw new AppException("Dung lượng không hợp lệ.");
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        var before = ws.QuotaBytes;
        ws.QuotaBytes = (long)(cmd.QuotaGb * WorkspaceProvisioning.Gb);
        if (ws.Type == WorkspaceType.Personal)
            await db.Users.Where(u => u.Id == ws.OwnerId).ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, ws.QuotaBytes), ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { quotaFrom = before, quotaTo = ws.QuotaBytes }), ct);
        return Unit.Value;
    }
}

/// <summary>#59: set the personal quota of one user, or of every user of a unit (optionally including sub-units).</summary>
public sealed record SetPersonalQuotaCommand(IReadOnlyList<Guid>? UserIds, Guid? OrganizationId, bool IncludeSubOrganizations, double QuotaGb) : IRequest<int>;

public sealed class SetPersonalQuotaHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<SetPersonalQuotaCommand, int>
{
    public async Task<int> Handle(SetPersonalQuotaCommand cmd, CancellationToken ct)
    {
        if (cmd.QuotaGb < 0) throw new AppException("Dung lượng không hợp lệ.");
        if (cmd.UserIds is not { Count: > 0 } && cmd.OrganizationId is null) throw new AppException("Vui lòng chọn người dùng hoặc đơn vị.");
        var bytes = (long)(cmd.QuotaGb * WorkspaceProvisioning.Gb);
        var users = db.Users.AsQueryable();
        if (cmd.UserIds is { Count: > 0 }) users = users.Where(u => cmd.UserIds.Contains(u.Id));
        else if (cmd.IncludeSubOrganizations)
        {
            var path = await db.Organizations.Where(o => o.Id == cmd.OrganizationId).Select(o => o.Path).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy đơn vị.");
            users = users.Where(u => u.Organization != null && u.Organization.Path.StartsWith(path));
        }
        else users = users.Where(u => u.OrganizationId == cmd.OrganizationId);
        var ids = await users.Select(u => u.Id).ToListAsync(ct);
        await db.Users.Where(u => ids.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, bytes), ct);
        await db.Workspaces.Where(w => w.Type == WorkspaceType.Personal && ids.Contains(w.OwnerId)).ExecuteUpdateAsync(s => s.SetProperty(w => w.QuotaBytes, bytes), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceName: "Cấu hình dung lượng cá nhân",
            Detail: new { cmd.OrganizationId, cmd.IncludeSubOrganizations, users = ids.Count, quotaBytes = bytes }), ct);
        return ids.Count;
    }
}
