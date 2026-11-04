using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Workspaces;

// Perm-Phase 5 (spec 6): Space templates, status, member groups with user groups and units, settings, system trash of Spaces.

public sealed record SpaceTemplate(string Code, string Name, string Description, IReadOnlyList<string> Folders);

public static class SpaceTemplates
{
    public static readonly IReadOnlyList<SpaceTemplate> All =
    [
        new("blank", "Trống", "Không có sẵn thư mục.", []),
        new("department", "Phòng ban", "Văn bản, báo cáo, biểu mẫu của một phòng ban.", ["Văn bản đến", "Văn bản đi", "Báo cáo", "Biểu mẫu", "Tài liệu nội bộ"]),
        new("project", "Dự án", "Kế hoạch, thiết kế, biên bản và báo cáo tiến độ của một dự án.", ["Kế hoạch", "Tài liệu thiết kế", "Biên bản họp", "Báo cáo tiến độ", "Hợp đồng"]),
        new("campaign", "Chiến dịch truyền thông", "Sản phẩm truyền thông của một chiến dịch: kịch bản, hình ảnh, video, ấn phẩm.",
            ["Kịch bản", "Hình ảnh", "Video", "Ấn phẩm", "Báo cáo kết quả"]),
    ];

    public static SpaceTemplate? Find(string? code) => code is null ? null : All.FirstOrDefault(t => t.Code == code);

    /// <summary>The template's folders at the top of a new Space.</summary>
    public static void Apply(IAppDbContext db, Workspace ws, SpaceTemplate template)
    {
        foreach (var name in template.Folders)
        {
            var folder = new Folder { WorkspaceId = ws.Id, Name = name };
            folder.Path = StorageNames.FolderPath(null, folder.Id);
            db.Folders.Add(folder);
        }
        ws.Template = template.Code;
    }
}

public sealed record ListSpaceTemplatesQuery : IRequest<IReadOnlyList<SpaceTemplate>>;

public sealed record SpaceGroupMemberDto(PrincipalKind PrincipalKind, Guid PrincipalId, string Name, WorkspaceMemberRole MemberRole, DateTime AddedAt);

public sealed record ListSpaceGroupMembersQuery(Guid WorkspaceId) : IRequest<IReadOnlyList<SpaceGroupMemberDto>>;

/// <summary>Puts user groups or units (with their sub-units) in a member group of the Space (Chủ sở hữu, Thành viên, Khách xem…).</summary>
public sealed record AddSpaceGroupMembersCommand(Guid WorkspaceId, IReadOnlyList<PrincipalTarget> Principals, WorkspaceMemberRole Role) : IRequest<int>;

public sealed record RemoveSpaceGroupMemberCommand(Guid WorkspaceId, Guid PrincipalId) : IRequest<Unit>;

/// <summary>active / archived; "deleted" goes through <see cref="DeleteWorkspaceCommand"/>.</summary>
public sealed record SetSpaceStatusCommand(Guid WorkspaceId, SpaceStatus Status) : IRequest<Unit>;

public sealed record SpaceSettingsDto(Guid WorkspaceId, bool AllowDownload, bool AllowSync, int? TrashRetentionDays, bool ActivityForMembers, Guid? CoverFileId,
    SpaceStatus Status, string? Template, bool CanEdit);

public sealed record GetSpaceSettingsQuery(Guid WorkspaceId) : IRequest<SpaceSettingsDto>;

public sealed record SetSpaceSettingsCommand(Guid WorkspaceId, bool AllowDownload, bool AllowSync, int? TrashRetentionDays, bool ActivityForMembers, Guid? CoverFileId)
    : IRequest<Unit>;

public sealed class SetSpaceSettingsValidator : AbstractValidator<SetSpaceSettingsCommand>
{
    public SetSpaceSettingsValidator() =>
        RuleFor(x => x.TrashRetentionDays).InclusiveBetween(1, 3650).When(x => x.TrashRetentionDays.HasValue).WithMessage("Số ngày giữ thùng rác từ 1 đến 3650.");
}

