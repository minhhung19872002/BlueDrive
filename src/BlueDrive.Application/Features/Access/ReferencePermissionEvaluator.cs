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
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == securableId && !s.IsDeleted, ct);
        if (node is null) return AccessRight.None;
        var there = node.Kind switch
        {
            SecurableKind.File => await db.Files.AnyAsync(f => f.Id == node.Id && !f.IsDeleted, ct),
            SecurableKind.Folder => await db.Folders.AnyAsync(f => f.Id == node.Id && !f.IsDeleted, ct),
            SecurableKind.Space or SecurableKind.Drive => await db.Workspaces.AnyAsync(w => w.Id == node.Id, ct),
            _ => true,
        };
        if (!there) return AccessRight.None;
        var who = Restrictions.User.From(user.UserId, user.Codes, user.Principals);

        // Nearest node with its own ACL, found by walking up (never through AclScopeId).
        var scope = node;
        while (!scope.HasUniqueAcl && scope.ParentId is { } parent)
            scope = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == parent, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var direct = AccessRight.None;
        var membership = AccessRight.None;
        foreach (var e in await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == scope.Id).ToListAsync(ct))
        {
            if (!user.Principals.Contains(e.PrincipalId) || e.ExpiresAt is { } until && until <= now) continue;
            if (!e.AppliesToChildren && scope.Id != node.Id) continue; // "chỉ mục này"
            var level = await db.PermissionLevels.AsNoTracking().FirstAsync(l => l.Id == e.PermissionLevelId, ct);
            if (e.Source == AclSource.SpaceDefault) membership |= level.Permissions;
            else direct |= level.Permissions;
        }
        Restrictions.FileFacts? file = node.Kind != SecurableKind.File ? null
            : await db.Files.AsNoTracking().Where(f => f.Id == node.Id).Select(f => new Restrictions.FileFacts(f.CreatedBy, f.ApprovalStatus == ApprovalStatus.Approved, f.CheckedOutBy, f.PublishedVersionId != null)).FirstOrDefaultAsync(ct);
        var state = await SpaceState.LoadAsync(db, node.SpaceId ?? node.PersonalDriveId, ct);
        var rights = state.Apply(Restrictions.Apply(who, node.OwnerUserId == user.UserId, direct, membership, file, state.Locked));
        var label = node.Kind is SecurableKind.File or SecurableKind.Folder ? (await new Labels(db).ForAsync(node.Id, ct))?.Label : null;
        return Labels.Apply(label, node.SpaceId is not null, membership, rights);
    }

    /// <summary>The current model's scale (None → Manage), for comparing the two models.</summary>
    public static AccessLevel ToLevel(AccessRight rights) =>
        rights.Has(AccessRight.ManagePermissions) ? AccessLevel.Manage
        : rights.Has(AccessRight.EditItems) ? AccessLevel.Edit
        : rights.Has(AccessRight.Download) ? AccessLevel.Download
        : rights.Has(AccessRight.ViewItems) ? AccessLevel.View
        : AccessLevel.None;
}
