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
    DateTime DeletedAt, Guid? DeletedBy, string? DeletedByName, string OriginalLocation, bool OriginalLocationExists, DateTime PurgeAt, int Stage);

public static class TrashAccess
{
    /// <summary>
    /// The trash functions of the system role (trash.view / trash.edit / trash.delete) — except in one's own "Tài liệu của tôi", where
    /// the owner may do everything whatever the role (like Google Drive).
    /// </summary>
    public static async Task EnsureRoleAsync(IAppDbContext db, IPermissionService perms, Guid userId, Guid workspaceId, string permission, string message, CancellationToken ct)
    {
        if (await perms.HasAsync(userId, permission, ct)) return;
        if (await db.Workspaces.AnyAsync(w => w.Id == workspaceId && w.Type == WorkspaceType.Personal && w.OwnerId == userId, ct)) return;
        throw new ForbiddenException(message);
    }

    /// <summary>Trash of a workspace: members with edit rights (and the owner) — plus everyone for items they deleted themselves.</summary>
    public static async Task<bool> CanManageTrashAsync(IResourceAuthorizationService auth, Guid userId, Guid workspaceId, CancellationToken ct)
        => (await auth.GetAccessAsync(userId, ResourceType.Workspace, workspaceId, ct)).Level >= AccessLevel.Edit;

    /// <summary>Spec 8.1: the second stage is seen by who manages the Space (the owner of a Drive) and by administrators.</summary>
    public static async Task<bool> CanManageSecondStageAsync(IPermissionService perms, Access.IItemPermissionService engine, Guid userId, Guid workspaceId, CancellationToken ct)
        => await perms.HasAsync(userId, Perm.WorkspaceAdmin, ct)
           || Domain.Permissions.AccessRights.Has(await engine.GetEffectiveAsync(userId, workspaceId, ct), Domain.Permissions.AccessRight.ManageSpace);

    /// <summary>Names of the folders above an item ("Hồ sơ/2025"), kept with what is deleted so that a missing parent can be made again.</summary>
    public static async Task<string?> OriginalPathAsync(IAppDbContext db, string? folderPath, CancellationToken ct)
    {
        var ids = Retention.RetentionService.FolderIds(folderPath);
        if (ids.Count == 0) return null;
        var names = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        return string.Join('/', ids.Select(id => names.GetValueOrDefault(id, "").Replace("/", "-")));
    }
}

/// <summary>
/// "Thùng rác của tôi" (like Google Drive): everything deleted in my own drive, and anywhere else what I uploaded / made or deleted myself —
/// whoever deleted it. First stage only (the second is the managers'); I may restore it or send it on to the second stage.
/// </summary>
public sealed record ListMyTrashQuery : PageQuery, IRequest<PagedResult<TrashItemDto>>;

