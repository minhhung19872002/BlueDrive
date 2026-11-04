using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Security trimming in SQL (spec 4.4): which items a user may see, expressed as a sub-query on <c>securables</c> × <c>scope_grants</c>
/// so listings, search, dashboards and sync filter before paging. Same rules as <see cref="IItemPermissionService"/>: the owner rule,
/// rights of the user's principals on the item's scope, the role's "doc.view", and files not approved yet visible only to their uploader
/// and to people who may edit, approve or see drafts there.
/// </summary>
public sealed class AccessFilter(IAppDbContext db, IPermissionService rbac)
{
    public sealed record Viewer(Guid UserId, bool CanViewDocs, IReadOnlyList<Guid> Principals);

    private readonly Dictionary<Guid, Viewer> _viewers = [];

    public async Task<Viewer> ForAsync(Guid userId, CancellationToken ct)
    {
        if (_viewers.TryGetValue(userId, out var known)) return known;
        var codes = await rbac.GetPermissionsAsync(userId, ct);
        var principals = await db.PrincipalMemberships.Where(m => m.UserId == userId).Select(m => m.PrincipalId).ToListAsync(ct);
        return _viewers[userId] = new Viewer(userId, codes.Contains(Perm.DocView) || codes.Contains(Perm.DocAdmin), principals);
    }

    /// <summary>Scopes where one of the user's principals holds any of <paramref name="rights"/>.</summary>
    public IQueryable<Guid> Scopes(Viewer v, AccessRight rights) =>
        db.ScopeGrants.Where(g => v.Principals.Contains(g.PrincipalId) && (g.Permissions & rights) != 0).Select(g => g.ScopeId);

    /// <summary>Ids of every item (Space, drive, folder, file) the user may see in a list.</summary>
    public IQueryable<Guid> Visible(Viewer v)
    {
        if (!v.CanViewDocs) return db.Securables.Where(_ => false).Select(s => s.Id);
        var scopes = Scopes(v, AccessRight.ViewItems);
        var selfOnly = SelfOnly(v, AccessRight.ViewItems);
        var deleted = db.Workspaces.Where(w => w.Status == SpaceStatus.Deleted).Select(w => w.Id);
        return db.Securables.Where(s => !s.IsDeleted && !deleted.Contains(s.SpaceId ?? s.PersonalDriveId ?? Guid.Empty)
                                        && (s.OwnerUserId == v.UserId || scopes.Contains(s.AclScopeId) || selfOnly.Contains(s.Id))).Select(s => s.Id);
    }

    /// <summary>Nodes granted "chỉ mục này" (not in scope_grants, which hold what the nodes below inherit).</summary>
    private IQueryable<Guid> SelfOnly(Viewer v, AccessRight rights)
    {
        var now = DateTime.UtcNow;
        return db.AclEntries.Where(e => !e.AppliesToChildren && v.Principals.Contains(e.PrincipalId) && (e.ExpiresAt == null || e.ExpiresAt > now))
            .Join(db.PermissionLevels, e => e.PermissionLevelId, l => l.Id, (e, l) => new { e.ScopeId, l.Permissions })
            .Where(x => (x.Permissions & rights) != 0).Select(x => x.ScopeId);
    }

    public IQueryable<Folder> Folders(IQueryable<Folder> q, Viewer v)
    {
        var visible = Visible(v);
        return q.Where(f => visible.Contains(f.Id));
    }

    public IQueryable<StoredFile> Files(IQueryable<StoredFile> q, Viewer v)
    {
        var visible = Visible(v);
        var reviewing = Scopes(v, AccessRight.EditItems | AccessRight.ApproveItems | AccessRight.ViewDrafts);
        var reviewingSelf = SelfOnly(v, AccessRight.EditItems | AccessRight.ApproveItems | AccessRight.ViewDrafts);
        var reviewable = db.Securables.Where(s => s.OwnerUserId == v.UserId || reviewing.Contains(s.AclScopeId) || reviewingSelf.Contains(s.Id)).Select(s => s.Id);
        return q.Where(f => visible.Contains(f.Id) && (f.ApprovalStatus == ApprovalStatus.Approved || f.CreatedBy == v.UserId || reviewable.Contains(f.Id)));
    }

    /// <summary>Workspaces (Spaces, drives) the user may open, or that hold something shared with them.</summary>
    public IQueryable<Guid> Workspaces(Viewer v)
    {
        var visible = Visible(v);
        return db.Securables.Where(s => visible.Contains(s.Id)).Select(s => s.SpaceId ?? s.PersonalDriveId ?? Guid.Empty).Distinct();
    }

    /// <summary>Workspaces whose root the user may see (member, owner, grant on the Space itself).</summary>
    public IQueryable<Guid> Roots(Viewer v)
    {
        var visible = Visible(v);
        return db.Securables.Where(s => (s.Kind == SecurableKind.Space || s.Kind == SecurableKind.Drive) && visible.Contains(s.Id)).Select(s => s.Id);
    }
}
