using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

// Sharing on the new permission model (spec 5.1, API section 10: items/{id}/permissions, inheritance, general-access).

public sealed record PermissionLevelDto(Guid Id, string Code, string Name, string Description, long Rights, bool IsSystem, int SortOrder);

public sealed record PrincipalTarget(PrincipalKind Kind, Guid Id);

/// <param name="Origin">direct (on this item), inherited (from a folder above), space (Space membership).</param>
/// <param name="Editable">Can be changed or removed here (a direct grant on this item, by someone who manages its permissions).</param>
public sealed record ItemPermissionDto(Guid Id, PrincipalKind PrincipalKind, Guid PrincipalId, string PrincipalName, string? Detail, Guid LevelId, string LevelName,
    DateTime? ExpiresAt, string Origin, string OriginLabel, bool Editable, bool AppliesToChildren);

/// <param name="GeneralAccess">restricted, organization (everyone in the unit using the system).</param>
public sealed record ItemPermissionsDto(Guid ItemId, string Name, SecurableKind Kind, bool HasUniqueAcl, string? InheritsFrom, Guid? InheritsFromId,
    bool CanManage, bool CanShare, string GeneralAccess, Guid? GeneralAccessLevelId, Guid? OwnerId, string? OwnerName, IReadOnlyList<ItemPermissionDto> Entries);

public sealed record ListPermissionLevelsQuery : IRequest<IReadOnlyList<PermissionLevelDto>>;

public sealed class ListPermissionLevelsHandler(IAppDbContext db) : IRequestHandler<ListPermissionLevelsQuery, IReadOnlyList<PermissionLevelDto>>
{
    public async Task<IReadOnlyList<PermissionLevelDto>> Handle(ListPermissionLevelsQuery q, CancellationToken ct) =>
        (await db.PermissionLevels.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct))
        .Select(l => new PermissionLevelDto(l.Id, l.Code, l.Name, l.Description, (long)l.Permissions, l.IsSystem, l.SortOrder)).ToList();
}

/// <summary>"Người có quyền truy cập": who has access to an item and where it comes from (spec 5.1).</summary>
public sealed record GetItemPermissionsQuery(Guid ItemId) : IRequest<ItemPermissionsDto>;

public sealed class GetItemPermissionsHandler(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, TimeProvider clock)
    : IRequestHandler<GetItemPermissionsQuery, ItemPermissionsDto>
{
    public async Task<ItemPermissionsDto> Handle(GetItemPermissionsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var rights = await engine.DemandAsync(uid, q.ItemId, AccessRight.ViewItems, ct);
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == q.ItemId, ct);
        var scope = node.AclScopeId == node.Id ? node : await db.Securables.AsNoTracking().FirstAsync(s => s.Id == node.AclScopeId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var entries = await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == scope.Id && (e.ExpiresAt == null || e.ExpiresAt > now) && (e.AppliesToChildren || scope.Id == node.Id))
            .Join(db.PermissionLevels, e => e.PermissionLevelId, l => l.Id, (e, l) => new { e, l.Name, l.SortOrder }).ToListAsync(ct);
        var names = await PrincipalDisplay.ResolveAsync(db, entries.Select(x => x.e.PrincipalId), ct);
        var canManage = rights.Has(AccessRight.ManagePermissions);
        var everyone = entries.Where(x => x.e.PrincipalKind == PrincipalKind.Everyone).OrderBy(x => x.SortOrder).FirstOrDefault();
        var owner = node.OwnerUserId is { } o ? await db.Users.AsNoTracking().Where(u => u.Id == o).Select(u => u.FullName).FirstOrDefaultAsync(ct) : null;
        var list = entries.Where(x => x.e.PrincipalKind != PrincipalKind.Everyone).OrderBy(x => x.e.Source == AclSource.SpaceDefault ? 0 : 1).ThenBy(x => x.SortOrder)
            .Select(x =>
            {
                var (name, detail) = names.GetValueOrDefault(x.e.PrincipalId, ("(không xác định)", null));
                var inheritedHere = scope.Id != node.Id;
                var origin = x.e.Source == AclSource.SpaceDefault ? "space" : inheritedHere || x.e.Source == AclSource.Inherited ? "inherited" : "direct";
                var label = origin switch
                {
                    "space" => "Thành viên Space",
                    "inherited" => inheritedHere ? $"Kế thừa từ \"{scope.Name}\"" : "Kế thừa (sao chép khi ngừng kế thừa)",
                    _ => x.e.PrincipalKind switch
                    {
                        PrincipalKind.Link => "Qua liên kết chia sẻ",
                        PrincipalKind.Group => "Trực tiếp (qua nhóm)",
                        PrincipalKind.OrgUnit or PrincipalKind.OrgUnitTree => "Trực tiếp (qua đơn vị)",
                        _ => "Trực tiếp",
                    },
                };
                return new ItemPermissionDto(x.e.Id, x.e.PrincipalKind, x.e.PrincipalId, name, detail, x.e.PermissionLevelId, x.Name, x.e.ExpiresAt, origin, label,
                    canManage && !inheritedHere && x.e.Source is not (AclSource.SpaceDefault or AclSource.Link), x.e.AppliesToChildren);
            }).ToList();
        return new ItemPermissionsDto(node.Id, node.Name, node.Kind, node.HasUniqueAcl, scope.Id != node.Id ? scope.Name : null, scope.Id != node.Id ? scope.Id : null,
            canManage, rights.Has(AccessRight.ShareInternal) || canManage, everyone is null ? "restricted" : "organization", everyone?.e.PermissionLevelId,
            node.OwnerUserId, owner, list);
    }
}

