using System.Text;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sync;

/// <summary>A sync root: "Tài liệu của tôi", a group / organization workspace, or the virtual "Được chia sẻ với tôi".</summary>
public sealed record SyncRootDto(string RootId, string Kind, string Name, Guid? WorkspaceId, bool CanUpload, bool CanEdit, bool Locked, bool OnlineOnly,
    long QuotaBytes, long UsedBytes);

/// <summary>
/// Item as seen by the sync client. ETag = quoted revision; ParentId null = top level of the root. <see cref="Virtual"/>: the folder of one
/// person (or group) in "Được chia sẻ với tôi" — not a folder on the server, nothing can be created, renamed or deleted there.
/// </summary>
public sealed record SyncItemDto(
    Guid Id, string Type, string RootId, Guid WorkspaceId, Guid? ParentId, string Name, string ETag, long Revision,
    Guid? VersionId, long? Size, string? Hash, DateTime ModifiedAt, bool CanDownload, bool CanEdit, bool Deleted, bool Virtual = false);

public static class SyncIds
{
    public const string SharedRoot = "shared";
    public static string Root(Guid workspaceId) => $"ws:{workspaceId}";
    public static string ETag(long revision) => $"\"{revision}\"";

    public static bool TryParseRoot(string rootId, out Guid workspaceId) =>
        Guid.TryParse(rootId.StartsWith("ws:", StringComparison.Ordinal) ? rootId[3..] : "", out workspaceId);

    /// <summary>If-Match: "*" or the item's current ETag (weak prefix and quotes ignored, so base_etag may be the bare revision).</summary>
    public static bool Matches(string? ifMatch, long revision) =>
        ifMatch is null || ifMatch.Split(',').Select(t => t.Trim()).Any(t => t == "*" || Normalize(t) == revision.ToString());

    private static string Normalize(string tag) => (tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag).Trim('"');

    private static readonly byte[] SharerMask = Convert.FromHexString("5B1E0D1E5A0C4E7F8B0B5348415245FF");

    /// <summary>
    /// Spec 7.2 "Được chia sẻ với tôi\&lt;Tên người chia sẻ&gt;": the virtual folder grouping what one workspace (a person's documents or a
    /// group) shares with the user. Its id is the workspace id masked, so it is stable and the workspace can be found back from it.
    /// </summary>
    public static Guid SharerFolder(Guid workspaceId) => Mask(workspaceId);

    public static Guid WorkspaceOfSharerFolder(Guid folderId) => Mask(folderId);

    private static Guid Mask(Guid g)
    {
        var b = g.ToByteArray();
        for (var i = 0; i < b.Length; i++) b[i] ^= SharerMask[i];
        return new Guid(b);
    }
}

/// <summary>Loads <see cref="WorkspaceAccessMap"/>s for one user and caches them for the request.</summary>
public sealed class SyncAccessContext(IAppDbContext db, IResourceAuthorizationService auth, IPermissionService perms, TimeProvider clock)
{
    private readonly Dictionary<Guid, WorkspaceAccessMap?> _maps = [];
    private PrincipalSet? _principals;
    private IReadOnlySet<string>? _perms;
    public Guid UserId { get; private set; }

    /// <summary>(Re)starts evaluation for a user; cached maps are dropped so items created earlier in the request are seen.</summary>
    public async Task InitAsync(Guid userId, CancellationToken ct)
    {
        UserId = userId;
        _maps.Clear();
        _principals = await auth.GetPrincipalsAsync(userId, ct);
        _perms = await perms.GetPermissionsAsync(userId, ct);
    }

    /// <summary>The user, or one of their groups / organisations / roles.</summary>
    public bool Concerns(Guid principalId)
    {
        var p = _principals!;
        return p.UserId == principalId || p.GroupIds.Contains(principalId) || p.OrganizationIds.Contains(principalId) || p.RoleIds.Contains(principalId);
    }

