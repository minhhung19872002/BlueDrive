using System.Security.Cryptography;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlueDrive.Application.Features.Sharing;

public sealed record LinkPersonDto(Guid Id, string Name);

/// <param name="Url">Empty for a link made before tokens were stored only as hashes (it still works, its address cannot be shown again).</param>
public sealed record ShareLinkDto(Guid Id, string Token, string Url, ResourceType ResourceType, Guid ResourceId, bool AllowDownload, bool AllowView,
    DateTime? ExpiresAt, int? MaxAccessCount, int AccessCount, bool IsRevoked, bool RequireLogin, DateTime CreatedAt, string Status, bool AllowEdit,
    ShareLinkScope Scope, Guid? LevelId, string? LevelName, IReadOnlyList<LinkPersonDto> People, string? CreatedByName);

public static class ShareLinkMapping
{
    public const string TokenPurpose = "share-link";

    public static string StatusOf(ShareLink l, DateTime now) => l.IsRevoked ? "revoked" : l.ExpiresAt < now ? "expired"
        : l.MaxAccessCount is { } m && l.AccessCount >= m ? "exhausted" : "active";

    /// <summary>The link's token when it can be derived again (links made since tokens are stored hashed), otherwise empty.</summary>
    public static string TokenOf(ShareLink l, ITokenService tokens)
    {
        var token = tokens.DeriveToken(TokenPurpose, l.Id);
        return PublicLinks.Hash(token) == l.TokenHash ? token : "";
    }

    public static async Task<List<ShareLinkDto>> ToDtosAsync(IAppDbContext db, IReadOnlyList<ShareLink> links, ITokenService tokens, string baseUrl, DateTime now,
        CancellationToken ct)
    {
        var ids = links.Select(l => l.Id).ToList();
        var people = await db.ShareLinkMembers.AsNoTracking().Where(m => ids.Contains(m.LinkId))
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.LinkId, u.Id, u.FullName }).ToListAsync(ct);
        var listed = links.Where(l => l.Scope == ShareLinkScope.Specific).Select(l => l.Id).ToHashSet();
        var levels = await db.PermissionLevels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var creators = links.Select(l => l.CreatedBy ?? Guid.Empty).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => creators.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return links.Select(l =>
        {
            var token = TokenOf(l, tokens);
            return new ShareLinkDto(l.Id, token, token == "" ? "" : $"{baseUrl.TrimEnd('/')}/s/{token}", l.ResourceType, l.ResourceId,
                l.AllowDownload, l.AllowView, l.ExpiresAt, l.MaxAccessCount, l.AccessCount, l.IsRevoked, l.RequireLogin, l.CreatedAt, StatusOf(l, now), l.AllowEdit,
                l.Scope, l.LevelId, l.LevelId is { } lv ? levels.GetValueOrDefault(lv) : null,
                listed.Contains(l.Id) ? people.Where(p => p.LinkId == l.Id).Select(p => new LinkPersonDto(p.Id, p.FullName)).ToList() : [],
                l.CreatedBy is { } c ? names.GetValueOrDefault(c) : null);
        }).ToList();
    }

    public static string ScopeLabel(ShareLinkScope s) => s switch
    {
        ShareLinkScope.Organization => "Liên kết: mọi người trong tổ chức",
        ShareLinkScope.Specific => "Liên kết: người cụ thể",
        ShareLinkScope.Anyone => "Liên kết: bất kỳ ai",
        _ => "Liên kết: người đã có quyền",
    };
}

/// <summary>
/// Spec 5.3. <c>Scope</c> null = the earlier form (RequireLogin: people with access; otherwise anyone, AllowEdit as editor).
/// <c>LevelId</c>: Chỉ xem / Bình luận / Chỉnh sửa / Chỉ xem (không tải); <c>AllowDownload</c> false blocks downloads.
/// <c>People</c>: who a "Người cụ thể" link works for.
/// </summary>
public sealed record SaveShareLinkCommand(Guid? Id, ResourceType ResourceType, Guid ResourceId, bool AllowDownload,
    DateTime? ExpiresAt, int? MaxAccessCount, bool RequireLogin, bool AllowEdit = false,
    ShareLinkScope? Scope = null, Guid? LevelId = null, IReadOnlyList<Guid>? People = null) : IRequest<ShareLinkDto>;