public sealed class ListMyTrashHandler(IAppDbContext db, ICurrentUser me, ISettingsService settings) : IRequestHandler<ListMyTrashQuery, PagedResult<TrashItemDto>>
{
    public async Task<PagedResult<TrashItemDto>> Handle(ListMyTrashQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var myDrives = db.Workspaces.Where(w => w.Type == WorkspaceType.Personal && w.OwnerId == uid).Select(w => w.Id);
        var folders = db.Folders.AsNoTracking().Where(f => f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && f.SecondStageAt == null
            && (myDrives.Contains(f.WorkspaceId) || f.CreatedBy == uid || f.DeletedBy == uid));
        var files = db.Files.AsNoTracking().Where(f => f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && f.SecondStageAt == null
            && (myDrives.Contains(f.WorkspaceId) || f.CreatedBy == uid || f.DeletedBy == uid));
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            folders = folders.Where(f => DbFn.Unaccent(f.Name).Contains(term));
            files = files.Where(f => DbFn.Unaccent(f.Name).Contains(term));
        }
        // Two queries merged in memory (see ListTrashHandler).
        var folderRows = await folders.Select(f => new { f.Id, Type = "folder", f.Name, f.WorkspaceId, Size = db.Files.Where(x => x.Folder != null && x.Folder.Path.StartsWith(f.Path) && x.IsDeleted).Sum(x => (long?)x.SizeBytes) ?? 0, MediaType = (MediaType?)null, DeletedAt = f.DeletedAt!.Value, f.DeletedBy, ParentId = f.OriginalParentId, f.OriginalPath })
            .ToListAsync(ct);
        var fileRows = await files.Select(f => new { f.Id, Type = "file", f.Name, f.WorkspaceId, Size = f.SizeBytes, MediaType = (MediaType?)f.MediaType, DeletedAt = f.DeletedAt!.Value, f.DeletedBy, ParentId = f.OriginalFolderId, f.OriginalPath })
            .ToListAsync(ct);
        var rows = folderRows.Concat(fileRows).OrderByDescending(x => x.DeletedAt).ToList();
        var total = rows.Count;
        var page = rows.Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize).ToList();
        var wsIds = page.Select(r => r.WorkspaceId).Distinct().ToList();
        var workspaces = await db.Workspaces.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var parentIds = page.Where(r => r.ParentId.HasValue).Select(r => r.ParentId!.Value).Distinct().ToList();
        var parents = await db.Folders.AsNoTracking().Where(f => parentIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var userIds = page.Where(r => r.DeletedBy.HasValue).Select(r => r.DeletedBy!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var defaultDays = await settings.GetAsync(SettingKeys.TrashRetentionDays, SettingKeys.DefaultTrashDays, ct);
        var items = new List<TrashItemDto>();
        foreach (var r in page)
        {
            var ws = workspaces[r.WorkspaceId];
            Folder? parent = r.ParentId is { } pid ? parents.GetValueOrDefault(pid) : null;
            var loc = await FileLocation.BuildAsync(db, ws, parent, uid, ct);
            var exists = r.ParentId is null || (parent is not null && !parent.IsDeleted);
            var text = parent is null && r.OriginalPath is { } path ? $"{loc[0].Name}/{path}" : FileLocation.ToText(loc);
            items.Add(new TrashItemDto(r.Id, r.Type, r.Name, ws.Id, loc[0].Name, r.Size, r.MediaType, r.DeletedAt, r.DeletedBy,
                r.DeletedBy is { } d ? users.GetValueOrDefault(d) : null, text, exists, r.DeletedAt.AddDays(ws.TrashRetentionDays ?? defaultDays), 1));
        }
        return new PagedResult<TrashItemDto>(items, total, q.SafePage, q.SafePageSize);
    }
}

/// <summary>#61: trash per workspace (personal and group): who deleted, when, original location. Stage 2 (spec 8.1) is the managers' one.</summary>
public sealed record ListTrashQuery(Guid WorkspaceId, int Stage = 1) : PageQuery, IRequest<PagedResult<TrashItemDto>>;

