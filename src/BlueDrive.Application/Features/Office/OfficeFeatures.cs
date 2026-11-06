using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Sharing;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using BlueDrive.Domain.Permissions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Application.Features.Office;

/// <summary>#42: editor config for the current version; edit mode when the user may edit (co-editing shares the key).</summary>
/// <param name="VersionId">An older version to read in the editor ("Phiên bản" → Xem): read-only, needs the right to see versions.</param>
public sealed record GetOfficeConfigQuery(Guid FileId, bool ForceView = false, bool Mobile = false, Guid? VersionId = null) : IRequest<OfficeEditorConfig>;

public sealed class GetOfficeConfigHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms,
    IOfficeEditorProvider office, IContentTicketService tickets, IAuditLogger audit, TimeProvider clock, Access.IItemPermissionService engine, Libraries.Libraries libraries,
    Storage.QuotaService quota, Access.Labels labels)
    : IRequestHandler<GetOfficeConfigQuery, OfficeEditorConfig>
{
    public async Task<OfficeEditorConfig> Handle(GetOfficeConfigQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var access = await auth.EnsureAsync(uid, ResourceType.File, q.FileId, AccessLevel.View, ct);
        var f = await db.Files.AsNoTracking().FirstAsync(x => x.Id == q.FileId, ct);
        if (!office.CanView(f.Extension)) throw new AppException($"Định dạng .{f.Extension} không hỗ trợ mở bằng trình soạn thảo trực tuyến.");
        // People who may not see drafts read the latest published version (spec 1.4).
        var rights = await engine.GetEffectiveAsync(uid, f.Id, ct);
        var versionId = Libraries.Libraries.VersionFor(f, rights) ?? throw new NotFoundException("Không tìm thấy tài liệu.");
        if (q.VersionId is { } wanted && wanted != versionId)
        {
            if (!rights.Has(Domain.Permissions.AccessRight.ViewVersions)) throw new ForbiddenException("Bạn không có quyền xem các phiên bản của tài liệu.");
            var v = await db.FileVersions.AsNoTracking().Where(x => x.Id == wanted && x.FileId == f.Id).Select(x => new { x.IsPublished }).FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Không tìm thấy phiên bản.");
            if (!v.IsPublished && !rights.Has(Domain.Permissions.AccessRight.ViewDrafts)) throw new ForbiddenException("Bạn không có quyền xem bản nháp.");
            versionId = wanted; // an older version opens read-only (editing needs the current one)
        }
        await ScanGate.EnsureAsync(db, f, versionId, uid, ct);
        // The item's own rights decide (like rename / new version): a "Chỉ xem" role shared "Chỉnh sửa" edits that document. A library that
        // requires a check-out opens for editing only to who checked it out.
        var mayChange = true;
        try { await libraries.EnsureMayChangeContentAsync(f, uid, ct); }
        catch (AppException) { mayChange = false; }
        var full = await quota.IsFullAsync(f.WorkspaceId, ct); // spec 8.2: no new versions when full
        var canEdit = !q.ForceView && access.CanEdit && office.CanEdit(f.Extension) && mayChange && versionId == f.CurrentVersionId && !full;
        // Comments are saved into the document (a new version), so they change it: who was given "Người nhận xét" (or more) on the document
        // itself, or whose role may change documents. Only being a member of the group (capped by a "Chỉ xem" role) views.
        var canComment = !q.ForceView && !canEdit && rights.Has(Domain.Permissions.AccessRight.Comment) && versionId == f.CurrentVersionId && !full
                         && (await perms.HasAsync(uid, Perm.DocEdit, ct)
                             || (await engine.ExplainAsync(uid, f.Id, ct)).Sources.Any(s => s.Origin != Domain.Enums.AclSource.SpaceDefault
                                                                                          && s.Rights.Has(Domain.Permissions.AccessRight.Comment)));
        // Spec 11.9: "Chỉ xem" or a label asking for it — a watermark, and no download, print or copy.
        var watermark = !access.CanDownload || (await labels.ForAsync(f.Id, ct))?.Label.Watermark == true;
        var name = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
        var docTicket = tickets.Issue(new ContentTicket(uid, "file", f.Id, versionId, true, null, null), TimeSpan.FromHours(24));
        var config = office.BuildConfig(new OfficeEditorRequest(f.Id, versionId, f.Name, f.Extension,
            $"{office.CallbackBaseUrl}/api/v1/content/{Uri.EscapeDataString(docTicket)}", $"{office.CallbackBaseUrl}/api/office/callback/{f.Id}",
            canEdit, access.CanDownload && !watermark, uid, name, false, q.Mobile, null, canComment,
            rights.Has(Domain.Permissions.AccessRight.Print) && !watermark, rights.Has(Domain.Permissions.AccessRight.CopyContent) && !watermark)) with { Watermark = watermark };
        await RecentTracker.TouchAsync(db, uid, f.Id, canEdit ? "edit" : "view", clock.GetUtcNow().UtcDateTime, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.View, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { office = config.Mode }), ct);
        return config;
    }
}

