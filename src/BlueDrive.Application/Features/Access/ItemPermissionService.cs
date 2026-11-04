using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BlueDrive.Application.Features.Access;

/// <summary>One reason a user holds rights on an item (spec 4.3 "Explain").</summary>
public sealed record PermissionSource(string Kind, string Principal, PrincipalKind PrincipalKind, string Level, AccessRight Rights, string Scope,
    AclSource Origin, DateTime? ExpiresAt);

public sealed record PermissionExplanation(AccessRight Effective, string ScopeName, bool Inherited, IReadOnlyList<PermissionSource> Sources,
    IReadOnlyList<string> Restrictions);

/// <summary>
/// Permission engine of the new model (spec 4.3): rights = OR of the live ACL entries of the user's principals on the item's scope
/// (<see cref="Securable.AclScopeId"/>), the owner rule, then the restrictions (spec 3.4). The scope part is cached in memory per
/// (user, scope, membership version, ACL version) and per request; changing an ACL or a membership bumps its version.
/// Named apart from <see cref="IPermissionService"/>, which holds the role (RBAC) codes.
/// </summary>
public interface IItemPermissionService
{
    Task<AccessRight> GetEffectiveAsync(Guid userId, Guid itemId, CancellationToken ct = default);
    Task<bool> HasAsync(Guid userId, Guid itemId, AccessRight wanted, CancellationToken ct = default);
    /// <summary>404 when the user may not even see the item (its existence stays hidden), 403 when a right is missing.</summary>
    Task<AccessRight> DemandAsync(Guid userId, Guid itemId, AccessRight wanted, CancellationToken ct = default);
    Task<PermissionExplanation> ExplainAsync(Guid userId, Guid itemId, CancellationToken ct = default);
    /// <summary>Rights before the "locked Space" restriction, and whether the Space is locked (writes answer 423, not 403).</summary>
    Task<(AccessRight Rights, bool Locked)> GetWithLockAsync(Guid userId, Guid itemId, CancellationToken ct = default);
}

