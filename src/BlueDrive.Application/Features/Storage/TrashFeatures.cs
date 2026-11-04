using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Retention;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Application.Features.Storage;

public sealed record TrashItemDto(Guid Id, string Type, string Name, Guid WorkspaceId, string WorkspaceName, long SizeBytes, MediaType? MediaType,
    DateTime DeletedAt, Guid? DeletedBy, string? DeletedByName, string OriginalLocation, bool OriginalLocationExists, DateTime PurgeAt);

public static class TrashAccess
{
    /// <summary>Trash of a workspace: members with edit rights (and the owner) — plus everyone for items they deleted themselves.</summary>
    public static async Task<bool> CanManageTrashAsync(IResourceAuthorizationService auth, Guid userId, Guid workspaceId, CancellationToken ct)
        => (await auth.GetAccessAsync(userId, ResourceType.Workspace, workspaceId, ct)).Level >= AccessLevel.Edit;
}

/// <summary>#61: trash per workspace (personal and group): who deleted, when, original location.</summary>
public sealed record ListTrashQuery(Guid WorkspaceId) : PageQuery, IRequest<PagedResult<TrashItemDto>>;

public sealed class ListTrashHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, ISettingsService settings) : IRequestHandler<ListTrashQuery, PagedResult<TrashItemDto>>
{
    public async Task<PagedResult<TrashItemDto>> Handle(ListTrashQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == q.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        var full = await TrashAccess.CanManageTrashAsync(auth, uid, ws.Id, ct);
        var retention = await settings.GetAsync(SettingKeys.TrashRetentionDays, 30, ct);
        var folders = db.Folders.AsNoTracking().Where(f => f.WorkspaceId == ws.Id && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && (full || f.DeletedBy == uid));
        var files = db.Files.AsNoTracking().Where(f => f.WorkspaceId == ws.Id && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && (full || f.DeletedBy == uid));
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            folders = folders.Where(f => DbFn.Unaccent(f.Name).Contains(term));
            files = files.Where(f => DbFn.Unaccent(f.Name).Contains(term));
        }
        // Two queries merged in memory: a SQL UNION types the folders' NULL media type as int, which breaks reading files' text-stored enum.
        var folderRows = await folders.Select(f => new { f.Id, Type = "folder", f.Name, Size = db.Files.Where(x => x.Folder != null && x.Folder.Path.StartsWith(f.Path) && x.IsDeleted).Sum(x => (long?)x.SizeBytes) ?? 0, MediaType = (MediaType?)null, DeletedAt = f.DeletedAt!.Value, f.DeletedBy, ParentId = f.OriginalParentId })
            .ToListAsync(ct);
        var fileRows = await files.Select(f => new { f.Id, Type = "file", f.Name, Size = f.SizeBytes, MediaType = (MediaType?)f.MediaType, DeletedAt = f.DeletedAt!.Value, f.DeletedBy, ParentId = f.OriginalFolderId })
            .ToListAsync(ct);
        var rows = folderRows.Concat(fileRows).OrderByDescending(x => x.DeletedAt).ToList();
        var total = rows.Count;
        var page = rows.Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize).ToList();
        var parentIds = page.Where(r => r.ParentId.HasValue).Select(r => r.ParentId!.Value).Distinct().ToList();
        var parents = await db.Folders.AsNoTracking().Where(f => parentIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var userIds = page.Where(r => r.DeletedBy.HasValue).Select(r => r.DeletedBy!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var items = new List<TrashItemDto>();
        foreach (var r in page)
        {
            Folder? parent = r.ParentId is { } pid ? parents.GetValueOrDefault(pid) : null;
            var loc = await FileLocation.BuildAsync(db, ws, parent, uid, ct);
            var exists = r.ParentId is null || (parent is not null && !parent.IsDeleted);
            items.Add(new TrashItemDto(r.Id, r.Type, r.Name, ws.Id, loc[0].Name, r.Size, r.MediaType, r.DeletedAt, r.DeletedBy,
                r.DeletedBy is { } d ? users.GetValueOrDefault(d) : null, FileLocation.ToText(loc), exists, r.DeletedAt.AddDays(retention)));
        }
        return new PagedResult<TrashItemDto>(items, total, q.SafePage, q.SafePageSize);
    }
}