public sealed class SaveShareLinkValidator : AbstractValidator<SaveShareLinkCommand>
{
    public SaveShareLinkValidator()
    {
        RuleFor(x => x.ResourceType).Must(t => t != ResourceType.Workspace).WithMessage("Chỉ tạo liên kết cho thư mục hoặc tệp.");
        RuleFor(x => x.ExpiresAt).Must(d => d is null || d > DateTime.UtcNow).WithMessage("Ngày hết hạn phải ở tương lai.");
        RuleFor(x => x.MaxAccessCount).InclusiveBetween(1, 1_000_000).When(x => x.MaxAccessCount.HasValue).WithMessage("Số lượt truy cập tối đa phải từ 1 trở lên.");
        RuleFor(x => x.People).NotEmpty().When(x => x.Scope == ShareLinkScope.Specific).WithMessage("Vui lòng chọn người được dùng liên kết.");
        RuleFor(x => x.People!.Count).LessThanOrEqualTo(500).When(x => x.People is not null).WithMessage("Tối đa 500 người cho một liên kết.");
    }
}

public sealed class SaveShareLinkHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IItemPermissionService engine,
    ITokenService tokens, IConfiguration config, IAuditLogger audit, TimeProvider clock, ISettingsService settings, LinkModel model, IUserNotifier notify,
    SharingPolicy policy) : IRequestHandler<SaveShareLinkCommand, ShareLinkDto>
{
    /// <summary>Levels a link may give.</summary>
    private static readonly Guid[] LinkLevels = [SystemLevels.Viewer, SystemLevels.Commenter, SystemLevels.Editor, SystemLevels.ViewOnly];

    public async Task<ShareLinkDto> Handle(SaveShareLinkCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        ShareLink link;
        var created = cmd.Id is null;
        if (cmd.Id is { } id)
        {
            link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");
            if (link.CreatedBy != uid) await auth.EnsureAsync(uid, link.ResourceType, link.ResourceId, AccessLevel.Manage, ct);
        }
        else
        {
            await auth.EnsureAsync(uid, cmd.ResourceType, cmd.ResourceId, AccessLevel.View, ct);
            link = new ShareLink { ResourceType = cmd.ResourceType, ResourceId = cmd.ResourceId };
            link.TokenHash = PublicLinks.Hash(tokens.DeriveToken(ShareLinkMapping.TokenPurpose, link.Id));
            db.ShareLinks.Add(link);
        }
        var scope = cmd.Scope ?? (cmd.RequireLogin ? ShareLinkScope.Existing : ShareLinkScope.Anyone);
        Guid? levelId = scope == ShareLinkScope.Existing ? null
            : cmd.LevelId ?? (cmd.AllowEdit ? SystemLevels.Editor : cmd.AllowDownload ? SystemLevels.Viewer : SystemLevels.ViewOnly);
        if (levelId is { } lv && !LinkLevels.Contains(lv)) throw new AppException("Liên kết chỉ cấp được quyền Chỉ xem, Bình luận, Chỉnh sửa hoặc Chỉ xem.");
        var level = levelId is { } l2 ? SystemLevels.All.First(x => x.Id == l2) : null;
        var allowDownload = cmd.AllowDownload && (level is null || level.Permissions.Has(AccessRight.Download));
        var allowEdit = level?.Id == SystemLevels.Editor;

        // You can give through a link at most what you have (like sharing with people); downloads and editing need your own right.
        var (rights, locked) = await engine.GetWithLockAsync(uid, link.ResourceId, ct);
        // A link that gives access ("Bất kỳ ai", people, unit) needs a sharing right on the item itself, like sharing with people; a link for
        // "Người đã có quyền" gives nothing new, so whoever sees the item may get it (Google Drive: "Sao chép liên kết").
        if (level is not null && (rights & (AccessRight.ShareInternal | AccessRight.CreateLinks | AccessRight.ManagePermissions)) == 0)
            throw new ForbiddenException("Bạn không có quyền chia sẻ mục này.");
        if (level is not null && !rights.Has(AccessRight.ManagePermissions) && !rights.Has(level.Permissions & ~AccessRight.Download))
            throw new ForbiddenException("Bạn không thể tạo liên kết với quyền cao hơn quyền bạn đang có.");
        if (allowDownload && !link.AllowDownload && !rights.Has(AccessRight.Download))
            throw new ForbiddenException("Bạn không có quyền tải xuống nên không thể cho phép tải xuống qua liên kết.");
        if (allowEdit && !link.AllowEdit)
        {
            if (scope == ShareLinkScope.Anyone && !await settings.GetAsync(SettingKeys.ShareLinkAllowEdit, true, ct))
                throw new ForbiddenException("Quản trị hệ thống đã tắt chỉnh sửa qua liên kết chia sẻ.");
            if (!rights.Has(AccessRight.EditItems))
                throw new ForbiddenException("Bạn không có quyền chỉnh sửa nên không thể cho phép chỉnh sửa qua liên kết.");
        }
        if (locked && allowEdit) throw new AppException("Không gian đang bị khóa, liên kết chỉ có thể cấp quyền xem.", 423);

        var people = scope == ShareLinkScope.Specific ? (cmd.People ?? []).Distinct().ToList() : [];
        if (people.Count > 0 && await db.Users.CountAsync(u => people.Contains(u.Id) && u.IsActive, ct) != people.Count)
            throw new AppException("Có người được chọn không tồn tại hoặc đã bị khóa.");

        // Spec 5.4: an "anyone" link only where every tier allows it, within its longest life; a link reaching people outside the Space only
        // where members may share outside it (and it gives the item its own permissions).
        var expiresAt = scope == ShareLinkScope.Anyone ? await policy.EnsureAnyoneLinkAsync(link.ResourceId, cmd.ExpiresAt, ct) : cmd.ExpiresAt;
        if (scope is ShareLinkScope.Organization or ShareLinkScope.Specific or ShareLinkScope.Anyone)
            await policy.EnsureCanShareAsync(uid, link.ResourceId,
                scope == ShareLinkScope.Specific ? people.Select(p => (PrincipalKind.User, p)).ToList() : [(PrincipalKind.Everyone, StableGuid.Everyone)],
                breaksInheritance: scope != ShareLinkScope.Anyone, ct);

        link.Scope = scope;
        link.LevelId = levelId;
        link.AllowDownload = allowDownload;
        link.AllowEdit = allowEdit;
        link.AllowView = true;
        link.ExpiresAt = expiresAt;
        link.MaxAccessCount = cmd.MaxAccessCount;
        link.RequireLogin = scope != ShareLinkScope.Anyone;
        await db.SaveChangesAsync(ct);
        var added = await model.ApplyAsync(link, people, uid, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var dto = (await ShareLinkMapping.ToDtosAsync(db, [link], tokens, config["App:PublicBaseUrl"] ?? "", now, ct))[0];
        if (added.Count > 0)
        {
            var sharer = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
            var name = await db.Securables.Where(s => s.Id == link.ResourceId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "";
            foreach (var p in added.Where(p => p != uid))
                await notify.NotifyUserAsync(p, "share", $"{sharer} đã gửi bạn liên kết tới \"{name}\"", null, dto.Url == "" ? null : $"/s/{dto.Token}", ct);
        }
        await audit.LogAsync(new AuditEntry(created ? AuditAction.LinkCreated : AuditAction.Share, link.ResourceType, link.ResourceId, null, null,
            new { shareLink = link.Id, scope = link.Scope, level = level?.Code, link.AllowDownload, link.ExpiresAt, link.MaxAccessCount, people = people.Count }), ct);
        return dto;
    }
}

/// <summary>
/// A link of the kinds "Mọi người trong tổ chức" and "Người cụ thể" is a principal of the permission model (spec 5.3): it has an entry with its
/// level on the shared item (until the link's end date), and the people it works for are its members — listed people at once, anyone
/// signed in once they open it. Revoking or changing the kind takes that away again.
/// </summary>
public sealed class LinkModel(IAppDbContext db, AclEditor editor, TimeProvider clock)
{
    /// <summary>Brings the model in line with the link; returns the people newly listed (to be told).</summary>
    public async Task<List<Guid>> ApplyAsync(ShareLink link, IReadOnlyList<Guid> people, Guid? by, CancellationToken ct)
    {
        var grants = !link.IsRevoked && link.Scope is ShareLinkScope.Organization or ShareLinkScope.Specific && link.LevelId is not null;
        await RemoveEntriesAsync(link.Id, ct);
        var principal = await db.Principals.FirstOrDefaultAsync(p => p.Id == link.Id, ct);
        if (principal is null) db.Principals.Add(principal = new Principal { Id = link.Id, Kind = PrincipalKind.Link });
        principal.RefId = link.ResourceId;
        principal.DisplayName = ShareLinkMapping.ScopeLabel(link.Scope);
        await db.SaveChangesAsync(ct);
        if (grants && await db.Securables.AnyAsync(s => s.Id == link.ResourceId, ct))
            await editor.GrantAsync(link.ResourceId, PrincipalKind.Link, link.Id, link.LevelId!.Value, link.ExpiresAt, by, ct, source: AclSource.Link);

        var current = await db.ShareLinkMembers.Where(m => m.LinkId == link.Id).Select(m => m.UserId).ToListAsync(ct);
        // Listed people are exactly the list; an organization link keeps who already opened it; other kinds have no members.
        var keep = !grants ? [] : link.Scope == ShareLinkScope.Specific ? people.ToList() : current;
        var gone = current.Except(keep).ToList();
        var added = keep.Except(current).ToList();
        await db.ShareLinkMembers.Where(m => m.LinkId == link.Id && gone.Contains(m.UserId)).ExecuteDeleteAsync(ct);
        await db.PrincipalMemberships.Where(m => m.PrincipalId == link.Id && gone.Contains(m.UserId)).ExecuteDeleteAsync(ct);
        foreach (var u in added) await AddMemberAsync(link.Id, u, ct);
        await BumpAsync(gone.Concat(added), ct);
        return added;
    }

    /// <summary>Someone signed in opened an organization link: they become a member of it (their access then comes from the model).</summary>
    public async Task RedeemAsync(ShareLink link, Guid userId, CancellationToken ct)
    {
        if (await db.ShareLinkMembers.AnyAsync(m => m.LinkId == link.Id && m.UserId == userId, ct)) return;
        await AddMemberAsync(link.Id, userId, ct);
        await BumpAsync([userId], ct);
    }

    public async Task RevokeAsync(ShareLink link, CancellationToken ct)
    {
        await RemoveEntriesAsync(link.Id, ct);
        var members = await db.ShareLinkMembers.Where(m => m.LinkId == link.Id).Select(m => m.UserId).ToListAsync(ct);
        await db.ShareLinkMembers.Where(m => m.LinkId == link.Id).ExecuteDeleteAsync(ct);
        await db.PrincipalMemberships.Where(m => m.PrincipalId == link.Id).ExecuteDeleteAsync(ct);
        await BumpAsync(members, ct);
    }

    private async Task RemoveEntriesAsync(Guid linkId, CancellationToken ct)
    {
        var scopes = await db.AclEntries.Where(e => e.PrincipalId == linkId).Select(e => e.ScopeId).Distinct().ToListAsync(ct);
        if (scopes.Count == 0) return;
        await db.AclEntries.Where(e => e.PrincipalId == linkId).ExecuteDeleteAsync(ct);
        foreach (var s in scopes) await editor.TouchAsync(s, ct);
    }

    private async Task AddMemberAsync(Guid linkId, Guid userId, CancellationToken ct)
    {
        db.ShareLinkMembers.Add(new ShareLinkMember { LinkId = linkId, UserId = userId, AddedAt = clock.GetUtcNow().UtcDateTime });
        if (!await db.PrincipalMemberships.AnyAsync(m => m.PrincipalId == linkId && m.UserId == userId, ct))
            db.PrincipalMemberships.Add(new PrincipalMembership { PrincipalId = linkId, UserId = userId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Their memberships changed: cached permissions keyed on the membership version go stale.</summary>
    private async Task BumpAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        foreach (var u in userIds.Distinct())
            if (await db.MembershipVersions.Where(v => v.UserId == u).ExecuteUpdateAsync(x => x.SetProperty(v => v.Version, v => v.Version + 1), ct) == 0)
            {
                db.MembershipVersions.Add(new MembershipVersion { UserId = u, Version = 1 });
                await db.SaveChangesAsync(ct);
            }
    }
}

public sealed record ListShareLinksQuery(ResourceType ResourceType, Guid ResourceId) : IRequest<IReadOnlyList<ShareLinkDto>>;

public sealed class ListShareLinksHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IConfiguration config, TimeProvider clock, ITokenService tokens)
    : IRequestHandler<ListShareLinksQuery, IReadOnlyList<ShareLinkDto>>
{
    public async Task<IReadOnlyList<ShareLinkDto>> Handle(ListShareLinksQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var a = await auth.EnsureAsync(uid, q.ResourceType, q.ResourceId, AccessLevel.View, ct);
        var links = await db.ShareLinks.AsNoTracking().Where(l => l.ResourceType == q.ResourceType && l.ResourceId == q.ResourceId && (a.CanManage || l.CreatedBy == uid))
            .OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        return await ShareLinkMapping.ToDtosAsync(db, links, tokens, config["App:PublicBaseUrl"] ?? "", clock.GetUtcNow().UtcDateTime, ct);
    }
}

public sealed record RevokeShareLinkCommand(Guid Id) : IRequest<Unit>;

public sealed class RevokeShareLinkHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IAuditLogger audit, LinkModel model)
    : IRequestHandler<RevokeShareLinkCommand, Unit>
{
    public async Task<Unit> Handle(RevokeShareLinkCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");
        if (link.CreatedBy != uid) await auth.EnsureAsync(uid, link.ResourceType, link.ResourceId, AccessLevel.Manage, ct);
        link.IsRevoked = true;
        await db.SaveChangesAsync(ct);
        await model.RevokeAsync(link, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.LinkRevoked, link.ResourceType, link.ResourceId, null, null, new { shareLink = link.Id }), ct);
        return Unit.Value;
    }
}

// ---------- Public access (/s/{token}) ----------
public sealed record PublicShareInfoDto(string Name, ResourceType ResourceType, long SizeBytes, MediaType? MediaType, string? MimeType, string? Extension,
    bool AllowDownload, bool RequireLogin, DateTime? ExpiresAt, string SharedByName, string? OrganizationName, bool AllowEdit);

/// <param name="AllowDownload">This visitor may download the shared item (<see cref="PublicLinks.CanDownloadAsync"/>).</param>
public sealed record PublicAccessGrant(string AccessToken, DateTime ExpiresAt, bool AllowDownload);

public sealed record PublicLinkTicket(Guid LinkId, Guid? UserId);

public interface IPublicLinkTickets
{
    string Issue(PublicLinkTicket t);
    PublicLinkTicket? Read(string token);
}

public sealed class PublicLinkTickets(IDataProtectionProvider provider) : IPublicLinkTickets
{
    private readonly ITimeLimitedDataProtector _p = provider.CreateProtector("BlueDrive.PublicLink.v1").ToTimeLimitedDataProtector();
    public string Issue(PublicLinkTicket t) => _p.Protect(JsonSerializer.Serialize(t), TimeSpan.FromHours(4));
    public PublicLinkTicket? Read(string token)
    {
        try { return JsonSerializer.Deserialize<PublicLinkTicket>(_p.Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }
}

public static class PublicLinks
{
    /// <summary>
    /// Whether this visitor may download through the link. "Anyone with the link": the link's own setting. Restricted (signed in, the
    /// link is only the item's address): the visitor's own right, so "Chỉ xem (không tải)" holds when opened through the link.
    /// </summary>
    public static async Task<bool> CanDownloadAsync(IResourceAuthorizationService auth, ShareLink link, Guid? visitor, ResourceType type, Guid resourceId,
        CancellationToken ct) =>
        link.RequireLogin
            ? visitor is { } uid && (await auth.GetAccessAsync(uid, type, resourceId, ct)).CanDownload
            : link.AllowDownload;

    /// <summary>SHA-256 (lower-case hex) of a link token: what the database keeps.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public static async Task<ShareLink> LoadActiveAsync(IAppDbContext db, string token, DateTime now, CancellationToken ct)
    {
        var hash = Hash(token);
        var link = await db.ShareLinks.FirstOrDefaultAsync(l => l.TokenHash == hash, ct) ?? throw new NotFoundException("Liên kết chia sẻ không tồn tại.");
        switch (ShareLinkMapping.StatusOf(link, now))
        {
            case "revoked": throw new AppException("Liên kết chia sẻ đã bị thu hồi.", 410);
            case "expired": throw new AppException("Liên kết chia sẻ đã hết hạn.", 410);
            case "exhausted": throw new AppException("Liên kết chia sẻ đã hết số lượt truy cập cho phép.", 410);
        }
        var alive = link.ResourceType == ResourceType.File
            ? await db.Files.AnyAsync(f => f.Id == link.ResourceId && !f.IsDeleted && f.ApprovalStatus == ApprovalStatus.Approved && f.ScanStatus != ScanStatus.Infected, ct)
            : await db.Folders.AnyAsync(f => f.Id == link.ResourceId && !f.IsDeleted, ct);
        if (!alive) throw new AppException("Tư liệu được chia sẻ không còn tồn tại.", 410);
        return link;
    }

    /// <summary>
    /// Resolves a file inside the shared scope (the file itself or any file under the shared folder) whose content may be served:
    /// quarantined or not-yet-scanned files are refused with 423 (a link visitor is never the uploader).
    /// </summary>
    public static async Task<StoredFile> FileInScopeAsync(IAppDbContext db, ShareLink link, Guid? fileId, CancellationToken ct)
    {
        StoredFile file;
        if (link.ResourceType == ResourceType.File)
        {
            if (fileId.HasValue && fileId != link.ResourceId) throw new NotFoundException("Không tìm thấy tệp trong liên kết chia sẻ.");
            file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == link.ResourceId, ct);
        }
        else
        {
            var root = await db.Folders.AsNoTracking().FirstAsync(f => f.Id == link.ResourceId, ct);
            file = await db.Files.AsNoTracking().Include(f => f.Folder)
                       .FirstOrDefaultAsync(f => f.Id == fileId && !f.IsDeleted && f.ApprovalStatus == ApprovalStatus.Approved && f.Folder != null && f.Folder.Path.StartsWith(root.Path), ct)
                   ?? throw new NotFoundException("Không tìm thấy tệp trong liên kết chia sẻ.");
        }
        await ScanGate.EnsureAsync(db, file, null, null, ct);
        return file;
    }
}

public sealed record GetPublicShareQuery(string Token) : IRequest<PublicShareInfoDto>;

public sealed class GetPublicShareHandler(IAppDbContext db, TimeProvider clock) : IRequestHandler<GetPublicShareQuery, PublicShareInfoDto>
{
    public async Task<PublicShareInfoDto> Handle(GetPublicShareQuery q, CancellationToken ct)
    {
        var link = await PublicLinks.LoadActiveAsync(db, q.Token, clock.GetUtcNow().UtcDateTime, ct);
        var sharer = await db.Users.AsNoTracking().Where(u => u.Id == link.CreatedBy).Select(u => new { u.FullName, Org = u.Organization != null ? u.Organization.Name : null }).FirstOrDefaultAsync(ct);
        if (link.ResourceType == ResourceType.File)
        {
            var f = await db.Files.AsNoTracking().FirstAsync(x => x.Id == link.ResourceId, ct);
            return new PublicShareInfoDto(f.Name, ResourceType.File, f.SizeBytes, f.MediaType, f.MimeType, f.Extension, link.AllowDownload, link.RequireLogin, link.ExpiresAt, sharer?.FullName ?? "", sharer?.Org, link.AllowEdit);
        }
        var folder = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == link.ResourceId, ct);
        return new PublicShareInfoDto(folder.Name, ResourceType.Folder, 0, null, null, null, link.AllowDownload, link.RequireLogin, link.ExpiresAt, sharer?.FullName ?? "", sharer?.Org, link.AllowEdit);
    }
}

/// <summary>Opens a link (login / access check for restricted links), counts one access and logs it.</summary>
public sealed record OpenPublicShareCommand(string Token) : IRequest<PublicAccessGrant>;

public sealed class OpenPublicShareHandler(IAppDbContext db, ICurrentUser me, IPublicLinkTickets tickets, IAuditLogger audit, TimeProvider clock,
    IResourceAuthorizationService auth, LinkModel links)
    : IRequestHandler<OpenPublicShareCommand, PublicAccessGrant>
{
    public async Task<PublicAccessGrant> Handle(OpenPublicShareCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var link = await PublicLinks.LoadActiveAsync(db, cmd.Token, now, ct);
        if (link.RequireLogin && !me.IsAuthenticated) throw new AppException("Liên kết này yêu cầu đăng nhập hệ thống trước khi xem.", 401);
        if (link.Scope == ShareLinkScope.Specific && !await db.ShareLinkMembers.AnyAsync(m => m.LinkId == link.Id && m.UserId == me.UserId, ct)
            && !await auth.CanAsync(me.RequireUserId(), link.ResourceType, link.ResourceId, AccessLevel.View, ct))
            throw new AppException("Liên kết này chỉ dành cho những người được người chia sẻ chỉ định.", 403);
        if (link.Scope == ShareLinkScope.Organization) await links.RedeemAsync(link, me.RequireUserId(), ct);
        // "Hạn chế" (like Google Drive's Restricted): only people who have access to the item open it; the link is just its address.
        if (link.RequireLogin && !await auth.CanAsync(me.RequireUserId(), link.ResourceType, link.ResourceId, AccessLevel.View, ct))
            throw new AppException("Bạn chưa có quyền truy cập tư liệu này. Hãy đề nghị người chia sẻ cấp quyền cho bạn.", 403);
        // Atomic increment that also enforces the access limit under concurrency.
        var updated = await db.ShareLinks.Where(l => l.Id == link.Id && !l.IsRevoked && (l.MaxAccessCount == null || l.AccessCount < l.MaxAccessCount))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessCount, l => l.AccessCount + 1), ct);
        if (updated == 0) throw new AppException("Liên kết chia sẻ đã hết số lượt truy cập cho phép.", 410);
        await audit.LogAsync(new AuditEntry(AuditAction.LinkUsed, link.ResourceType, link.ResourceId, null, null, new { shareLink = link.Id, success = true, count = link.AccessCount + 1 }), ct);
        var download = await PublicLinks.CanDownloadAsync(auth, link, me.UserId, link.ResourceType, link.ResourceId, ct);
        return new PublicAccessGrant(tickets.Issue(new PublicLinkTicket(link.Id, me.UserId)), now.AddHours(4), download);
    }
}