public sealed class ItemPermissionService(IAppDbContext db, IPermissionService rbac, IMemoryCache cache, TimeProvider clock) : IItemPermissionService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly Dictionary<(Guid, Guid), (AccessRight, bool)> _request = [];

    public async Task<AccessRight> GetEffectiveAsync(Guid userId, Guid itemId, CancellationToken ct = default)
    {
        var (rights, locked) = await GetWithLockAsync(userId, itemId, ct);
        return locked ? rights & ~AccessRights.Writes : rights;
    }

    public async Task<(AccessRight Rights, bool Locked)> GetWithLockAsync(Guid userId, Guid itemId, CancellationToken ct = default)
    {
        if (_request.TryGetValue((userId, itemId), out var known)) return known;
        var node = await db.Securables.AsNoTracking().Where(s => s.Id == itemId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Kind, s.AclScopeId, s.OwnerUserId, Root = s.SpaceId ?? s.PersonalDriveId }).FirstOrDefaultAsync(ct);
        var result = (AccessRight.None, false);
        if (node is not null && await StillThereAsync(node.Kind, node.Id, ct))
        {
            var user = await UserAsync(userId, ct);
            if (user.CanViewDocs)
            {
                var (direct, membership) = await ScopeRightsAsync(user, node.AclScopeId, node.Id == node.AclScopeId, ct);
                result = (Restrictions.Apply(user, node.OwnerUserId == userId, direct, membership, await FileFactsAsync(node.Kind, node.Id, ct), locked: false),
                    await LockedAsync(node.Root, ct));
            }
        }
        _request[(userId, itemId)] = result;
        return result;
    }

    public async Task<bool> HasAsync(Guid userId, Guid itemId, AccessRight wanted, CancellationToken ct = default) =>
        (await GetEffectiveAsync(userId, itemId, ct)).Has(wanted);

    public async Task<AccessRight> DemandAsync(Guid userId, Guid itemId, AccessRight wanted, CancellationToken ct = default)
    {
        var rights = await GetEffectiveAsync(userId, itemId, ct);
        if (!rights.Has(AccessRight.ViewItems)) throw new NotFoundException("Không tìm thấy tư liệu hoặc bạn không có quyền truy cập.");
        if (!rights.Has(wanted)) throw new ForbiddenException("Bạn không có quyền thực hiện thao tác này trên tư liệu.");
        return rights;
    }

    public async Task<PermissionExplanation> ExplainAsync(Guid userId, Guid itemId, CancellationToken ct = default)
    {
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == itemId && !s.IsDeleted, ct)
                   ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        var scope = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == node.AclScopeId, ct);
        var user = await UserAsync(userId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await db.AclEntries.AsNoTracking()
            .Where(e => e.ScopeId == node.AclScopeId && user.Principals.Contains(e.PrincipalId) && (e.ExpiresAt == null || e.ExpiresAt > now)
                        && (e.AppliesToChildren || node.Id == node.AclScopeId))
            .Join(db.PermissionLevels, e => e.PermissionLevelId, l => l.Id, (e, l) => new { e, l.Name, l.Permissions })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.e.PrincipalId).Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var sources = rows.Select(r => new PermissionSource(KindLabel(r.e.PrincipalKind, r.e.Source), names.GetValueOrDefault(r.e.PrincipalId, ""),
            r.e.PrincipalKind, r.Name, r.Permissions, scope.Name, r.e.Source, r.e.ExpiresAt)).ToList();
        if (node.OwnerUserId == userId)
            sources.Insert(0, new PermissionSource("Chủ sở hữu (Drive của tôi)", names.GetValueOrDefault(userId, ""), PrincipalKind.User, "Chủ sở hữu / Quản lý",
                AccessRights.All, scope.Name, AclSource.Direct, null));
        var restrictions = new List<string>();
        if (!user.CanViewDocs) restrictions.Add("Vai trò hệ thống không có quyền xem tư liệu.");
        if (!user.CanEditDocs) restrictions.Add("Vai trò hệ thống không có quyền sửa tư liệu: quyền ghi có được qua thành viên Space bị bỏ (trừ thêm mới, phê duyệt).");
        if (await LockedAsync(node.SpaceId ?? node.PersonalDriveId, ct)) restrictions.Add("Không gian đang bị khóa: chỉ còn quyền đọc.");
        if (await FileFactsAsync(node.Kind, node.Id, ct) is { Approved: false }) restrictions.Add("Tệp chưa được duyệt: chỉ người tải lên, người sửa và người duyệt thấy.");
        return new PermissionExplanation(await GetEffectiveAsync(userId, itemId, ct), scope.Name, scope.Id != node.Id, sources, restrictions);
    }

    private static string KindLabel(PrincipalKind kind, AclSource source) => (kind, source) switch
    {
        (_, AclSource.SpaceDefault) => "Thành viên Space",
        (PrincipalKind.User, AclSource.Inherited) => "Kế thừa (cấp trực tiếp ở thư mục trên)",
        (PrincipalKind.User, _) => "Trực tiếp",
        (PrincipalKind.Group, _) => "Qua nhóm",
        (PrincipalKind.OrgUnit, _) => "Qua đơn vị",
        (PrincipalKind.OrgUnitTree, _) => "Qua đơn vị (gồm đơn vị con)",
        (PrincipalKind.SpaceGroup, _) => "Qua nhóm của Space",
        (PrincipalKind.Everyone, _) => "Mọi người trong đơn vị",
        (PrincipalKind.Role, _) => "Qua vai trò",
        (PrincipalKind.Link, _) => "Qua liên kết",
        _ => "Khác",
    };

    // ------------------------------------------------------------------------------------------------ parts

    /// <summary>Role codes and flattened principals of a user (principals cached per membership version).</summary>
    private async Task<Restrictions.User> UserAsync(Guid userId, CancellationToken ct)
    {
        var version = await db.MembershipVersions.Where(v => v.UserId == userId).Select(v => (long?)v.Version).FirstOrDefaultAsync(ct) ?? 0;
        var codes = await rbac.GetPermissionsAsync(userId, ct); // cached by the RBAC service
        var principals = await cache.GetOrCreateAsync(("perm-user", userId, version), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            return await db.PrincipalMemberships.Where(m => m.UserId == userId).Select(m => m.PrincipalId).ToListAsync(ct);
        }) ?? [];
        return Restrictions.User.From(userId, codes, principals);
    }

    /// <summary>OR of the live entries on a scope, apart: direct grants, and Space membership (the part capped by the role).</summary>
    private async Task<(AccessRight Direct, AccessRight Membership)> ScopeRightsAsync(Restrictions.User user, Guid scopeId, bool scopeNode, CancellationToken ct)
    {
        var aclVersion = await db.AclScopes.Where(s => s.ScopeId == scopeId).Select(s => (long?)s.AclVersion).FirstOrDefaultAsync(ct) ?? 0;
        var membershipVersion = await db.MembershipVersions.Where(v => v.UserId == user.UserId).Select(v => (long?)v.Version).FirstOrDefaultAsync(ct) ?? 0;
        var key = ("perm-scope", user.UserId, scopeId, scopeNode, membershipVersion, aclVersion);
        var now = clock.GetUtcNow().UtcDateTime;
        // The answer is valid until the first of its entries runs out (checked on the application's clock).
        if (cache.TryGetValue(key, out (AccessRight Direct, AccessRight Membership, DateTime Until) hit) && now < hit.Until) return (hit.Direct, hit.Membership);
        var live = await db.AclEntries.AsNoTracking()
            .Where(e => e.ScopeId == scopeId && user.Principals.Contains(e.PrincipalId) && (e.ExpiresAt == null || e.ExpiresAt > now) && (e.AppliesToChildren || scopeNode))
            .Join(db.PermissionLevels, e => e.PermissionLevelId, l => l.Id, (e, l) => new { e.Source, e.ExpiresAt, l.Permissions })
            .ToListAsync(ct);
        var result = (live.Where(e => e.Source != AclSource.SpaceDefault).Aggregate(AccessRight.None, (m, e) => m | e.Permissions),
            live.Where(e => e.Source == AclSource.SpaceDefault).Aggregate(AccessRight.None, (m, e) => m | e.Permissions));
        // An entry running out ends the cached answer with it.
        var until = live.Where(e => e.ExpiresAt is not null).Select(e => e.ExpiresAt!.Value).DefaultIfEmpty(now + CacheFor).Min();
        cache.Set(key, (result.Item1, result.Item2, until), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheFor });
        return result;
    }

    /// <summary>
    /// The folder or file itself must exist and not be in the trash: bulk deletes and restores of a backup do not go through the change
    /// tracker, so the node alone is not trusted for that.
    /// </summary>
    private async Task<bool> StillThereAsync(SecurableKind kind, Guid id, CancellationToken ct) => kind switch
    {
        SecurableKind.File => await db.Files.AnyAsync(f => f.Id == id && !f.IsDeleted, ct),
        SecurableKind.Folder => await db.Folders.AnyAsync(f => f.Id == id && !f.IsDeleted, ct),
        SecurableKind.Space or SecurableKind.Drive => await db.Workspaces.AnyAsync(w => w.Id == id, ct),
        _ => true,
    };

    private async Task<Restrictions.FileFacts?> FileFactsAsync(SecurableKind kind, Guid id, CancellationToken ct) =>
        kind != SecurableKind.File ? null
        : await db.Files.AsNoTracking().Where(f => f.Id == id).Select(f => new Restrictions.FileFacts(f.CreatedBy, f.ApprovalStatus == ApprovalStatus.Approved)).FirstOrDefaultAsync(ct);

    private async Task<bool> LockedAsync(Guid? root, CancellationToken ct) => root is { } id && await db.Workspaces.AnyAsync(w => w.Id == id && w.IsLocked, ct);
}