/// <summary>#62: restore to the original location; if the parent no longer exists, restore to the workspace root.</summary>
public sealed record RestoreItemsCommand(IReadOnlyList<ItemRef> Items) : IRequest<int>;

public sealed class RestoreItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit)
    : IRequestHandler<RestoreItemsCommand, int>
{
    public async Task<int> Handle(RestoreItemsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.TrashRestore, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền khôi phục từ thùng rác.");
        var count = 0;
        foreach (var item in cmd.Items)
        {
            if (item.IsFile)
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.Id == item.Id && x.IsDeleted && x.IsTrashRoot && x.PreservedAt == null, ct) ?? throw new NotFoundException("Không tìm thấy mục trong thùng rác.");
                await EnsureTrashRight(uid, f.WorkspaceId, f.DeletedBy, ct);
                var parentOk = f.OriginalFolderId is null || await db.Folders.AnyAsync(x => x.Id == f.OriginalFolderId && !x.IsDeleted, ct);
                f.FolderId = parentOk ? f.OriginalFolderId : null;
                f.Name = await StorageNames.UniqueFileNameAsync(db, f.WorkspaceId, f.FolderId, f.Name, f.Id, ct);
                f.IsDeleted = false; f.IsTrashRoot = false; f.DeletedAt = null; f.DeletedBy = null;
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Restore, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { toRoot = !parentOk }), ct);
            }
            else
            {
                var folder = await db.Folders.FirstOrDefaultAsync(x => x.Id == item.Id && x.IsDeleted && x.IsTrashRoot && x.PreservedAt == null, ct) ?? throw new NotFoundException("Không tìm thấy mục trong thùng rác.");
                await EnsureTrashRight(uid, folder.WorkspaceId, folder.DeletedBy, ct);
                var deletedAt = folder.DeletedAt;
                var parent = folder.OriginalParentId is { } op ? await db.Folders.FirstOrDefaultAsync(x => x.Id == op && !x.IsDeleted, ct) : null;
                var oldPath = folder.Path;
                folder.ParentId = parent?.Id;
                folder.Path = StorageNames.FolderPath(parent, folder.Id);
                folder.Name = await StorageNames.UniqueFolderNameAsync(db, folder.WorkspaceId, folder.ParentId, folder.Name, folder.Id, ct);
                folder.IsDeleted = false; folder.IsTrashRoot = false; folder.DeletedAt = null; folder.DeletedBy = null;
                await db.SaveChangesAsync(ct);
                if (oldPath != folder.Path)
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE folders SET path = {folder.Path} || substr(path, {oldPath.Length + 1}) WHERE path LIKE {oldPath + "%"} AND id <> {folder.Id}", ct);
                // Bring back only what was deleted together with the folder (items trashed separately stay in the trash).
                await db.Folders.Where(x => x.Path.StartsWith(folder.Path) && x.IsDeleted && !x.IsTrashRoot && x.DeletedAt == deletedAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, false).SetProperty(x => x.DeletedAt, (DateTime?)null).SetProperty(x => x.DeletedBy, (Guid?)null), ct);
                await db.Files.Where(x => x.Folder != null && x.Folder.Path.StartsWith(folder.Path) && x.IsDeleted && !x.IsTrashRoot && x.DeletedAt == deletedAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, false).SetProperty(x => x.DeletedAt, (DateTime?)null).SetProperty(x => x.DeletedBy, (Guid?)null), ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Restore, ResourceType.Folder, folder.Id, folder.Name, folder.WorkspaceId, new { toRoot = parent is null && folder.OriginalParentId is not null }), ct);
            }
            count++;
        }
        return count;
    }

    private async Task EnsureTrashRight(Guid uid, Guid workspaceId, Guid? deletedBy, CancellationToken ct)
    {
        if (deletedBy == uid) return;
        if (!await TrashAccess.CanManageTrashAsync(auth, uid, workspaceId, ct)) throw new ForbiddenException("Bạn không có quyền với thùng rác của nhóm này.");
    }
}

/// <summary>#62: permanent deletion (selected items or "Dọn sạch thùng rác"); releases quota.</summary>
public sealed record PurgeItemsCommand(IReadOnlyList<ItemRef>? Items, Guid? EmptyWorkspaceId) : IRequest<int>;