/// <summary>Display names of principals, with a detail line (username, "nhóm", "đơn vị").</summary>
public static class PrincipalDisplay
{
    public static async Task<Dictionary<Guid, (string Name, string? Detail)>> ResolveAsync(IAppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var principals = await db.Principals.AsNoTracking().Where(p => list.Contains(p.Id)).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).Select(u => new { u.Id, u.Username, u.Email }).ToDictionaryAsync(u => u.Id, ct);
        return principals.ToDictionary(p => p.Id, p => (p.DisplayName, p.Kind switch
        {
            PrincipalKind.User => users.TryGetValue(p.Id, out var u) ? u.Email ?? u.Username : null,
            PrincipalKind.Group => "Nhóm người dùng",
            PrincipalKind.OrgUnit => "Đơn vị",
            PrincipalKind.OrgUnitTree => "Đơn vị (gồm đơn vị con)",
            PrincipalKind.SpaceGroup => "Nhóm của Space",
            PrincipalKind.Role => "Vai trò",
            _ => (string?)null,
        }));
    }
}

/// <summary>Grants a level to people, groups or units on an item; optionally until a date, with a notification (spec 5.1).</summary>
public sealed record GrantItemPermissionCommand(Guid ItemId, IReadOnlyList<PrincipalTarget> Targets, Guid LevelId, DateTime? ExpiresAt, bool Notify = true, string? Message = null)
    : IRequest<int>;

public sealed class GrantItemPermissionValidator : AbstractValidator<GrantItemPermissionCommand>
{
    public GrantItemPermissionValidator()
    {
        RuleFor(x => x.Targets).NotEmpty().WithMessage("Vui lòng chọn người, nhóm hoặc đơn vị.");
        RuleFor(x => x.ExpiresAt).Must(d => d is null || d > DateTime.UtcNow).WithMessage("Ngày hết hạn phải ở tương lai.");
        RuleFor(x => x.Message).MaximumLength(1000);
    }
}