/// <summary>
/// Restrictions applied after the OR (spec 3.4), shared by the engine and the reference evaluator. Rules carried over from the current
/// model (docs/migration-permissions.md, section 4a): no "doc.view" → nothing; no "doc.edit" → Space membership and ownership give no
/// writes (adding and approving stay); "Đóng góp" edits its own files; a file not approved yet is seen only by its uploader, editors and
/// approvers; a locked Space keeps reading only.
/// </summary>
public static class Restrictions
{
    public sealed record User(Guid UserId, bool CanViewDocs, bool CanEditDocs, IReadOnlyList<Guid> Principals)
    {
        public static User From(Guid userId, IReadOnlySet<string> codes, IReadOnlyList<Guid> principals)
        {
            var admin = codes.Contains(Perm.DocAdmin);
            return new User(userId, admin || codes.Contains(Perm.DocView), admin || codes.Contains(Perm.DocEdit), principals);
        }
    }

    public sealed record FileFacts(Guid? CreatedBy, bool Approved);

    private const AccessRight KeptWithoutDocEdit = AccessRight.AddItems | AccessRight.ApproveItems;

    public static AccessRight Apply(User user, bool owner, AccessRight direct, AccessRight membership, FileFacts? file, bool locked)
    {
        if (!user.CanViewDocs) return AccessRight.None;
        AccessRight Cap(AccessRight r) => user.CanEditDocs ? r : r & ~AccessRights.Writes | (r & KeptWithoutDocEdit);
        var rights = direct | Cap(membership);
        if (owner) rights |= Cap(AccessRights.All);
        if (file is not null)
        {
            if (rights.Has(AccessRight.EditOwnItemsOnly) && file.CreatedBy == user.UserId && user.CanEditDocs)
                rights |= AccessRight.EditItems | AccessRight.DeleteItems | AccessRight.RenameMove;
            if (!file.Approved && file.CreatedBy != user.UserId && !owner
                && !rights.Has(AccessRight.EditItems) && !rights.Has(AccessRight.ApproveItems) && !rights.Has(AccessRight.ViewDrafts))
                return AccessRight.None;
        }
        return locked ? rights & ~AccessRights.Writes : rights;
    }
}
