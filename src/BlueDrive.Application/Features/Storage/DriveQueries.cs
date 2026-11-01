using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

public sealed record DriveItemDto(
    Guid Id, string Type, string Name, Guid WorkspaceId, Guid? ParentId, long SizeBytes, MediaType? MediaType, string? Extension, string? MimeType,
    DateTime CreatedAt, DateTime? UpdatedAt, Guid? CreatedBy, string? CreatedByName, bool IsStarred, bool HasThumbnail, ApprovalStatus? ApprovalStatus,
    int? VersionNo, string? Title, IReadOnlyList<string>? Tags, string? WorkspaceName = null, string? ThumbnailUrl = null);

/// <summary>Adds short-lived signed thumbnail URLs (usable in &lt;img src&gt;) to listing items.</summary>
public static class ThumbnailUrls
{
    public static string For(IContentTicketService tickets, Guid userId, Guid fileId, bool large = false)
        => $"/api/v1/content/{Uri.EscapeDataString(tickets.Issue(new ContentTicket(userId, large ? "preview" : "thumb", fileId, null, true, null, null), TimeSpan.FromHours(6)))}";

    public static List<DriveItemDto> Apply(IEnumerable<DriveItemDto> items, IContentTicketService tickets, Guid userId)
        => items.Select(i => i.HasThumbnail && i.Type == "file" ? i with { ThumbnailUrl = For(tickets, userId, i.Id) } : i).ToList();
}

public sealed record BreadcrumbItem(Guid? Id, string Name);

public sealed record AccessFlags(bool CanView, bool CanDownload, bool CanEdit, bool CanUpload, bool CanManage, bool CanApprove, bool WorkspaceLocked)
{
    public static AccessFlags From(ResourceAccess a) => new(a.CanView, a.CanDownload, a.CanEdit, a.CanUpload && !a.WorkspaceLocked, a.CanManage, a.CanApprove, a.WorkspaceLocked);
}

public sealed record FolderInfoDto(Guid? Id, string Name, Guid WorkspaceId, string WorkspaceName, WorkspaceType WorkspaceType, Guid? ParentId,
    bool RequiresApproval, bool BreakInheritance, IReadOnlyList<BreadcrumbItem> Breadcrumb, AccessFlags Access);

public static class DriveProjection
{
    public static IQueryable<DriveItemDto> Files(IAppDbContext db, IQueryable<StoredFile> q, Guid userId) => q.Select(f => new DriveItemDto(
        f.Id, "file", f.Name, f.WorkspaceId, f.FolderId, f.SizeBytes, f.MediaType, f.Extension, f.MimeType, f.CreatedAt, f.UpdatedAt, f.CreatedBy,
        db.Users.Where(u => u.Id == f.CreatedBy).Select(u => u.FullName).FirstOrDefault(),
        db.UserFavorites.Any(s => s.UserId == userId && s.ItemType == FavoriteItemType.File && s.ItemId == f.Id),
        f.ThumbnailKey != null, f.ApprovalStatus, db.FileVersions.Where(v => v.Id == f.CurrentVersionId).Select(v => (int?)v.VersionNo).FirstOrDefault(),
        f.Title, f.Tags, f.Workspace!.Name));

    public static IQueryable<DriveItemDto> Folders(IAppDbContext db, IQueryable<Folder> q, Guid userId) => q.Select(f => new DriveItemDto(
        f.Id, "folder", f.Name, f.WorkspaceId, f.ParentId, 0, null, null, null, f.CreatedAt, f.UpdatedAt, f.CreatedBy,
        db.Users.Where(u => u.Id == f.CreatedBy).Select(u => u.FullName).FirstOrDefault(),
        db.UserFavorites.Any(s => s.UserId == userId && s.ItemType == FavoriteItemType.Folder && s.ItemId == f.Id),
        false, null, null, null, null, f.Workspace!.Name));

    /// <summary>Hides not-yet-approved files from users who can neither edit nor approve (except their own uploads).</summary>
    public static IQueryable<StoredFile> VisibleApproval(IQueryable<StoredFile> q, ResourceAccess access, Guid userId)
        => access.Level >= AccessLevel.Edit || access.CanApprove ? q : q.Where(f => f.ApprovalStatus == ApprovalStatus.Approved || f.CreatedBy == userId);

