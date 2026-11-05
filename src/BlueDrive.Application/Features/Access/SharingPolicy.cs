using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Policy that decides sharing on an item (spec 5.4). External sharing goes through tiers — system ≥ Space ≥ folders above ≥ the item — and the
/// strictest tier wins. The Space adds: may members share with people outside it, may single files / folders be shared (stop inheriting), who
/// decides access requests.
/// </summary>
/// <param name="ExternalSource">Which tier set <paramref name="External"/> ("Hệ thống", the Space, a folder or the item).</param>
/// <param name="AnyoneLinkMaxDays">Longest life of an "anyone" link (0 = no limit).</param>
public sealed record EffectiveSharingPolicy(ExternalSharing External, string ExternalSource, int AnyoneLinkMaxDays, bool MembersShareOutside,
    bool AllowItemSharing, AccessRequestDeciders Deciders, bool InSpace, ExternalSharing? ItemExternal);

public sealed class SharingPolicy(IAppDbContext db, ISettingsService settings, IItemPermissionService engine, TimeProvider clock, Labels labels)
{
    public static string Label(ExternalSharing e) => e switch
    {
        ExternalSharing.Off => "Chỉ nội bộ",
        ExternalSharing.ExistingGuests => "Chỉ khách đã có trong hệ thống",
        ExternalSharing.NewAndExistingGuests => "Khách mới và khách đã có",
        _ => "Bất kỳ ai (liên kết ẩn danh)",
    };

    public static ExternalSharing Parse(string? value) => value switch
    {
        "off" => ExternalSharing.Off,
        "existing_guests" => ExternalSharing.ExistingGuests,
        "new_and_existing_guests" => ExternalSharing.NewAndExistingGuests,
        _ => ExternalSharing.Anyone,
    };

    public async Task<EffectiveSharingPolicy> EffectiveAsync(Guid itemId, CancellationToken ct)
    {
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == itemId, ct) ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        var external = Parse(await settings.GetAsync(SettingKeys.ShareExternal, "anyone", ct));
        var source = "Hệ thống";
        var maxDays = await settings.GetAsync(SettingKeys.ShareAnyoneLinkMaxDays, 30, ct);
        var rootId = node.SpaceId ?? node.PersonalDriveId;
        var ws = rootId is { } r ? await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == r, ct) : null;
        if (ws?.ExternalSharing is { } wsLevel && wsLevel < external)
        {
            external = wsLevel;
            source = node.SpaceId is null ? "Drive của tôi" : $"Space \"{ws.Name}\"";
        }
        // Folders above (and the item itself): the strictest wins.
        var ids = node.Path.Split('.').Select(p => Guid.TryParseExact(p, "N", out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var folders = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id) && f.ExternalSharing != null)
            .Select(f => new { f.Id, f.Name, f.ExternalSharing }).ToListAsync(ct);
        var file = node.Kind == SecurableKind.File
            ? await db.Files.AsNoTracking().Where(f => f.Id == node.Id && f.ExternalSharing != null).Select(f => new { f.Id, f.Name, f.ExternalSharing }).FirstOrDefaultAsync(ct)
            : null;
        foreach (var t in file is null ? folders : folders.Append(file))
            if (t.ExternalSharing!.Value < external)
            {
                external = t.ExternalSharing.Value;
                source = t.Id == node.Id ? "Mục này" : $"Thư mục \"{t.Name}\"";
            }
        // The sensitivity label (spec 5.4 "Nhãn phân loại" tier).
        if (node.Kind is SecurableKind.File or SecurableKind.Folder && await labels.ForAsync(node.Id, ct) is { } lab)
        {
            var cap = lab.Label.BlockExternal ? ExternalSharing.Off : lab.Label.BlockAnyoneLinks ? ExternalSharing.NewAndExistingGuests : ExternalSharing.Anyone;
            if (cap < external)
            {
                external = cap;
                source = $"Nhãn \"{lab.Label.Name}\"";
            }
        }
        var own = node.Kind == SecurableKind.File ? file?.ExternalSharing : folders.FirstOrDefault(f => f.Id == node.Id)?.ExternalSharing;
        var inSpace = node.SpaceId is not null;
        return new EffectiveSharingPolicy(external, source, maxDays, !inSpace || (ws?.MembersShareOutside ?? true), !inSpace || (ws?.AllowItemSharing ?? true),
            ws?.AccessRequestDeciders ?? AccessRequestDeciders.ItemManagers, inSpace, own);
    }

    /// <summary>An "anyone" link: allowed by every tier; its end date within the longest life allowed (none given = the longest).</summary>
    public async Task<DateTime?> EnsureAnyoneLinkAsync(Guid itemId, DateTime? expiresAt, CancellationToken ct)
    {
        var p = await EffectiveAsync(itemId, ct);
        if (p.External < ExternalSharing.Anyone)
            throw new ForbiddenException($"Chính sách chia sẻ ({p.ExternalSource}: {Label(p.External)}) không cho phép liên kết cho bất kỳ ai.");
        if (p.AnyoneLinkMaxDays <= 0) return expiresAt;
        var max = clock.GetUtcNow().UtcDateTime.AddDays(p.AnyoneLinkMaxDays);
        if (expiresAt is null) return max;
        // A date picked "until that day" ends at the end of the day: within a day it is the longest life, beyond it is refused.
        if (expiresAt > max.AddDays(1))
            throw new AppException($"Liên kết cho bất kỳ ai dùng tối đa {p.AnyoneLinkMaxDays} ngày theo chính sách chia sẻ.");
        return expiresAt > max ? max : expiresAt;
    }

    /// <summary>
    /// The Space's rules for giving access to an item: sharing single items (stopping inheritance) and sharing with people outside the Space
    /// (users who are no member of it, any group or unit, an organization link). Who manages the Space is not bound by them.
    /// </summary>
    public async Task EnsureCanShareAsync(Guid uid, Guid itemId, IReadOnlyCollection<(PrincipalKind Kind, Guid Id)> targets, bool breaksInheritance, CancellationToken ct)
    {
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == itemId, ct);
        if (node.SpaceId is not { } space) return;
        if ((await engine.GetEffectiveAsync(uid, node.SpaceId.Value, ct)).Has(AccessRight.ManageSpace)) return;
        var p = await EffectiveAsync(itemId, ct);
        if (breaksInheritance && node.ParentId is not null && !p.AllowItemSharing)
            throw new ForbiddenException("Space này không cho phép chia sẻ riêng từng tệp, thư mục. Hãy đề nghị Quản lý Space thêm người vào Space.");
        if (p.MembersShareOutside || targets.Count == 0) return;
        var groups = Enum.GetValues<SpaceGroupKind>().Select(k => PermissionProjection.GroupOf(space, k)).ToList();
        var users = targets.Where(t => t.Kind == PrincipalKind.User).Select(t => t.Id).ToList();
        var members = await db.PrincipalMemberships.AsNoTracking().Where(m => users.Contains(m.UserId) && groups.Contains(m.PrincipalId))
            .Select(m => m.UserId).Distinct().CountAsync(ct);
        // The Space's own groups are inside it; any other group, unit or Space is outside.
        var inside = users.Count + targets.Count(t => t.Kind == PrincipalKind.SpaceGroup && groups.Contains(t.Id));
        if (inside != targets.Count || members != users.Count)
            throw new ForbiddenException("Space này chỉ cho phép thành viên chia sẻ với người trong Space.");
    }
}
