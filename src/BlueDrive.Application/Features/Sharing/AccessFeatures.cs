using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sharing;

public sealed record PrincipalRef(PrincipalType Type, Guid Id);

/// <param name="FromParent">Comes from a parent folder or the workspace (lost when the item stops inheriting, unless copied).</param>
public sealed record AccessEntryDto(Guid? Id, PrincipalType PrincipalType, Guid PrincipalId, string PrincipalName, AccessLevel AccessLevel,
    bool Inherit, DateTime? ExpiresAt, string Source, string? SourceName, bool Editable, bool FromParent = false);

public sealed record ResourceAccessListDto(ResourceType ResourceType, Guid ResourceId, string Name, bool BreakInheritance, bool CanManage, IReadOnlyList<AccessEntryDto> Entries);

public static class PrincipalNames
{
    public static async Task<Dictionary<(PrincipalType, Guid), string>> ResolveAsync(IAppDbContext db, IEnumerable<(PrincipalType Type, Guid Id)> refs, CancellationToken ct)
    {
        var list = refs.Distinct().ToList();
        var users = list.Where(r => r.Type == PrincipalType.User).Select(r => r.Id).ToList();
        var groups = list.Where(r => r.Type == PrincipalType.Group).Select(r => r.Id).ToList();
        var orgs = list.Where(r => r.Type == PrincipalType.Organization).Select(r => r.Id).ToList();
        var roles = list.Where(r => r.Type == PrincipalType.Role).Select(r => r.Id).ToList();
        var result = new Dictionary<(PrincipalType, Guid), string>();
        foreach (var u in await db.Users.Where(x => users.Contains(x.Id)).Select(x => new { x.Id, x.FullName, x.Username }).ToListAsync(ct)) result[(PrincipalType.User, u.Id)] = $"{u.FullName} ({u.Username})";
        foreach (var g in await db.UserGroups.Where(x => groups.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[(PrincipalType.Group, g.Id)] = $"Nhóm: {g.Name}";
        foreach (var o in await db.Organizations.Where(x => orgs.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[(PrincipalType.Organization, o.Id)] = $"Đơn vị: {o.Name}";
        foreach (var r in await db.Roles.Where(x => roles.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[(PrincipalType.Role, r.Id)] = $"Vai trò: {r.Name}";
        return result;
    }

    public static async Task<List<Guid>> ExpandToUsersAsync(IAppDbContext db, IEnumerable<PrincipalRef> targets, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        foreach (var t in targets)
        {
            switch (t.Type)
            {
                case PrincipalType.User: ids.Add(t.Id); break;
                case PrincipalType.Group: foreach (var u in await db.UserGroupMembers.Where(m => m.GroupId == t.Id).Select(m => m.UserId).ToListAsync(ct)) ids.Add(u); break;
                case PrincipalType.Organization:
                    var path = await db.Organizations.Where(o => o.Id == t.Id).Select(o => o.Path).FirstOrDefaultAsync(ct);
                    if (path is not null) foreach (var u in await db.Users.Where(x => x.Organization != null && x.Organization.Path.StartsWith(path)).Select(x => x.Id).ToListAsync(ct)) ids.Add(u);
                    break;
                case PrincipalType.Role: foreach (var u in await db.UserRoles.Where(r => r.RoleId == t.Id).Select(r => r.UserId).ToListAsync(ct)) ids.Add(u); break;
            }
        }
        return [.. ids];
    }

    public static async Task EnsureExistsAsync(IAppDbContext db, PrincipalRef p, CancellationToken ct)
    {
        var ok = p.Type switch
        {
            PrincipalType.User => await db.Users.AnyAsync(x => x.Id == p.Id && x.IsActive, ct),
            PrincipalType.Group => await db.UserGroups.AnyAsync(x => x.Id == p.Id, ct),
            PrincipalType.Organization => await db.Organizations.AnyAsync(x => x.Id == p.Id, ct),
            PrincipalType.Role => await db.Roles.AnyAsync(x => x.Id == p.Id, ct),
            _ => false,
        };
        if (!ok) throw new NotFoundException("Không tìm thấy người dùng/nhóm/đơn vị được chọn.");
    }
}

/// <summary>#56 "Quản lý quyền truy cập": direct + inherited entries, members and shares of a folder/file/workspace.</summary>
public sealed record GetResourceAccessQuery(ResourceType Type, Guid Id) : IRequest<ResourceAccessListDto>;

public sealed class GetResourceAccessHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<GetResourceAccessQuery, ResourceAccessListDto>
{
    public async Task<ResourceAccessListDto> Handle(GetResourceAccessQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var access = await auth.EnsureAsync(uid, q.Type, q.Id, AccessLevel.View, ct);
        Guid wsId;
        string name;
        List<Guid> chain;
        var breakInheritance = false;
        switch (q.Type)
        {
            case ResourceType.File:
                var f = await db.Files.AsNoTracking().Include(x => x.Folder).FirstAsync(x => x.Id == q.Id, ct);
                wsId = f.WorkspaceId; name = f.Name; chain = LegacyResourceAuthorizationService.PathIds(f.Folder?.Path); breakInheritance = f.BreakInheritance;
                break;
            case ResourceType.Folder:
                var fo = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == q.Id, ct);
                wsId = fo.WorkspaceId; name = fo.Name; chain = LegacyResourceAuthorizationService.PathIds(fo.Path).Skip(1).ToList(); breakInheritance = fo.BreakInheritance;
                break;
            default:
                var w = await db.Workspaces.AsNoTracking().FirstAsync(x => x.Id == q.Id, ct);
                wsId = w.Id; name = w.Name; chain = [];
                break;
        }
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == wsId, ct);
        var folderNames = await db.Folders.AsNoTracking().Where(f => chain.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => new { f.Name, f.BreakInheritance }, ct);

        var entries = new List<AccessEntryDto>();
        var direct = await db.ResourcePermissions.AsNoTracking().Where(a => a.ResourceType == q.Type && a.ResourceId == q.Id).ToListAsync(ct);
        // Inherited ACL stops at the first ancestor (or self) that breaks inheritance.
        var inheritedFolders = new List<Guid>();
        var broken = breakInheritance;
        if (!broken)
            foreach (var id in chain)
            {
                inheritedFolders.Add(id);
                if (folderNames.TryGetValue(id, out var fn) && fn.BreakInheritance) { broken = true; break; }
            }
        var inherited = await db.ResourcePermissions.AsNoTracking()
            .Where(a => a.Inherit && ((a.ResourceType == ResourceType.Folder && inheritedFolders.Contains(a.ResourceId)) || (!broken && q.Type != ResourceType.Workspace && a.ResourceType == ResourceType.Workspace && a.ResourceId == wsId)))
            .ToListAsync(ct);
        var members = broken ? await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == wsId && (m.MemberRole == WorkspaceMemberRole.Owner || m.MemberRole == WorkspaceMemberRole.Manager)).ToListAsync(ct)
            : await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == wsId).ToListAsync(ct);
        var shares = await db.Shares.AsNoTracking().Where(s => (s.ResourceType == q.Type && s.ResourceId == q.Id) || (s.ResourceType == ResourceType.Folder && inheritedFolders.Contains(s.ResourceId))).ToListAsync(ct);

        var names = await PrincipalNames.ResolveAsync(db,
            direct.Select(a => (a.PrincipalType, a.PrincipalId)).Concat(inherited.Select(a => (a.PrincipalType, a.PrincipalId)))
                .Concat(members.Select(m => (PrincipalType.User, m.UserId))).Concat(shares.Select(s => (s.SharedWithType, s.SharedWithId))).Append((PrincipalType.User, ws.OwnerId)), ct);
        string N(PrincipalType t, Guid id) => names.GetValueOrDefault((t, id), "(không xác định)");

        entries.Add(new AccessEntryDto(null, PrincipalType.User, ws.OwnerId, N(PrincipalType.User, ws.OwnerId), AccessLevel.Manage, true, null, "owner", ws.Type == WorkspaceType.Personal ? "Chủ sở hữu" : "Trưởng nhóm", false, false));
        foreach (var m in members.Where(m => m.UserId != ws.OwnerId))
            entries.Add(new AccessEntryDto(null, PrincipalType.User, m.UserId, N(PrincipalType.User, m.UserId), LegacyResourceAuthorizationService.LevelOf(m.MemberRole), true, null, "member", $"Thành viên nhóm ({MemberRoleNames.Of(m.MemberRole)})", false, true));
        foreach (var a in inherited)
            entries.Add(new AccessEntryDto(a.Id, a.PrincipalType, a.PrincipalId, N(a.PrincipalType, a.PrincipalId), a.AccessLevel, true, a.ExpiresAt, "inherited",
                a.ResourceType == ResourceType.Workspace ? $"Kế thừa từ nhóm \"{ws.Name}\"" : $"Kế thừa từ thư mục \"{folderNames.GetValueOrDefault(a.ResourceId)?.Name}\"", false, true));
        foreach (var s in shares)
            entries.Add(new AccessEntryDto(s.Id, s.SharedWithType, s.SharedWithId, N(s.SharedWithType, s.SharedWithId), s.AccessLevel, true, null, "share",
                s.ResourceId == q.Id ? "Chia sẻ trực tiếp mục này" : $"Chia sẻ từ thư mục \"{folderNames.GetValueOrDefault(s.ResourceId)?.Name}\"",
                // Shares of this very item can be changed or removed here; shares of a parent folder are managed on that folder.
                s.ResourceId == q.Id && access.CanManage, s.ResourceId != q.Id));
        foreach (var a in direct)
            entries.Add(new AccessEntryDto(a.Id, a.PrincipalType, a.PrincipalId, N(a.PrincipalType, a.PrincipalId), a.AccessLevel, a.Inherit, a.ExpiresAt, "direct", "Phân quyền trực tiếp", access.CanManage));
        return new ResourceAccessListDto(q.Type, q.Id, name, breakInheritance, access.CanManage, entries);
    }
}

public static class MemberRoleNames
{
    public static string Of(WorkspaceMemberRole r) => r switch
    {
        WorkspaceMemberRole.Owner => "Trưởng nhóm", WorkspaceMemberRole.Manager => "Quản lý", WorkspaceMemberRole.Editor => "Chỉnh sửa",
        WorkspaceMemberRole.Uploader => "Tải lên", WorkspaceMemberRole.Approver => "Phê duyệt", _ => "Chỉ xem",
    };
}

public sealed record AclEntryInput(PrincipalType PrincipalType, Guid PrincipalId, AccessLevel AccessLevel, bool Inherit = true, DateTime? ExpiresAt = null);

/// <summary>Replaces the direct ACL of a resource and optionally toggles inheritance (#30, #55, #56).</summary>
public sealed record ShareLevelInput(Guid Id, AccessLevel AccessLevel);

/// <param name="Shares">When given: the shares of this item to keep, with their level; the item's other shares are revoked.</param>
public sealed record SetResourceAccessCommand(ResourceType Type, Guid Id, IReadOnlyList<AclEntryInput> Entries, bool? BreakInheritance, bool CopyInheritedOnBreak = false,
    IReadOnlyList<ShareLevelInput>? Shares = null) : IRequest<Unit>;

public sealed class SetResourceAccessValidator : AbstractValidator<SetResourceAccessCommand>
{
    public SetResourceAccessValidator()
    {
        RuleForEach(x => x.Entries).Must(e => e.AccessLevel != AccessLevel.None).WithMessage("Mức quyền không hợp lệ.");
        RuleForEach(x => x.Entries).Must(e => e.ExpiresAt is null || e.ExpiresAt > DateTime.UtcNow).WithMessage("Ngày hết hạn phải ở tương lai.");
        RuleForEach(x => x.Shares).Must(e => e.AccessLevel is AccessLevel.View or AccessLevel.Download or AccessLevel.Edit)
            .WithMessage("Mức quyền chia sẻ chỉ gồm Xem, Xem + Tải xuống, Chỉnh sửa.");
        RuleFor(x => x.BreakInheritance).Null().When(x => x.Type == ResourceType.Workspace).WithMessage("Chỉ thư mục hoặc tệp mới có thể ngừng kế thừa quyền.");
    }
}

public sealed class SetResourceAccessHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IMediator mediator, IAuditLogger audit,
    Access.LegacyAclSync sync)
    : IRequestHandler<SetResourceAccessCommand, Unit>
{
    public async Task<Unit> Handle(SetResourceAccessCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.DocShare, ct) && !await perms.HasAsync(uid, Perm.DocAdmin, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền phân quyền/chia sẻ tư liệu.");
        await auth.EnsureAsync(uid, cmd.Type, cmd.Id, AccessLevel.Manage, ct);
        foreach (var e in cmd.Entries) await PrincipalNames.EnsureExistsAsync(db, new PrincipalRef(e.PrincipalType, e.PrincipalId), ct);

        if (cmd.BreakInheritance is { } brk)
        {
            var folder = cmd.Type == ResourceType.Folder ? await db.Folders.FirstAsync(f => f.Id == cmd.Id, ct) : null;
            var file = cmd.Type == ResourceType.File ? await db.Files.FirstAsync(f => f.Id == cmd.Id, ct) : null;
            var wasBroken = folder?.BreakInheritance ?? file!.BreakInheritance;
            if (brk && !wasBroken && cmd.CopyInheritedOnBreak)
            {
                // Like SharePoint "Stop inheriting permissions": everyone who had access through the parent keeps it as a direct grant
                // (inherited ACL, folder shares, plain workspace members; owner and managers keep access anyway).
                var current = await mediator.Send(new GetResourceAccessQuery(cmd.Type, cmd.Id), ct);
                var copied = current.Entries
                    .Where(e => e.FromParent && !(e.Source == "member" && e.AccessLevel >= AccessLevel.Manage))
                    .GroupBy(e => (e.PrincipalType, e.PrincipalId)).Select(g => g.MaxBy(e => e.AccessLevel)!);
                foreach (var inh in copied)
                    if (!cmd.Entries.Any(x => x.PrincipalType == inh.PrincipalType && x.PrincipalId == inh.PrincipalId))
                        db.ResourcePermissions.Add(new ResourcePermission { ResourceType = cmd.Type, ResourceId = cmd.Id, PrincipalType = inh.PrincipalType, PrincipalId = inh.PrincipalId, AccessLevel = inh.AccessLevel, Inherit = true, ExpiresAt = inh.ExpiresAt });
            }
            if (folder is not null) folder.BreakInheritance = brk;
            if (file is not null) file.BreakInheritance = brk;
        }
        var existing = await db.ResourcePermissions.Where(a => a.ResourceType == cmd.Type && a.ResourceId == cmd.Id).ToListAsync(ct);
        var before = existing.Select(a => new { a.PrincipalType, a.PrincipalId, a.AccessLevel }).ToList();
        db.ResourcePermissions.RemoveRange(existing);
        foreach (var e in cmd.Entries.GroupBy(e => (e.PrincipalType, e.PrincipalId)).Select(g => g.MaxBy(x => x.AccessLevel)!))
            db.ResourcePermissions.Add(new ResourcePermission { ResourceType = cmd.Type, ResourceId = cmd.Id, PrincipalType = e.PrincipalType, PrincipalId = e.PrincipalId, AccessLevel = e.AccessLevel, Inherit = e.Inherit, ExpiresAt = e.ExpiresAt });
        object? sharesChange = null;
        if (cmd.Shares is { } keep)
        {
            var shares = await db.Shares.Where(x => x.ResourceType == cmd.Type && x.ResourceId == cmd.Id).ToListAsync(ct);
            var levels = keep.GroupBy(k => k.Id).ToDictionary(g => g.Key, g => g.Last().AccessLevel);
            var removed = shares.Where(x => !levels.ContainsKey(x.Id)).ToList();
            var changed = shares.Where(x => levels.TryGetValue(x.Id, out var l) && l != x.AccessLevel).ToList();
            foreach (var x in changed) x.AccessLevel = levels[x.Id];
            db.Shares.RemoveRange(removed);
            if (removed.Count + changed.Count > 0)
                sharesChange = new
                {
                    removed = removed.Select(x => new { x.Id, x.SharedWithType, x.SharedWithId }),
                    changed = changed.Select(x => new { x.Id, x.SharedWithType, x.SharedWithId, x.AccessLevel }),
                };
        }
        await db.SaveChangesAsync(ct);
        await sync.SyncAsync(cmd.Type, cmd.Id, cmd.CopyInheritedOnBreak, uid, ct);
        var name = cmd.Type switch
        {
            ResourceType.File => await db.Files.Where(f => f.Id == cmd.Id).Select(f => f.Name).FirstAsync(ct),
            ResourceType.Folder => await db.Folders.Where(f => f.Id == cmd.Id).Select(f => f.Name).FirstAsync(ct),
            _ => await db.Workspaces.Where(f => f.Id == cmd.Id).Select(f => f.Name).FirstAsync(ct),
        };
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, cmd.Type, cmd.Id, name, null, new { before, after = cmd.Entries, cmd.BreakInheritance, shares = sharesChange }), ct);
        return Unit.Value;
    }
}
