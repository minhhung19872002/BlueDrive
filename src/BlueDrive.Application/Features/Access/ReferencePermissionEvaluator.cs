using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// The slow, obviously-correct evaluation of the new model (spec 4.3), used as the yardstick in tests and in the migration check: walks
/// from the node up to the nearest node with its own ACL (never reads <see cref="Securable.AclScopeId"/>), ORs the rights of the live
/// entries of the user's principals, applies the owner rule, then the restrictions (spec 3.4). No cache.
/// </summary>
public sealed class ReferencePermissionEvaluator(IAppDbContext db, IPermissionService rbac, TimeProvider clock)
{
    /// <summary>What is evaluated about the user (loaded once when many items are checked for the same user).</summary>
    public sealed record UserContext(Guid UserId, IReadOnlySet<string> Codes, IReadOnlyList<Guid> Principals);

    public async Task<UserContext> LoadUserAsync(Guid userId, CancellationToken ct) => new(userId, await rbac.GetPermissionsAsync(userId, ct),
        await db.PrincipalMemberships.Where(m => m.UserId == userId).Select(m => m.PrincipalId).ToListAsync(ct));

    public async Task<AccessRight> EvaluateAsync(Guid userId, Guid securableId, CancellationToken ct) =>
        await EvaluateAsync(await LoadUserAsync(userId, ct), securableId, ct);

    public async Task<AccessRight> EvaluateAsync(UserContext user, Guid securableId, CancellationToken ct)
    {
        var userId = user.UserId;
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == securableId && !s.IsDeleted, ct);
        if (node is null) return AccessRight.None;
        var root = node.SpaceId ?? node.PersonalDriveId;
        var codes = user.Codes;
        var docAdmin = codes.Contains(Perm.DocAdmin);
        if (!docAdmin && !codes.Contains(Perm.DocView)) return AccessRight.None;
        var canEditDocs = docAdmin || codes.Contains(Perm.DocEdit);
        if (node.OwnerUserId == userId)
            return Restrict(canEditDocs ? AccessRights.All : AccessRights.All & ~AccessRights.Writes | AccessRight.AddItems | AccessRight.ApproveItems, root, await LockedAsync(root, ct));

        var scope = node;
        while (!scope.HasUniqueAcl && scope.ParentId is { } parent)
            scope = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == parent, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var principals = user.Principals;
        var live = await db.AclEntries.AsNoTracking()
            .Where(e => e.ScopeId == scope.Id && principals.Contains(e.PrincipalId) && (e.ExpiresAt == null || e.ExpiresAt > now))
            .Join(db.PermissionLevels, e => e.PermissionLevelId, l => l.Id, (e, l) => new { e.Source, l.Permissions })
            .ToListAsync(ct);

        // Restriction kept from the current model: a system role without "doc.edit" caps what Space membership and ownership give
        // (not direct grants), keeping upload and approval.
        var rights = AccessRight.None;
        foreach (var e in live)
            rights |= e.Source == AclSource.SpaceDefault && !canEditDocs ? e.Permissions & ~AccessRights.Writes | (e.Permissions & (AccessRight.AddItems | AccessRight.ApproveItems)) : e.Permissions;

        if (node.Kind == SecurableKind.File)
        {
            var file = await db.Files.AsNoTracking().Where(f => f.Id == node.Id).Select(f => new { f.CreatedBy, f.ApprovalStatus }).FirstAsync(ct);
            // "Đóng góp": edit and delete what you created.
            if (rights.Has(AccessRight.EditOwnItemsOnly) && file.CreatedBy == userId && canEditDocs)
                rights |= AccessRight.EditItems | AccessRight.DeleteItems | AccessRight.RenameMove;
            // Not approved yet: only its uploader, editors and approvers see it (ViewDrafts).
            if (file.ApprovalStatus != ApprovalStatus.Approved && file.CreatedBy != userId
                && !rights.Has(AccessRight.EditItems) && !rights.Has(AccessRight.ApproveItems) && !rights.Has(AccessRight.ViewDrafts))
                return AccessRight.None;
        }
        return Restrict(rights, root, await LockedAsync(root, ct));
    }

    private async Task<bool> LockedAsync(Guid? root, CancellationToken ct) =>
        root is { } id && await db.Workspaces.AnyAsync(w => w.Id == id && w.IsLocked, ct);

    /// <summary>Restrictions applied after the OR (spec 3.4): a locked Space keeps only reading.</summary>
    private static AccessRight Restrict(AccessRight rights, Guid? root, bool locked) => locked ? rights & ~AccessRights.Writes : rights;

    /// <summary>The current model's scale (None → Manage), for comparing the two models.</summary>
    public static AccessLevel ToLevel(AccessRight rights) =>
        rights.Has(AccessRight.ManagePermissions) ? AccessLevel.Manage
        : rights.Has(AccessRight.EditItems) ? AccessLevel.Edit
        : rights.Has(AccessRight.Download) ? AccessLevel.Download
        : rights.Has(AccessRight.ViewItems) ? AccessLevel.View
        : AccessLevel.None;
}