/// <summary>Who may grant what: managers anything; people allowed to share (Biên tập) at most what they hold themselves (spec 13.7).</summary>
internal static class GrantRules
{
    public static async Task<(AccessRight Rights, PermissionLevel Level)> CheckAsync(IAppDbContext db, IItemPermissionService engine, IPermissionService rbac,
        Guid uid, Guid itemId, Guid levelId, CancellationToken ct)
    {
        if (!await rbac.HasAsync(uid, Perm.DocShare, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền chia sẻ tư liệu.");
        var rights = await engine.DemandAsync(uid, itemId, AccessRight.ViewItems, ct);
        var level = await db.PermissionLevels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == levelId, ct) ?? throw new NotFoundException("Không tìm thấy cấp quyền.");
        if (!rights.Has(AccessRight.ManagePermissions))
        {
            if (!rights.Has(AccessRight.ShareInternal)) throw new ForbiddenException("Bạn không có quyền chia sẻ mục này.");
            if (!rights.Has(level.Permissions)) throw new ForbiddenException("Bạn không thể cấp quyền cao hơn quyền bạn đang có.");
        }
        var (_, locked) = await engine.GetWithLockAsync(uid, itemId, ct);
        if (locked && (level.Permissions & AccessRights.Writes) != 0) throw new AppException("Không gian đang bị khóa, chỉ có thể cấp quyền xem.", 423);
        return (rights, level);
    }
}

public sealed class GrantItemPermissionHandler(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, IPermissionService rbac, AclEditor editor,
    IUserNotifier notify, IAuditLogger audit, SharingPolicy policy) : IRequestHandler<GrantItemPermissionCommand, int>
{
    public async Task<int> Handle(GrantItemPermissionCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var (_, level) = await GrantRules.CheckAsync(db, engine, rbac, uid, cmd.ItemId, cmd.LevelId, ct);
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == cmd.ItemId, ct);
        await policy.EnsureCanShareAsync(uid, cmd.ItemId, cmd.Targets.Where(t => !(t.Kind == PrincipalKind.User && t.Id == uid)).Select(t => (t.Kind, t.Id)).ToList(),
            breaksInheritance: true, ct);
        var count = 0;
        foreach (var t in cmd.Targets.Distinct())
        {
            if (t.Kind is not (PrincipalKind.User or PrincipalKind.Group or PrincipalKind.OrgUnit or PrincipalKind.OrgUnitTree))
                throw new AppException("Chỉ cấp quyền cho người dùng, nhóm hoặc đơn vị.");
            if (!await db.Principals.AnyAsync(p => p.Id == t.Id && p.Kind == t.Kind, ct)) throw new NotFoundException("Không tìm thấy người, nhóm hoặc đơn vị được chọn.");
            if (t.Kind == PrincipalKind.User && t.Id == uid) continue;
            // One direct grant per principal on the item: a new grant replaces the level and the date.
            var existing = await db.AclEntries.FirstOrDefaultAsync(e => e.ScopeId == cmd.ItemId && e.PrincipalId == t.Id && e.Source == AclSource.Direct, ct);
            if (existing is not null && node.HasUniqueAcl)
            {
                existing.PermissionLevelId = level.Id;
                existing.ExpiresAt = cmd.ExpiresAt;
                existing.GrantedBy = uid;
                existing.Message = cmd.Message;
                await db.SaveChangesAsync(ct);
                await editor.TouchAsync(cmd.ItemId, ct);
            }
            else
            {
                await editor.GrantAsync(cmd.ItemId, t.Kind, t.Id, level.Id, cmd.ExpiresAt, uid, ct, cmd.Message);
                node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == cmd.ItemId, ct);
            }
            count++;
        }
        if (cmd.Notify)
        {
            var sharer = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
            var recipients = await RecipientsAsync(db, cmd.Targets, uid, ct);
            var link = node.Kind == SecurableKind.File ? $"/files/{node.Id}" : "/shared";
            foreach (var r in recipients)
                await notify.NotifyUserAsync(r, "share", $"{sharer} đã chia sẻ \"{node.Name}\" với bạn ({level.Name})", cmd.Message, link, ct);
        }
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionGranted, Kind(node.Kind), node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId,
            new { targets = cmd.Targets, level = level.Code, cmd.ExpiresAt }), ct);
        return count;
    }

    /// <summary>Users behind the chosen principals (flattened memberships), without the sharer.</summary>
    public static async Task<List<Guid>> RecipientsAsync(IAppDbContext db, IEnumerable<PrincipalTarget> targets, Guid except, CancellationToken ct)
    {
        var ids = targets.Select(t => t.Id).ToList();
        return await db.PrincipalMemberships.Where(m => ids.Contains(m.PrincipalId) && m.UserId != except).Select(m => m.UserId).Distinct().ToListAsync(ct);
    }

    public static ResourceType Kind(SecurableKind kind) => kind switch
    {
        SecurableKind.File => ResourceType.File,
        SecurableKind.Folder => ResourceType.Folder,
        _ => ResourceType.Workspace,
    };
}

