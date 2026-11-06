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

/// <param name="RightNames">The rights of the level by name (the administration matrix).</param>
public sealed record PermissionLevelDto(Guid Id, string Code, string Name, string Description, long Rights, bool IsSystem, int SortOrder,
    IReadOnlyList<string>? RightNames = null, bool IsCustomized = false);

public sealed record PrincipalTarget(PrincipalKind Kind, Guid Id);

/// <param name="Origin">direct (on this item), inherited (from a folder above), space (Space membership).</param>
/// <param name="Editable">Can be changed or removed here (a direct grant on this item, by someone who manages its permissions).</param>
public sealed record ItemPermissionDto(Guid Id, PrincipalKind PrincipalKind, Guid PrincipalId, string PrincipalName, string? Detail, Guid LevelId, string LevelName,
    DateTime? ExpiresAt, string Origin, string OriginLabel, bool Editable, bool AppliesToChildren);

/// <param name="GeneralAccess">restricted, organization (everyone in the unit using the system).</param>
/// <param name="ApprovalEnabled">The item's library approves content (#43): only then is "Duyệt nội dung" worth offering.</param>
public sealed record ItemPermissionsDto(Guid ItemId, string Name, SecurableKind Kind, bool HasUniqueAcl, string? InheritsFrom, Guid? InheritsFromId,
    bool CanManage, bool CanShare, string GeneralAccess, Guid? GeneralAccessLevelId, Guid? OwnerId, string? OwnerName, IReadOnlyList<ItemPermissionDto> Entries,
    bool ApprovalEnabled = false);

public sealed record ListPermissionLevelsQuery : IRequest<IReadOnlyList<PermissionLevelDto>>;

public sealed class ListPermissionLevelsHandler(IAppDbContext db) : IRequestHandler<ListPermissionLevelsQuery, IReadOnlyList<PermissionLevelDto>>
{
    public async Task<IReadOnlyList<PermissionLevelDto>> Handle(ListPermissionLevelsQuery q, CancellationToken ct) =>
        (await db.PermissionLevels.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct))
        .Select(l => new PermissionLevelDto(l.Id, l.Code, l.Name, l.Description, (long)l.Permissions, l.IsSystem, l.SortOrder,
            AccessRights.Descriptions.Keys.Where(r => l.Permissions.Has(r)).Select(r => r.ToString()).ToList(), l.IsCustomized)).ToList();
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
        // A Space group nobody is in (a Drive's "Thành viên", a Space without visitors…) gives nobody anything: not listed.
        var groupIds = entries.Where(x => x.e.PrincipalKind == PrincipalKind.SpaceGroup).Select(x => x.e.PrincipalId).Distinct().ToList();
        var peopled = (await db.SpaceGroupMembers.AsNoTracking().Where(m => groupIds.Contains(m.SpaceGroupId)).Select(m => m.SpaceGroupId).Distinct().ToListAsync(ct)).ToHashSet();
        var list = entries.Where(x => x.e.PrincipalKind != PrincipalKind.Everyone && (x.e.PrincipalKind != PrincipalKind.SpaceGroup || peopled.Contains(x.e.PrincipalId)))
            // Space membership first (by level); grants in the order they were given, so changing a level does not move the row.
            .OrderBy(x => x.e.Source == AclSource.SpaceDefault ? 0 : 1).ThenBy(x => x.e.Source == AclSource.SpaceDefault ? x.SortOrder : 0).ThenBy(x => x.e.GrantedAt).ThenBy(x => x.e.Id)
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
        list = await SpaceShares.CollapseAsync(db, list, ct);
        return new ItemPermissionsDto(node.Id, node.Name, node.Kind, node.HasUniqueAcl, scope.Id != node.Id ? scope.Name : null, scope.Id != node.Id ? scope.Id : null,
            canManage, rights.Has(AccessRight.ShareInternal) || canManage, everyone is null ? "restricted" : "organization", everyone?.e.PermissionLevelId,
            node.OwnerUserId, owner, list, await ApprovalEnabledAsync(node, ct));
    }

    /// <summary>Same rule as uploads: a folder carries its library's setting; a file at the root of a Space follows the default library.</summary>
    private async Task<bool> ApprovalEnabledAsync(Securable node, CancellationToken ct)
    {
        var workspaceId = node.SpaceId ?? node.PersonalDriveId;
        switch (node.Kind)
        {
            case SecurableKind.Folder:
                return await db.Folders.AsNoTracking().AnyAsync(f => f.Id == node.Id && f.RequiresApproval, ct);
            case SecurableKind.File:
                var inFolder = await db.Files.AsNoTracking().Where(f => f.Id == node.Id).Select(f => f.Folder != null ? (bool?)f.Folder.RequiresApproval : null).FirstOrDefaultAsync(ct);
                if (inFolder is { } v) return v;
                var lib = workspaceId is { } w ? PermissionProjection.LibraryOf(w) : Guid.Empty;
                return await db.LibrarySettings.AsNoTracking().AnyAsync(l => l.Id == lib && l.ContentApproval, ct);
            default:
                return workspaceId is { } ws && await db.LibrarySettings.AsNoTracking().AnyAsync(l => l.WorkspaceId == ws && l.ContentApproval, ct);
        }
    }
}