    public async Task<WorkspaceAccessMap?> MapAsync(Guid workspaceId, CancellationToken ct)
    {
        if (_maps.TryGetValue(workspaceId, out var m)) return m;
        m = await WorkspaceAccessMap.LoadAsync(db, _principals!, _perms!, workspaceId, clock.GetUtcNow().UtcDateTime, ct);
        _maps[workspaceId] = m;
        return m;
    }

    /// <summary>Workspaces outside the user's roots where something is shared with them (the "Được chia sẻ với tôi" root).</summary>
    public async Task<List<Guid>> SharedWorkspacesAsync(IEnumerable<Guid> rootWorkspaces, CancellationToken ct)
    {
        var p = _principals!;
        var roots = rootWorkspaces.ToList();
        var now = clock.GetUtcNow().UtcDateTime;
        // Workspaces holding an item granted to the user directly (new permission model), outside their own roots.
        var principals = await db.PrincipalMemberships.Where(m => m.UserId == p.UserId).Select(m => m.PrincipalId).ToListAsync(ct);
        var ids = await db.AclEntries.Where(e => e.Source == AclSource.Direct && principals.Contains(e.PrincipalId) && (e.ExpiresAt == null || e.ExpiresAt > now))
            .Join(db.Securables.Where(n => !n.IsDeleted && (n.Kind == SecurableKind.Folder || n.Kind == SecurableKind.File)), e => e.ScopeId, n => n.Id,
                (e, n) => n.SpaceId ?? n.PersonalDriveId ?? Guid.Empty)
            .Distinct().ToListAsync(ct);
        return ids.Except(roots).ToList();
    }

    public SyncItemDto Folder(WorkspaceAccessMap map, Guid id, Guid? parentId, string name, long revision, DateTime modifiedAt)
    {
        var level = map.FolderLevel(id);
        return new SyncItemDto(id, "folder", RootOf(map), map.WorkspaceId, ParentOf(map, parentId), name, SyncIds.ETag(revision), revision,
            null, null, null, modifiedAt, level >= AccessLevel.Download, level >= AccessLevel.Edit && !map.Locked, false);
    }

    public SyncItemDto File(WorkspaceAccessMap map, AccessLevel level, Guid id, Guid? folderId, string name, long revision, Guid? versionId, long size, string? hash, DateTime modifiedAt) =>
        new(id, "file", RootOf(map), map.WorkspaceId, ParentOf(map, folderId), name, SyncIds.ETag(revision), revision,
            versionId, size, hash, modifiedAt, level >= AccessLevel.Download, level >= AccessLevel.Edit && !map.Locked, false);

    /// <summary>In "Được chia sẻ với tôi", what has no visible parent goes in the folder of the person / group who shares it.</summary>
    private static Guid? ParentOf(WorkspaceAccessMap map, Guid? parentId) =>
        map.IsRoot ? parentId : map.VisibleParent(parentId) ?? SyncIds.SharerFolder(map.WorkspaceId);

    /// <summary>The virtual folder of <paramref name="map"/>'s workspace in "Được chia sẻ với tôi" (read-only; the revision follows its name).</summary>
    public static SyncItemDto SharerFolder(WorkspaceAccessMap map)
    {
        var revision = 1 + (BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(map.SharerName))) & 0x3FFF_FFFF_FFFF_FFFF);
        return new SyncItemDto(SyncIds.SharerFolder(map.WorkspaceId), "folder", SyncIds.SharedRoot, map.WorkspaceId, null, map.SharerName,
            SyncIds.ETag(revision), revision, null, null, null, DateTime.UnixEpoch, true, false, false, Virtual: true);
    }

    public static string RootOf(WorkspaceAccessMap map) => map.IsRoot ? SyncIds.Root(map.WorkspaceId) : SyncIds.SharedRoot;
}

// ---------------------------------------------------------------- roots

public sealed record ListSyncRootsQuery : IRequest<IReadOnlyList<SyncRootDto>>;

