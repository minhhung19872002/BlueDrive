using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Sharing;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Spec 5.5: access given until a date is announced 3 days before it ends — to the person (or the one who gave it, for a group or a unit) and to
/// who gave it — with a "Gia hạn" button, and again once it has ended; sharing links about to end are announced to who made them.
/// </summary>
public sealed class ExpiryReminders(IAppDbContext db, IUserNotifier notify, TimeProvider clock)
{
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(3);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var sent = 0;
        var levels = await db.PermissionLevels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        // Access about to end, then access that has just ended (older ones from before these notices are left alone).
        var soon = await db.AclEntries.Where(e => e.Source == AclSource.Direct && e.ExpiresAt > now && e.ExpiresAt <= now + Ahead && e.ReminderSentAt == null)
            .OrderBy(e => e.ExpiresAt).Take(500).ToListAsync(ct);
        var ended = await db.AclEntries.Where(e => e.Source == AclSource.Direct && e.ExpiresAt <= now && e.ExpiresAt > now.AddDays(-2) && e.ExpiredNoticeAt == null)
            .OrderBy(e => e.ExpiresAt).Take(500).ToListAsync(ct);
        var ids = soon.Concat(ended).Select(e => e.ScopeId).Distinct().ToList();
        var items = await db.Securables.AsNoTracking().Where(s => ids.Contains(s.Id) && !s.IsDeleted).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var names = await PrincipalDisplay.ResolveAsync(db, soon.Concat(ended).Select(e => e.PrincipalId), ct);
        foreach (var (e, isEnded) in soon.Select(e => (e, false)).Concat(ended.Select(e => (e, true))))
        {
            if (isEnded) e.ExpiredNoticeAt = now; else e.ReminderSentAt = now;
            if (!items.TryGetValue(e.ScopeId, out var item)) continue;
            var level = levels.GetValueOrDefault(e.PermissionLevelId, "");
            var date = Vn(e.ExpiresAt!.Value);
            var link = $"/renew/permission/{e.Id}";
            if (e.PrincipalKind == PrincipalKind.User)
                await notify.NotifyUserAsync(e.PrincipalId, "permission_expiry",
                    isEnded ? $"Quyền \"{level}\" của bạn trên \"{item}\" đã hết hạn" : $"Quyền \"{level}\" của bạn trên \"{item}\" sẽ hết hạn lúc {date}",
                    isEnded ? "Bạn có thể đề nghị người chia sẻ cấp lại." : "Bấm Gia hạn để đề nghị người chia sẻ gia hạn.", link, ct);
            if (e.GrantedBy is { } by && !(e.PrincipalKind == PrincipalKind.User && e.PrincipalId == by))
            {
                var who = names.GetValueOrDefault(e.PrincipalId, ("", null)).Name;
                await notify.NotifyUserAsync(by, "permission_expiry",
                    isEnded ? $"Quyền của {who} trên \"{item}\" đã hết hạn" : $"Quyền của {who} trên \"{item}\" sẽ hết hạn lúc {date}",
                    "Bấm Gia hạn để kéo dài quyền.", link, ct);
            }
            sent++;
        }

