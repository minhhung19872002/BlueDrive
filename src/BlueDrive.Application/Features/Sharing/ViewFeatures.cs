using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sharing;

// Spec 1.3: the views "Tôi đã chia sẻ" and "Bộ nhớ".

/// <param name="People">People, groups and units I gave access to on it (still valid).</param>
/// <param name="Links">My sharing links on it that still work.</param>
public sealed record SharedByMeDto(DriveItemDto Item, int People, int Links, DateTime LastSharedAt);

public sealed record ListSharedByMeQuery : PageQuery, IRequest<PagedResult<SharedByMeDto>>;

/// <param name="TotalBytes">With every kept version (what counts against the quota).</param>
public sealed record StorageItemDto(DriveItemDto Item, long TotalBytes, int Versions, long OldVersionBytes);

/// <summary>"Bộ nhớ" (spec 8.2): the files of my drive — or of a Space I manage — by the space they take, largest first.</summary>
public sealed record ListStorageQuery(Guid? WorkspaceId = null) : PageQuery, IRequest<PagedResult<StorageItemDto>>;

/// <summary>"Xóa phiên bản cũ": every version but the current and the published one goes (not while retained or held); the space comes back.</summary>
public sealed record PruneOldVersionsCommand(Guid FileId) : IRequest<PruneOldVersionsResult>;

public sealed record PruneOldVersionsResult(int Deleted, long FreedBytes);