public sealed class ListSyncRootsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, SyncPolicyService policy)
    : IRequestHandler<ListSyncRootsQuery, IReadOnlyList<SyncRootDto>>
{
    public async Task<IReadOnlyList<SyncRootDto>> Handle(ListSyncRootsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        var p = await policy.GetAsync(ct);
        var query = await Workspaces.WorkspaceQueries.AccessibleAsync(db, uid, ct);
        var list = await query.Where(w => (w.Type != WorkspaceType.Personal || w.OwnerId == uid) && (w.Type == WorkspaceType.Personal || w.AllowSync))
            .OrderBy(w => w.Type).ThenBy(w => w.Name).ToListAsync(ct);
        var roots = new List<SyncRootDto>();
        foreach (var w in list)
        {
            var a = await auth.GetAccessAsync(uid, ResourceType.Workspace, w.Id, ct);
            if (!a.CanView) continue;
            roots.Add(new SyncRootDto(SyncIds.Root(w.Id), w.Type == WorkspaceType.Personal ? "personal" : "workspace",
                w.Type == WorkspaceType.Personal ? "Tài liệu của tôi" : w.Name, w.Id, a.CanUpload && !a.WorkspaceLocked, a.CanEdit, w.IsLocked,
                p.OnlineOnlyWorkspaceIds.Contains(w.Id), w.QuotaBytes, w.UsedBytes));
        }
        roots.Add(new SyncRootDto(SyncIds.SharedRoot, "shared", "Được chia sẻ với tôi", null, false, false, false, false, 0, 0));
        return roots;
    }
}

// ---------------------------------------------------------------- snapshot (paged full listing)

public sealed record SyncSnapshotQuery(string RootId, string? PageToken, Guid? FolderId) : IRequest<SyncSnapshotDto>;

/// <summary><see cref="Cursor"/> is the journal position when the snapshot started: apply /sync/changes from there afterwards.</summary>
public sealed record SyncSnapshotDto(IReadOnlyList<SyncItemDto> Items, string? NextPageToken, long Cursor);

internal sealed record SnapshotToken(long Cursor, int Phase, Guid? WorkspaceId, string? AfterPath, Guid? AfterId);

