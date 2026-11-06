using BlueDrive.Application.Features.Access;
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
    Guid OwnerId, string OwnerName, long QuotaBytes, long UsedBytes, bool IsLocked, WorkspaceMemberRole? MyRole, int MemberCount, DateTime CreatedAt,
    SpaceStatus Status = SpaceStatus.Active, Guid? CoverFileId = null);

/// <param name="CanViewDocs">The member's system role lets them see documents at all (without it, being in the group shows them nothing).</param>
public sealed record WorkspaceMemberDto(Guid UserId, string Username, string FullName, string? OrganizationName, WorkspaceMemberRole MemberRole, DateTime AddedAt,
    bool CanViewDocs = true);

public static class WorkspaceQueries
{
    public static IQueryable<WorkspaceDto> Project(IAppDbContext db, IQueryable<Workspace> q, Guid userId) => q.Select(w => new WorkspaceDto(
        w.Id, w.Name, w.Description, w.Type, w.OrganizationId, w.Organization != null ? w.Organization.Name : null, w.OwnerId, w.Owner!.FullName,
        w.QuotaBytes, w.UsedBytes, w.IsLocked, w.Members.Where(m => m.UserId == userId).Select(m => (WorkspaceMemberRole?)m.MemberRole).FirstOrDefault(),
        w.Members.Count, w.CreatedAt, w.Status, w.CoverFileId));

    /// <summary>
    /// Workspaces whose root the user may see on the new permission model: their Drive của tôi, Spaces they are a member of (Space groups),
    /// and Spaces granted to them, one of their groups, units or roles at Space level.
    /// </summary>
    public static async Task<IQueryable<Workspace>> AccessibleAsync(IAppDbContext db, Guid userId, CancellationToken ct)
    {
        var principals = await db.PrincipalMemberships.Where(m => m.UserId == userId).Select(m => m.PrincipalId).ToListAsync(ct);
        var scopes = db.ScopeGrants.Where(g => principals.Contains(g.PrincipalId) && (g.Permissions & Domain.Permissions.AccessRight.ViewItems) != 0).Select(g => g.ScopeId);
        var roots = db.Securables.Where(s => (s.Kind == SecurableKind.Space || s.Kind == SecurableKind.Drive) && !s.IsDeleted
                                             && (s.OwnerUserId == userId || scopes.Contains(s.AclScopeId))).Select(s => s.Id);
        // A deleted Space (system trash) is nobody's to open.
        return db.Workspaces.AsNoTracking().Where(w => roots.Contains(w.Id) && w.Status != SpaceStatus.Deleted);
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
        if (!await IsManagerAsync(db, auth, userId, ws, ct)) throw new ForbiddenException("Chỉ trưởng nhóm, quản lý nhóm hoặc quản trị viên được thay đổi nhóm tài nguyên.");
        if (!await perms.HasAsync(userId, Perm.WorkspaceEdit, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền chỉnh sửa nhóm tài nguyên.");
    }

    /// <summary>
    /// Who administers the group (same rule as changing it): only they see its members and handle people's access; the other
    /// members work with the documents.
    /// </summary>
    public static async Task<bool> CanAdministerAsync(IAppDbContext db, IResourceAuthorizationService auth, IPermissionService perms, Guid userId, Workspace ws,
        CancellationToken ct) =>
        ws.OwnerId == userId || await perms.HasAsync(userId, Perm.WorkspaceAdmin, ct)
        || (await IsManagerAsync(db, auth, userId, ws, ct) && await perms.HasAsync(userId, Perm.WorkspaceEdit, ct));

    /// <summary>Group tabs about its people (members, summary per member, editing history): administrators only.</summary>
    public static async Task EnsureAdministersAsync(IAppDbContext db, IResourceAuthorizationService auth, IPermissionService perms, Guid userId, Guid workspaceId,
        CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == workspaceId, ct);
        if (!await CanAdministerAsync(db, auth, perms, userId, ws, ct))
            throw new ForbiddenException("Chỉ người quản trị nhóm được xem mục này.");
    }

    private static async Task<bool> IsManagerAsync(IAppDbContext db, IResourceAuthorizationService auth, Guid userId, Workspace ws, CancellationToken ct) =>
        await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == ws.Id && m.UserId == userId
                                                && (m.MemberRole == WorkspaceMemberRole.Manager || m.MemberRole == WorkspaceMemberRole.Owner), ct)
        || (await auth.GetAccessAsync(userId, ResourceType.Workspace, ws.Id, ct)).Level >= AccessLevel.Manage;