    public static IQueryable<StoredFile> SortFiles(IQueryable<StoredFile> q, string? sort) => sort switch
    {
        "name" => q.OrderBy(f => f.Name),
        "-name" => q.OrderByDescending(f => f.Name),
        "size" => q.OrderBy(f => f.SizeBytes),
        "-size" => q.OrderByDescending(f => f.SizeBytes),
        "createdAt" => q.OrderBy(f => f.CreatedAt),
        "-createdAt" => q.OrderByDescending(f => f.CreatedAt),
        "updatedAt" => q.OrderBy(f => f.UpdatedAt ?? f.CreatedAt),
        "-updatedAt" => q.OrderByDescending(f => f.UpdatedAt ?? f.CreatedAt),
        _ => q.OrderBy(f => f.Name),
    };

    public static IQueryable<Folder> SortFolders(IQueryable<Folder> q, string? sort) => sort switch
    {
        "-name" => q.OrderByDescending(f => f.Name),
        "createdAt" => q.OrderBy(f => f.CreatedAt),
        "updatedAt" => q.OrderBy(f => f.UpdatedAt ?? f.CreatedAt),
        "-createdAt" or "-updatedAt" => q.OrderByDescending(f => f.UpdatedAt ?? f.CreatedAt),
        _ => q.OrderBy(f => f.Name),
    };
}

/// <summary>Folder contents: sub-folders first, then files; server-side paging across both (#18, #44).</summary>
public sealed record ListItemsQuery(Guid WorkspaceId, Guid? FolderId, MediaType? MediaType) : PageQuery, IRequest<PagedResult<DriveItemDto>>;

public sealed class ListItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets) : IRequestHandler<ListItemsQuery, PagedResult<DriveItemDto>>
{
    public async Task<PagedResult<DriveItemDto>> Handle(ListItemsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var access = q.FolderId is { } fid ? await auth.GetAccessAsync(uid, ResourceType.Folder, fid, ct) : await auth.GetAccessAsync(uid, ResourceType.Workspace, q.WorkspaceId, ct);
        if (q.FolderId.HasValue && !await db.Folders.AnyAsync(f => f.Id == q.FolderId && f.WorkspaceId == q.WorkspaceId && !f.IsDeleted, ct))
            throw new NotFoundException("Không tìm thấy thư mục.");

        var folders = db.Folders.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && f.ParentId == q.FolderId && !f.IsDeleted);
        var files = db.Files.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && f.FolderId == q.FolderId && !f.IsDeleted);
        if (!access.CanView)
        {
            // No access to the container: show only children granted directly (shared items).
            var p = await auth.GetPrincipalsAsync(uid, ct);
            var principals = p.GroupIds.Concat(p.OrganizationIds).Concat(p.RoleIds).Append(uid).ToList();
            var grantedFolders = db.Shares.Where(s => s.ResourceType == ResourceType.Folder && principals.Contains(s.SharedWithId)).Select(s => s.ResourceId)
                .Concat(db.ResourcePermissions.Where(a => a.ResourceType == ResourceType.Folder && principals.Contains(a.PrincipalId)).Select(a => a.ResourceId));
            var grantedFiles = db.Shares.Where(s => s.ResourceType == ResourceType.File && principals.Contains(s.SharedWithId)).Select(s => s.ResourceId)
                .Concat(db.ResourcePermissions.Where(a => a.ResourceType == ResourceType.File && principals.Contains(a.PrincipalId)).Select(a => a.ResourceId));
            folders = folders.Where(f => grantedFolders.Contains(f.Id));
            files = files.Where(f => grantedFiles.Contains(f.Id) && f.ApprovalStatus == ApprovalStatus.Approved);
            if (!await folders.AnyAsync(ct) && !await files.AnyAsync(ct))
                throw new NotFoundException("Không tìm thấy thư mục hoặc bạn không có quyền truy cập.");
        }
        else files = DriveProjection.VisibleApproval(files, access, uid);

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            folders = folders.Where(f => DbFn.Unaccent(f.Name).Contains(term));
            files = files.Where(f => DbFn.Unaccent(f.Name).Contains(term) || (f.Title != null && DbFn.Unaccent(f.Title).Contains(term)));
        }
        if (q.MediaType.HasValue)
        {
            files = files.Where(f => f.MediaType == q.MediaType);
            folders = folders.Where(_ => false);
        }

        var folderCount = await folders.CountAsync(ct);
        var fileCount = await files.CountAsync(ct);
        var skip = (q.SafePage - 1) * q.SafePageSize;
        var take = q.SafePageSize;
        var items = new List<DriveItemDto>(take);
        if (skip < folderCount)
            items.AddRange(await DriveProjection.Folders(db, DriveProjection.SortFolders(folders, q.Sort).Skip(skip).Take(take), uid).ToListAsync(ct));
        var remaining = take - items.Count;
        if (remaining > 0)
        {
            var fileSkip = Math.Max(0, skip - folderCount);
            items.AddRange(await DriveProjection.Files(db, DriveProjection.SortFiles(files, q.Sort).Skip(fileSkip).Take(remaining), uid).ToListAsync(ct));
        }
        return new PagedResult<DriveItemDto>(ThumbnailUrls.Apply(items, tickets, uid), folderCount + fileCount, q.SafePage, q.SafePageSize);
    }
}