public sealed class SyncSnapshotHandler(IAppDbContext db, ICurrentUser me, SyncAccessContext access, SyncPolicyService policy)
    : IRequestHandler<SyncSnapshotQuery, SyncSnapshotDto>
{
    private const int PageSize = 1000;

    public async Task<SyncSnapshotDto> Handle(SyncSnapshotQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        await access.InitAsync(uid, ct);
        var token = Decode(q.PageToken) ?? new SnapshotToken(await db.ChangeJournal.MaxAsync(c => (long?)c.Seq, ct) ?? 0, 0, null, null, null);

        // Workspaces making up the root, in a stable order.
        List<Guid> workspaces;
        if (q.RootId == SyncIds.SharedRoot)
        {
            var roots = (await Workspaces.WorkspaceQueries.AccessibleAsync(db, uid, ct)).Select(w => w.Id);
            var noSync = await db.Workspaces.Where(w => !w.AllowSync && w.Type != WorkspaceType.Personal).Select(w => w.Id).ToListAsync(ct);
            workspaces = (await access.SharedWorkspacesAsync(await roots.ToListAsync(ct), ct)).Where(x => !noSync.Contains(x)).OrderBy(x => x).ToList();
        }
        else if (SyncIds.TryParseRoot(q.RootId, out var wsId))
        {
            var map = await access.MapAsync(wsId, ct);
            if (map is null || !map.IsRoot) throw new NotFoundException("Không tìm thấy thư mục gốc đồng bộ hoặc bạn không có quyền truy cập.");
            workspaces = [wsId];
        }
        else throw new AppException("root_id không hợp lệ.");

        // Optional subtree (re-listing after a permission change): limit to the folder's descendants.
        string? subtree = null;
        if (q.FolderId is { } fid)
        {
            var f = await db.Folders.AsNoTracking().Where(x => x.Id == fid && !x.IsDeleted).Select(x => new { x.Path, x.WorkspaceId }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy thư mục.");
            if (!workspaces.Contains(f.WorkspaceId)) throw new NotFoundException("Thư mục không thuộc thư mục gốc này.");
            workspaces = [f.WorkspaceId];
            subtree = f.Path;
        }

        var items = new List<SyncItemDto>(PageSize);
        var wsIndex = token.WorkspaceId is { } current ? Math.Max(0, workspaces.IndexOf(current)) : 0;
        var phase = token.Phase;
        var afterPath = token.AfterPath;
        var afterId = token.AfterId;
        for (; wsIndex < workspaces.Count; wsIndex++, phase = 0, afterPath = null, afterId = null)
        {
            var ws = workspaces[wsIndex];
            var map = await access.MapAsync(ws, ct);
            if (map is null) continue;
            if (phase == 0 && afterPath is null && subtree is null && !map.IsRoot && map.HasAnyAccess)
            {
                // "Được chia sẻ với tôi\<Tên người chia sẻ>" first, then what it holds.
                items.Add(SyncAccessContext.SharerFolder(map));
                afterPath = ""; // every folder path sorts after ""
                if (items.Count == PageSize) return Page(items, new SnapshotToken(token.Cursor, 0, ws, afterPath, null), token.Cursor);
            }
            if (phase == 0)
            {
                // Folders, parents before children (path order).
                while (items.Count < PageSize)
                {
                    var folders = db.Folders.AsNoTracking().Where(f => f.WorkspaceId == ws && !f.IsDeleted);
                    if (subtree is not null) folders = folders.Where(f => f.Path.StartsWith(subtree));
                    if (afterPath is not null) folders = folders.Where(f => string.Compare(f.Path, afterPath) > 0);
                    var batch = await folders.OrderBy(f => f.Path).Take(PageSize)
                        .Select(f => new { f.Id, f.ParentId, f.Name, f.Path, f.Revision, Modified = f.UpdatedAt ?? f.CreatedAt }).ToListAsync(ct);
                    if (batch.Count == 0) break;
                    foreach (var f in batch)
                    {
                        afterPath = f.Path;
                        if (map.FolderLevel(f.Id) < AccessLevel.View) continue;
                        items.Add(access.Folder(map, f.Id, f.ParentId, f.Name, f.Revision, f.Modified));
                        if (items.Count == PageSize) break;
                    }
                    if (batch.Count < PageSize && items.Count < PageSize) { afterPath = null; break; }
                }
                if (items.Count == PageSize) return Page(items, new SnapshotToken(token.Cursor, 0, ws, afterPath, null), token.Cursor);
                phase = 1;
            }
            // Files by id.
            var folderIds = subtree is null ? null
                : await db.Folders.AsNoTracking().Where(f => f.WorkspaceId == ws && !f.IsDeleted && f.Path.StartsWith(subtree)).Select(f => f.Id).ToListAsync(ct);
            while (items.Count < PageSize)
            {
                var files = db.Files.AsNoTracking().Where(f => f.WorkspaceId == ws && !f.IsDeleted);
                if (folderIds is not null) files = files.Where(f => f.FolderId != null && folderIds.Contains(f.FolderId.Value));
                if (afterId is { } a) files = files.Where(f => f.Id.CompareTo(a) > 0);
                var batch = await files.OrderBy(f => f.Id).Take(PageSize)
                    .Select(f => new { f.Id, f.FolderId, f.Name, f.Revision, f.CurrentVersionId, f.SizeBytes, f.ChecksumSha256, f.ApprovalStatus, f.CreatedBy,
                        f.CheckedOutBy, f.PublishedVersionId,
                PubSize = db.FileVersions.Where(pv => pv.Id == f.PublishedVersionId).Select(pv => (long?)pv.SizeBytes).FirstOrDefault(),
                PubSum = db.FileVersions.Where(pv => pv.Id == f.PublishedVersionId).Select(pv => pv.Checksum).FirstOrDefault(),
                        Modified = f.UpdatedAt ?? f.CreatedAt }).ToListAsync(ct);
                if (batch.Count == 0) break;
                foreach (var f in batch)
                {
                    afterId = f.Id;
                    var level = map.FileLevel(f.Id, f.FolderId, f.ApprovalStatus, f.CreatedBy, f.CheckedOutBy, f.PublishedVersionId != null);
                    if (level < AccessLevel.View) continue;
                    // Who may not edit syncs the latest published version, not a draft (spec 1.4).
                    var published = level < AccessLevel.Edit && f.PublishedVersionId is { } pid && pid != f.CurrentVersionId;
                    items.Add(access.File(map, level, f.Id, f.FolderId, f.Name, f.Revision, published ? f.PublishedVersionId : f.CurrentVersionId,
                        published ? f.PubSize ?? f.SizeBytes : f.SizeBytes, published ? f.PubSum : f.ChecksumSha256, f.Modified));
                    if (items.Count == PageSize) break;
                }
                if (batch.Count < PageSize && items.Count < PageSize) break;
            }
            if (items.Count == PageSize) return Page(items, new SnapshotToken(token.Cursor, 1, ws, null, afterId), token.Cursor);
        }
        return new SyncSnapshotDto(items, null, token.Cursor);
    }

    private static SyncSnapshotDto Page(List<SyncItemDto> items, SnapshotToken next, long cursor) =>
        new(items, Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(next))), cursor);

    private static SnapshotToken? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try { return JsonSerializer.Deserialize<SnapshotToken>(Encoding.UTF8.GetString(Convert.FromBase64String(token))); }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new AppException("page_token không hợp lệ."); }
    }
}