    public static void EnsureNotLocked(Workspace ws)
    {
        if (ws.IsLocked) throw new AppException("Nhóm tài nguyên đang bị khóa, chỉ cho phép xem.", 423);
    }
}

/// <param name="IncludeArchived">Archived Spaces are hidden from the default list (spec 6) but can be listed.</param>
public sealed record ListMyWorkspacesQuery(WorkspaceType? Type, bool IncludeArchived = false) : IRequest<IReadOnlyList<WorkspaceDto>>;

public sealed class ListMyWorkspacesHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms)
    : IRequestHandler<ListMyWorkspacesQuery, IReadOnlyList<WorkspaceDto>>
{
    public async Task<IReadOnlyList<WorkspaceDto>> Handle(ListMyWorkspacesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var query = await WorkspaceQueries.AccessibleAsync(db, uid, ct);
        // Group / organization workspaces need "Nhóm tài nguyên - Xem"; the personal space is always listed.
        var seesGroups = await perms.HasAsync(uid, Perm.WorkspaceView, ct);
        query = query.Where(w => w.Type == WorkspaceType.Personal ? w.OwnerId == uid : seesGroups);
        if (q.Type.HasValue) query = query.Where(w => w.Type == q.Type);
        if (!q.IncludeArchived) query = query.Where(w => w.Status == SpaceStatus.Active);
        return await WorkspaceQueries.Project(db, query.OrderBy(w => w.Type).ThenBy(w => w.Name), uid).ToListAsync(ct);
    }
}

/// <summary>Admin screen "Cấu hình nhóm tài nguyên": every group/organization workspace.</summary>
public sealed record ListAllWorkspacesQuery(WorkspaceType? Type, Guid? OrganizationId, bool? IsLocked) : PageQuery, IRequest<PagedResult<WorkspaceDto>>;

public sealed class ListAllWorkspacesHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListAllWorkspacesQuery, PagedResult<WorkspaceDto>>
{
    public Task<PagedResult<WorkspaceDto>> Handle(ListAllWorkspacesQuery q, CancellationToken ct)
    {
        var query = db.Workspaces.AsNoTracking().Where(w => (q.Type.HasValue ? w.Type == q.Type : w.Type != WorkspaceType.Personal) && w.Status != SpaceStatus.Deleted);
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
        return await WorkspaceQueries.Project(db, db.Workspaces.AsNoTracking().Where(w => w.Id == q.Id && w.Status != SpaceStatus.Deleted), uid).FirstOrDefaultAsync(ct)
               ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
    }
}

/// <param name="Template">On creation: Space template (blank, department, project, campaign) whose folders are made at once.</param>
public sealed record SaveWorkspaceCommand(Guid? Id, string Name, string? Description, WorkspaceType Type, Guid? OrganizationId, double? QuotaGb,
    IReadOnlyList<Guid>? MemberIds, WorkspaceMemberRole MemberRole = WorkspaceMemberRole.Editor, string? Template = null) : IRequest<Guid>;