public sealed record GetFolderInfoQuery(Guid WorkspaceId, Guid? FolderId) : IRequest<FolderInfoDto>;

public sealed class GetFolderInfoHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<GetFolderInfoQuery, FolderInfoDto>
{
    public async Task<FolderInfoDto> Handle(GetFolderInfoQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == q.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        var wsName = ws.Type == WorkspaceType.Personal ? (ws.OwnerId == uid ? "Tài liệu của tôi" : ws.Name) : ws.Name;
        if (q.FolderId is not { } fid)
        {
            var a = await auth.GetAccessAsync(uid, ResourceType.Workspace, ws.Id, ct);
            return new FolderInfoDto(null, wsName, ws.Id, wsName, ws.Type, null, false, false, [new BreadcrumbItem(null, wsName)], AccessFlags.From(a));
        }
        var folder = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fid && f.WorkspaceId == ws.Id && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy thư mục.");
        var access = await auth.GetAccessAsync(uid, ResourceType.Folder, fid, ct);
        var ids = ResourceAuthorizationService.PathIds(folder.Path);
        var names = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var crumbs = new List<BreadcrumbItem> { new(null, wsName) };
        crumbs.AddRange(ids.AsEnumerable().Reverse().Where(names.ContainsKey).Select(id => new BreadcrumbItem(id, names[id])));
        return new FolderInfoDto(folder.Id, folder.Name, ws.Id, wsName, ws.Type, folder.ParentId, folder.RequiresApproval, folder.BreakInheritance, crumbs, AccessFlags.From(access));
    }
}

public sealed record FolderTreeNode(Guid Id, Guid? ParentId, string Name, List<FolderTreeNode> Children);

/// <summary>#82: folder tree of a workspace (used by pickers "Sao chép tới..." / "Di chuyển tới...").</summary>
public sealed record GetFolderTreeQuery(Guid WorkspaceId) : IRequest<IReadOnlyList<FolderTreeNode>>;

public sealed class GetFolderTreeHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<GetFolderTreeQuery, IReadOnlyList<FolderTreeNode>>
{
    public async Task<IReadOnlyList<FolderTreeNode>> Handle(GetFolderTreeQuery q, CancellationToken ct)
    {
        await auth.EnsureAsync(me.RequireUserId(), ResourceType.Workspace, q.WorkspaceId, AccessLevel.View, ct);
        var rows = await db.Folders.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && !f.IsDeleted).OrderBy(f => f.Name)
            .Select(f => new { f.Id, f.ParentId, f.Name }).ToListAsync(ct);
        var nodes = rows.ToDictionary(r => r.Id, r => new FolderTreeNode(r.Id, r.ParentId, r.Name, []));
        var roots = new List<FolderTreeNode>();
        foreach (var n in nodes.Values)
            if (n.ParentId is { } p && nodes.TryGetValue(p, out var parent)) parent.Children.Add(n); else roots.Add(n);
        return roots;
    }
}

public sealed record FileVersionDto(Guid Id, int VersionNo, long SizeBytes, string? ChangeNote, Guid? CreatedBy, string? CreatedByName, DateTime CreatedAt, bool IsCurrent);

