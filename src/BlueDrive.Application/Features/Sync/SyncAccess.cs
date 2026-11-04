using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sync;

/// <summary>
/// Effective access of one user to every folder / file of a workspace, on the new permission model: the workspace's nodes and the user's
/// rights on each ACL scope are loaded once, then <see cref="Access.Restrictions"/> (the engine's rules) gives each item its rights. Used by
/// the sync snapshot and change feed so a 100,000-item tree is evaluated with a handful of queries instead of one per item.
/// </summary>
public sealed class WorkspaceAccessMap
{
    public Guid WorkspaceId { get; private set; }
    public WorkspaceType WorkspaceType { get; private set; }
    public string WorkspaceName { get; private set; } = "";
    public bool Locked { get; private set; }
    /// <summary>The user may see the workspace itself (owner, Space member, grant on the Space): it is one of their sync roots.</summary>
    public bool IsRoot { get; private set; }
    public bool CanApproveAll { get; private set; }
    /// <summary>Name of the folder grouping this workspace in "Được chia sẻ với tôi": the owner of a personal workspace, else the workspace.</summary>
    public string SharerName { get; private set; } = "";

    private readonly Dictionary<Guid, (Guid? Parent, AccessLevel Level)> _folders = [];
    private readonly Dictionary<Guid, (Guid Scope, bool Owner)> _fileNodes = [];
    private readonly Dictionary<Guid, (Domain.Permissions.AccessRight Direct, Domain.Permissions.AccessRight Membership)> _scopes = [];
    /// <summary>"Chỉ mục này" grants: they count only for the scope's own node.</summary>
    private readonly Dictionary<Guid, (Domain.Permissions.AccessRight Direct, Domain.Permissions.AccessRight Membership)> _selfOnly = [];
    private Access.Restrictions.User _user = null!;
    /// <summary>Effective sensitivity label of each node (own, nearest folder above, Space default) — spec 7.1.</summary>
    private readonly Dictionary<Guid, SensitivityLabel?> _labels = [];
    private bool _inSpace;
    private AccessLevel _rootLevel;

    public static async Task<WorkspaceAccessMap?> LoadAsync(IAppDbContext db, PrincipalSet p, IReadOnlySet<string> perms, Guid workspaceId, DateTime now, CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => new { w.Id, w.OwnerId, w.IsLocked, w.Type, w.Name, w.DefaultLabelId }).FirstOrDefaultAsync(ct);
        if (ws is null) return null;
        var principals = await db.PrincipalMemberships.Where(m => m.UserId == p.UserId).Select(m => m.PrincipalId).ToListAsync(ct);
        var user = Access.Restrictions.User.From(p.UserId, perms, principals);
        if (!user.CanViewDocs) return null;

        var nodes = await db.Securables.AsNoTracking().Where(n => (n.SpaceId ?? n.PersonalDriveId) == workspaceId && !n.IsDeleted)
            .Select(n => new { n.Id, n.Kind, n.ParentId, n.AclScopeId, n.OwnerUserId }).ToListAsync(ct);
        var scopeIds = nodes.Select(n => n.AclScopeId).Distinct().ToList();
        var live = await db.AclEntries.AsNoTracking()
            .Where(e => scopeIds.Contains(e.ScopeId) && principals.Contains(e.PrincipalId) && (e.ExpiresAt == null || e.ExpiresAt > now))
            .Join(db.PermissionLevels, e => e.PermissionLevelId, l => l.Id, (e, l) => new { e.ScopeId, e.Source, e.AppliesToChildren, l.Permissions }).ToListAsync(ct);
        var sharer = ws.Type == WorkspaceType.Personal
            ? await db.Users.AsNoTracking().Where(u => u.Id == ws.OwnerId).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;

        var map = new WorkspaceAccessMap
        {
            WorkspaceId = ws.Id, WorkspaceType = ws.Type, WorkspaceName = ws.Name, Locked = ws.IsLocked, _user = user, _inSpace = ws.Type != WorkspaceType.Personal,
            SharerName = string.IsNullOrWhiteSpace(sharer) ? ws.Name : sharer.Trim(),
        };
        foreach (var g in live.Where(e => !e.AppliesToChildren).GroupBy(e => e.ScopeId))
            map._selfOnly[g.Key] = (g.Where(e => e.Source != AclSource.SpaceDefault).Aggregate(Domain.Permissions.AccessRight.None, (m, e) => m | e.Permissions),
                g.Where(e => e.Source == AclSource.SpaceDefault).Aggregate(Domain.Permissions.AccessRight.None, (m, e) => m | e.Permissions));
        foreach (var g in live.Where(e => e.AppliesToChildren).GroupBy(e => e.ScopeId))
            map._scopes[g.Key] = (g.Where(e => e.Source != AclSource.SpaceDefault).Aggregate(Domain.Permissions.AccessRight.None, (m, e) => m | e.Permissions),
                g.Where(e => e.Source == AclSource.SpaceDefault).Aggregate(Domain.Permissions.AccessRight.None, (m, e) => m | e.Permissions));