public sealed class SaveWorkspaceValidator : AbstractValidator<SaveWorkspaceCommand>
{
    public SaveWorkspaceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên nhóm tài nguyên.").MaximumLength(255);
        RuleFor(x => x.Type).Must(t => t != WorkspaceType.Personal).WithMessage("Không thể tạo thêm không gian cá nhân.");
        RuleFor(x => x.OrganizationId).NotNull().When(x => x.Type == WorkspaceType.Organization).WithMessage("Nhóm tài nguyên của đơn vị phải chọn đơn vị.");
        RuleFor(x => x.QuotaGb).InclusiveBetween(0, 100_000).When(x => x.QuotaGb.HasValue).WithMessage("Dung lượng không hợp lệ.");
        RuleFor(x => x.MemberRole).Must(r => r != WorkspaceMemberRole.Owner).WithMessage("Không thể thêm thành viên với vai trò trưởng nhóm.");
        RuleFor(x => x.Template).Must(t => t is null || SpaceTemplates.Find(t) is not null).WithMessage("Mẫu Space không hợp lệ.");
    }
}

public sealed class SaveWorkspaceHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, ISettingsService settings,
    IResourceAuthorizationService auth, IAuditLogger audit, TimeProvider clock, Access.LegacyAclSync sync) : IRequestHandler<SaveWorkspaceCommand, Guid>
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
            if (SpaceTemplates.Find(cmd.Template) is { } template) SpaceTemplates.Apply(db, ws, template);
        }
        else
        {
            ws = await db.Workspaces.Include(w => w.Members).FirstOrDefaultAsync(w => w.Id == cmd.Id && w.Status != SpaceStatus.Deleted, ct)
                 ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
            if (ws.Type == WorkspaceType.Personal) throw new AppException("Không gian cá nhân không chỉnh sửa tại đây.");
            if (ws.Status == SpaceStatus.Archived) throw new AppException("Space đã lưu trữ: chỉ đọc. Hãy đưa Space hoạt động lại trước khi sửa.", 423);
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
        if (isNew) await sync.SyncAsync(ResourceType.Workspace, ws.Id, copyOnBreak: true, uid, ct);
        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceType.Workspace, ws.Id, ws.Name, ws.Id,
            new { ws.Type, ws.OrganizationId, quotaBytes = ws.QuotaBytes, members = cmd.MemberIds }), ct);
        return ws.Id;
    }
}

public sealed record DeleteWorkspaceCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteWorkspaceHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, IAuditLogger audit, TimeProvider clock, Retention.RetentionService retention)
    : IRequestHandler<DeleteWorkspaceCommand, Unit>
{
    public async Task<Unit> Handle(DeleteWorkspaceCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        if (ws.Type == WorkspaceType.Personal) throw new AppException("Không thể xóa không gian cá nhân.");
        if (ws.OwnerId != uid && !await perms.HasAsync(uid, Perm.WorkspaceDelete, ct)) throw new ForbiddenException("Chỉ trưởng nhóm hoặc quản trị viên được xóa nhóm tài nguyên.");
        if (ws.Status == SpaceStatus.Deleted) throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        // Spec 6: a Space under a retention policy or a legal hold cannot be deleted.
        if (await retention.WorkspaceBlockAsync(ws.Id, ct) is { } block) throw new ConflictException($"Không xóa được Space: đang có {block}.");
        // To the system trash (spec 6): nobody opens it any more; administrators restore it within 93 days, then it is purged.
        ws.Status = SpaceStatus.Deleted;
        ws.StatusChangedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { trashDays = SpaceHandlers.TrashLife.TotalDays }), ct);
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
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == q.WorkspaceId, ct);
        if (!await WorkspaceManagement.CanAdministerAsync(db, auth, perms, uid, ws, ct))
            throw new ForbiddenException("Chỉ người quản trị nhóm được xem danh sách thành viên.");
        var members = await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == q.WorkspaceId).OrderBy(m => m.MemberRole).ThenBy(m => m.User!.FullName)
            .Select(m => new WorkspaceMemberDto(m.UserId, m.User!.Username, m.User.FullName, m.User.Organization != null ? m.User.Organization.Name : null, m.MemberRole, m.AddedAt))
            .ToListAsync(ct);
        var result = new List<WorkspaceMemberDto>(members.Count);
        foreach (var m in members)
        {
            var codes = await perms.GetPermissionsAsync(m.UserId, ct); // cached per user by the RBAC service
            result.Add(m with { CanViewDocs = codes.Contains(Perm.DocView) || codes.Contains(Perm.DocAdmin) });
        }
        return result;
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

public sealed class RemoveWorkspaceMemberHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit,
    PermissionProjection projection)
    : IRequestHandler<RemoveWorkspaceMemberCommand, Unit>
{
    public async Task<Unit> Handle(RemoveWorkspaceMemberCommand cmd, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        var uid = me.RequireUserId();
        // Only who administers the group adds or removes people, themselves included: a member does not leave on their own.
        await WorkspaceManagement.EnsureCanChangeAsync(db, auth, perms, uid, ws, ct);
        WorkspaceManagement.EnsureNotLocked(ws);
        if (cmd.UserId == ws.OwnerId) throw new AppException("Không thể xóa trưởng nhóm khỏi nhóm. Hãy chuyển trưởng nhóm trước.");
        var removed = await db.WorkspaceMembers.Where(m => m.WorkspaceId == ws.Id && m.UserId == cmd.UserId).ExecuteDeleteAsync(ct);
        if (removed == 0) throw new NotFoundException("Người dùng không phải thành viên nhóm.");
        // A bulk delete skips the save interceptor: project now, or the person keeps the Space's groups (and sees the Space).
        await projection.ProjectAsync([ws.Id], [cmd.UserId], ct);
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
public sealed record SetPersonalQuotaCommand(IReadOnlyList<Guid>? UserIds, Guid? OrganizationId, bool IncludeSubOrganizations, double QuotaGb,
    Guid? RoleId = null, bool SetAsDefault = false) : IRequest<int>;

public sealed class SetPersonalQuotaHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<SetPersonalQuotaCommand, int>
{
    public async Task<int> Handle(SetPersonalQuotaCommand cmd, CancellationToken ct)
    {
        if (cmd.QuotaGb < 0) throw new AppException("Dung lượng không hợp lệ.");
        if (cmd.UserIds is not { Count: > 0 } && cmd.OrganizationId is null && cmd.RoleId is null) throw new AppException("Vui lòng chọn người dùng, đơn vị hoặc vai trò.");
        var bytes = (long)(cmd.QuotaGb * WorkspaceProvisioning.Gb);
        var users = db.Users.AsQueryable();
        if (cmd.UserIds is { Count: > 0 }) users = users.Where(u => cmd.UserIds.Contains(u.Id));
        else if (cmd.RoleId is { } roleId)
        {
            if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct)) throw new NotFoundException("Không tìm thấy vai trò.");
            users = users.Where(u => u.UserRoles.Any(r => r.RoleId == roleId));
            if (cmd.SetAsDefault) await db.Roles.Where(r => r.Id == roleId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DefaultQuotaGb, cmd.QuotaGb), ct);
        }
        else if (cmd.IncludeSubOrganizations)
        {
            var path = await db.Organizations.Where(o => o.Id == cmd.OrganizationId).Select(o => o.Path).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy đơn vị.");
            users = users.Where(u => u.Organization != null && u.Organization.Path.StartsWith(path));
        }
        else users = users.Where(u => u.OrganizationId == cmd.OrganizationId);
        if (cmd.SetAsDefault && cmd.RoleId is null && cmd.UserIds is not { Count: > 0 } && cmd.OrganizationId is { } org)
            await db.Organizations.Where(o => o.Id == org).ExecuteUpdateAsync(s => s.SetProperty(o => o.DefaultQuotaGb, cmd.QuotaGb), ct);
        var ids = await users.Select(u => u.Id).ToListAsync(ct);
        await db.Users.Where(u => ids.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, bytes), ct);
        await db.Workspaces.Where(w => w.Type == WorkspaceType.Personal && ids.Contains(w.OwnerId)).ExecuteUpdateAsync(s => s.SetProperty(w => w.QuotaBytes, bytes), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceName: "Cấu hình dung lượng cá nhân",
            Detail: new { cmd.OrganizationId, cmd.IncludeSubOrganizations, cmd.RoleId, cmd.SetAsDefault, users = ids.Count, quotaBytes = bytes }), ct);
        return ids.Count;
    }
}