public sealed class PurgeItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, TrashPurger purger)
    : IRequestHandler<PurgeItemsCommand, int>
{
    public async Task<int> Handle(PurgeItemsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.TrashPurge, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền xóa vĩnh viễn.");
        var items = cmd.Items?.ToList() ?? [];
        var mode = PurgeMode.Manual;
        if (cmd.EmptyWorkspaceId is { } wsId)
        {
            var full = await TrashAccess.CanManageTrashAsync(auth, uid, wsId, ct);
            items.AddRange(await db.Files.Where(f => f.WorkspaceId == wsId && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && (full || f.DeletedBy == uid)).Select(f => new ItemRef("file", f.Id)).ToListAsync(ct));
            items.AddRange(await db.Folders.Where(f => f.WorkspaceId == wsId && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && (full || f.DeletedBy == uid)).Select(f => new ItemRef("folder", f.Id)).ToListAsync(ct));
            mode = PurgeMode.Auto;
        }
        var count = 0;
        foreach (var item in items)
        {
            var (ws, deletedBy) = item.IsFile
                ? await db.Files.Where(f => f.Id == item.Id && f.IsDeleted && f.IsTrashRoot && f.PreservedAt == null).Select(f => new ValueTuple<Guid, Guid?>(f.WorkspaceId, f.DeletedBy)).FirstOrDefaultAsync(ct)
                : await db.Folders.Where(f => f.Id == item.Id && f.IsDeleted && f.IsTrashRoot && f.PreservedAt == null).Select(f => new ValueTuple<Guid, Guid?>(f.WorkspaceId, f.DeletedBy)).FirstOrDefaultAsync(ct);
            if (ws == Guid.Empty) throw new NotFoundException("Không tìm thấy mục trong thùng rác.");
            if (deletedBy != uid && !await TrashAccess.CanManageTrashAsync(auth, uid, ws, ct)) throw new ForbiddenException("Bạn không có quyền với thùng rác của nhóm này.");
            if (await purger.PurgeAsync(item, "manual", ct, mode) != PurgeOutcome.Held) count++;
        }
        return count;
    }
}

/// <summary>Manual: a legal hold refuses the purge with a message; Auto (jobs, "Dọn sạch"): what is held is left as it is.</summary>
public enum PurgeMode { Manual, Auto }

public enum PurgeOutcome { Purged, Preserved, Held }