// ---------------------------------------------------------------- changes (delta feed)

public sealed record SyncChangeDto(long Seq, string Op, string ItemType, Guid ItemId, Guid WorkspaceId, SyncItemDto? Item, DateTime OccurredAt, Guid? DeviceId);

public sealed record SyncChangesDto(IReadOnlyList<SyncChangeDto> Changes, long NextCursor, bool HasMore);

public sealed record SyncChangesQuery(long Cursor, int Limit = 500) : IRequest<SyncChangesDto>;

/// <summary>410 Gone: the cursor predates the retained journal, the client must run a full resync (snapshot).</summary>
public sealed class CursorExpiredException() : AppException("Mốc đồng bộ đã quá cũ, cần đồng bộ lại toàn bộ.", 410);

public sealed class SyncChangesHandler(IAppDbContext db, ICurrentUser me, SyncAccessContext access, SyncPolicyService policy)
    : IRequestHandler<SyncChangesQuery, SyncChangesDto>
{
    public async Task<SyncChangesDto> Handle(SyncChangesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        await access.InitAsync(uid, ct);
        var min = await db.ChangeJournal.MinAsync(c => (long?)c.Seq, ct);
        if (min is { } m && q.Cursor + 1 < m) throw new CursorExpiredException();
        var limit = Math.Clamp(q.Limit, 1, 1000);
        var rows = await db.ChangeJournal.AsNoTracking().Where(c => c.Seq > q.Cursor).OrderBy(c => c.Seq).Take(limit).ToListAsync(ct);
        var roots = (await Workspaces.WorkspaceQueries.AccessibleAsync(db, uid, ct)).Select(w => w.Id);
        var rootIds = (await roots.ToListAsync(ct)).ToHashSet();

        var fileIds = rows.Where(r => r.ItemType == ChangeItemType.File).Select(r => r.ItemId).Distinct().ToList();
        var folderIds = rows.Where(r => r.ItemType == ChangeItemType.Folder).Select(r => r.ItemId).Distinct().ToList();
        var files = await db.Files.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.WorkspaceId, f.FolderId, f.Name, f.Revision, f.CurrentVersionId, f.SizeBytes, f.ChecksumSha256, f.ApprovalStatus, f.CreatedBy, f.IsDeleted,
                f.CheckedOutBy, f.PublishedVersionId,
                PubSize = db.FileVersions.Where(pv => pv.Id == f.PublishedVersionId).Select(pv => (long?)pv.SizeBytes).FirstOrDefault(),
                PubSum = db.FileVersions.Where(pv => pv.Id == f.PublishedVersionId).Select(pv => pv.Checksum).FirstOrDefault(),
                Modified = f.UpdatedAt ?? f.CreatedAt }).ToDictionaryAsync(f => f.Id, ct);
        var folders = await db.Folders.AsNoTracking().Where(f => folderIds.Contains(f.Id))
            .Select(f => new { f.Id, f.WorkspaceId, f.ParentId, f.Name, f.Revision, f.IsDeleted, Modified = f.UpdatedAt ?? f.CreatedAt }).ToDictionaryAsync(f => f.Id, ct);

        var changes = new List<SyncChangeDto>();
        var sharerFolders = new HashSet<Guid>();
        foreach (var r in rows)
        {
            var map = await access.MapAsync(r.WorkspaceId, ct);
            var inScope = map is not null && (map.IsRoot || map.HasAnyAccess || rootIds.Contains(r.WorkspaceId));
            SyncChangeDto Bare(string op) => new(r.Seq, op, Name(r.ItemType), r.ItemId, r.WorkspaceId, null, r.OccurredAt, r.DeviceId);

            if (r.ItemType == ChangeItemType.Workspace)
            {
                // Whole root: deleted, or membership / workspace ACL changed (clients ignore roots they do not have).
                changes.Add(Bare(r.Op != ChangeOp.Purge && map?.IsRoot == true ? "permission_changed" : "access_revoked"));
                continue;
            }
            if (r.Op is ChangeOp.Delete or ChangeOp.Purge)
            {
                if (inScope) changes.Add(Bare(Op(r.Op)));
                continue;
            }

            SyncItemDto? item = null;
            if (map is not null && r.ItemType == ChangeItemType.File && files.TryGetValue(r.ItemId, out var f) && !f.IsDeleted)
            {
                var level = map.FileLevel(f.Id, f.FolderId, f.ApprovalStatus, f.CreatedBy, f.CheckedOutBy, f.PublishedVersionId != null);
                var published = level < AccessLevel.Edit && f.PublishedVersionId is { } pid && pid != f.CurrentVersionId;
                if (level >= AccessLevel.View)
                    item = access.File(map, level, f.Id, f.FolderId, f.Name, f.Revision, published ? f.PublishedVersionId : f.CurrentVersionId,
                        published ? f.PubSize ?? f.SizeBytes : f.SizeBytes, published ? f.PubSum : f.ChecksumSha256, f.Modified);
            }
            else if (map is not null && r.ItemType == ChangeItemType.Folder && folders.TryGetValue(r.ItemId, out var fo) && !fo.IsDeleted)
            {
                if (map.FolderLevel(fo.Id) >= AccessLevel.View) item = access.Folder(map, fo.Id, fo.ParentId, fo.Name, fo.Revision, fo.Modified);
            }

            if (item is not null)
            {
                // The person's / group's folder in "Được chia sẻ với tôi" comes first (the client may not have it yet).
                if (item.ParentId == SyncIds.SharerFolder(r.WorkspaceId) && sharerFolders.Add(r.WorkspaceId))
                    changes.Add(new SyncChangeDto(r.Seq, "update_meta", "folder", item.ParentId.Value, r.WorkspaceId, SyncAccessContext.SharerFolder(map!), r.OccurredAt, null));
                // Spec 4.4: a permission change that makes the item visible is "access_granted" (older clients treat it as an upsert).
                changes.Add(Bare(r.Op == ChangeOp.PermissionChanged ? "access_granted" : Op(r.Op)) with { Item = item });
            }
            else if (r.Op is ChangeOp.PermissionChanged or ChangeOp.UpdateMeta
                     && (inScope || r.PrincipalId is { } who && access.Concerns(who))) // the last share taken back: only the person concerned is told
            {
                changes.Add(Bare("access_revoked"));
                // Nothing of that workspace is shared any more: its folder in "Được chia sẻ với tôi" goes too.
                if (map is { IsRoot: false, HasAnyAccess: false } && r.PrincipalId is { } p && access.Concerns(p))
                    changes.Add(new SyncChangeDto(r.Seq, "access_revoked", "folder", SyncIds.SharerFolder(r.WorkspaceId), r.WorkspaceId, null, r.OccurredAt, null));
            }
        }
        var next = rows.Count > 0 ? rows[^1].Seq : q.Cursor;
        return new SyncChangesDto(changes, next, rows.Count == limit);
    }

    private static string Name(ChangeItemType t) => t switch { ChangeItemType.File => "file", ChangeItemType.Folder => "folder", _ => "workspace" };

    private static string Op(ChangeOp op) => op switch
    {
        ChangeOp.Create => "create", ChangeOp.UpdateContent => "update_content", ChangeOp.UpdateMeta => "update_meta", ChangeOp.Rename => "rename",
        ChangeOp.Move => "move", ChangeOp.Delete => "delete", ChangeOp.Restore => "restore", ChangeOp.Purge => "purge", _ => "permission_changed",
    };
}