public sealed record ListPublicShareItemsQuery(string Token, string Access, Guid? FolderId) : IRequest<PublicFolderListing>;

public sealed record PublicFolderListing(string FolderName, IReadOnlyList<BreadcrumbItem> Breadcrumb, IReadOnlyList<DriveItemDto> Items);

public sealed class ListPublicShareItemsHandler(IAppDbContext db, IPublicLinkTickets tickets, TimeProvider clock) : IRequestHandler<ListPublicShareItemsQuery, PublicFolderListing>
{
    public async Task<PublicFolderListing> Handle(ListPublicShareItemsQuery q, CancellationToken ct)
    {
        var link = await PublicLinks.LoadActiveAsync(db, q.Token, clock.GetUtcNow().UtcDateTime, ct);
        if (tickets.Read(q.Access) is not { } t || t.LinkId != link.Id) throw new AppException("Phiên xem liên kết đã hết hạn. Vui lòng mở lại liên kết.", 401);
        if (link.ResourceType != ResourceType.Folder) throw new AppException("Liên kết này chia sẻ một tệp, không phải thư mục.");
        var root = await db.Folders.AsNoTracking().FirstAsync(f => f.Id == link.ResourceId, ct);
        var current = q.FolderId is { } fid
            ? await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fid && !f.IsDeleted && f.Path.StartsWith(root.Path), ct) ?? throw new NotFoundException("Không tìm thấy thư mục.")
            : root;
        var ids = LegacyResourceAuthorizationService.PathIds(current.Path).TakeWhile(id => id != root.Id).Append(root.Id).Reverse().ToList();
        var names = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var crumbs = ids.Select(id => new BreadcrumbItem(id, names[id])).ToList();
        var anon = Guid.Empty;
        var items = await DriveProjection.Folders(db, db.Folders.AsNoTracking().Where(f => f.ParentId == current.Id && !f.IsDeleted).OrderBy(f => f.Name), anon).ToListAsync(ct);
        items.AddRange(await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => f.FolderId == current.Id && !f.IsDeleted && f.ApprovalStatus == ApprovalStatus.Approved).OrderBy(f => f.Name), anon).ToListAsync(ct));
        // Do not leak internal identities on the public page.
        return new PublicFolderListing(current.Name, crumbs, items.Select(i => i with { CreatedBy = null, CreatedByName = null, WorkspaceName = null }).ToList());
    }
}

