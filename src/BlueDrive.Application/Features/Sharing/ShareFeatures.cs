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

public sealed record ShareDto(Guid Id, ResourceType ResourceType, Guid ResourceId, PrincipalType SharedWithType, Guid SharedWithId, string SharedWithName,
    AccessLevel AccessLevel, string? Message, Guid SharedBy, string SharedByName, DateTime CreatedAt,
    Guid? InheritedFromId = null, string? InheritedFromName = null);

/// <summary>#49 internal sharing with users, groups or organizations: Xem / Xem + Tải xuống / Chỉnh sửa.</summary>
public sealed record CreateShareCommand(ResourceType ResourceType, Guid ResourceId, IReadOnlyList<PrincipalRef> Targets, AccessLevel AccessLevel, string? Message) : IRequest<int>;

public sealed class CreateShareValidator : AbstractValidator<CreateShareCommand>
{
    public CreateShareValidator()
    {
        RuleFor(x => x.Targets).NotEmpty().WithMessage("Vui lòng chọn người, nhóm hoặc đơn vị được chia sẻ.");
        RuleFor(x => x.AccessLevel).Must(l => l is AccessLevel.View or AccessLevel.Download or AccessLevel.Edit).WithMessage("Mức quyền chia sẻ chỉ gồm Xem, Xem + Tải xuống, Chỉnh sửa.");
        RuleFor(x => x.ResourceType).Must(t => t != ResourceType.Workspace).WithMessage("Chỉ chia sẻ thư mục hoặc tệp.");
        RuleFor(x => x.Message).MaximumLength(1000);
    }
}

public sealed class CreateShareHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IUserNotifier notify,
    IAuditLogger audit, TimeProvider clock, Access.LegacyAclSync sync) : IRequestHandler<CreateShareCommand, int>
{
    public async Task<int> Handle(CreateShareCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.DocShare, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền chia sẻ tư liệu.");
        var access = await auth.EnsureAsync(uid, cmd.ResourceType, cmd.ResourceId, AccessLevel.View, ct);
        // You can grant at most what you have (editors may share for editing; managers anything).
        if (cmd.AccessLevel > access.Level) throw new ForbiddenException("Bạn không thể chia sẻ với quyền cao hơn quyền bạn đang có.");
        if (access.WorkspaceLocked && cmd.AccessLevel >= AccessLevel.Edit) throw new AppException("Nhóm tài nguyên đang bị khóa, chỉ có thể chia sẻ quyền xem/tải xuống.", 423);
        var name = cmd.ResourceType == ResourceType.File
            ? await db.Files.Where(f => f.Id == cmd.ResourceId).Select(f => f.Name).FirstAsync(ct)
            : await db.Folders.Where(f => f.Id == cmd.ResourceId).Select(f => f.Name).FirstAsync(ct);
        var sharer = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var count = 0;
        foreach (var t in cmd.Targets.Distinct())
        {
            await PrincipalNames.EnsureExistsAsync(db, t, ct);
            if (t.Type == PrincipalType.User && t.Id == uid) continue;
            var existing = await db.Shares.FirstOrDefaultAsync(s => s.ResourceType == cmd.ResourceType && s.ResourceId == cmd.ResourceId && s.SharedWithType == t.Type && s.SharedWithId == t.Id, ct);
            if (existing is null) db.Shares.Add(new Share { ResourceType = cmd.ResourceType, ResourceId = cmd.ResourceId, SharedBy = uid, SharedWithType = t.Type, SharedWithId = t.Id, AccessLevel = cmd.AccessLevel, Message = cmd.Message, CreatedAt = now });
            else { existing.AccessLevel = cmd.AccessLevel; existing.Message = cmd.Message; existing.SharedBy = uid; existing.CreatedAt = now; }
            count++;
        }
        await db.SaveChangesAsync(ct);
        await sync.SyncAsync(cmd.ResourceType, cmd.ResourceId, copyOnBreak: true, uid, ct);
        var recipients = (await PrincipalNames.ExpandToUsersAsync(db, cmd.Targets, ct)).Where(x => x != uid).ToList();
        var link = cmd.ResourceType == ResourceType.File ? $"/files/{cmd.ResourceId}" : "/shared";
        foreach (var r in recipients)
            await notify.NotifyUserAsync(r, "share", $"{sharer} đã chia sẻ \"{name}\" với bạn", cmd.Message, link, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Share, cmd.ResourceType, cmd.ResourceId, name, null, new { targets = cmd.Targets, level = cmd.AccessLevel, recipients = recipients.Count }), ct);
        return count;
    }
}

public sealed record ListSharesOfResourceQuery(ResourceType ResourceType, Guid ResourceId) : IRequest<IReadOnlyList<ShareDto>>;

