using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sync;

/// <summary>
/// Effective access of one user to every folder / file of a workspace, computed in memory from the ACL, shares, membership
/// and break-inheritance flags with the same rules as <see cref="ResourceAuthorizationService"/>. Used by the sync snapshot
/// and change feed so a 100,000-item tree is evaluated with a handful of queries instead of one per item.
/// </summary>
public sealed class WorkspaceAccessMap
{
    public Guid WorkspaceId { get; private init; }
    public WorkspaceType WorkspaceType { get; private init; }
    public string WorkspaceName { get; private init; } = "";
    public bool Locked { get; private init; }
    /// <summary>Owner, member or workspace-level ACL: the workspace is one of the user's sync roots.</summary>
    public bool IsRoot { get; private init; }
    public bool CanApproveAll { get; private init; }
    /// <summary>Name of the folder grouping this workspace in "Được chia sẻ với tôi": the owner of a personal workspace, else the workspace.</summary>
    public string SharerName { get; private init; } = "";

    private readonly Dictionary<Guid, (Guid? Parent, AccessLevel Level, AccessLevel Down)> _folders = [];
    private readonly Dictionary<Guid, AccessLevel> _fileGrants = [];
    /// <summary>Files with unique permissions: only their own grants (and owner / managers) apply.</summary>
    private HashSet<Guid> _brokenFiles = [];
    private AccessLevel _brokenLevel;
    private AccessLevel _rootLevel;
    private Guid _userId;

    public static async Task<WorkspaceAccessMap?> LoadAsync(IAppDbContext db, PrincipalSet p, IReadOnlySet<string> perms, Guid workspaceId, DateTime now, CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => new { w.Id, w.OwnerId, w.IsLocked, w.Type, w.Name }).FirstOrDefaultAsync(ct);
        if (ws is null || !p.CanViewDocs && !p.IsDocAdmin) return null;
        var principals = p.GroupIds.Concat(p.OrganizationIds).Concat(p.RoleIds).Append(p.UserId).ToList();