        var links = await db.ShareLinks.Where(l => !l.IsRevoked && l.ExpiresAt > now && l.ExpiresAt <= now + Ahead && l.ReminderSentAt == null && l.CreatedBy != null)
            .Take(500).ToListAsync(ct);
        var linkItems = links.Select(l => l.ResourceId).Distinct().ToList();
        var linkNames = await db.Securables.AsNoTracking().Where(s => linkItems.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        foreach (var l in links)
        {
            l.ReminderSentAt = now;
            await notify.NotifyUserAsync(l.CreatedBy!.Value, "permission_expiry",
                $"Liên kết chia sẻ \"{linkNames.GetValueOrDefault(l.ResourceId, "")}\" sẽ hết hạn lúc {Vn(l.ExpiresAt!.Value)}", "Bấm Gia hạn để kéo dài liên kết.",
                $"/renew/link/{l.Id}", ct);
            sent++;
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Vietnam time (UTC+7) for messages.</summary>
    public static string Vn(DateTime utc) => utc.AddHours(7).ToString("HH:mm dd/MM/yyyy");
}

// ---------- "Gia hạn" ----------

/// <summary>What the renewal page shows: whose access (or which link), on what, until when, and what the viewer may do.</summary>
public sealed record RenewalInfoDto(string Kind, Guid Id, Guid ItemId, string ItemName, SecurableKind ItemKind, string Who, string? LevelName, DateTime? ExpiresAt,
    bool Expired, bool CanExtend, bool CanRequest, int? MaxDays);

public sealed record GetRenewalQuery(string Kind, Guid Id) : IRequest<RenewalInfoDto>;

/// <summary>Who gave the access / made the link (or manages the item) pushes the end date: from today or from the old date, whichever is later.</summary>
public sealed record ExtendCommand(string Kind = "", Guid Id = default, int Days = 30) : IRequest<RenewalInfoDto>;

public sealed class ExtendValidator : AbstractValidator<ExtendCommand>
{
    public ExtendValidator() => RuleFor(x => x.Days).InclusiveBetween(1, 365).WithMessage("Số ngày gia hạn từ 1 đến 365.");
}

/// <summary>The person whose access ends asks for more (an access request for the same level).</summary>
public sealed record RequestRenewalCommand(Guid EntryId) : IRequest<Guid>;

public sealed class RenewalHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, AclEditor editor, SharingPolicy policy, LinkModel links,
    IUserNotifier notify, IAuditLogger audit, ISender mediator, TimeProvider clock)
    : IRequestHandler<GetRenewalQuery, RenewalInfoDto>, IRequestHandler<ExtendCommand, RenewalInfoDto>, IRequestHandler<RequestRenewalCommand, Guid>
{
    public async Task<RenewalInfoDto> Handle(GetRenewalQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        if (q.Kind == "link")
        {
            var l = await LinkAsync(q.Id, ct);
            var node = await NodeAsync(l.ResourceId, ct);
            var can = l.CreatedBy == uid || await engine.HasAsync(uid, l.ResourceId, AccessRight.ManagePermissions, ct);
            if (!can) throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");
            var level = l.LevelId is { } lv ? await db.PermissionLevels.Where(x => x.Id == lv).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
            int? max = l.Scope == ShareLinkScope.Anyone ? (await policy.EffectiveAsync(l.ResourceId, ct)).AnyoneLinkMaxDays : null;
            return new RenewalInfoDto("link", l.Id, node.Id, node.Name, node.Kind, ShareLinkMapping.ScopeLabel(l.Scope), level, l.ExpiresAt, l.ExpiresAt <= now, !l.IsRevoked,
                false, max is > 0 ? max : null);
        }
        var e = await EntryAsync(q.Id, ct);
        var item = await NodeAsync(e.ScopeId, ct);
        var canExtend = await CanExtendAsync(uid, e, ct);
        var mine = e.PrincipalKind == PrincipalKind.User && e.PrincipalId == uid;
        if (!canExtend && !mine) throw new NotFoundException("Không tìm thấy quyền này.");
        var who = (await PrincipalDisplay.ResolveAsync(db, [e.PrincipalId], ct)).GetValueOrDefault(e.PrincipalId, ("", null)).Name;
        var levelName = await db.PermissionLevels.Where(x => x.Id == e.PermissionLevelId).Select(x => x.Name).FirstAsync(ct);
        // Someone without access any more sees the item's name: it is their own (ended) access.
        return new RenewalInfoDto("permission", e.Id, item.Id, item.Name, item.Kind, who, levelName, e.ExpiresAt, e.ExpiresAt <= now, canExtend, mine && !canExtend, null);
    }

    public async Task<RenewalInfoDto> Handle(ExtendCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        if (cmd.Kind == "link")
        {
            var l = await LinkAsync(cmd.Id, ct);
            if (l.CreatedBy != uid && !await engine.HasAsync(uid, l.ResourceId, AccessRight.ManagePermissions, ct)) throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");
            if (l.IsRevoked) throw new ConflictException("Liên kết đã bị thu hồi.");
            var until = Later(l.ExpiresAt, now).AddDays(cmd.Days);
            if (l.Scope == ShareLinkScope.Anyone)
            {
                // Still allowed for anyone; never longer than the policy's longest life from today.
                var p = await policy.EffectiveAsync(l.ResourceId, ct);
                if (p.External < ExternalSharing.Anyone)
                    throw new ForbiddenException($"Chính sách chia sẻ ({p.ExternalSource}: {SharingPolicy.Label(p.External)}) không cho phép liên kết cho bất kỳ ai.");
                if (p.AnyoneLinkMaxDays > 0 && until > now.AddDays(p.AnyoneLinkMaxDays)) until = now.AddDays(p.AnyoneLinkMaxDays);
            }
            l.ExpiresAt = until;
            l.ReminderSentAt = null;
            await db.SaveChangesAsync(ct);
            var people = await db.ShareLinkMembers.Where(m => m.LinkId == l.Id).Select(m => m.UserId).ToListAsync(ct);
            await links.ApplyAsync(l, people, uid, ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Share, l.ResourceType, l.ResourceId, null, null, new { shareLink = l.Id, renewedUntil = until }), ct);
            return await Handle(new GetRenewalQuery("link", l.Id), ct);
        }
        var e = await db.AclEntries.FirstOrDefaultAsync(x => x.Id == cmd.Id && x.Source == AclSource.Direct, ct) ?? throw new NotFoundException("Không tìm thấy quyền này.");
        if (!await CanExtendAsync(uid, e, ct)) throw new NotFoundException("Không tìm thấy quyền này.");
        var node = await NodeAsync(e.ScopeId, ct);
        e.ExpiresAt = Later(e.ExpiresAt, now).AddDays(cmd.Days);
        e.ReminderSentAt = null;
        e.ExpiredNoticeAt = null;
        await db.SaveChangesAsync(ct);
        await editor.TouchAsync(e.ScopeId, ct);
        if (e.PrincipalKind == PrincipalKind.User && e.PrincipalId != uid)
            await notify.NotifyUserAsync(e.PrincipalId, "permission_expiry", $"Quyền của bạn trên \"{node.Name}\" đã được gia hạn đến {ExpiryReminders.Vn(e.ExpiresAt.Value)}", null,
                node.Kind == SecurableKind.File ? $"/files/{node.Id}" : "/shared", ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionLevelChanged, GrantItemPermissionHandler.Kind(node.Kind), node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId,
            new { entry = e.Id, renewedUntil = e.ExpiresAt }), ct);
        return await Handle(new GetRenewalQuery("permission", e.Id), ct);
    }

    public async Task<Guid> Handle(RequestRenewalCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var e = await EntryAsync(cmd.EntryId, ct);
        if (!(e.PrincipalKind == PrincipalKind.User && e.PrincipalId == uid)) throw new NotFoundException("Không tìm thấy quyền này.");
        var until = e.ExpiresAt is { } d ? ExpiryReminders.Vn(d) : "";
        return await mediator.Send(new RequestAccessCommand(e.ScopeId, e.PermissionLevelId, $"Đề nghị gia hạn quyền (hết hạn {until})."), ct);
    }

    private static DateTime Later(DateTime? a, DateTime now) => a is { } x && x > now ? x : now;

    private async Task<bool> CanExtendAsync(Guid uid, AclEntry e, CancellationToken ct) =>
        e.GrantedBy == uid || await engine.HasAsync(uid, e.ScopeId, AccessRight.ManagePermissions, ct);

    private async Task<AclEntry> EntryAsync(Guid id, CancellationToken ct) =>
        await db.AclEntries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Source == AclSource.Direct, ct) ?? throw new NotFoundException("Không tìm thấy quyền này.");

    private async Task<ShareLink> LinkAsync(Guid id, CancellationToken ct) =>
        await db.ShareLinks.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");

    private async Task<Securable> NodeAsync(Guid id, CancellationToken ct) =>
        await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tư liệu.");
}