public sealed class ListSharesOfResourceHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<ListSharesOfResourceQuery, IReadOnlyList<ShareDto>>
{
    public async Task<IReadOnlyList<ShareDto>> Handle(ListSharesOfResourceQuery q, CancellationToken ct)
    {
        await auth.EnsureAsync(me.RequireUserId(), q.ResourceType, q.ResourceId, AccessLevel.View, ct);
        var rows = await db.Shares.AsNoTracking().Where(s => s.ResourceType == q.ResourceType && s.ResourceId == q.ResourceId).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);

        // Shares of ancestor folders apply here too (same rule as ResourceAuthorizationService): nearest first,
        // stopping at a folder that breaks inheritance. They are listed read-only with their origin.
        var ancestors = await InheritanceChainAsync(q, ct);
        var ancestorIds = ancestors.Select(a => a.Id).ToList();
        var inherited = ancestorIds.Count == 0 ? []
            : await db.Shares.AsNoTracking().Where(s => s.ResourceType == ResourceType.Folder && ancestorIds.Contains(s.ResourceId)).ToListAsync(ct);
        inherited = inherited.OrderBy(s => ancestorIds.IndexOf(s.ResourceId)).ThenByDescending(s => s.CreatedAt).ToList();
        var folderNames = ancestors.ToDictionary(a => a.Id, a => a.Name);

        var all = rows.Concat(inherited).ToList();
        var names = await PrincipalNames.ResolveAsync(db, all.Select(r => (r.SharedWithType, r.SharedWithId)).Concat(all.Select(r => (PrincipalType.User, r.SharedBy))), ct);
        ShareDto Map(Share s, bool viaParent) => new(s.Id, s.ResourceType, s.ResourceId, s.SharedWithType, s.SharedWithId, names.GetValueOrDefault((s.SharedWithType, s.SharedWithId), ""),
            s.AccessLevel, s.Message, s.SharedBy, names.GetValueOrDefault((PrincipalType.User, s.SharedBy), ""), s.CreatedAt,
            viaParent ? s.ResourceId : null, viaParent ? folderNames.GetValueOrDefault(s.ResourceId) : null);
        return rows.Select(s => Map(s, false)).Concat(inherited.Select(s => Map(s, true))).ToList();
    }

    /// <summary>Ancestor folders whose shares are inherited by the resource, nearest first.</summary>
    private async Task<List<(Guid Id, string Name)>> InheritanceChainAsync(ListSharesOfResourceQuery q, CancellationToken ct)
    {
        string? path;
        var selfBreaks = false;
        if (q.ResourceType == ResourceType.File)
        {
            var file = await db.Files.AsNoTracking().Where(f => f.Id == q.ResourceId).Select(f => new { Path = f.Folder != null ? f.Folder.Path : null, f.BreakInheritance }).FirstOrDefaultAsync(ct);
            path = file?.Path;
            selfBreaks = file?.BreakInheritance ?? false;
        }
        else if (q.ResourceType == ResourceType.Folder)
        {
            var self = await db.Folders.AsNoTracking().Where(f => f.Id == q.ResourceId).Select(f => new { f.Path, f.BreakInheritance }).FirstOrDefaultAsync(ct);
            path = self?.Path;
            selfBreaks = self?.BreakInheritance ?? false;
        }
        else return [];
        if (selfBreaks || string.IsNullOrEmpty(path)) return [];
        var ids = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Reverse().Where(id => id != q.ResourceId).ToList();
        if (ids.Count == 0) return [];
        var folders = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id)).Select(f => new { f.Id, f.Name, f.BreakInheritance }).ToDictionaryAsync(f => f.Id, ct);
        var chain = new List<(Guid, string)>();
        foreach (var id in ids)
        {
            if (!folders.TryGetValue(id, out var f)) break;
            chain.Add((f.Id, f.Name));
            if (f.BreakInheritance) break;
        }
        return chain;
    }
}

public sealed record DeleteShareCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteShareHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IAuditLogger audit, Access.LegacyAclSync sync)
    : IRequestHandler<DeleteShareCommand, Unit>
{
    public async Task<Unit> Handle(DeleteShareCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var s = await db.Shares.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy lượt chia sẻ.");
        // The sharer, the recipient (leaving) or a manager of the item may revoke.
        var isRecipient = s.SharedWithType == PrincipalType.User && s.SharedWithId == uid;
        if (s.SharedBy != uid && !isRecipient) await auth.EnsureAsync(uid, s.ResourceType, s.ResourceId, AccessLevel.Manage, ct);
        db.Shares.Remove(s);
        await db.SaveChangesAsync(ct);
        await sync.SyncAsync(s.ResourceType, s.ResourceId, copyOnBreak: true, uid, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Unshare, s.ResourceType, s.ResourceId, null, null, new { shareId = s.Id, s.SharedWithType, s.SharedWithId }), ct);
        return Unit.Value;
    }
}