public sealed record CatalogTagDto(Guid Id, Guid CatalogId, string CatalogName, string Name);

public sealed record FileDetailDto(
    Guid Id, string Name, string? Title, string? Description, IReadOnlyList<string> Tags, string Extension, string MimeType, MediaType MediaType,
    string MediaTypeName, long SizeBytes, Guid WorkspaceId, string WorkspaceName, WorkspaceType WorkspaceType, Guid? FolderId, IReadOnlyList<BreadcrumbItem> Location,
    Guid? CreatedBy, string? CreatedByName, DateTime CreatedAt, Guid? UpdatedBy, string? UpdatedByName, DateTime? UpdatedAt,
    ApprovalStatus ApprovalStatus, string? RejectionReason, ScanStatus ScanStatus, int? Width, int? Height, double? DurationSeconds,
    bool HasThumbnail, bool HasPreview, string? ChecksumSha256, IReadOnlyList<CatalogTagDto> Catalogs, IReadOnlyList<FileVersionDto> Versions,
    bool IsStarred, AccessFlags Access, bool IsDeleted, string? ThumbnailUrl = null, string? PreviewUrl = null);

public sealed record GetFileDetailQuery(Guid Id, bool RecordView = true) : IRequest<FileDetailDto>;

public sealed class GetFileDetailHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets, TimeProvider clock)
    : IRequestHandler<GetFileDetailQuery, FileDetailDto>
{
    public async Task<FileDetailDto> Handle(GetFileDetailQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var access = await auth.EnsureAsync(uid, ResourceType.File, q.Id, AccessLevel.View, ct);
        var f = await db.Files.AsNoTracking().Include(x => x.Workspace).Include(x => x.Folder).FirstAsync(x => x.Id == q.Id, ct);
        var location = await FileLocation.BuildAsync(db, f.Workspace!, f.Folder, uid, ct);
        var userIds = new[] { f.CreatedBy, f.UpdatedBy }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var versions = await db.FileVersions.AsNoTracking().Where(v => v.FileId == f.Id).OrderByDescending(v => v.VersionNo)
            .Select(v => new FileVersionDto(v.Id, v.VersionNo, v.SizeBytes, v.ChangeNote, v.CreatedBy,
                db.Users.Where(u => u.Id == v.CreatedBy).Select(u => u.FullName).FirstOrDefault(), v.CreatedAt, v.Id == f.CurrentVersionId)).ToListAsync(ct);
        var catalogs = await db.FileCatalogItems.AsNoTracking().Where(c => c.FileId == f.Id)
            .Select(c => new CatalogTagDto(c.CatalogItemId, c.CatalogItem!.CatalogId, c.CatalogItem.Catalog!.Name, c.CatalogItem.Name)).ToListAsync(ct);
        var starred = await db.UserFavorites.AnyAsync(s => s.UserId == uid && s.ItemType == FavoriteItemType.File && s.ItemId == f.Id, ct);
        if (q.RecordView) await RecentTracker.TouchAsync(db, uid, f.Id, "view", clock.GetUtcNow().UtcDateTime, ct);
        return new FileDetailDto(f.Id, f.Name, f.Title, f.Description, f.Tags, f.Extension, f.MimeType, f.MediaType, MediaTypeNames.Of(f.MediaType), f.SizeBytes,
            f.WorkspaceId, location[0].Name, f.Workspace!.Type, f.FolderId, location, f.CreatedBy, f.CreatedBy is { } c ? users.GetValueOrDefault(c) : null, f.CreatedAt,
            f.UpdatedBy, f.UpdatedBy is { } u2 ? users.GetValueOrDefault(u2) : null, f.UpdatedAt, f.ApprovalStatus, f.RejectionReason, f.ScanStatus,
            f.Width, f.Height, f.DurationSeconds, f.ThumbnailKey != null, f.PreviewKey != null, f.ChecksumSha256, catalogs, versions, starred,
            AccessFlags.From(access), f.IsDeleted,
            f.ThumbnailKey != null ? ThumbnailUrls.For(tickets, uid, f.Id) : null, f.PreviewKey != null ? ThumbnailUrls.For(tickets, uid, f.Id, large: true) : null);
    }
}