/// <summary>
/// Sharing with a whole group Space (e.g. "DEV") grants the level to each of its groups (Chủ sở hữu, Thành viên…). People see and change
/// that as one grant: the list shows one row per Space, and changing or removing it applies to all of the Space's matching entries.
/// </summary>
public static class SpaceShares
{
    public const string Detail = "Nhóm (mọi thành viên)";

    public static async Task<List<ItemPermissionDto>> CollapseAsync(IAppDbContext db, List<ItemPermissionDto> list, CancellationToken ct)
    {
        // Space membership rows (a Drive's own owner, "Thành viên Space") stay as they are: only grants made on items are merged.
        var groupIds = list.Where(e => e.PrincipalKind == PrincipalKind.SpaceGroup && e.Origin != "space").Select(e => e.PrincipalId).Distinct().ToList();
        if (groupIds.Count == 0) return list;
        var spaceOf = await db.Principals.AsNoTracking().Where(p => groupIds.Contains(p.Id) && p.RefId != null).ToDictionaryAsync(p => p.Id, p => p.RefId!.Value, ct);
        var spaceIds = spaceOf.Values.Distinct().ToList();
        var names = await db.Workspaces.AsNoTracking().Where(w => spaceIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var seen = new HashSet<(Guid, Guid, DateTime?, string, bool, bool)>();
        var result = new List<ItemPermissionDto>(list.Count);
        foreach (var e in list)
        {
            if (e.PrincipalKind != PrincipalKind.SpaceGroup || e.Origin == "space" || !spaceOf.TryGetValue(e.PrincipalId, out var space) || !names.TryGetValue(space, out var name))
            {
                result.Add(e);
                continue;
            }
            if (seen.Add((space, e.LevelId, e.ExpiresAt, e.Origin, e.AppliesToChildren, e.Editable)))
                result.Add(e with { PrincipalId = space, PrincipalName = name, Detail = Detail });
        }
        return result;
    }

    /// <summary>The direct entries on the same scope that together make one Space grant with <paramref name="entry"/> (itself included).</summary>
    public static async Task<List<AclEntry>> SiblingsAsync(IAppDbContext db, AclEntry entry, bool tracked, CancellationToken ct)
    {
        if (entry.PrincipalKind != PrincipalKind.SpaceGroup) return [entry];
        var space = await db.Principals.AsNoTracking().Where(p => p.Id == entry.PrincipalId).Select(p => p.RefId).FirstOrDefaultAsync(ct);
        if (space is null) return [entry];
        var groups = db.Principals.Where(p => p.Kind == PrincipalKind.SpaceGroup && p.RefId == space).Select(p => p.Id);
        var query = db.AclEntries.Where(e => e.ScopeId == entry.ScopeId && e.PrincipalKind == PrincipalKind.SpaceGroup && groups.Contains(e.PrincipalId)
                                             && e.Source != AclSource.SpaceDefault && e.PermissionLevelId == entry.PermissionLevelId
                                             && e.ExpiresAt == entry.ExpiresAt && e.AppliesToChildren == entry.AppliesToChildren);
        var list = await (tracked ? query : query.AsNoTracking()).ToListAsync(ct);
        return list.Count == 0 ? [entry] : list;
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
/// <param name="Emails">People by email: an internal user with that email, or a guest from outside (invited if new; spec 2, 5.4).</param>
public sealed record GrantItemPermissionCommand(Guid ItemId, IReadOnlyList<PrincipalTarget> Targets, Guid LevelId, DateTime? ExpiresAt, bool Notify = true, string? Message = null,
    IReadOnlyList<string>? Emails = null) : IRequest<int>;

public sealed class GrantItemPermissionValidator : AbstractValidator<GrantItemPermissionCommand>
{
    public GrantItemPermissionValidator()
    {
        RuleFor(x => x).Must(x => x.Targets.Count > 0 || x.Emails is { Count: > 0 }).WithMessage("Vui lòng chọn người, nhóm, đơn vị hoặc nhập email.");
        RuleFor(x => x.Emails!.Count).LessThanOrEqualTo(50).When(x => x.Emails is not null).WithMessage("Tối đa 50 email mỗi lần.");
        RuleFor(x => x.ExpiresAt).Must(d => d is null || d > DateTime.UtcNow).WithMessage("Ngày hết hạn phải ở tương lai.");
        RuleFor(x => x.Message).MaximumLength(1000);
    }
}

/// <summary>Who may grant what: managers anything; people allowed to share (Chỉnh sửa) at most what they hold themselves (spec 13.7).</summary>
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
    IUserNotifier notify, IAuditLogger audit, SharingPolicy policy, GuestService guests) : IRequestHandler<GrantItemPermissionCommand, int>
{
    public async Task<int> Handle(GrantItemPermissionCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var (_, level) = await GrantRules.CheckAsync(db, engine, rbac, uid, cmd.ItemId, cmd.LevelId, ct);
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == cmd.ItemId, ct);
        // People by email: internal users, or guests from outside (the policy of every tier decides; new guests are invited).
        var byEmail = new List<(Guid Id, bool IsGuest)>();
        foreach (var address in (cmd.Emails ?? []).Select(GuestService.Normalize).Distinct())
        {
            var (id, isGuest, _) = await guests.ResolveAsync(address, cmd.ItemId, uid, ct);
            byEmail.Add((id, isGuest));
        }
        var emailTargets = byEmail.Select(x => new PrincipalTarget(x.IsGuest ? PrincipalKind.Guest : PrincipalKind.User, x.Id)).ToList();
        var targets = new List<PrincipalTarget>();
        foreach (var t in cmd.Targets.Concat(emailTargets).Distinct())
        {
            // A group Space (e.g. "DEV", by its id) stands for all its groups, so everyone in it — and whoever joins later — gets the level.
            if (t.Kind == PrincipalKind.SpaceGroup && !await db.Principals.AnyAsync(p => p.Id == t.Id && p.Kind == PrincipalKind.SpaceGroup, ct))
            {
                var groups = await db.Principals.AsNoTracking().Where(p => p.Kind == PrincipalKind.SpaceGroup && p.RefId == t.Id).Select(p => p.Id).ToListAsync(ct);
                if (groups.Count == 0) throw new NotFoundException("Không tìm thấy nhóm được chọn.");
                targets.AddRange(groups.Select(g => new PrincipalTarget(PrincipalKind.SpaceGroup, g)));
            }
            else targets.Add(t);
        }
        await policy.EnsureCanShareAsync(uid, cmd.ItemId, targets.Where(t => !(t.Kind == PrincipalKind.User && t.Id == uid))
            .Select(t => (t.Kind == PrincipalKind.Guest ? PrincipalKind.User : t.Kind, t.Id)).ToList(), breaksInheritance: true, ct);
        var count = 0;
        foreach (var t in targets)
        {
            if (t.Kind is not (PrincipalKind.User or PrincipalKind.Group or PrincipalKind.OrgUnit or PrincipalKind.OrgUnitTree or PrincipalKind.Guest or PrincipalKind.SpaceGroup))
                throw new AppException("Chỉ cấp quyền cho người dùng, khách, nhóm hoặc đơn vị.");
            if (!emailTargets.Contains(t) && !await db.Principals.AnyAsync(p => p.Id == t.Id && p.Kind == t.Kind, ct))
                throw new NotFoundException("Không tìm thấy người, nhóm hoặc đơn vị được chọn.");
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
        var sharer = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
        var link = node.Kind == SecurableKind.File ? $"/files/{node.Id}" : "/shared";
        if (cmd.Notify)
        {
            var recipients = (await RecipientsAsync(db, cmd.Targets, uid, ct)).Concat(byEmail.Select(x => x.Id)).Where(x => x != uid).Distinct();
            foreach (var r in recipients)
                await notify.NotifyUserAsync(r, "share", $"{sharer} đã chia sẻ \"{node.Name}\" với bạn ({level.Name})", cmd.Message, link, ct);
        }
        // A guest always gets the email: it is how they find the item and sign in.
        foreach (var g in byEmail.Where(x => x.IsGuest)) await guests.SendInvitationAsync(g.Id, sharer, node.Name, link, cmd.Message, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionGranted, Kind(node.Kind), node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId,
            new { targets, level = level.Code, cmd.ExpiresAt, guests = byEmail.Count(x => x.IsGuest) }), ct);
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
        if (cmd.LevelId is { } levelId && !await db.PermissionLevels.AnyAsync(l => l.Id == levelId, ct)) throw new NotFoundException("Không tìm thấy cấp quyền.");
        if (!cmd.ClearExpiry && cmd.ExpiresAt is { } until && until <= clock.GetUtcNow().UtcDateTime) throw new AppException("Ngày hết hạn phải ở tương lai.");
        // A grant to a whole Space is one row on screen but one entry per group of the Space: change them together.
        foreach (var e in await SpaceShares.SiblingsAsync(db, entry, tracked: true, ct))
        {
            if (cmd.LevelId is { } level) e.PermissionLevelId = level;
            if (cmd.ClearExpiry) e.ExpiresAt = null;
            else if (cmd.ExpiresAt is { } date) e.ExpiresAt = date;
            e.Source = AclSource.Direct;
        }
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
        foreach (var e in await SpaceShares.SiblingsAsync(db, entry, tracked: false, ct)) await editor.RevokeAsync(e.Id, ct);
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
