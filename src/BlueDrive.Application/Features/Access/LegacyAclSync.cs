using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// The current sharing screens ("Quản lý quyền truy cập", "Chia sẻ") still write the old tables (resource_permissions, shares, the
/// break-inheritance flag) and read them back. After each such change the ACL of that one item in the new model, which decides access, is
/// made to match them:
/// <list type="bullet">
/// <item>stopped inheriting → the people who manage the Space (its owners group) + the item's own rows (copies of inherited access, when
/// chosen, are rows of their own in the old model);</item>
/// <item>only direct grants (shared, permission given) → a copy of the parent's current access + the item's own rows;</item>
/// <item>neither → it inherits again.</item>
/// </list>
/// Grants given with the new sharing screens go straight to the new model (<see cref="AclEditor"/>).
/// </summary>
public sealed class LegacyAclSync(IAppDbContext db, AclEditor editor, TimeProvider clock)
{
    public async Task SyncAsync(ResourceType type, Guid id, bool copyOnBreak, Guid? by, CancellationToken ct)
    {
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (node is null) return; // not projected yet: the projection imports it
        var acl = await db.ResourcePermissions.AsNoTracking().Where(a => a.ResourceType == type && a.ResourceId == id).ToListAsync(ct);
        var shares = await db.Shares.AsNoTracking().Where(s => s.ResourceType == type && s.ResourceId == id).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var desired = acl.Select(a => (P: Map(a.PrincipalType, a.PrincipalId), Level: PermissionProjection.LevelOf(a.AccessLevel), a.ExpiresAt, By: a.CreatedBy, Children: a.Inherit, Message: (string?)null))
            .Concat(shares.Select(s => (P: Map(s.SharedWithType, s.SharedWithId), Level: PermissionProjection.LevelOf(s.AccessLevel), ExpiresAt: (DateTime?)null, By: (Guid?)s.SharedBy, Children: true, s.Message)))
            .Where(d => d.P is not null)
            .Select(d => new AclEntry
            {
                ScopeId = id, PrincipalKind = d.P!.Value.Kind, PrincipalId = d.P!.Value.Id, PermissionLevelId = d.Level, ExpiresAt = d.ExpiresAt,
                GrantedBy = d.By, GrantedAt = now, Source = AclSource.Direct, AppliesToChildren = d.Children, Message = d.Message,
            }).ToList();

        var root = type == ResourceType.Workspace;
        var breaks = type switch
        {
            ResourceType.Folder => await db.Folders.Where(f => f.Id == id).Select(f => f.BreakInheritance).FirstOrDefaultAsync(ct),
            ResourceType.File => await db.Files.Where(f => f.Id == id).Select(f => f.BreakInheritance).FirstOrDefaultAsync(ct),
            _ => true,
        };
        if (!root && !breaks && desired.Count == 0)
        {
            if (node.HasUniqueAcl) await editor.RestoreInheritanceAsync(id, recursive: false, ct);
            return;
        }
        if (!node.HasUniqueAcl) await editor.BreakInheritanceAsync(id, copy: true, by, ct);

        // Rebuild the item's ACL: what it keeps from above (unless it is a Space / drive, whose Space groups the projection owns) + its rows.
        await db.AclEntries.Where(e => e.ScopeId == id && (e.Source == AclSource.Direct || (!root && e.Source != AclSource.Direct))).ExecuteDeleteAsync(ct);
        if (!root && node.ParentId is { } parentId)
        {
            var parentScope = await db.Securables.Where(s => s.Id == parentId).Select(s => s.AclScopeId).FirstAsync(ct);
            var above = await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == parentScope && e.AppliesToChildren).ToListAsync(ct);
            if (breaks)
            {
                // Old rule: below a break only the Space's owners and managers keep their access.
                var managing = (await db.PermissionLevels.AsNoTracking().ToListAsync(ct)).Where(l => l.Permissions.Has(AccessRight.ManagePermissions)).Select(l => l.Id).ToHashSet();
                above = above.Where(e => e.Source == AclSource.SpaceDefault && managing.Contains(e.PermissionLevelId)).ToList();
            }
            db.AclEntries.AddRange(above.Select(e => new AclEntry
            {
                ScopeId = id, PrincipalKind = e.PrincipalKind, PrincipalId = e.PrincipalId, PermissionLevelId = e.PermissionLevelId, ExpiresAt = e.ExpiresAt,
                GrantedBy = e.GrantedBy, GrantedAt = e.GrantedAt, Source = e.Source == AclSource.Direct ? AclSource.Inherited : e.Source,
            }));
        }
        db.AclEntries.AddRange(desired);
        await db.SaveChangesAsync(ct);
        await editor.TouchAsync(id, ct);
    }

    /// <summary>
    /// "Chia sẻ" through the old API (POST/DELETE /shares) for one principal: only that principal's own entry on the item changes
    /// (null level = removed); everything else on the item — grants made with the new screens included — stays.
    /// </summary>
    public async Task SetOneAsync(Guid id, PrincipalType type, Guid principalId, AccessLevel? level, string? message, Guid? by, CancellationToken ct)
    {
        if (Map(type, principalId) is not { } p) return;
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (node is null) return; // not projected yet: the projection imports it
        if (node.HasUniqueAcl)
            await db.AclEntries.Where(e => e.ScopeId == id && e.Source == AclSource.Direct && e.PrincipalKind == p.Kind && e.PrincipalId == p.Id).ExecuteDeleteAsync(ct);
        if (level is { } l) await editor.GrantAsync(id, p.Kind, p.Id, PermissionProjection.LevelOf(l), null, by, ct, message);
        else if (node.HasUniqueAcl) await editor.TouchAsync(id, ct);
    }

    /// <summary>Old principal → principal of the new model (unit grants always included the sub-units).</summary>
    public static (PrincipalKind Kind, Guid Id)? Map(PrincipalType type, Guid id) => type switch
    {
        PrincipalType.User => (PrincipalKind.User, id),
        PrincipalType.Group => (PrincipalKind.Group, id),
        PrincipalType.Role => (PrincipalKind.Role, id),
        PrincipalType.Organization => (PrincipalKind.OrgUnitTree, PermissionProjection.UnitTreeOf(id)),
        _ => null,
    };
}