/// <summary>
/// Physical deletion shared by the purge command and the retention job. Spec 7.2–7.3: nothing under a legal hold is purged (not even by an
/// administrator); what a retention policy still keeps goes to the preservation store instead.
/// </summary>
public sealed class TrashPurger(IAppDbContext db, IFileContentService content, QuotaService quota, IAuditLogger audit, ILogger<TrashPurger> logger,
    RetentionService retention, TimeProvider clock)
{
    public async Task<PurgeOutcome> PurgeAsync(ItemRef item, string reason, CancellationToken ct, PurgeMode mode = PurgeMode.Auto)
    {
        List<StoredFile> files;
        List<Guid> folderIds = [];
        Guid wsId;
        string name;
        if (item.IsFile)
        {
            files = await db.Files.Where(f => f.Id == item.Id).ToListAsync(ct);
            if (files.Count == 0) return PurgeOutcome.Purged;
            wsId = files[0].WorkspaceId;
            name = files[0].Name;
        }
        else
        {
            var root = await db.Folders.FirstOrDefaultAsync(f => f.Id == item.Id, ct);
            if (root is null) return PurgeOutcome.Purged;
            wsId = root.WorkspaceId;
            name = root.Name;
            folderIds = await db.Folders.Where(f => f.Path.StartsWith(root.Path)).Select(f => f.Id).ToListAsync(ct);
            files = await db.Files.Where(f => f.FolderId != null && folderIds.Contains(f.FolderId.Value)).ToListAsync(ct);
        }

        var protections = await retention.ForFilesAsync(files.Select(f => f.Id).ToList(), ct);
        if (protections.Values.FirstOrDefault(p => p.Held) is { } held)
        {
            if (mode == PurgeMode.Manual) throw new ConflictException($"Không xóa vĩnh viễn được \"{name}\": tư liệu đang bị giữ pháp lý \"{held.Hold}\".");
            return PurgeOutcome.Held;
        }
        var kept = protections.Where(p => p.Value.Retained).Select(p => p.Key).ToHashSet();
        if (kept.Count > 0)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            await db.Files.Where(f => kept.Contains(f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.PreservedAt, now), ct);
            if (!item.IsFile) await db.Folders.Where(f => f.Id == item.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.PreservedAt, now), ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Purge, item.IsFile ? ResourceType.File : ResourceType.Folder, item.Id, name, wsId,
                new { reason, preserved = kept.Count, policy = protections[kept.First()].PolicyName }), ct);
            // The folders stay (the preserved files are in them); what nothing keeps goes now.
            files = files.Where(f => !kept.Contains(f.Id)).ToList();
            if (files.Count > 0) await DeleteAsync(item, wsId, name, files, [], reason, journal: false, ct);
            return PurgeOutcome.Preserved;
        }
        await DeleteAsync(item, wsId, name, files, folderIds, reason, journal: true, ct);
        return PurgeOutcome.Purged;
    }

    private async Task DeleteAsync(ItemRef item, Guid wsId, string name, List<StoredFile> files, List<Guid> folderIds, string reason, bool journal, CancellationToken ct)
    {
        var fileIds = files.Select(f => f.Id).ToList();
        var versions = await db.FileVersions.Where(v => fileIds.Contains(v.FileId)).ToListAsync(ct);
        var bytes = versions.Sum(v => v.SizeBytes);
        var keys = versions.Select(v => v.StorageKey).Distinct().ToList();
        var thumbKeys = files.SelectMany(f => new[] { f.ThumbnailKey, f.PreviewKey }).Where(k => k is not null).Select(k => k!).Distinct().ToList();

        await db.Comments.Where(c => fileIds.Contains(c.FileId)).ExecuteDeleteAsync(ct);
        await db.UserFavorites.Where(x => fileIds.Contains(x.ItemId) || folderIds.Contains(x.ItemId)).ExecuteDeleteAsync(ct);
        await db.RecentItems.Where(x => fileIds.Contains(x.ItemId)).ExecuteDeleteAsync(ct);
        await db.Shares.Where(x => fileIds.Contains(x.ResourceId) || folderIds.Contains(x.ResourceId)).ExecuteDeleteAsync(ct);
        await db.ShareLinks.Where(x => fileIds.Contains(x.ResourceId) || folderIds.Contains(x.ResourceId)).ExecuteDeleteAsync(ct);
        await db.ResourcePermissions.Where(x => fileIds.Contains(x.ResourceId) || folderIds.Contains(x.ResourceId)).ExecuteDeleteAsync(ct);
        await db.FileCatalogItems.Where(x => fileIds.Contains(x.FileId)).ExecuteDeleteAsync(ct);
        await db.FileColumnValues.Where(x => fileIds.Contains(x.FileId)).ExecuteDeleteAsync(ct);
        await db.FolderColumnDefaults.Where(x => folderIds.Contains(x.FolderId)).ExecuteDeleteAsync(ct);
        await db.FileVersions.Where(v => fileIds.Contains(v.FileId)).ExecuteDeleteAsync(ct);
        await db.Files.Where(f => fileIds.Contains(f.Id)).ExecuteDeleteAsync(ct);
        // Own ACLs kept with deleted items (to restore them) go with the items.
        var nodeIds = fileIds.Concat(folderIds).ToList();
        await db.ScopeGrants.Where(g => nodeIds.Contains(g.ScopeId)).ExecuteDeleteAsync(ct);
        await db.AclEntries.Where(e => nodeIds.Contains(e.ScopeId)).ExecuteDeleteAsync(ct);
        await db.AclScopes.Where(a => nodeIds.Contains(a.ScopeId)).ExecuteDeleteAsync(ct);
        // Shortcuts kept in the folders purged go with them (shortcuts to what is purged stay, shown as deleted).
        if (folderIds.Count > 0) await db.Shortcuts.Where(s => s.FolderId != null && folderIds.Contains(s.FolderId.Value)).ExecuteDeleteAsync(ct);
        if (folderIds.Count > 0) await db.Folders.Where(f => folderIds.Contains(f.Id)).ExecuteDeleteAsync(ct);
        // Bulk deletes bypass the change journal interceptor: record the purge of the trash root for desktop sync.
        if (journal)
            db.ChangeJournal.Add(new ChangeJournalEntry
            {
                WorkspaceId = wsId, ItemId = item.Id, ItemType = item.IsFile ? ChangeItemType.File : ChangeItemType.Folder, Op = ChangeOp.Purge, Name = name,
                ModifiedAt = DateTime.UtcNow, OccurredAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync(ct);
        await quota.AddUsageAsync(wsId, -bytes, ct);

        // Copy-on-write blobs are removed only when no other version references them.
        var stillUsed = await db.FileVersions.Where(v => keys.Contains(v.StorageKey)).Select(v => v.StorageKey).Distinct().ToListAsync(ct);
        var stillUsedThumbs = await db.Files.Where(f => (f.ThumbnailKey != null && thumbKeys.Contains(f.ThumbnailKey)) || (f.PreviewKey != null && thumbKeys.Contains(f.PreviewKey)))
            .Select(f => new[] { f.ThumbnailKey, f.PreviewKey }).ToListAsync(ct);
        var usedThumbs = stillUsedThumbs.SelectMany(x => x).Where(x => x is not null).ToHashSet();
        // Derived keys may carry "#wrappedKey"; the blob key is the part before it.
        foreach (var key in keys.Except(stillUsed).Concat(thumbKeys.Where(k => !usedThumbs.Contains(k)).Select(k => k.Split('#')[0])))
        {
            try { await content.DeleteAsync(key, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Không xóa được dữ liệu {Key}", key); }
        }
        await audit.LogAsync(new AuditEntry(AuditAction.Purge, item.IsFile ? ResourceType.File : ResourceType.Folder, item.Id, name, wsId,
            new { reason, files = fileIds.Count, folders = folderIds.Count, bytes }), ct);
    }
}

/// <summary>Retention job: purge trash roots older than trash.retention_days (default 30).</summary>
public sealed record PurgeExpiredTrashCommand : IRequest<int>;

public sealed class PurgeExpiredTrashHandler(IAppDbContext db, ISettingsService settings, TrashPurger purger, TimeProvider clock) : IRequestHandler<PurgeExpiredTrashCommand, int>
{
    public async Task<int> Handle(PurgeExpiredTrashCommand cmd, CancellationToken ct)
    {
        var days = await settings.GetAsync(SettingKeys.TrashRetentionDays, 30, ct);
        // A Space may set its own number of days (spec 6); the system's applies elsewhere (0 = kept).
        var own = await db.Workspaces.Where(w => w.TrashRetentionDays != null).ToDictionaryAsync(w => w.Id, w => w.TrashRetentionDays!.Value, ct);
        var shortest = own.Values.Append(days > 0 ? days : int.MaxValue).Min();
        if (shortest == int.MaxValue) return 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var first = now.AddDays(-shortest);
        // Two queries: EF cannot translate Concat after projecting into a record constructor.
        var files = await db.Files.Where(f => f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && f.DeletedAt < first).Select(f => new { f.Id, f.WorkspaceId, f.DeletedAt }).ToListAsync(ct);
        var folders = await db.Folders.Where(f => f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && f.DeletedAt < first).Select(f => new { f.Id, f.WorkspaceId, f.DeletedAt }).ToListAsync(ct);
        bool Due(Guid ws, DateTime? at) => (own.TryGetValue(ws, out var d) ? d : days) is var keep && keep > 0 && at < now.AddDays(-keep);
        var items = files.Where(f => Due(f.WorkspaceId, f.DeletedAt)).Select(f => (Ref: new ItemRef("file", f.Id), f.WorkspaceId))
            .Concat(folders.Where(f => Due(f.WorkspaceId, f.DeletedAt)).Select(f => (Ref: new ItemRef("folder", f.Id), f.WorkspaceId))).ToList();
        foreach (var i in items) await purger.PurgeAsync(i.Ref, $"auto_retention_{(own.TryGetValue(i.WorkspaceId, out var d) ? d : days)}_days", ct);
        return items.Count;
    }
}