/// <summary>Changes the level or the end date of a direct grant on the item.</summary>
public sealed record UpdateItemPermissionCommand(Guid ItemId, Guid EntryId, Guid? LevelId, DateTime? ExpiresAt, bool ClearExpiry = false) : IRequest<Unit>;

public sealed class UpdateItemPermissionHandler(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, AclEditor editor, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<UpdateItemPermissionCommand, Unit>
{
    public async Task<Unit> Handle(UpdateItemPermissionCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, cmd.ItemId, AccessRight.ManagePermissions, ct);
        var entry = await db.AclEntries.FirstOrDefaultAsync(e => e.Id == cmd.EntryId && e.ScopeId == cmd.ItemId && e.Source != AclSource.SpaceDefault, ct)
                    ?? throw new NotFoundException("Không tìm thấy quyền này trên mục (quyền kế thừa được quản lý ở thư mục cha).");
        var before = new { entry.PermissionLevelId, entry.ExpiresAt };
        if (cmd.LevelId is { } levelId)
        {
            if (!await db.PermissionLevels.AnyAsync(l => l.Id == levelId, ct)) throw new NotFoundException("Không tìm thấy cấp quyền.");
            entry.PermissionLevelId = levelId;
        }
        if (cmd.ClearExpiry) entry.ExpiresAt = null;
        else if (cmd.ExpiresAt is { } until)
        {
            if (until <= clock.GetUtcNow().UtcDateTime) throw new AppException("Ngày hết hạn phải ở tương lai.");
            entry.ExpiresAt = until;
        }
        entry.Source = AclSource.Direct;
        await db.SaveChangesAsync(ct);
        await editor.TouchAsync(cmd.ItemId, ct);
        var name = await db.Securables.Where(s => s.Id == cmd.ItemId).Select(s => new { s.Name, s.Kind }).FirstAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionLevelChanged, GrantItemPermissionHandler.Kind(name.Kind), cmd.ItemId, name.Name, null,
            new { entry = entry.Id, entry.PrincipalId, before, after = new { entry.PermissionLevelId, entry.ExpiresAt } }), ct);
        return Unit.Value;
    }
}

/// <summary>Removes a direct grant of the item (a person may also leave a grant given to them).</summary>
public sealed record RevokeItemPermissionCommand(Guid ItemId, Guid EntryId) : IRequest<Unit>;

public sealed class RevokeItemPermissionHandler(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, AclEditor editor, IAuditLogger audit)
    : IRequestHandler<RevokeItemPermissionCommand, Unit>
{
    public async Task<Unit> Handle(RevokeItemPermissionCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var entry = await db.AclEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == cmd.EntryId && e.ScopeId == cmd.ItemId && e.Source != AclSource.SpaceDefault, ct)
                    ?? throw new NotFoundException("Không tìm thấy quyền này trên mục (quyền kế thừa được quản lý ở thư mục cha).");
        if (!(entry.PrincipalKind == PrincipalKind.User && entry.PrincipalId == uid))
            await engine.DemandAsync(uid, cmd.ItemId, AccessRight.ManagePermissions, ct);
        await editor.RevokeAsync(entry.Id, ct);
        // The same person's rows in the old tables (old sharing screens, mobile) go too, so that nothing still reads them as shared.
        var legacyId = entry.PrincipalKind is PrincipalKind.OrgUnitTree or PrincipalKind.OrgUnit
            ? await db.Principals.Where(p => p.Id == entry.PrincipalId).Select(p => p.RefId).FirstOrDefaultAsync(ct) ?? entry.PrincipalId
            : entry.PrincipalId;
        await db.Shares.Where(s => s.ResourceId == cmd.ItemId && s.SharedWithId == legacyId).ExecuteDeleteAsync(ct);
        await db.ResourcePermissions.Where(r => r.ResourceId == cmd.ItemId && r.PrincipalId == legacyId).ExecuteDeleteAsync(ct);
        var node = await db.Securables.Where(s => s.Id == cmd.ItemId).Select(s => new { s.Name, s.Kind }).FirstAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionRevoked, GrantItemPermissionHandler.Kind(node.Kind), cmd.ItemId, node.Name, null,
            new { entry = entry.Id, entry.PrincipalKind, entry.PrincipalId, entry.PermissionLevelId }), ct);
        return Unit.Value;
    }
}