/// <summary>#19/#72 "Được chia sẻ với tôi", filterable by sharer and time.</summary>
public sealed record ListSharedWithMeQuery(Guid? SharedBy, DateTime? From, DateTime? To, MediaType? MediaType) : PageQuery, IRequest<PagedResult<SharedItemDto>>;

public sealed record SharedItemDto(DriveItemDto Item, Guid ShareId, Guid SharedBy, string SharedByName, AccessLevel AccessLevel, string? Message, DateTime SharedAt);

public sealed class ListSharedWithMeHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets, Access.AccessFilter filter)
    : IRequestHandler<ListSharedWithMeQuery, PagedResult<SharedItemDto>>
{
    public async Task<PagedResult<SharedItemDto>> Handle(ListSharedWithMeQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var p = await auth.GetPrincipalsAsync(uid, ct);
        var ids = p.GroupIds.Concat(p.OrganizationIds).Concat(p.RoleIds).Append(uid).ToList();
        var shares = db.Shares.AsNoTracking().Where(s => ids.Contains(s.SharedWithId) && s.SharedBy != uid);
        if (q.SharedBy.HasValue) shares = shares.Where(s => s.SharedBy == q.SharedBy);
        if (q.From.HasValue) shares = shares.Where(s => s.CreatedAt >= q.From);
        if (q.To.HasValue) shares = shares.Where(s => s.CreatedAt < q.To.Value.AddDays(1));
        // Only resources still alive and still visible to this user (new permission model); newest share per resource.
        var visible = filter.Visible(await filter.ForAsync(uid, ct));
        shares = shares.Where(s => visible.Contains(s.ResourceId));
        var rows = await shares.Where(s => (s.ResourceType == ResourceType.File && db.Files.Any(f => f.Id == s.ResourceId && !f.IsDeleted && f.ApprovalStatus == ApprovalStatus.Approved
                                                && (q.MediaType == null || f.MediaType == q.MediaType)))
                                           || (s.ResourceType == ResourceType.Folder && q.MediaType == null && db.Folders.Any(f => f.Id == s.ResourceId && !f.IsDeleted)))
            .OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        rows = rows.GroupBy(r => r.ResourceId).Select(g => g.First()).ToList();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            var fileNames = await db.Files.Where(f => rows.Select(r => r.ResourceId).Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
            var folderNames = await db.Folders.Where(f => rows.Select(r => r.ResourceId).Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
            rows = rows.Where(r => TextUtil.Unaccent(fileNames.GetValueOrDefault(r.ResourceId) ?? folderNames.GetValueOrDefault(r.ResourceId)).Contains(term)).ToList();
        }
        var total = rows.Count;
        var page = rows.Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize).ToList();
        var fileIds = page.Where(r => r.ResourceType == ResourceType.File).Select(r => r.ResourceId).ToList();
        var folderIds = page.Where(r => r.ResourceType == ResourceType.Folder).Select(r => r.ResourceId).ToList();
        var items = (await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => fileIds.Contains(f.Id)), uid).ToListAsync(ct))
            .Concat(await DriveProjection.Folders(db, db.Folders.AsNoTracking().Where(f => folderIds.Contains(f.Id)), uid).ToListAsync(ct))
            .ToList();
        var itemsById = ThumbnailUrls.Apply(items, tickets, uid).ToDictionary(i => i.Id);
        var names = await PrincipalNames.ResolveAsync(db, page.Select(r => (PrincipalType.User, r.SharedBy)), ct);
        return new PagedResult<SharedItemDto>(page.Where(r => itemsById.ContainsKey(r.ResourceId))
            .Select(r => new SharedItemDto(itemsById[r.ResourceId], r.Id, r.SharedBy, names.GetValueOrDefault((PrincipalType.User, r.SharedBy), ""), r.AccessLevel, r.Message, r.CreatedAt)).ToList(),
            total, q.SafePage, q.SafePageSize);
    }
}

/// <summary>People who shared with me (filter dropdown).</summary>
public sealed record ListSharersQuery : IRequest<IReadOnlyList<Users.UserLookupDto>>;

public sealed class ListSharersHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<ListSharersQuery, IReadOnlyList<Users.UserLookupDto>>
{
    public async Task<IReadOnlyList<Users.UserLookupDto>> Handle(ListSharersQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var p = await auth.GetPrincipalsAsync(uid, ct);
        var ids = p.GroupIds.Concat(p.OrganizationIds).Concat(p.RoleIds).Append(uid).ToList();
        var sharers = db.Shares.Where(s => ids.Contains(s.SharedWithId) && s.SharedBy != uid).Select(s => s.SharedBy).Distinct();
        return await db.Users.AsNoTracking().Where(u => sharers.Contains(u.Id)).OrderBy(u => u.FullName)
            .Select(u => new Users.UserLookupDto(u.Id, u.Username, u.FullName, u.Organization != null ? u.Organization.Name : null, u.AvatarFileId)).ToListAsync(ct);
    }
}