// ---------- Approval workflow (#43 flow, role "Phê duyệt") ----------
public sealed record PendingApprovalDto(DriveItemDto Item, string Location, DateTime SubmittedAt);

public sealed record ListPendingApprovalsQuery(ApprovalStatus Status = ApprovalStatus.Pending) : PageQuery, IRequest<PagedResult<PendingApprovalDto>>;

public sealed class ListPendingApprovalsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms)
    : IRequestHandler<ListPendingApprovalsQuery, PagedResult<PendingApprovalDto>>
{
    public async Task<PagedResult<PendingApprovalDto>> Handle(ListPendingApprovalsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var rbac = await perms.HasAsync(uid, Perm.DocApprove, ct);
        var candidates = await db.Files.AsNoTracking().Include(f => f.Workspace).Include(f => f.Folder)
            .Where(f => !f.IsDeleted && f.ApprovalStatus == q.Status).OrderBy(f => f.CreatedAt).Take(2000).ToListAsync(ct);
        var visible = new List<Domain.Entities.StoredFile>();
        foreach (var f in candidates)
        {
            var a = await auth.GetAccessAsync(uid, ResourceType.File, f.Id, ct);
            if (a.CanApprove || (rbac && a.CanView)) visible.Add(f);
        }
        var page = visible.Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize).ToList();
        var ids = page.Select(f => f.Id).ToList();
        var dtos = (await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => ids.Contains(f.Id)), uid).ToListAsync(ct)).ToDictionary(d => d.Id);
        var result = new List<PendingApprovalDto>();
        foreach (var f in page)
            result.Add(new PendingApprovalDto(dtos[f.Id], FileLocation.ToText(await FileLocation.BuildAsync(db, f.Workspace!, f.Folder, uid, ct)), f.UpdatedAt ?? f.CreatedAt));
        return new PagedResult<PendingApprovalDto>(result, visible.Count, q.SafePage, q.SafePageSize);
    }
}