        var member = await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == workspaceId && m.UserId == p.UserId)
            .Select(m => (WorkspaceMemberRole?)m.MemberRole).FirstOrDefaultAsync(ct);
        var acl = await db.ResourcePermissions.AsNoTracking()
            .Where(a => (a.ExpiresAt == null || a.ExpiresAt > now) && principals.Contains(a.PrincipalId))
            .Where(a => (a.ResourceType == ResourceType.Workspace && a.ResourceId == workspaceId)
                        || (a.ResourceType == ResourceType.Folder && db.Folders.Any(f => f.Id == a.ResourceId && f.WorkspaceId == workspaceId))
                        || (a.ResourceType == ResourceType.File && db.Files.Any(f => f.Id == a.ResourceId && f.WorkspaceId == workspaceId)))
            .ToListAsync(ct);
        acl = acl.Where(a => p.Matches(a.PrincipalType, a.PrincipalId)).ToList();
        var shares = await db.Shares.AsNoTracking().Where(s => principals.Contains(s.SharedWithId))
            .Where(s => (s.ResourceType == ResourceType.Folder && db.Folders.Any(f => f.Id == s.ResourceId && f.WorkspaceId == workspaceId))
                        || (s.ResourceType == ResourceType.File && db.Files.Any(f => f.Id == s.ResourceId && f.WorkspaceId == workspaceId)))
            .ToListAsync(ct);
        shares = shares.Where(s => p.Matches(s.SharedWithType, s.SharedWithId)).ToList();

        var owner = ws.OwnerId == p.UserId;
        var sharer = ws.Type == WorkspaceType.Personal
            ? await db.Users.AsNoTracking().Where(u => u.Id == ws.OwnerId).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;
        var wsAcl = acl.Where(a => a.ResourceType == ResourceType.Workspace).Select(a => a.AccessLevel).DefaultIfEmpty(AccessLevel.None).Max();
        var isRoot = owner || member.HasValue || wsAcl > AccessLevel.None;
        var map = new WorkspaceAccessMap
        {
            WorkspaceId = ws.Id, WorkspaceType = ws.Type, WorkspaceName = ws.Name, Locked = ws.IsLocked, IsRoot = isRoot, _userId = p.UserId,
            SharerName = string.IsNullOrWhiteSpace(sharer) ? ws.Name : sharer.Trim(),
            CanApproveAll = owner || p.IsDocAdmin && isRoot || member is WorkspaceMemberRole.Owner or WorkspaceMemberRole.Manager or WorkspaceMemberRole.Approver
                            || perms.Contains(Perm.DocApprove),
        };
        // Owner / member levels are capped by the role's doc.edit (PrincipalSet.CapByRole); ACL and shares are not.
        var ownerLevel = owner || (p.IsDocAdmin && isRoot) ? p.CapByRole(AccessLevel.Manage) : AccessLevel.None;
        var full = ownerLevel == AccessLevel.Manage;
        var memberLevel = member is { } r ? p.CapByRole(ResourceAuthorizationService.LevelOf(r)) : AccessLevel.None;
        var managerial = member is WorkspaceMemberRole.Manager or WorkspaceMemberRole.Owner;
        var brokenLevel = Max(ownerLevel, managerial ? memberLevel : AccessLevel.None);
        map._rootLevel = full ? AccessLevel.Manage : Max(ownerLevel, Max(memberLevel, wsAcl));

        var folderSelf = new Dictionary<Guid, AccessLevel>();
        var folderDown = new Dictionary<Guid, AccessLevel>();
        foreach (var a in acl.Where(a => a.ResourceType == ResourceType.Folder))
        {
            folderSelf[a.ResourceId] = Max(folderSelf.GetValueOrDefault(a.ResourceId), a.AccessLevel);
            if (a.Inherit) folderDown[a.ResourceId] = Max(folderDown.GetValueOrDefault(a.ResourceId), a.AccessLevel);
        }
        foreach (var s in shares.Where(s => s.ResourceType == ResourceType.Folder))
        {
            folderSelf[s.ResourceId] = Max(folderSelf.GetValueOrDefault(s.ResourceId), s.AccessLevel);
            folderDown[s.ResourceId] = Max(folderDown.GetValueOrDefault(s.ResourceId), s.AccessLevel);
        }
        foreach (var a in acl.Where(a => a.ResourceType == ResourceType.File)) map._fileGrants[a.ResourceId] = Max(map._fileGrants.GetValueOrDefault(a.ResourceId), a.AccessLevel);
        foreach (var s in shares.Where(s => s.ResourceType == ResourceType.File)) map._fileGrants[s.ResourceId] = Max(map._fileGrants.GetValueOrDefault(s.ResourceId), s.AccessLevel);

        map._brokenFiles = (await db.Files.AsNoTracking().Where(f => f.WorkspaceId == workspaceId && f.BreakInheritance && !f.IsDeleted).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        map._brokenLevel = full ? AccessLevel.Manage : brokenLevel;

        // Parents before children: materialized paths sort that way.
        var folders = await db.Folders.AsNoTracking().Where(f => f.WorkspaceId == workspaceId && !f.IsDeleted)
            .Select(f => new { f.Id, f.ParentId, f.Path, f.BreakInheritance }).OrderBy(f => f.Path).ToListAsync(ct);
        foreach (var f in folders)
        {
            AccessLevel inherited;
            if (full) inherited = AccessLevel.Manage;
            else if (f.BreakInheritance) inherited = brokenLevel;
            else inherited = f.ParentId is { } pid && map._folders.TryGetValue(pid, out var parent) ? parent.Down : map._rootLevel;
            var level = Max(inherited, folderSelf.GetValueOrDefault(f.Id));
            var down = Max(inherited, folderDown.GetValueOrDefault(f.Id));
            map._folders[f.Id] = (f.ParentId, level, down);
        }
        return map;
    }

    public AccessLevel FolderLevel(Guid folderId) => _folders.TryGetValue(folderId, out var f) ? f.Level : AccessLevel.None;

    public bool FolderExists(Guid folderId) => _folders.ContainsKey(folderId);

    public AccessLevel FileLevel(Guid fileId, Guid? folderId, ApprovalStatus approval, Guid? createdBy)
    {
        var inherited = _brokenFiles.Contains(fileId) ? _brokenLevel
            : folderId is { } fid ? (_folders.TryGetValue(fid, out var f) ? f.Down : AccessLevel.None) : _rootLevel;
        var level = Max(inherited, _fileGrants.GetValueOrDefault(fileId));
        // Approval workflow: plain viewers only see approved files (and their own uploads).
        if (approval != ApprovalStatus.Approved && createdBy != _userId && level < AccessLevel.Edit && !CanApproveAll) return AccessLevel.None;
        return level;
    }

    /// <summary>Parent as seen by the user: a folder they cannot see is skipped (the item becomes top-level of the shared root).</summary>
    public Guid? VisibleParent(Guid? parentId) => parentId is { } id && FolderLevel(id) >= AccessLevel.View ? id : null;

    public bool HasAnyAccess => _rootLevel > AccessLevel.None || _folders.Values.Any(f => f.Level > AccessLevel.None) || _fileGrants.Count > 0;

    private static AccessLevel Max(AccessLevel a, AccessLevel b) => a > b ? a : b;
}
