using System.Security.Cryptography;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlueDrive.Application.Features.Sharing;

public sealed record ShareLinkDto(Guid Id, string Token, string Url, ResourceType ResourceType, Guid ResourceId, bool AllowDownload, bool AllowView,
    bool HasPassword, DateTime? ExpiresAt, int? MaxAccessCount, int AccessCount, bool IsRevoked, bool RequireLogin, DateTime CreatedAt, string Status, bool AllowEdit);

public static class ShareLinkMapping
{
    public static string StatusOf(ShareLink l, DateTime now) => l.IsRevoked ? "revoked" : l.ExpiresAt < now ? "expired"
        : l.MaxAccessCount is { } m && l.AccessCount >= m ? "exhausted" : "active";

    public static ShareLinkDto ToDto(ShareLink l, string baseUrl, DateTime now) => new(l.Id, l.Token, $"{baseUrl.TrimEnd('/')}/s/{l.Token}", l.ResourceType, l.ResourceId,
        l.AllowDownload, l.AllowView, l.PasswordHash is not null, l.ExpiresAt, l.MaxAccessCount, l.AccessCount, l.IsRevoked, l.RequireLogin, l.CreatedAt, StatusOf(l, now), l.AllowEdit);
}

public sealed record SaveShareLinkCommand(Guid? Id, ResourceType ResourceType, Guid ResourceId, bool AllowDownload, string? Password, bool ClearPassword,
    DateTime? ExpiresAt, int? MaxAccessCount, bool RequireLogin, bool AllowEdit = false) : IRequest<ShareLinkDto>;

public sealed class SaveShareLinkValidator : AbstractValidator<SaveShareLinkCommand>
{
    public SaveShareLinkValidator()
    {
        RuleFor(x => x.ResourceType).Must(t => t != ResourceType.Workspace).WithMessage("Chỉ tạo liên kết cho thư mục hoặc tệp.");
        RuleFor(x => x.ExpiresAt).Must(d => d is null || d > DateTime.UtcNow).WithMessage("Ngày hết hạn phải ở tương lai.");
        RuleFor(x => x.MaxAccessCount).InclusiveBetween(1, 1_000_000).When(x => x.MaxAccessCount.HasValue).WithMessage("Số lượt truy cập tối đa phải từ 1 trở lên.");
        RuleFor(x => x.Password).MinimumLength(4).When(x => !string.IsNullOrEmpty(x.Password)).WithMessage("Mật khẩu liên kết tối thiểu 4 ký tự.").MaximumLength(128);
    }
}

public sealed class SaveShareLinkHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IPasswordService passwords,
    ITokenService tokens, IConfiguration config, IAuditLogger audit, TimeProvider clock, ISettingsService settings) : IRequestHandler<SaveShareLinkCommand, ShareLinkDto>
{
    public async Task<ShareLinkDto> Handle(SaveShareLinkCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.DocShare, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền chia sẻ tư liệu.");
        ShareLink link;
        if (cmd.Id is { } id)
        {
            link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");
            if (link.CreatedBy != uid) await auth.EnsureAsync(uid, link.ResourceType, link.ResourceId, AccessLevel.Manage, ct);
            // Same rule as on creation: a link cannot be switched to "download allowed" by someone who may not download.
            if (cmd.AllowDownload && !link.AllowDownload
                && !(await auth.EnsureAsync(uid, link.ResourceType, link.ResourceId, AccessLevel.View, ct)).CanDownload)
                throw new ForbiddenException("Bạn không có quyền tải xuống nên không thể cho phép tải xuống qua liên kết.");
        }
        else
        {
            var access = await auth.EnsureAsync(uid, cmd.ResourceType, cmd.ResourceId, AccessLevel.View, ct);
            if (cmd.AllowDownload && !access.CanDownload) throw new ForbiddenException("Bạn không có quyền tải xuống nên không thể tạo liên kết cho phép tải xuống.");
            link = new ShareLink { ResourceType = cmd.ResourceType, ResourceId = cmd.ResourceId, Token = tokens.GenerateOpaqueToken(32) };
            db.ShareLinks.Add(link);
        }
        if (cmd.AllowEdit && !link.AllowEdit)
        {
            // Same rule as download: only someone who may edit can let others edit through the link.
            if (!await settings.GetAsync(SettingKeys.ShareLinkAllowEdit, true, ct))
                throw new ForbiddenException("Quản trị hệ thống đã tắt chỉnh sửa qua liên kết chia sẻ.");
            if (!(await auth.EnsureAsync(uid, link.ResourceType, link.ResourceId, AccessLevel.View, ct)).CanEdit)
                throw new ForbiddenException("Bạn không có quyền chỉnh sửa nên không thể cho phép chỉnh sửa qua liên kết.");
        }
        link.AllowDownload = cmd.AllowDownload;
        link.AllowEdit = cmd.AllowEdit;
        link.AllowView = true;
        link.ExpiresAt = cmd.ExpiresAt;
        link.MaxAccessCount = cmd.MaxAccessCount;
        link.RequireLogin = cmd.RequireLogin;
        if (!string.IsNullOrEmpty(cmd.Password)) link.PasswordHash = passwords.Hash(cmd.Password);
        else if (cmd.ClearPassword) link.PasswordHash = null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Share, link.ResourceType, link.ResourceId, null, null,
            new { shareLink = link.Id, link.AllowDownload, link.AllowEdit, link.ExpiresAt, link.MaxAccessCount, link.RequireLogin, hasPassword = link.PasswordHash != null, created = cmd.Id is null }), ct);
        return ShareLinkMapping.ToDto(link, config["App:PublicBaseUrl"] ?? "", clock.GetUtcNow().UtcDateTime);
    }
}