/// <summary>
/// Editor config for a public link (#49: Office files on /s/{token}). View only, unless the link allows editing ("Cho phép chỉnh sửa"):
/// then anyone with the link edits in the same co-editing session as signed-in users (signed in: under their account; otherwise as
/// "Khách"), straight away, as long as the person
/// who made the link may still edit the document (and the administrator has not turned link editing off).
/// </summary>
public sealed record GetPublicOfficeConfigQuery(string Token, string Access, Guid? FileId) : IRequest<OfficeEditorConfig>;

public sealed class GetPublicOfficeConfigHandler(IAppDbContext db, IPublicLinkTickets linkTickets, IOfficeEditorProvider office, IResourceAuthorizationService auth,
    ISettingsService settings, ICurrentUser me, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<GetPublicOfficeConfigQuery, OfficeEditorConfig>
{
    public async Task<OfficeEditorConfig> Handle(GetPublicOfficeConfigQuery q, CancellationToken ct)
    {
        var link = await PublicLinks.LoadActiveAsync(db, q.Token, clock.GetUtcNow().UtcDateTime, ct);
        if (linkTickets.Read(q.Access) is not { } t || t.LinkId != link.Id) throw new AppException("Phiên xem liên kết đã hết hạn. Vui lòng mở lại liên kết.", 401);
        var f = await PublicLinks.FileInScopeAsync(db, link, q.FileId, ct);
        if (!office.CanView(f.Extension)) throw new AppException("Định dạng không hỗ trợ xem trực tuyến.");
        var url = $"{office.CallbackBaseUrl}/api/v1/public/s/{q.Token}/content?access={Uri.EscapeDataString(q.Access)}&fileId={f.Id}";
        var download = await PublicLinks.CanDownloadAsync(auth, link, t.UserId, ResourceType.File, f.Id, ct);
        // Restricted link: the visitor's own rights (they are signed in and may see the item). "Anyone with the link" as editor: the
        // link creator's right to edit, unless the administrator turned link editing off.
        var edit = office.CanEdit(f.Extension) && (link.RequireLogin
            ? me.UserId is { } visitor && (await auth.GetAccessAsync(visitor, ResourceType.File, f.Id, ct)).CanEdit
            : link.AllowEdit && link.CreatedBy is { } owner && await settings.GetAsync(SettingKeys.ShareLinkAllowEdit, true, ct)
              && (await auth.GetAccessAsync(owner, ResourceType.File, f.Id, ct)).CanEdit);
        if (!edit)
            return office.BuildConfig(new OfficeEditorRequest(f.Id, f.CurrentVersionId!.Value, f.Name, f.Extension, url, null, false, download, null, "Khách", true));
        // Signed in: edits under their own account and name (the version is theirs). Otherwise a guest of the link.
        var account = me.UserId is { } sid ? await db.Users.Where(u => u.Id == sid).Select(u => u.FullName).FirstOrDefaultAsync(ct) : null;
        var name = account ?? "Khách";
        var guestId = account is null ? $"{LinkEditor.Prefix}{link.Id:N}:{Guid.NewGuid():N}" : null;
        // The full editor (toolbars): the embedded type only shows the document.
        var config = office.BuildConfig(new OfficeEditorRequest(f.Id, f.CurrentVersionId!.Value, f.Name, f.Extension, url,
            $"{office.CallbackBaseUrl}/api/office/callback/{f.Id}", true, download, account is null ? null : me.UserId, name, false, GuestId: guestId));
        await audit.LogAsync(new AuditEntry(AuditAction.View, ResourceType.File, f.Id, f.Name, f.WorkspaceId,
            new { office = config.Mode, shareLink = link.Id, editor = name, guest = account is null }, me.UserId, null), ct);
        return config;
    }
}

/// <summary>Editors who came through a share link: ONLYOFFICE user id "link:&lt;link id&gt;:&lt;session&gt;" (they have no account).</summary>
public static class LinkEditor
{
    public const string Prefix = "link:";

    public static Guid? LinkId(string? editor) =>
        editor is not null && editor.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(editor.AsSpan(Prefix.Length, 32), "N", out var id) ? id : null;
}

/// <summary>#21/#33/#41: "Mới" → blank Word/Excel/PowerPoint/text file created from a template, then opened in the editor.</summary>
public sealed record CreateDocumentCommand(Guid WorkspaceId, Guid? FolderId, string Kind, string? Name) : IRequest<UploadResult>;

public sealed class CreateDocumentValidator : AbstractValidator<CreateDocumentCommand>
{
    public CreateDocumentValidator()
    {
        RuleFor(x => x.Kind).Must(k => k is "docx" or "xlsx" or "pptx" or "txt").WithMessage("Loại tài liệu phải là docx, xlsx, pptx hoặc txt.");
        RuleFor(x => x.Name).MaximumLength(180);
    }
}

public sealed class CreateDocumentHandler(IOfficeEditorProvider office, ISender mediator, IFileStorageTemp temp) : IRequestHandler<CreateDocumentCommand, UploadResult>
{
    public async Task<UploadResult> Handle(CreateDocumentCommand cmd, CancellationToken ct)
    {
        var bytes = office.BlankTemplate(cmd.Kind) ?? throw new AppException("Không có mẫu tài liệu trống cho định dạng này.");
        var defaultName = cmd.Kind switch { "docx" => "Tài liệu mới", "xlsx" => "Bảng tính mới", "pptx" => "Bản trình chiếu mới", _ => "Văn bản mới" };
        var baseName = string.IsNullOrWhiteSpace(cmd.Name) ? defaultName : Path.GetFileNameWithoutExtension(cmd.Name.Trim());
        var path = await temp.WriteTempAsync(bytes, ct);
        try
        {
            return await mediator.Send(new CompleteUploadCommand(path, new UploadMetadata($"{baseName}.{cmd.Kind}", cmd.WorkspaceId, cmd.FolderId, null, null,
                null, null, null, null, "Tạo mới trực tuyến")), ct);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Scratch files on local disk for content that must pass through the upload pipeline.</summary>
public interface IFileStorageTemp
{
    Task<string> WriteTempAsync(byte[] content, CancellationToken ct);
    string NewTempPath();
}

/// <summary>Document Server callback: status 2 (closed, ready) or 6 (force-save) → store a new version (#26/#42/#43).</summary>
public sealed record OfficeCallbackCommand(Guid FileId, string Body, string? AuthorizationHeader) : IRequest<int>;

public sealed class OfficeCallbackHandler(IAppDbContext db, IOfficeEditorProvider office, IFileContentService content, QuotaService quota, IBackgroundJobs jobs,
    IFileStorageTemp temp, IAuditLogger audit, TimeProvider clock, ILogger<OfficeCallbackHandler> logger, OfficeEditingTracker editing, Libraries.Libraries libraries)
    : IRequestHandler<OfficeCallbackCommand, int>
{
    public async Task<int> Handle(OfficeCallbackCommand cmd, CancellationToken ct)
    {
        var cb = office.ParseCallback(cmd.Body, cmd.AuthorizationHeader);
        if (cb is null) return 1;
        editing.OnCallback(cmd.FileId, cb.Status, cb.Users);
        if (cb.Status is not (2 or 6) || string.IsNullOrEmpty(cb.DownloadUrl)) return 0;
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == cmd.FileId && !f.IsDeleted, ct);
        if (file is null) return 1;
        var last = cb.Users.FirstOrDefault();
        // Saved by someone who came through an editing link: the version is the link creator's (they let them edit), the audit says so.
        var viaLink = LinkEditor.LinkId(last);
        Guid? userId = Guid.TryParse(last, out var u) ? u
            : viaLink is { } lid ? await db.ShareLinks.Where(l => l.Id == lid).Select(l => l.CreatedBy).FirstOrDefaultAsync(ct) ?? file.UpdatedBy ?? file.CreatedBy
            : file.UpdatedBy ?? file.CreatedBy;
        var path = temp.NewTempPath();
        try
        {
            await using (var src = await office.DownloadAsync(cb.DownloadUrl, ct))
            await using (var fs = File.Create(path)) await src.CopyToAsync(fs, ct);
            StoredContent stored;
            await using (var fs = File.OpenRead(path)) stored = await content.SaveAsync(fs, "files", ct);
            if (stored.Sha256 == file.ChecksumSha256)
            {
                await content.DeleteAsync(stored.StorageKey, ct);
                return 0;
            }
            var next = (await db.FileVersions.Where(v => v.FileId == file.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0) + 1;
            var now = clock.GetUtcNow().UtcDateTime;
            var version = new FileVersion
            {
                FileId = file.Id, VersionNo = next, StorageKey = stored.StorageKey, SizeBytes = stored.SizeBytes, Checksum = stored.Sha256, CreatedBy = userId, CreatedAt = now,
                ChangeNote = cb.Status == 6 ? "Lưu từ trình soạn thảo trực tuyến" : "Chỉnh sửa trực tuyến", IsEncrypted = stored.IsEncrypted, WrappedKey = stored.WrappedKey,
            };
            db.FileVersions.Add(version);
            await libraries.StampAsync(file, version, publish: false, ct);
            file.SizeBytes = stored.SizeBytes;
            file.ChecksumSha256 = stored.Sha256;
            file.UpdatedAt = now;
            file.UpdatedBy = userId;
            file.ThumbnailKey = null;
            await db.SaveChangesAsync(ct);
            await quota.AddUsageAsync(file.WorkspaceId, stored.SizeBytes, ct);
            await libraries.PruneAsync(file, ct);
            if (userId is { } uid) await RecentTracker.TouchAsync(db, uid, file.Id, "edit", now, ct);
            jobs.EnqueueFileProcessing(file.Id, version.Id);
            var username = userId is { } id ? await db.Users.Where(x => x.Id == id).Select(x => x.Username).FirstOrDefaultAsync(ct) : null;
            await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceType.File, file.Id, file.Name, file.WorkspaceId,
                new { online = true, version = next, editors = cb.Users, shareLink = viaLink }, userId, username), ct);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogError(ex, "Không lưu được phiên bản từ ONLYOFFICE cho tệp {FileId}", cmd.FileId);
            return 1;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

// ---------- Versions (#26, #37, #43) ----------
public sealed record ListVersionsQuery(Guid FileId) : IRequest<IReadOnlyList<FileVersionDto>>;

public sealed class ListVersionsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<ListVersionsQuery, IReadOnlyList<FileVersionDto>>
{
    public async Task<IReadOnlyList<FileVersionDto>> Handle(ListVersionsQuery q, CancellationToken ct)
    {
        await auth.EnsureAsync(me.RequireUserId(), ResourceType.File, q.FileId, AccessLevel.View, ct);
        var current = await db.Files.Where(f => f.Id == q.FileId).Select(f => f.CurrentVersionId).FirstAsync(ct);
        return await db.FileVersions.AsNoTracking().Where(v => v.FileId == q.FileId).OrderByDescending(v => v.VersionNo)
            .Select(v => new FileVersionDto(v.Id, v.VersionNo, v.SizeBytes, v.ChangeNote, v.CreatedBy, db.Users.Where(u => u.Id == v.CreatedBy).Select(u => u.FullName).FirstOrDefault(),
                v.CreatedAt, v.Id == current, v.Major, v.Minor, v.IsPublished)).ToListAsync(ct);
    }
}

/// <summary>Restoring an old version creates a new version pointing to the same blob (history stays intact).</summary>
public sealed record RestoreVersionCommand(Guid FileId, Guid VersionId) : IRequest<FileVersionDto>;

public sealed class RestoreVersionHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, QuotaService quota, IBackgroundJobs jobs, IAuditLogger audit, TimeProvider clock,
    Libraries.Libraries libraries)
    : IRequestHandler<RestoreVersionCommand, FileVersionDto>
{
    public async Task<FileVersionDto> Handle(RestoreVersionCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await auth.EnsureAsync(uid, ResourceType.File, cmd.FileId, AccessLevel.Edit, ct);
        var file = await db.Files.FirstAsync(f => f.Id == cmd.FileId, ct);
        var old = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == cmd.VersionId && v.FileId == file.Id, ct) ?? throw new NotFoundException("Không tìm thấy phiên bản.");
        if (old.Id == file.CurrentVersionId) throw new AppException("Đây đã là phiên bản hiện tại.");
        await libraries.EnsureMayChangeContentAsync(file, uid, ct);
        await quota.EnsureAvailableAsync(file.WorkspaceId, old.SizeBytes, ct);
        var next = await db.FileVersions.Where(v => v.FileId == file.Id).MaxAsync(v => v.VersionNo, ct) + 1;
        var now = clock.GetUtcNow().UtcDateTime;
        var v = new FileVersion
        {
            FileId = file.Id, VersionNo = next, StorageKey = old.StorageKey, SizeBytes = old.SizeBytes, Checksum = old.Checksum, CreatedBy = uid, CreatedAt = now,
            ChangeNote = $"Khôi phục từ phiên bản {old.VersionNo}", IsEncrypted = old.IsEncrypted, WrappedKey = old.WrappedKey,
        };
        db.FileVersions.Add(v);
        await libraries.StampAsync(file, v, publish: false, ct);
        file.SizeBytes = v.SizeBytes;
        file.ChecksumSha256 = v.Checksum;
        file.ThumbnailKey = null;
        await db.SaveChangesAsync(ct);
        await quota.AddUsageAsync(file.WorkspaceId, v.SizeBytes, ct);
        jobs.EnqueueFileProcessing(file.Id, v.Id);
        await audit.LogAsync(new AuditEntry(AuditAction.VersionRestore, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { from = old.VersionNo, to = next }), ct);
        var name = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
        return new FileVersionDto(v.Id, v.VersionNo, v.SizeBytes, v.ChangeNote, uid, name, now, true, v.Major, v.Minor, v.IsPublished);
    }
}