        // Labels: own (file / folder), else the nearest folder's above, else the Space's default.
        var labels = await db.SensitivityLabels.AsNoTracking().Where(l => l.IsActive).ToDictionaryAsync(l => l.Id, ct);
        if (labels.Count > 0)
        {
            var own = (await db.Folders.AsNoTracking().Where(f => f.WorkspaceId == workspaceId && f.LabelId != null).Select(f => new { f.Id, f.LabelId }).ToListAsync(ct))
                .Concat(await db.Files.AsNoTracking().Where(f => f.WorkspaceId == workspaceId && f.LabelId != null).Select(f => new { f.Id, f.LabelId }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => labels.GetValueOrDefault(x.LabelId!.Value));
            var parents = nodes.ToDictionary(n => n.Id, n => n.ParentId);
            SensitivityLabel? Effective(Guid id)
            {
                if (map._labels.TryGetValue(id, out var known)) return known;
                var l = own.GetValueOrDefault(id) ?? (parents.GetValueOrDefault(id) is { } parent ? Effective(parent) : ws.DefaultLabelId is { } d ? labels.GetValueOrDefault(d) : null);
                return map._labels[id] = l;
            }
            foreach (var n in nodes) Effective(n.Id);
        }

        // A Library sits between the workspace and its top folders: for the features, a top folder's parent is the workspace (null).
        var library = Access.PermissionProjection.LibraryOf(workspaceId);
        var rootRights = Domain.Permissions.AccessRight.None;
        foreach (var n in nodes)
        {
            var owner = n.OwnerUserId == p.UserId;
            switch (n.Kind)
            {
                case SecurableKind.Space or SecurableKind.Drive:
                    rootRights = map.Rights(n.Id, n.AclScopeId, owner, null);
                    break;
                case SecurableKind.Folder:
                    map._folders[n.Id] = (n.ParentId == library ? null : n.ParentId, Access.ReferencePermissionEvaluator.ToLevel(map.Rights(n.Id, n.AclScopeId, owner, null)));
                    break;
                case SecurableKind.File:
                    map._fileNodes[n.Id] = (n.AclScopeId, owner);
                    break;
            }
        }
        map._rootLevel = Access.ReferencePermissionEvaluator.ToLevel(rootRights);
        map.IsRoot = map._rootLevel > AccessLevel.None;
        map.CanApproveAll = (rootRights & Domain.Permissions.AccessRight.ApproveItems) != 0;
        return map;
    }

    /// <summary>Rights before the lock restriction (callers check <see cref="Locked"/>), as the engine computes them.</summary>
    private Domain.Permissions.AccessRight Rights(Guid node, Guid scope, bool owner, Access.Restrictions.FileFacts? file)
    {
        var (direct, membership) = _scopes.GetValueOrDefault(scope);
        if (node == scope && _selfOnly.TryGetValue(scope, out var own)) { direct |= own.Direct; membership |= own.Membership; }
        return Access.Labels.Apply(_labels.GetValueOrDefault(node), _inSpace, membership, Access.Restrictions.Apply(_user, owner, direct, membership, file, locked: false));
    }

    public AccessLevel FolderLevel(Guid folderId) => _folders.TryGetValue(folderId, out var f) ? f.Level : AccessLevel.None;

    public bool FolderExists(Guid folderId) => _folders.ContainsKey(folderId);

    /// <summary>
    /// A file whose label forbids syncing to computers (spec 7.1), or that one may only view ("Chỉ xem": no download, spec 11), is not part of
    /// the synced tree.
    /// </summary>
    public AccessLevel FileLevel(Guid fileId, Guid? folderId, ApprovalStatus approval, Guid? createdBy, Guid? checkedOutBy = null, bool published = true)
    {
        if (!_fileNodes.TryGetValue(fileId, out var n) || _labels.GetValueOrDefault(fileId) is { BlockSync: true }) return AccessLevel.None;
        var level = Access.ReferencePermissionEvaluator.ToLevel(Rights(fileId, n.Scope, n.Owner,
            new Access.Restrictions.FileFacts(createdBy, approval == ApprovalStatus.Approved, checkedOutBy, published)));
        return level >= AccessLevel.Download ? level : AccessLevel.None;
    }

    /// <summary>Parent as seen by the user: a folder they cannot see is skipped (the item becomes top-level of the shared root).</summary>
    public Guid? VisibleParent(Guid? parentId) => parentId is { } id && FolderLevel(id) >= AccessLevel.View ? id : null;

    public bool HasAnyAccess => _rootLevel > AccessLevel.None || _folders.Values.Any(f => f.Level > AccessLevel.None)
                                || _fileNodes.Any(n => Rights(n.Key, n.Value.Scope, n.Value.Owner, null) != Domain.Permissions.AccessRight.None);
}