public sealed record ListShareLinksQuery(ResourceType ResourceType, Guid ResourceId) : IRequest<IReadOnlyList<ShareLinkDto>>;

public sealed class ListShareLinksHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IConfiguration config, TimeProvider clock)
    : IRequestHandler<ListShareLinksQuery, IReadOnlyList<ShareLinkDto>>
{
    public async Task<IReadOnlyList<ShareLinkDto>> Handle(ListShareLinksQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var a = await auth.EnsureAsync(uid, q.ResourceType, q.ResourceId, AccessLevel.View, ct);
        var links = await db.ShareLinks.AsNoTracking().Where(l => l.ResourceType == q.ResourceType && l.ResourceId == q.ResourceId && (a.CanManage || l.CreatedBy == uid))
            .OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return links.Select(l => ShareLinkMapping.ToDto(l, config["App:PublicBaseUrl"] ?? "", now)).ToList();
    }
}

public sealed record RevokeShareLinkCommand(Guid Id) : IRequest<Unit>;

public sealed class RevokeShareLinkHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IAuditLogger audit) : IRequestHandler<RevokeShareLinkCommand, Unit>
{
    public async Task<Unit> Handle(RevokeShareLinkCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");
        if (link.CreatedBy != uid) await auth.EnsureAsync(uid, link.ResourceType, link.ResourceId, AccessLevel.Manage, ct);
        link.IsRevoked = true;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Unshare, link.ResourceType, link.ResourceId, null, null, new { shareLink = link.Id }), ct);
        return Unit.Value;
    }
}

// ---------- Public access (/s/{token}) ----------
public sealed record PublicShareInfoDto(string Name, ResourceType ResourceType, long SizeBytes, MediaType? MediaType, string? MimeType, string? Extension,
    bool AllowDownload, bool RequiresPassword, bool RequireLogin, DateTime? ExpiresAt, string SharedByName, string? OrganizationName, bool AllowEdit);

public sealed record PublicAccessGrant(string AccessToken, DateTime ExpiresAt);

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
    public static async Task<ShareLink> LoadActiveAsync(IAppDbContext db, string token, DateTime now, CancellationToken ct)
    {
        var link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Token == token, ct) ?? throw new NotFoundException("Liên kết chia sẻ không tồn tại.");
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
            return new PublicShareInfoDto(f.Name, ResourceType.File, f.SizeBytes, f.MediaType, f.MimeType, f.Extension, link.AllowDownload, link.PasswordHash != null, link.RequireLogin, link.ExpiresAt, sharer?.FullName ?? "", sharer?.Org, link.AllowEdit);
        }
        var folder = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == link.ResourceId, ct);
        return new PublicShareInfoDto(folder.Name, ResourceType.Folder, 0, null, null, null, link.AllowDownload, link.PasswordHash != null, link.RequireLogin, link.ExpiresAt, sharer?.FullName ?? "", sharer?.Org, link.AllowEdit);
    }
}

/// <summary>Opens a link (password / login check), counts one access and logs it.</summary>
public sealed record OpenPublicShareCommand(string Token, string? Password) : IRequest<PublicAccessGrant>;

public sealed class OpenPublicShareHandler(IAppDbContext db, ICurrentUser me, IPasswordService passwords, IPublicLinkTickets tickets, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<OpenPublicShareCommand, PublicAccessGrant>
{
    public async Task<PublicAccessGrant> Handle(OpenPublicShareCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var link = await PublicLinks.LoadActiveAsync(db, cmd.Token, now, ct);
        if (link.RequireLogin && !me.IsAuthenticated) throw new AppException("Liên kết này yêu cầu đăng nhập hệ thống trước khi xem.", 401);
        if (link.PasswordHash is not null && (string.IsNullOrEmpty(cmd.Password) || !passwords.Verify(link.PasswordHash, cmd.Password)))
        {
            await audit.LogAsync(new AuditEntry(AuditAction.ShareLinkAccess, link.ResourceType, link.ResourceId, null, null, new { shareLink = link.Id, success = false, reason = "wrong_password" }), ct);
            throw new AppException("Mật khẩu liên kết không đúng.", 403);
        }
        // Atomic increment that also enforces the access limit under concurrency.
        var updated = await db.ShareLinks.Where(l => l.Id == link.Id && !l.IsRevoked && (l.MaxAccessCount == null || l.AccessCount < l.MaxAccessCount))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessCount, l => l.AccessCount + 1), ct);
        if (updated == 0) throw new AppException("Liên kết chia sẻ đã hết số lượt truy cập cho phép.", 410);
        await audit.LogAsync(new AuditEntry(AuditAction.ShareLinkAccess, link.ResourceType, link.ResourceId, null, null, new { shareLink = link.Id, success = true, count = link.AccessCount + 1 }), ct);
        return new PublicAccessGrant(tickets.Issue(new PublicLinkTicket(link.Id, me.UserId)), now.AddHours(4));
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
        var ids = ResourceAuthorizationService.PathIds(current.Path).TakeWhile(id => id != root.Id).Append(root.Id).Reverse().ToList();
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