// ---------------------------------------------------------------- one item

public sealed record GetSyncItemQuery(Guid Id) : IRequest<SyncItemDto>;

public sealed class GetSyncItemHandler(IAppDbContext db, ICurrentUser me, SyncAccessContext access) : IRequestHandler<GetSyncItemQuery, SyncItemDto>
{
    public async Task<SyncItemDto> Handle(GetSyncItemQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await access.InitAsync(uid, ct);
        return await SyncItems.LoadAsync(db, access, q.Id, ct) ?? throw new NotFoundException("Không tìm thấy tài liệu hoặc bạn không có quyền truy cập.");
    }
}

public static class SyncItems
{
    public static async Task<SyncItemDto?> LoadAsync(IAppDbContext db, SyncAccessContext access, Guid id, CancellationToken ct)
    {
        var f = await db.Files.AsNoTracking().Where(x => x.Id == id && !x.IsDeleted)
            .Select(x => new { x.Id, x.WorkspaceId, x.FolderId, x.Name, x.Revision, x.CurrentVersionId, x.SizeBytes, x.ChecksumSha256, x.ApprovalStatus, x.CreatedBy,
                x.CheckedOutBy, x.PublishedVersionId,
                PubSize = db.FileVersions.Where(pv => pv.Id == x.PublishedVersionId).Select(pv => (long?)pv.SizeBytes).FirstOrDefault(),
                PubSum = db.FileVersions.Where(pv => pv.Id == x.PublishedVersionId).Select(pv => pv.Checksum).FirstOrDefault(),
                Modified = x.UpdatedAt ?? x.CreatedAt }).FirstOrDefaultAsync(ct);
        if (f is not null)
        {
            var map = await access.MapAsync(f.WorkspaceId, ct);
            var level = map?.FileLevel(f.Id, f.FolderId, f.ApprovalStatus, f.CreatedBy, f.CheckedOutBy, f.PublishedVersionId != null) ?? AccessLevel.None;
            var published = level < AccessLevel.Edit && f.PublishedVersionId is { } pid && pid != f.CurrentVersionId;
            return level < AccessLevel.View ? null : access.File(map!, level, f.Id, f.FolderId, f.Name, f.Revision, published ? f.PublishedVersionId : f.CurrentVersionId,
                published ? f.PubSize ?? f.SizeBytes : f.SizeBytes, published ? f.PubSum : f.ChecksumSha256, f.Modified);
        }
        var fo = await db.Folders.AsNoTracking().Where(x => x.Id == id && !x.IsDeleted)
            .Select(x => new { x.Id, x.WorkspaceId, x.ParentId, x.Name, x.Revision, Modified = x.UpdatedAt ?? x.CreatedAt }).FirstOrDefaultAsync(ct);
        if (fo is null)
        {
            var ws = SyncIds.WorkspaceOfSharerFolder(id);
            var smap = await db.Workspaces.AnyAsync(w => w.Id == ws, ct) ? await access.MapAsync(ws, ct) : null;
            return smap is { IsRoot: false, HasAnyAccess: true } ? SyncAccessContext.SharerFolder(smap) : null;
        }
        var fmap = await access.MapAsync(fo.WorkspaceId, ct);
        return fmap is null || fmap.FolderLevel(fo.Id) < AccessLevel.View ? null : access.Folder(fmap, fo.Id, fo.ParentId, fo.Name, fo.Revision, fo.Modified);
    }
}