/// <summary>Spaces in the system trash (deleted, kept 93 days): administrators restore or purge them.</summary>
public sealed record DeletedSpaceDto(Guid Id, string Name, string OwnerName, long UsedBytes, DateTime DeletedAt, DateTime PurgeAt);

public sealed record ListDeletedSpacesQuery : IRequest<IReadOnlyList<DeletedSpaceDto>>;

public sealed record RestoreSpaceCommand(Guid WorkspaceId) : IRequest<Unit>;

public sealed record PurgeSpaceCommand(Guid WorkspaceId) : IRequest<Unit>;

/// <summary>Job: Spaces deleted more than 93 days ago are purged with everything in them.</summary>
public sealed record PurgeExpiredSpacesCommand : IRequest<int>;

public sealed class SpaceHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, IPermissionService perms, IAuditLogger audit, TrashPurger purger,
    TimeProvider clock)
    : IRequestHandler<ListSpaceTemplatesQuery, IReadOnlyList<SpaceTemplate>>, IRequestHandler<ListSpaceGroupMembersQuery, IReadOnlyList<SpaceGroupMemberDto>>,
      IRequestHandler<AddSpaceGroupMembersCommand, int>, IRequestHandler<RemoveSpaceGroupMemberCommand, Unit>, IRequestHandler<SetSpaceStatusCommand, Unit>,
      IRequestHandler<GetSpaceSettingsQuery, SpaceSettingsDto>, IRequestHandler<SetSpaceSettingsCommand, Unit>,
      IRequestHandler<ListDeletedSpacesQuery, IReadOnlyList<DeletedSpaceDto>>, IRequestHandler<RestoreSpaceCommand, Unit>, IRequestHandler<PurgeSpaceCommand, Unit>,
      IRequestHandler<PurgeExpiredSpacesCommand, int>
{
    public static readonly TimeSpan TrashLife = TimeSpan.FromDays(93);

    public Task<IReadOnlyList<SpaceTemplate>> Handle(ListSpaceTemplatesQuery q, CancellationToken ct) => Task.FromResult(SpaceTemplates.All);

    private async Task<Workspace> SpaceAsync(Guid id, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy không gian.");
        if (ws.Type == WorkspaceType.Personal) throw new AppException("Drive của tôi không có cài đặt này.");
        return ws;
    }

    /// <summary>Who manages the Space (Chủ sở hữu group, or the system's workspace administrators).</summary>
    private async Task EnsureManagesAsync(Workspace ws, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (await perms.HasAsync(uid, Perm.WorkspaceAdmin, ct)) return;
        if (!(await engine.GetWithLockAsync(uid, ws.Id, ct)).Rights.Has(AccessRight.ManageSpace))
            throw new ForbiddenException("Chỉ Quản lý Space được thay đổi Space.");
    }

    public async Task<IReadOnlyList<SpaceGroupMemberDto>> Handle(ListSpaceGroupMembersQuery q, CancellationToken ct)
    {
        await engine.DemandAsync(me.RequireUserId(), q.WorkspaceId, AccessRight.ViewItems, ct);
        var rows = await db.WorkspaceGroupMembers.AsNoTracking().Where(m => m.WorkspaceId == q.WorkspaceId).ToListAsync(ct);
        var names = await PrincipalDisplay.ResolveAsync(db, rows.Select(r => r.PrincipalId), ct);
        return rows.Select(r => new SpaceGroupMemberDto(r.PrincipalKind, r.PrincipalId, names.GetValueOrDefault(r.PrincipalId, ("(không xác định)", null)).Name, r.MemberRole, r.AddedAt))
            .OrderBy(r => r.MemberRole).ThenBy(r => r.Name).ToList();
    }

    public async Task<int> Handle(AddSpaceGroupMembersCommand cmd, CancellationToken ct)
    {
        var ws = await SpaceAsync(cmd.WorkspaceId, ct);
        await EnsureManagesAsync(ws, ct);
        if (ws.Status != SpaceStatus.Active || ws.IsLocked) throw new AppException("Space đang bị khóa hoặc đã lưu trữ.", 423);
        var count = 0;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var p in cmd.Principals.Distinct())
        {
            if (p.Kind is not (PrincipalKind.Group or PrincipalKind.OrgUnitTree)) throw new AppException("Chỉ thêm nhóm người dùng hoặc đơn vị; người dùng thêm ở danh sách thành viên.");
            if (!await db.Principals.AnyAsync(x => x.Id == p.Id && x.Kind == p.Kind, ct)) throw new NotFoundException("Không tìm thấy nhóm hoặc đơn vị.");
            var row = await db.WorkspaceGroupMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ws.Id && m.PrincipalId == p.Id, ct);
            if (row is null) db.WorkspaceGroupMembers.Add(new WorkspaceGroupMember { WorkspaceId = ws.Id, PrincipalKind = p.Kind, PrincipalId = p.Id, MemberRole = cmd.Role, AddedAt = now });
            else row.MemberRole = cmd.Role;
            count++;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { action = "add_groups", cmd.Principals, role = cmd.Role }), ct);
        return count;
    }

    public async Task<Unit> Handle(RemoveSpaceGroupMemberCommand cmd, CancellationToken ct)
    {
        var ws = await SpaceAsync(cmd.WorkspaceId, ct);
        await EnsureManagesAsync(ws, ct);
        var row = await db.WorkspaceGroupMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ws.Id && m.PrincipalId == cmd.PrincipalId, ct)
                  ?? throw new NotFoundException("Nhóm hoặc đơn vị không thuộc Space.");
        db.WorkspaceGroupMembers.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { action = "remove_group", cmd.PrincipalId }), ct);
        return Unit.Value;
    }

    public async Task<Unit> Handle(SetSpaceStatusCommand cmd, CancellationToken ct)
    {
        var ws = await SpaceAsync(cmd.WorkspaceId, ct);
        await EnsureManagesAsync(ws, ct);
        if (cmd.Status == SpaceStatus.Deleted) throw new AppException("Dùng chức năng Xóa Space.");
        if (ws.Status == SpaceStatus.Deleted) throw new AppException("Space đã bị xóa; quản trị khôi phục ở Thùng rác Space.");
        ws.Status = cmd.Status;
        ws.StatusChangedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { status = cmd.Status }), ct);
        return Unit.Value;
    }

    public async Task<SpaceSettingsDto> Handle(GetSpaceSettingsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, q.WorkspaceId, AccessRight.ViewItems, ct);
        var ws = await SpaceAsync(q.WorkspaceId, ct);
        var can = await perms.HasAsync(uid, Perm.WorkspaceAdmin, ct) || (await engine.GetWithLockAsync(uid, ws.Id, ct)).Rights.Has(AccessRight.ManageSpace);
        return new SpaceSettingsDto(ws.Id, ws.AllowDownload, ws.AllowSync, ws.TrashRetentionDays, ws.ActivityForMembers, ws.CoverFileId, ws.Status, ws.Template, can);
    }

    public async Task<Unit> Handle(SetSpaceSettingsCommand cmd, CancellationToken ct)
    {
        var ws = await SpaceAsync(cmd.WorkspaceId, ct);
        await EnsureManagesAsync(ws, ct);
        if (cmd.CoverFileId is { } cover && !await db.Files.AnyAsync(f => f.Id == cover && f.WorkspaceId == ws.Id && !f.IsDeleted && f.MediaType == MediaType.Image, ct))
            throw new AppException("Ảnh bìa phải là một ảnh trong Space.");
        var before = new { ws.AllowDownload, ws.AllowSync, ws.TrashRetentionDays, ws.ActivityForMembers, ws.CoverFileId };
        ws.AllowDownload = cmd.AllowDownload;
        ws.AllowSync = cmd.AllowSync;
        ws.TrashRetentionDays = cmd.TrashRetentionDays;
        ws.ActivityForMembers = cmd.ActivityForMembers;
        ws.CoverFileId = cmd.CoverFileId;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { before, after = cmd }), ct);
        return Unit.Value;
    }

    public async Task<IReadOnlyList<DeletedSpaceDto>> Handle(ListDeletedSpacesQuery q, CancellationToken ct)
    {
        if (!await perms.HasAsync(me.RequireUserId(), Perm.WorkspaceAdmin, ct)) throw new ForbiddenException();
        var rows = await db.Workspaces.AsNoTracking().Where(w => w.Status == SpaceStatus.Deleted)
            .Select(w => new { w.Id, w.Name, Owner = w.Owner!.FullName, w.UsedBytes, w.StatusChangedAt }).ToListAsync(ct);
        return rows.Select(r => new DeletedSpaceDto(r.Id, r.Name, r.Owner, r.UsedBytes, r.StatusChangedAt ?? DateTime.MinValue, (r.StatusChangedAt ?? DateTime.MinValue) + TrashLife))
            .OrderByDescending(r => r.DeletedAt).ToList();
    }

    public async Task<Unit> Handle(RestoreSpaceCommand cmd, CancellationToken ct)
    {
        if (!await perms.HasAsync(me.RequireUserId(), Perm.WorkspaceAdmin, ct)) throw new ForbiddenException();
        var ws = await SpaceAsync(cmd.WorkspaceId, ct);
        if (ws.Status != SpaceStatus.Deleted) throw new AppException("Space không ở trong thùng rác.");
        ws.Status = SpaceStatus.Active;
        ws.StatusChangedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Restore, ResourceType.Workspace, ws.Id, ws.Name, ws.Id), ct);
        return Unit.Value;
    }

    public async Task<Unit> Handle(PurgeSpaceCommand cmd, CancellationToken ct)
    {
        if (!await perms.HasAsync(me.RequireUserId(), Perm.WorkspaceAdmin, ct)) throw new ForbiddenException();
        var ws = await SpaceAsync(cmd.WorkspaceId, ct);
        if (ws.Status != SpaceStatus.Deleted) throw new AppException("Chỉ xóa vĩnh viễn được Space đang ở trong thùng rác.");
        await PurgeAsync(ws, "manual", ct);
        return Unit.Value;
    }

    public async Task<int> Handle(PurgeExpiredSpacesCommand cmd, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - TrashLife;
        var spaces = await db.Workspaces.Where(w => w.Status == SpaceStatus.Deleted && w.StatusChangedAt < cutoff).ToListAsync(ct);
        foreach (var ws in spaces) await PurgeAsync(ws, "auto_93_days", ct);
        return spaces.Count;
    }

    /// <summary>Everything in the Space goes for good (contents, versions, sharing), then the Space itself.</summary>
    private async Task PurgeAsync(Workspace ws, string reason, CancellationToken ct)
    {
        foreach (var id in await db.Folders.Where(f => f.WorkspaceId == ws.Id && f.ParentId == null).Select(f => f.Id).ToListAsync(ct))
            await purger.PurgeAsync(new ItemRef("folder", id), reason, ct);
        foreach (var id in await db.Files.Where(f => f.WorkspaceId == ws.Id && f.FolderId == null).Select(f => f.Id).ToListAsync(ct))
            await purger.PurgeAsync(new ItemRef("file", id), reason, ct);
        await db.ResourcePermissions.Where(p => p.ResourceType == ResourceType.Workspace && p.ResourceId == ws.Id).ExecuteDeleteAsync(ct);
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == ws.Id).ExecuteDeleteAsync(ct);
        await db.WorkspaceGroupMembers.Where(m => m.WorkspaceId == ws.Id).ExecuteDeleteAsync(ct);
        await db.Shortcuts.Where(s => s.WorkspaceId == ws.Id).ExecuteDeleteAsync(ct);
        await db.Folders.Where(f => f.WorkspaceId == ws.Id).ExecuteDeleteAsync(ct);
        // Bulk: purging updated the Space's usage behind the tracked row.
        db.ChangeTracker.Clear();
        await db.Workspaces.Where(w => w.Id == ws.Id).ExecuteDeleteAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Purge, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new { reason }), ct);
    }
}