public sealed class ListTrashHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, ISettingsService settings, IPermissionService perms,
    Access.IItemPermissionService engine) : IRequestHandler<ListTrashQuery, PagedResult<TrashItemDto>>
{
    public async Task<PagedResult<TrashItemDto>> Handle(ListTrashQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == q.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        await TrashAccess.EnsureRoleAsync(db, perms, uid, ws.Id, Perm.TrashView, "Vai trò của bạn không có quyền xem thùng rác.", ct);
        var second = q.Stage == 2;
        if (second && !await TrashAccess.CanManageSecondStageAsync(perms, engine, uid, ws.Id, ct))
            throw new ForbiddenException("Chỉ Quản lý Space và quản trị xem được thùng rác tầng 2.");
        var full = second || await TrashAccess.CanManageTrashAsync(auth, uid, ws.Id, ct);
        // Not even an empty list for a Space / Drive one has nothing to do with (its existence stays hidden).
        if (!full && (await auth.GetAccessAsync(uid, ResourceType.Workspace, ws.Id, ct)).Level < AccessLevel.View
            && !await db.Files.AnyAsync(f => f.WorkspaceId == ws.Id && f.DeletedBy == uid, ct) && !await db.Folders.AnyAsync(f => f.WorkspaceId == ws.Id && f.DeletedBy == uid, ct))
            throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        var retention = ws.TrashRetentionDays ?? await settings.GetAsync(SettingKeys.TrashRetentionDays, SettingKeys.DefaultTrashDays, ct);
        var folders = db.Folders.AsNoTracking().Where(f => f.WorkspaceId == ws.Id && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null
            && (f.SecondStageAt != null) == second && (full || f.DeletedBy == uid));
        var files = db.Files.AsNoTracking().Where(f => f.WorkspaceId == ws.Id && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null
            && (f.SecondStageAt != null) == second && (full || f.DeletedBy == uid));
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            folders = folders.Where(f => DbFn.Unaccent(f.Name).Contains(term));
            files = files.Where(f => DbFn.Unaccent(f.Name).Contains(term));
        }
        // Two queries merged in memory: a SQL UNION types the folders' NULL media type as int, which breaks reading files' text-stored enum.
        var folderRows = await folders.Select(f => new { f.Id, Type = "folder", f.Name, Size = db.Files.Where(x => x.Folder != null && x.Folder.Path.StartsWith(f.Path) && x.IsDeleted).Sum(x => (long?)x.SizeBytes) ?? 0, MediaType = (MediaType?)null, DeletedAt = f.DeletedAt!.Value, f.DeletedBy, ParentId = f.OriginalParentId, f.OriginalPath })
            .ToListAsync(ct);
        var fileRows = await files.Select(f => new { f.Id, Type = "file", f.Name, Size = f.SizeBytes, MediaType = (MediaType?)f.MediaType, DeletedAt = f.DeletedAt!.Value, f.DeletedBy, ParentId = f.OriginalFolderId, f.OriginalPath })
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
            var text = parent is null && r.OriginalPath is { } path ? $"{loc[0].Name}/{path}" : FileLocation.ToText(loc);
            items.Add(new TrashItemDto(r.Id, r.Type, r.Name, ws.Id, loc[0].Name, r.Size, r.MediaType, r.DeletedAt, r.DeletedBy,
                r.DeletedBy is { } d ? users.GetValueOrDefault(d) : null, text, exists, r.DeletedAt.AddDays(retention), second ? 2 : 1));
        }
        return new PagedResult<TrashItemDto>(items, total, q.SafePage, q.SafePageSize);
    }
}

/// <summary>
/// #62: restore to the original location, with the item's own permissions (they stay with it while it is deleted). Spec 8.1: when the parent
/// folder is no longer there, the folders above it are made again (same names) — not restored with their content.
/// </summary>
public sealed record RestoreItemsCommand(IReadOnlyList<ItemRef> Items) : IRequest<int>;

