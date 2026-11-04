using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Perm-Phase 1: builds the new storage and permission model (Spaces, drives, libraries, securables, principals, flattened memberships,
/// Space groups, permission levels, ACL scopes, entries and scope_grants) from the current model (workspaces, members, folders, files,
/// resource_permissions, shares, groups, units, roles). The current model stays the source of writes until Perm-Phase 2, so this runs on a
/// schedule and on demand; ids of derived rows are stable (<see cref="StableGuid"/>). Rules: docs/migration-permissions.md, section 4a.
/// </summary>
public sealed class PermissionProjection(IAppDbContext db, TimeProvider clock)
{
    public sealed record Result(int Securables, int Scopes, int Entries, int Grants, int Principals, int Memberships);

    public static Guid LibraryOf(Guid workspaceId) => StableGuid.From(workspaceId, "library");
    public static Guid GroupOf(Guid workspaceId, SpaceGroupKind kind) => StableGuid.From(workspaceId, "group:" + kind);
    public static Guid UnitTreeOf(Guid orgId) => StableGuid.From(orgId, "org-tree");

    /// <summary>Old access level → permission level (old "Xem" could not download: "Chỉ xem (không tải)").</summary>
    public static Guid LevelOf(AccessLevel level) => level switch
    {
        AccessLevel.Manage => SystemLevels.Owner,
        AccessLevel.Edit => SystemLevels.Editor,
        AccessLevel.Download => SystemLevels.Viewer,
        _ => SystemLevels.ViewOnly,
    };

    private static SpaceGroupKind GroupFor(WorkspaceMemberRole role) => role switch
    {
        WorkspaceMemberRole.Owner or WorkspaceMemberRole.Manager => SpaceGroupKind.Owners,
        WorkspaceMemberRole.Editor => SpaceGroupKind.Members,
        WorkspaceMemberRole.Uploader => SpaceGroupKind.Contributors,
        WorkspaceMemberRole.Approver => SpaceGroupKind.Approvers,
        _ => SpaceGroupKind.Visitors,
    };

    private static readonly (SpaceGroupKind Kind, string Name, Guid Level)[] DefaultGroups =
    [
        (SpaceGroupKind.Owners, "Chủ sở hữu", SystemLevels.Owner),
        (SpaceGroupKind.Members, "Thành viên", SystemLevels.Editor),
        (SpaceGroupKind.Contributors, "Đóng góp", SystemLevels.Contributor),
        (SpaceGroupKind.Approvers, "Duyệt nội dung", SystemLevels.Approver),
        // Old "Người xem" members could view but not download: projected as "Chỉ xem (không tải)" so nobody gains a right. Spaces
        // created in the new model (Perm-Phase 5) give their visitors the spec's default, "Người xem".
        (SpaceGroupKind.Visitors, "Khách xem", SystemLevels.ViewOnly),
    ];

    public static string Label(Guid id) => id.ToString("N");

    private sealed record Grant(PrincipalKind Kind, Guid Principal, Guid Level, DateTime? ExpiresAt, Guid? GrantedBy, DateTime GrantedAt, AclSource Source, bool Inherits);

    /// <summary>Everything, from scratch.</summary>
    public Task<Result> RebuildAsync(CancellationToken ct) => ProjectAsync(null, null, ct);

    /// <summary>
    /// Projects only some workspaces (their nodes, Space groups and ACLs) and the memberships of some users — what one change touched
    /// (<see cref="IAccessModelSync"/>); <c>null</c> = all. Members of the projected workspaces (before and after) get their memberships
    /// recomputed too. Runs inside the caller's transaction when there is one.
    /// </summary>
    public async Task<Result> ProjectAsync(IReadOnlyCollection<Guid>? workspaceIds, IReadOnlyCollection<Guid>? userIds, CancellationToken ct)
    {
        var full = workspaceIds is null && userIds is null;
        var wsIds = workspaceIds ?? [];
        var now = clock.GetUtcNow().UtcDateTime;
        await EnsureLevelsAsync(ct);
        var levels = await db.PermissionLevels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Permissions, ct);

        var wsQuery = db.Workspaces.AsNoTracking();
        var workspaces = full ? await wsQuery.ToListAsync(ct) : await wsQuery.Where(w => wsIds.Contains(w.Id)).ToListAsync(ct);
        var members = full ? await db.WorkspaceMembers.AsNoTracking().ToListAsync(ct)
            : await db.WorkspaceMembers.AsNoTracking().Where(m => wsIds.Contains(m.WorkspaceId)).ToListAsync(ct);
        var folders = await db.Folders.AsNoTracking().Where(f => !f.IsDeleted && (full || wsIds.Contains(f.WorkspaceId)))
            .Select(f => new { f.Id, f.WorkspaceId, f.ParentId, f.Name, f.Path, f.BreakInheritance }).ToListAsync(ct);
        var files = await db.Files.AsNoTracking().Where(f => !f.IsDeleted && (full || wsIds.Contains(f.WorkspaceId)))
            .Select(f => new { f.Id, f.WorkspaceId, f.FolderId, f.Name, f.BreakInheritance }).ToListAsync(ct);
        var resourceIds = full ? null : wsIds.Concat(folders.Select(f => f.Id)).Concat(files.Select(f => f.Id)).ToHashSet();
        var acl = full ? await db.ResourcePermissions.AsNoTracking().ToListAsync(ct)
            : await db.ResourcePermissions.AsNoTracking().Where(r => resourceIds!.Contains(r.ResourceId)).ToListAsync(ct);
        var shares = full ? await db.Shares.AsNoTracking().ToListAsync(ct)
            : await db.Shares.AsNoTracking().Where(r => resourceIds!.Contains(r.ResourceId)).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.FullName, u.OrganizationId }).ToListAsync(ct);
        var orgs = await db.Organizations.AsNoTracking().Select(o => new { o.Id, o.Name, o.Path }).ToListAsync(ct);
        var groups = await db.UserGroups.AsNoTracking().Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        var groupMembers = await db.UserGroupMembers.AsNoTracking().Select(m => new { m.GroupId, m.UserId }).ToListAsync(ct);
        var nesting = await db.GroupNestings.AsNoTracking().ToListAsync(ct);
        var roles = await db.Roles.AsNoTracking().Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        var userRoles = await db.UserRoles.AsNoTracking().Select(r => new { r.UserId, r.RoleId }).ToListAsync(ct);
        var orgPath = orgs.ToDictionary(o => o.Id, o => o.Path);

        // ---- principals
        var principals = new List<Principal>
        {
            new() { Id = StableGuid.Everyone, Kind = PrincipalKind.Everyone, DisplayName = "Mọi người trong đơn vị" },
        };
        principals.AddRange(users.Select(u => new Principal { Id = u.Id, Kind = PrincipalKind.User, RefId = u.Id, DisplayName = u.FullName }));
        principals.AddRange(groups.Select(g => new Principal { Id = g.Id, Kind = PrincipalKind.Group, RefId = g.Id, DisplayName = g.Name }));
        principals.AddRange(roles.Select(r => new Principal { Id = r.Id, Kind = PrincipalKind.Role, RefId = r.Id, DisplayName = r.Name }));
        foreach (var o in orgs)
        {
            principals.Add(new Principal { Id = o.Id, Kind = PrincipalKind.OrgUnit, RefId = o.Id, DisplayName = o.Name });
            principals.Add(new Principal { Id = UnitTreeOf(o.Id), Kind = PrincipalKind.OrgUnitTree, RefId = o.Id, DisplayName = o.Name + " (gồm đơn vị con)" });
        }

        // ---- spaces, drives, libraries, Space groups, securables, scopes
        var spaces = new List<Space>();
        var drives = new List<PersonalDrive>();
        var libraries = new List<Library>();
        var spaceGroups = new List<SpaceGroup>();
        var spaceGroupMembers = new List<SpaceGroupMember>();
        var securables = new List<Securable>();
        var entries = new List<AclEntry>();
        var direct = new Dictionary<Guid, List<Grant>>();
        void AddDirect(Guid node, Grant g)
        {
            if (!direct.TryGetValue(node, out var list)) direct[node] = list = [];
            list.Add(g);
        }
        (PrincipalKind, Guid)? Map(PrincipalType type, Guid id) => type switch
        {
            PrincipalType.User => (PrincipalKind.User, id),
            PrincipalType.Group => (PrincipalKind.Group, id),
            PrincipalType.Role => (PrincipalKind.Role, id),
            PrincipalType.Organization => (PrincipalKind.OrgUnitTree, UnitTreeOf(id)), // old unit grants included sub-units
            _ => null,
        };
        foreach (var r in acl)
            if (Map(r.PrincipalType, r.PrincipalId) is var (k, pid))
                AddDirect(r.ResourceId, new Grant(k, pid, LevelOf(r.AccessLevel), r.ExpiresAt, r.CreatedBy, r.CreatedAt, AclSource.Direct, r.Inherit));
        foreach (var s in shares.Where(s => s.ResourceType != ResourceType.Workspace))
            if (Map(s.SharedWithType, s.SharedWithId) is var (k, pid))
                AddDirect(s.ResourceId, new Grant(k, pid, LevelOf(s.AccessLevel), null, s.SharedBy, s.CreatedAt, AclSource.Direct, true));

        var foldersByWs = folders.ToLookup(f => f.WorkspaceId);
        var filesByWs = files.ToLookup(f => f.WorkspaceId);
        foreach (var w in workspaces)
        {
            var personal = w.Type == WorkspaceType.Personal;
            if (personal) drives.Add(new PersonalDrive { Id = w.Id, UserId = w.OwnerId, QuotaBytes = w.QuotaBytes, UsedBytes = w.UsedBytes });
            else spaces.Add(new Space
            {
                Id = w.Id, TenantId = StableGuid.Tenant, Name = w.Name, Description = w.Description, OrgUnitId = w.OrganizationId,
                Status = SpaceStatus.Active, QuotaBytes = w.QuotaBytes, UsedBytes = w.UsedBytes,
            });
            var libraryId = LibraryOf(w.Id);
            libraries.Add(new Library
            {
                Id = libraryId, SpaceId = personal ? null : w.Id, PersonalDriveId = personal ? w.Id : null, Name = "Tài liệu", IsDefault = true,
            });
            foreach (var (kind, name, level) in DefaultGroups)
                spaceGroups.Add(new SpaceGroup { Id = GroupOf(w.Id, kind), RootId = w.Id, Kind = kind, Name = name, PermissionLevelId = level });
            var memberGroups = members.Where(m => m.WorkspaceId == w.Id).Select(m => (m.UserId, Kind: GroupFor(m.MemberRole))).ToList();
            if (!personal) memberGroups.Add((w.OwnerId, SpaceGroupKind.Owners));
            foreach (var (userId, kind) in memberGroups.Distinct())
                spaceGroupMembers.Add(new SpaceGroupMember { SpaceGroupId = GroupOf(w.Id, kind), PrincipalId = userId });
            principals.AddRange(DefaultGroups.Select(g => new Principal
            {
                Id = GroupOf(w.Id, g.Kind), Kind = PrincipalKind.SpaceGroup, RefId = w.Id, DisplayName = $"{g.Name} — {w.Name}",
            }));

            var owner = personal ? w.OwnerId : (Guid?)null;
            var rootPath = Label(w.Id);
            Securable Node(Guid id, SecurableKind kind, Guid? parent, string path, string name) => new()
            {
                Id = id, Kind = kind, ParentId = parent, LibraryId = kind is SecurableKind.Space or SecurableKind.Drive ? null : libraryId,
                SpaceId = personal ? null : w.Id, PersonalDriveId = personal ? w.Id : null, OwnerUserId = owner, Path = path, Name = name,
            };

            // Root: Space groups + grants on the workspace itself.
            var rootGrants = DefaultGroups.Select(g => new Grant(PrincipalKind.SpaceGroup, GroupOf(w.Id, g.Kind), g.Level, null, null, w.CreatedAt, AclSource.SpaceDefault, true))
                .Concat(direct.GetValueOrDefault(w.Id) ?? []).ToList();
            var ownersOnly = rootGrants.Where(g => g.Source == AclSource.SpaceDefault && g.Principal == GroupOf(w.Id, SpaceGroupKind.Owners)).ToList();
            var root = Node(w.Id, personal ? SecurableKind.Drive : SecurableKind.Space, null, rootPath, w.Name);
            var scopeGrants = new Dictionary<Guid, List<Grant>>();
            MakeScope(root, rootGrants);
            var library = Node(libraryId, SecurableKind.Library, w.Id, $"{rootPath}.{Label(libraryId)}", "Tài liệu");
            Place(library, root, breaks: false);

            var placed = new Dictionary<Guid, Securable> { [w.Id] = root, [libraryId] = library };
            foreach (var f in foldersByWs[w.Id].OrderBy(f => f.Path.Length))
            {
                var parent = f.ParentId is { } pid && placed.TryGetValue(pid, out var p) ? p : library;
                var node = Node(f.Id, SecurableKind.Folder, parent.Id, $"{parent.Path}.{Label(f.Id)}", f.Name);
                Place(node, parent, f.BreakInheritance);
                placed[f.Id] = node;
            }
            foreach (var f in filesByWs[w.Id])
            {
                var parent = f.FolderId is { } pid && placed.TryGetValue(pid, out var p) ? p : library;
                Place(Node(f.Id, SecurableKind.File, parent.Id, $"{parent.Path}.{Label(f.Id)}", f.Name), parent, f.BreakInheritance);
            }

            void MakeScope(Securable node, List<Grant> grants)
            {
                node.HasUniqueAcl = true;
                node.AclScopeId = node.Id;
                scopeGrants[node.Id] = grants;
                foreach (var g in grants)
                    entries.Add(new AclEntry
                    {
                        ScopeId = node.Id, PrincipalKind = g.Kind, PrincipalId = g.Principal, PermissionLevelId = g.Level, ExpiresAt = g.ExpiresAt,
                        GrantedBy = g.GrantedBy, GrantedAt = g.GrantedAt, Source = g.Source,
                    });
                securables.Add(node);
            }

            void Place(Securable node, Securable parent, bool breaks)
            {
                var own = direct.GetValueOrDefault(node.Id) ?? [];
                var parentScope = scopeGrants[parent.AclScopeId];
                // Grants of the parent that do not inherit ("Chỉ mục này") stop here: the child needs its own copy without them.
                var parentOwn = direct.GetValueOrDefault(parent.Id) ?? [];
                var parentStops = parent.HasUniqueAcl && parentOwn.Any(g => !g.Inherits);
                if (!breaks && own.Count == 0 && !parentStops)
                {
                    node.AclScopeId = parent.AclScopeId;
                    securables.Add(node);
                    return;
                }
                var inherited = breaks
                    ? ownersOnly // old rule: below a break only the workspace owners and managers keep their access
                    : parentScope.Where(g => !(parentStops && parentOwn.Contains(g) && !g.Inherits)).ToList();
                MakeScope(node, [.. inherited.Select(g => g.Source == AclSource.Direct ? g with { Source = AclSource.Inherited } : g), .. own]);
            }
        }

        // ---- memberships (flattened), for every user (full) or for those a change concerns
        var oldGroups = full ? [] : await db.SpaceGroups.AsNoTracking().Where(g => wsIds.Contains(g.RootId)).Select(g => g.Id).ToListAsync(ct);
        var otherSpaceGroupMembers = full ? []
            : await db.SpaceGroupMembers.AsNoTracking().Where(m => !oldGroups.Contains(m.SpaceGroupId)).ToListAsync(ct);
        var affected = full ? users.Select(u => u.Id).ToHashSet()
            : (userIds ?? []).Concat(spaceGroupMembers.Select(m => m.PrincipalId))
                .Concat(await db.SpaceGroupMembers.AsNoTracking().Where(m => oldGroups.Contains(m.SpaceGroupId)).Select(m => m.PrincipalId).ToListAsync(ct))
                .ToHashSet();
        var groupsOf = groupMembers.ToLookup(m => m.UserId, m => m.GroupId);
        var parentsOfGroup = nesting.ToLookup(n => n.ChildGroupId, n => n.ParentGroupId);
        var spaceGroupsOf = spaceGroupMembers.Concat(otherSpaceGroupMembers).ToLookup(m => m.PrincipalId, m => m.SpaceGroupId);
        var rolesOf = userRoles.ToLookup(r => r.UserId, r => r.RoleId);
        var memberships = new List<PrincipalMembership>();
        foreach (var u in users.Where(u => affected.Contains(u.Id)))
        {
            var set = new HashSet<Guid> { u.Id, StableGuid.Everyone };
            foreach (var g in Closure(groupsOf[u.Id], parentsOfGroup)) set.Add(g);
            foreach (var r in rolesOf[u.Id]) set.Add(r);
            if (u.OrganizationId is { } org)
            {
                set.Add(org);
                // The unit and every unit above it: a grant "unit and sub-units" on any of them reaches this user.
                foreach (var id in UnitChain(orgPath.GetValueOrDefault(org), org)) set.Add(UnitTreeOf(id));
            }
            // Space groups whose members are this user, one of their groups or one of their units.
            foreach (var p in set.ToList())
                foreach (var sg in spaceGroupsOf[p]) set.Add(sg);
            memberships.AddRange(set.Select(p => new PrincipalMembership { UserId = u.Id, PrincipalId = p }));
        }

        // ---- scope_grants: OR of the live entries per scope and principal
        var grants = entries.Where(e => e.ExpiresAt is null || e.ExpiresAt > now)
            .GroupBy(e => (e.ScopeId, e.PrincipalId))
            .Select(g => new ScopeGrant { ScopeId = g.Key.ScopeId, PrincipalId = g.Key.PrincipalId, Permissions = g.Aggregate(AccessRight.None, (m, e) => m | levels[e.PermissionLevelId]) })
            .ToList();
        // Versions only grow (cache keys of the permission engine): ticks of the rebuild.
        var scopes = securables.Where(s => s.HasUniqueAcl).Select(s => new AclScope { ScopeId = s.Id, AclVersion = now.Ticks }).ToList();

        Detach();
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (full)
        {
            await db.ScopeGrants.ExecuteDeleteAsync(ct);
            await db.AclEntries.ExecuteDeleteAsync(ct);
            await db.AclScopes.ExecuteDeleteAsync(ct);
            await db.Securables.ExecuteDeleteAsync(ct);
            await db.SpaceGroupMembers.ExecuteDeleteAsync(ct);
            await db.SpaceGroups.ExecuteDeleteAsync(ct);
            await db.Libraries.ExecuteDeleteAsync(ct);
            await db.Spaces.ExecuteDeleteAsync(ct);
            await db.PersonalDrives.ExecuteDeleteAsync(ct);
            await db.PrincipalMemberships.ExecuteDeleteAsync(ct);
            await db.Principals.ExecuteDeleteAsync(ct);
        }
        else
        {
            var nodes = db.Securables.Where(n => wsIds.Contains(n.SpaceId ?? n.PersonalDriveId ?? Guid.Empty)).Select(n => n.Id);
            await db.ScopeGrants.Where(g => nodes.Contains(g.ScopeId)).ExecuteDeleteAsync(ct);
            await db.AclEntries.Where(e => nodes.Contains(e.ScopeId)).ExecuteDeleteAsync(ct);
            await db.AclScopes.Where(a => nodes.Contains(a.ScopeId)).ExecuteDeleteAsync(ct);
            await db.Securables.Where(n => wsIds.Contains(n.SpaceId ?? n.PersonalDriveId ?? Guid.Empty)).ExecuteDeleteAsync(ct);
            await db.SpaceGroupMembers.Where(m => oldGroups.Contains(m.SpaceGroupId)).ExecuteDeleteAsync(ct);
            await db.SpaceGroups.Where(g => wsIds.Contains(g.RootId)).ExecuteDeleteAsync(ct);
            await db.Libraries.Where(l => wsIds.Contains(l.SpaceId ?? l.PersonalDriveId ?? Guid.Empty)).ExecuteDeleteAsync(ct);
            await db.Spaces.Where(x => wsIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            await db.PersonalDrives.Where(x => wsIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            await db.PrincipalMemberships.Where(m => affected.Contains(m.UserId)).ExecuteDeleteAsync(ct);
            // Users, groups, roles and units: few rows, always refreshed; Space group principals only for these workspaces.
            await db.Principals.Where(x => x.Kind != PrincipalKind.SpaceGroup || wsIds.Contains(x.RefId ?? Guid.Empty)).ExecuteDeleteAsync(ct);
        }
        db.Principals.AddRange(principals.DistinctBy(p => p.Id));
        db.Spaces.AddRange(spaces);
        db.PersonalDrives.AddRange(drives);
        db.Libraries.AddRange(libraries);
        db.SpaceGroups.AddRange(spaceGroups);
        db.SpaceGroupMembers.AddRange(spaceGroupMembers.DistinctBy(m => (m.SpaceGroupId, m.PrincipalId)));
        db.Securables.AddRange(securables);
        db.AclScopes.AddRange(scopes);
        db.AclEntries.AddRange(entries);
        db.ScopeGrants.AddRange(grants);
        db.PrincipalMemberships.AddRange(memberships);
        await db.SaveChangesAsync(ct);
        await BumpMembershipVersionsAsync(affected, ct);
        Detach();
        if (own is not null)
        {
            await own.CommitAsync(ct);
            await own.DisposeAsync();
        }
        return new Result(securables.Count, scopes.Count, entries.Count, grants.Count, principals.Count, memberships.Count);
    }

    /// <summary>
    /// The rows of the new model are rewritten with bulk deletes the change tracker does not see: forget what an earlier projection in
    /// the same unit of work left tracked, and what this one added.
    /// </summary>
    private void Detach()
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is Principal or PrincipalMembership or Space or PersonalDrive or Library or SpaceGroup
                     or SpaceGroupMember or Securable or AclScope or AclEntry or ScopeGrant or MembershipVersion).ToList())
            e.State = EntityState.Detached;
    }

    /// <summary>Seeds or refreshes the built-in permission levels (custom levels are left alone).</summary>
    public async Task EnsureLevelsAsync(CancellationToken ct)
    {
        var existing = await db.PermissionLevels.Where(l => l.IsSystem).ToDictionaryAsync(l => l.Id, ct);
        foreach (var l in SystemLevels.All)
        {
            if (!existing.TryGetValue(l.Id, out var row)) db.PermissionLevels.Add(row = new PermissionLevel { Id = l.Id, IsSystem = true });
            row.Code = l.Code;
            row.Name = l.Name;
            row.Description = l.Description;
            row.Permissions = l.Permissions;
            row.SortOrder = l.SortOrder;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task BumpMembershipVersionsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var known = await db.MembershipVersions.ToDictionaryAsync(v => v.UserId, ct);
        foreach (var id in userIds)
        {
            if (known.TryGetValue(id, out var v)) v.Version++;
            else db.MembershipVersions.Add(new MembershipVersion { UserId = id, Version = 1 });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Groups of a user and every group containing them (nesting), without looping on cycles.</summary>
    public static IEnumerable<Guid> Closure(IEnumerable<Guid> start, ILookup<Guid, Guid> parents)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>(start);
        while (stack.Count > 0)
        {
            var g = stack.Pop();
            if (!seen.Add(g)) continue;
            foreach (var p in parents[g]) stack.Push(p);
        }
        return seen;
    }

    /// <summary>Ids of a unit and all its ancestors, from its materialised path "/root/child/".</summary>
    public static IEnumerable<Guid> UnitChain(string? path, Guid self)
    {
        var ids = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        if (!ids.Contains(self)) ids.Add(self);
        return ids;
    }
}