public sealed class ViewHandlers(IAppDbContext db, ICurrentUser me, IContentTicketService tickets, Access.AccessFilter filter, TimeProvider clock,
    IPermissionService perms, Access.IItemPermissionService engine, Retention.RetentionService retention, IFileContentService content, Storage.QuotaService quota,
    IAuditLogger audit)
    : IRequestHandler<ListSharedByMeQuery, PagedResult<SharedByMeDto>>, IRequestHandler<ListStorageQuery, PagedResult<StorageItemDto>>,
      IRequestHandler<PruneOldVersionsCommand, PruneOldVersionsResult>
{
    public async Task<PagedResult<SharedByMeDto>> Handle(ListSharedByMeQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        var viewer = await filter.ForAsync(uid, ct);
        var visible = filter.Visible(viewer);
        var grants = await db.AclEntries.AsNoTracking()
            .Where(e => e.Source == AclSource.Direct && e.GrantedBy == uid && e.PrincipalId != uid && (e.ExpiresAt == null || e.ExpiresAt > now) && visible.Contains(e.ScopeId))
            .GroupBy(e => e.ScopeId).Select(g => new { Id = g.Key, N = g.Count(), Last = g.Max(e => e.GrantedAt) }).ToListAsync(ct);
        var links = await db.ShareLinks.AsNoTracking()
            .Where(l => l.CreatedBy == uid && !l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now) && visible.Contains(l.ResourceId))
            .GroupBy(l => l.ResourceId).Select(g => new { Id = g.Key, N = g.Count(), Last = g.Max(l => l.CreatedAt) }).ToListAsync(ct);
        var rows = grants.Select(g => g.Id).Union(links.Select(l => l.Id))
            .Select(id => (Id: id, People: grants.FirstOrDefault(g => g.Id == id)?.N ?? 0, Links: links.FirstOrDefault(l => l.Id == id)?.N ?? 0,
                Last: new[] { grants.FirstOrDefault(g => g.Id == id)?.Last, links.FirstOrDefault(l => l.Id == id)?.Last }.Max() ?? DateTime.MinValue))
            .OrderByDescending(r => r.Last).ToList();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            var all = rows.Select(r => r.Id).ToList();
            var names = await db.Securables.AsNoTracking().Where(s => all.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            rows = rows.Where(r => TextUtil.Unaccent(names.GetValueOrDefault(r.Id, "")).Contains(term)).ToList();
        }
        var page = rows.Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize).ToList();
        var ids = page.Select(r => r.Id).ToList();
        var items = (await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => ids.Contains(f.Id) && !f.IsDeleted), uid).ToListAsync(ct))
            .Concat(await DriveProjection.Folders(db, db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id) && !f.IsDeleted), uid).ToListAsync(ct)).ToList();
        var byId = ThumbnailUrls.Apply(items, tickets, uid).ToDictionary(i => i.Id);
        return new PagedResult<SharedByMeDto>(page.Where(r => byId.ContainsKey(r.Id)).Select(r => new SharedByMeDto(byId[r.Id], r.People, r.Links, r.Last)).ToList(),
            rows.Count, q.SafePage, q.SafePageSize);
    }

    public async Task<PagedResult<StorageItemDto>> Handle(ListStorageQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        Guid? drive;
        if (q.WorkspaceId is { } wsId)
        {
            if (!await perms.HasAsync(uid, Security.Perm.WorkspaceAdmin, ct)
                && !Domain.Permissions.AccessRights.Has(await engine.GetEffectiveAsync(uid, wsId, ct), Domain.Permissions.AccessRight.ManageSpace))
                throw new ForbiddenException("Chỉ Quản lý Space xem được bộ nhớ của Space.");
            drive = wsId;
        }
        else drive = await db.Workspaces.AsNoTracking().Where(w => w.OwnerId == uid && w.Type == WorkspaceType.Personal).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        if (drive is null) return new PagedResult<StorageItemDto>([], 0, q.SafePage, q.SafePageSize);
        var sizes = db.Files.AsNoTracking().Where(f => f.WorkspaceId == drive && !f.IsDeleted)
            .Select(f => new
            {
                f.Id,
                Total = db.FileVersions.Where(v => v.FileId == f.Id).Sum(v => (long?)v.SizeBytes) ?? f.SizeBytes,
                Versions = db.FileVersions.Count(v => v.FileId == f.Id),
                Old = db.FileVersions.Where(v => v.FileId == f.Id && v.Id != f.CurrentVersionId && v.Id != f.PublishedVersionId).Sum(v => (long?)v.SizeBytes) ?? 0,
            });
        var total = await sizes.CountAsync(ct);
        var page = await sizes.OrderByDescending(x => x.Total).ThenBy(x => x.Id).Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize).ToListAsync(ct);
        var ids = page.Select(p => p.Id).ToList();
        var items = ThumbnailUrls.Apply(await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => ids.Contains(f.Id)), uid).ToListAsync(ct), tickets, uid)
            .ToDictionary(i => i.Id);
        return new PagedResult<StorageItemDto>(page.Where(p => items.ContainsKey(p.Id)).Select(p => new StorageItemDto(items[p.Id], p.Total, p.Versions, p.Old)).ToList(),
            total, q.SafePage, q.SafePageSize);
    }

    public async Task<PruneOldVersionsResult> Handle(PruneOldVersionsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, cmd.FileId, Domain.Permissions.AccessRight.EditItems, ct);
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == cmd.FileId && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tệp.");
        if (await retention.ForFileAsync(file.Id, ct) is { } p)
            throw new ConflictException(p.Held ? $"Tệp đang bị giữ pháp lý \"{p.Hold}\": không xóa được phiên bản nào." : $"Tệp đang được lưu giữ theo \"{p.PolicyName}\": phiên bản cũ được giữ lại.");
        var old = await db.FileVersions.Where(v => v.FileId == file.Id && v.Id != file.CurrentVersionId && v.Id != file.PublishedVersionId).ToListAsync(ct);
        if (old.Count == 0) return new PruneOldVersionsResult(0, 0);
        db.FileVersions.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        foreach (var key in old.Select(v => v.StorageKey).Distinct())
            if (!await db.FileVersions.AnyAsync(v => v.StorageKey == key, ct)) await content.DeleteAsync(key, ct);
        var bytes = old.Sum(v => v.SizeBytes);
        await quota.AddUsageAsync(file.WorkspaceId, -bytes, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Purge, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { oldVersions = old.Count, bytes }), ct);
        return new PruneOldVersionsResult(old.Count, bytes);
    }
}