public sealed class RestoreItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit,
    Access.IItemPermissionService engine)
    : IRequestHandler<RestoreItemsCommand, int>
{
    public async Task<int> Handle(RestoreItemsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var count = 0;
        foreach (var item in cmd.Items)
        {
            if (item.IsFile)
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.Id == item.Id && x.IsDeleted && x.IsTrashRoot && x.PreservedAt == null, ct) ?? throw new NotFoundException("Không tìm thấy mục trong thùng rác.");
                await EnsureTrashRight(uid, f.WorkspaceId, f.DeletedBy, f.CreatedBy, f.SecondStageAt is not null, ct);
                var parentOk = f.OriginalFolderId is null || await db.Folders.AnyAsync(x => x.Id == f.OriginalFolderId && !x.IsDeleted, ct);
                var remade = !parentOk;
                f.FolderId = parentOk ? f.OriginalFolderId : (await RemakeParentsAsync(f.WorkspaceId, f.OriginalPath, ct))?.Id;
                f.Name = await StorageNames.UniqueFileNameAsync(db, f.WorkspaceId, f.FolderId, f.Name, f.Id, ct);
                f.IsDeleted = false; f.IsTrashRoot = false; f.DeletedAt = null; f.DeletedBy = null; f.SecondStageAt = null; f.OriginalPath = null;
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Restore, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { parentsRemade = remade }), ct);
            }
            else
            {
                var folder = await db.Folders.FirstOrDefaultAsync(x => x.Id == item.Id && x.IsDeleted && x.IsTrashRoot && x.PreservedAt == null, ct) ?? throw new NotFoundException("Không tìm thấy mục trong thùng rác.");
                await EnsureTrashRight(uid, folder.WorkspaceId, folder.DeletedBy, folder.CreatedBy, folder.SecondStageAt is not null, ct);
                var deletedAt = folder.DeletedAt;
                var parent = folder.OriginalParentId is { } op ? await db.Folders.FirstOrDefaultAsync(x => x.Id == op && !x.IsDeleted, ct) : null;
                var remade = parent is null && folder.OriginalParentId is not null;
                if (remade) parent = await RemakeParentsAsync(folder.WorkspaceId, folder.OriginalPath, ct);
                var oldPath = folder.Path;
                folder.ParentId = parent?.Id;
                folder.Path = StorageNames.FolderPath(parent, folder.Id);
                folder.Name = await StorageNames.UniqueFolderNameAsync(db, folder.WorkspaceId, folder.ParentId, folder.Name, folder.Id, ct);
                folder.IsDeleted = false; folder.IsTrashRoot = false; folder.DeletedAt = null; folder.DeletedBy = null; folder.SecondStageAt = null; folder.OriginalPath = null;
                await db.SaveChangesAsync(ct);
                if (oldPath != folder.Path)
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE folders SET path = {folder.Path} || substr(path, {oldPath.Length + 1}) WHERE path LIKE {oldPath + "%"} AND id <> {folder.Id}", ct);
                // Bring back only what was deleted together with the folder (items trashed separately stay in the trash).
                await db.Folders.Where(x => x.Path.StartsWith(folder.Path) && x.IsDeleted && !x.IsTrashRoot && x.DeletedAt == deletedAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, false).SetProperty(x => x.DeletedAt, (DateTime?)null).SetProperty(x => x.DeletedBy, (Guid?)null), ct);
                await db.Files.Where(x => x.Folder != null && x.Folder.Path.StartsWith(folder.Path) && x.IsDeleted && !x.IsTrashRoot && x.DeletedAt == deletedAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, false).SetProperty(x => x.DeletedAt, (DateTime?)null).SetProperty(x => x.DeletedBy, (Guid?)null), ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Restore, ResourceType.Folder, folder.Id, folder.Name, folder.WorkspaceId, new { parentsRemade = remade }), ct);
            }
            count++;
        }
        return count;
    }

    /// <summary>The folders named in <paramref name="originalPath"/>, from the root: the live one of that name is used, a missing one is made.</summary>
    private async Task<Folder?> RemakeParentsAsync(Guid workspaceId, string? originalPath, CancellationToken ct)
    {
        Folder? parent = null;
        foreach (var name in (originalPath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var parentId = parent?.Id;
            var existing = await db.Folders.FirstOrDefaultAsync(f => f.WorkspaceId == workspaceId && f.ParentId == parentId && !f.IsDeleted && f.Name == name, ct);
            if (existing is null)
            {
                existing = new Folder { WorkspaceId = workspaceId, ParentId = parentId, Name = name };
                existing.Path = StorageNames.FolderPath(parent, existing.Id);
                db.Folders.Add(existing);
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Create, ResourceType.Folder, existing.Id, existing.Name, workspaceId, new { remadeForRestore = true }), ct);
            }
            parent = existing;
        }
        return parent;
    }

    private async Task EnsureTrashRight(Guid uid, Guid workspaceId, Guid? deletedBy, Guid? createdBy, bool secondStage, CancellationToken ct)
    {
        // "Thùng rác của tôi": what one made or deleted comes back to them, whatever their role (like Google Drive).
        if (!secondStage && (deletedBy == uid || createdBy == uid)) return;
        await TrashAccess.EnsureRoleAsync(db, perms, uid, workspaceId, Perm.TrashRestore, "Vai trò của bạn không có quyền khôi phục từ thùng rác.", ct);
        if (secondStage)
        {
            if (!await TrashAccess.CanManageSecondStageAsync(perms, engine, uid, workspaceId, ct)) throw new ForbiddenException("Chỉ Quản lý Space và quản trị khôi phục được từ thùng rác tầng 2.");
            return;
        }
        if (deletedBy == uid) return;
        if (!await TrashAccess.CanManageTrashAsync(auth, uid, workspaceId, ct)) throw new ForbiddenException("Bạn không có quyền với thùng rác của nhóm này.");
    }
}

/// <summary>
/// #62 + spec 8.1: "Xóa vĩnh viễn" from the trash moves items to the second stage (who manages the Space can still restore them until the
/// trash time ends); from the second stage it deletes for good (retention and holds apply). "Dọn sạch" does the same for a whole stage.
/// </summary>
public sealed record PurgeItemsCommand(IReadOnlyList<ItemRef>? Items, Guid? EmptyWorkspaceId, int EmptyStage = 1) : IRequest<int>;