public static class FileLocation
{
    public static async Task<List<BreadcrumbItem>> BuildAsync(IAppDbContext db, Workspace ws, Folder? folder, Guid userId, CancellationToken ct)
    {
        var wsName = ws.Type == WorkspaceType.Personal ? (ws.OwnerId == userId ? "Tài liệu của tôi" : ws.Name) : ws.Name;
        var list = new List<BreadcrumbItem> { new(null, wsName) };
        if (folder is null) return list;
        var ids = ResourceAuthorizationService.PathIds(folder.Path);
        var names = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        list.AddRange(ids.AsEnumerable().Reverse().Where(names.ContainsKey).Select(id => new BreadcrumbItem(id, names[id])));
        return list;
    }

    public static string ToText(IEnumerable<BreadcrumbItem> crumbs) => string.Join(" / ", crumbs.Select(c => c.Name));
}

// ---------- Recent (#18), starred (#23), favorites ----------
public sealed record ListRecentQuery(int Take = 50) : IRequest<IReadOnlyList<DriveItemDto>>;

public sealed class ListRecentHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets) : IRequestHandler<ListRecentQuery, IReadOnlyList<DriveItemDto>>
{
    public async Task<IReadOnlyList<DriveItemDto>> Handle(ListRecentQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ids = await db.RecentItems.AsNoTracking().Where(r => r.UserId == uid).OrderByDescending(r => r.AccessedAt).Take(Math.Clamp(q.Take, 1, 200) * 2)
            .Select(r => r.ItemId).ToListAsync(ct);
        var items = await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => ids.Contains(f.Id) && !f.IsDeleted), uid).ToListAsync(ct);
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var result = new List<DriveItemDto>();
        foreach (var it in items.OrderBy(i => order[i.Id]))
        {
            if (result.Count >= q.Take) break;
            if (await auth.CanAsync(uid, ResourceType.File, it.Id, AccessLevel.View, ct)) result.Add(it);
        }
        return ThumbnailUrls.Apply(result, tickets, uid);
    }
}

public sealed record ListStarredQuery : IRequest<IReadOnlyList<DriveItemDto>>;

public sealed class ListStarredHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets) : IRequestHandler<ListStarredQuery, IReadOnlyList<DriveItemDto>>
{
    public async Task<IReadOnlyList<DriveItemDto>> Handle(ListStarredQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var favs = await db.UserFavorites.AsNoTracking().Where(s => s.UserId == uid).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var fileIds = favs.Where(f => f.ItemType == FavoriteItemType.File).Select(f => f.ItemId).ToList();
        var folderIds = favs.Where(f => f.ItemType == FavoriteItemType.Folder).Select(f => f.ItemId).ToList();
        var items = await DriveProjection.Folders(db, db.Folders.AsNoTracking().Where(f => folderIds.Contains(f.Id) && !f.IsDeleted), uid).ToListAsync(ct);
        items.AddRange(await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => fileIds.Contains(f.Id) && !f.IsDeleted), uid).ToListAsync(ct));
        var result = new List<DriveItemDto>();
        foreach (var it in items)
            if (await auth.CanAsync(uid, it.Type == "file" ? ResourceType.File : ResourceType.Folder, it.Id, AccessLevel.View, ct)) result.Add(it);
        return ThumbnailUrls.Apply(result, tickets, uid);
    }
}

public sealed record SetFavoriteCommand(FavoriteItemType ItemType, Guid ItemId, bool Starred) : IRequest<Unit>;

public sealed class SetFavoriteHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, TimeProvider clock) : IRequestHandler<SetFavoriteCommand, Unit>
{
    public async Task<Unit> Handle(SetFavoriteCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await auth.EnsureAsync(uid, cmd.ItemType == FavoriteItemType.File ? ResourceType.File : ResourceType.Folder, cmd.ItemId, AccessLevel.View, ct);
        var existing = await db.UserFavorites.FirstOrDefaultAsync(s => s.UserId == uid && s.ItemType == cmd.ItemType && s.ItemId == cmd.ItemId, ct);
        if (cmd.Starred && existing is null) db.UserFavorites.Add(new UserFavorite { UserId = uid, ItemType = cmd.ItemType, ItemId = cmd.ItemId, CreatedAt = clock.GetUtcNow().UtcDateTime });
        if (!cmd.Starred && existing is not null) db.UserFavorites.Remove(existing);
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