/// <summary>"Ngắt kế thừa" (copy today's access, or start with only who manages) / "Khôi phục kế thừa" (also for everything below).</summary>
public sealed record SetInheritanceCommand(Guid ItemId, bool Inherit, bool Copy = true, bool Recursive = false) : IRequest<Unit>;

public sealed class SetInheritanceHandler(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, AclEditor editor, IAuditLogger audit, SharingPolicy policy)
    : IRequestHandler<SetInheritanceCommand, Unit>
{
    public async Task<Unit> Handle(SetInheritanceCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, cmd.ItemId, AccessRight.ManagePermissions, ct);
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == cmd.ItemId, ct);
        if (node.ParentId is null) throw new AppException("Space và Drive của tôi luôn có quyền riêng.");
        if (cmd.Inherit) await editor.RestoreInheritanceAsync(cmd.ItemId, cmd.Recursive, ct);
        else
        {
            await policy.EnsureCanShareAsync(uid, cmd.ItemId, [], breaksInheritance: true, ct);
            await editor.BreakInheritanceAsync(cmd.ItemId, cmd.Copy, uid, ct);
        }
        await audit.LogAsync(new AuditEntry(cmd.Inherit ? AuditAction.InheritanceRestored : AuditAction.InheritanceBroken, GrantItemPermissionHandler.Kind(node.Kind),
            node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId, new { cmd.Copy, cmd.Recursive }), ct);
        return Unit.Value;
    }
}

/// <summary>"Quyền truy cập chung": restricted, or everyone in the unit using the system with a level (spec 5.1).</summary>
public sealed record SetGeneralAccessCommand(Guid ItemId, string Mode, Guid? LevelId) : IRequest<Unit>;

public sealed class SetGeneralAccessValidator : AbstractValidator<SetGeneralAccessCommand>
{
    public SetGeneralAccessValidator()
    {
        RuleFor(x => x.Mode).Must(m => m is "restricted" or "organization").WithMessage("Quyền truy cập chung chỉ gồm Bị hạn chế hoặc Mọi người trong đơn vị.");
        RuleFor(x => x.LevelId).NotNull().When(x => x.Mode == "organization").WithMessage("Vui lòng chọn cấp quyền.");
    }
}

public sealed class SetGeneralAccessHandler(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, IPermissionService rbac, AclEditor editor, IAuditLogger audit,
    SharingPolicy policy)
    : IRequestHandler<SetGeneralAccessCommand, Unit>
{
    public async Task<Unit> Handle(SetGeneralAccessCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, cmd.ItemId, AccessRight.ManagePermissions, ct);
        var everyone = await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == cmd.ItemId && e.PrincipalId == StableGuid.Everyone && e.Source == AclSource.Direct).ToListAsync(ct);
        foreach (var e in everyone) await editor.RevokeAsync(e.Id, ct);
        if (cmd.Mode == "organization")
        {
            await GrantRules.CheckAsync(db, engine, rbac, uid, cmd.ItemId, cmd.LevelId!.Value, ct);
            await policy.EnsureCanShareAsync(uid, cmd.ItemId, [(PrincipalKind.Everyone, StableGuid.Everyone)], breaksInheritance: true, ct);
            await editor.GrantAsync(cmd.ItemId, PrincipalKind.Everyone, StableGuid.Everyone, cmd.LevelId!.Value, null, uid, ct);
        }
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == cmd.ItemId, ct);
        await audit.LogAsync(new AuditEntry(cmd.Mode == "organization" ? AuditAction.PermissionGranted : AuditAction.PermissionRevoked, GrantItemPermissionHandler.Kind(node.Kind),
            node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId, new { generalAccess = cmd.Mode, cmd.LevelId }), ct);
        return Unit.Value;
    }
}