public sealed record ReviewFileCommand(Guid FileId, bool Approve, string? Reason) : IRequest<Unit>;

public sealed class ReviewFileValidator : AbstractValidator<ReviewFileCommand>
{
    public ReviewFileValidator() => RuleFor(x => x.Reason).NotEmpty().When(x => !x.Approve).WithMessage("Vui lòng nhập lý do từ chối.").MaximumLength(2000);
}

public sealed class ReviewFileHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IUserNotifier notify,
    IAuditLogger audit, TimeProvider clock) : IRequestHandler<ReviewFileCommand, Unit>
{
    public async Task<Unit> Handle(ReviewFileCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var a = await auth.EnsureAsync(uid, ResourceType.File, cmd.FileId, AccessLevel.View, ct);
        if (!a.CanApprove && !await perms.HasAsync(uid, Perm.DocApprove, ct)) throw new ForbiddenException("Bạn không có quyền phê duyệt tư liệu.");
        var f = await db.Files.FirstAsync(x => x.Id == cmd.FileId, ct);
        if (f.ApprovalStatus != ApprovalStatus.Pending) throw new ConflictException("Tư liệu không ở trạng thái chờ phê duyệt.");
        f.ApprovalStatus = cmd.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        f.ApprovedBy = uid;
        f.ApprovedAt = clock.GetUtcNow().UtcDateTime;
        f.RejectionReason = cmd.Approve ? null : cmd.Reason?.Trim();
        await db.SaveChangesAsync(ct);
        if (f.CreatedBy is { } uploader && uploader != uid)
            await notify.NotifyUserAsync(uploader, "approval", cmd.Approve ? $"Tư liệu \"{f.Name}\" đã được phê duyệt" : $"Tư liệu \"{f.Name}\" bị từ chối",
                cmd.Approve ? null : $"Lý do: {f.RejectionReason}", $"/files/{f.Id}", ct);
        await audit.LogAsync(new AuditEntry(cmd.Approve ? AuditAction.Approve : AuditAction.Reject, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { reason = f.RejectionReason }), ct);
        return Unit.Value;
    }
}

/// <summary>Send a draft/rejected file (back) to the approval queue.</summary>
public sealed record SubmitForApprovalCommand(Guid FileId) : IRequest<Unit>;

public sealed class SubmitForApprovalHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IUserNotifier notify, IAuditLogger audit)
    : IRequestHandler<SubmitForApprovalCommand, Unit>
{
    public async Task<Unit> Handle(SubmitForApprovalCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var f = await db.Files.FirstOrDefaultAsync(x => x.Id == cmd.FileId && !x.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tài liệu.");
        if (f.CreatedBy != uid) await auth.EnsureAsync(uid, ResourceType.File, f.Id, AccessLevel.Edit, ct);
        if (f.ApprovalStatus is ApprovalStatus.Pending or ApprovalStatus.Approved) throw new ConflictException("Tư liệu đang chờ duyệt hoặc đã được duyệt.");
        f.ApprovalStatus = ApprovalStatus.Pending;
        f.RejectionReason = null;
        await db.SaveChangesAsync(ct);
        await notify.NotifyPermissionAsync(Perm.DocApprove, "approval", $"Tư liệu \"{f.Name}\" chờ phê duyệt", null, "/approvals", ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { approval = "submitted" }), ct);
        return Unit.Value;
    }
}