public sealed class PurgeItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, TrashPurger purger,
    Access.IItemPermissionService engine, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<PurgeItemsCommand, int>
{
    public async Task<int> Handle(PurgeItemsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        const string noPurge = "Vai trò của bạn không có quyền xóa vĩnh viễn.";
        var items = cmd.Items?.ToList() ?? [];
        var mode = PurgeMode.Manual;
        if (cmd.EmptyWorkspaceId is { } wsId)
        {
            await TrashAccess.EnsureRoleAsync(db, perms, uid, wsId, Perm.TrashPurge, noPurge, ct);
            var second = cmd.EmptyStage == 2;
            if (second && !await TrashAccess.CanManageSecondStageAsync(perms, engine, uid, wsId, ct)) throw new ForbiddenException("Chỉ Quản lý Space và quản trị dọn được thùng rác tầng 2.");
            var full = second || await TrashAccess.CanManageTrashAsync(auth, uid, wsId, ct);
            items.AddRange(await db.Files.Where(f => f.WorkspaceId == wsId && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && (f.SecondStageAt != null) == second
                && (full || f.DeletedBy == uid)).Select(f => new ItemRef("file", f.Id)).ToListAsync(ct));
            items.AddRange(await db.Folders.Where(f => f.WorkspaceId == wsId && f.IsTrashRoot && f.IsDeleted && f.PreservedAt == null && (f.SecondStageAt != null) == second
                && (full || f.DeletedBy == uid)).Select(f => new ItemRef("folder", f.Id)).ToListAsync(ct));
            mode = PurgeMode.Auto;
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var count = 0;
        foreach (var item in items)
        {
            var row = item.IsFile
                ? await db.Files.Where(f => f.Id == item.Id && f.IsDeleted && f.IsTrashRoot && f.PreservedAt == null).Select(f => new { f.WorkspaceId, f.DeletedBy, f.CreatedBy, f.SecondStageAt, f.Name }).FirstOrDefaultAsync(ct)
                : await db.Folders.Where(f => f.Id == item.Id && f.IsDeleted && f.IsTrashRoot && f.PreservedAt == null).Select(f => new { f.WorkspaceId, f.DeletedBy, f.CreatedBy, f.SecondStageAt, f.Name }).FirstOrDefaultAsync(ct);
            if (row is null) throw new NotFoundException("Không tìm thấy mục trong thùng rác.");
            var mine = row.DeletedBy == uid || row.CreatedBy == uid;
            if (!(mine && row.SecondStageAt is null)) await TrashAccess.EnsureRoleAsync(db, perms, uid, row.WorkspaceId, Perm.TrashPurge, noPurge, ct);
            if (row.SecondStageAt is null)
            {
                if (!mine && !await TrashAccess.CanManageTrashAsync(auth, uid, row.WorkspaceId, ct)) throw new ForbiddenException("Bạn không có quyền với thùng rác của nhóm này.");
                if (item.IsFile) await db.Files.Where(f => f.Id == item.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.SecondStageAt, now), ct);
                else await db.Folders.Where(f => f.Id == item.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.SecondStageAt, now), ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Purge, item.IsFile ? ResourceType.File : ResourceType.Folder, item.Id, row.Name, row.WorkspaceId,
                    new { stage = 2 }), ct);
                count++;
                continue;
            }
            if (!await TrashAccess.CanManageSecondStageAsync(perms, engine, uid, row.WorkspaceId, ct)) throw new ForbiddenException("Chỉ Quản lý Space và quản trị xóa được khỏi thùng rác tầng 2.");
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

/// <summary>Trash job: purge trash roots (both stages) deleted longer ago than trash.retention_days (default 93, spec 8.1).</summary>
public sealed record PurgeExpiredTrashCommand : IRequest<int>;

public sealed class PurgeExpiredTrashHandler(IAppDbContext db, ISettingsService settings, TrashPurger purger, TimeProvider clock) : IRequestHandler<PurgeExpiredTrashCommand, int>
{
    public async Task<int> Handle(PurgeExpiredTrashCommand cmd, CancellationToken ct)
    {
        var days = await settings.GetAsync(SettingKeys.TrashRetentionDays, SettingKeys.DefaultTrashDays, ct);
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
