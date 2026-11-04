using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

/// <summary>Metadata sent with an upload (tus Upload-Metadata or multipart fields).</summary>
public sealed record UploadMetadata(
    string FileName, Guid WorkspaceId, Guid? FolderId, string? RelativePath, Guid? TargetFileId,
    string? Title, string? Description, IReadOnlyList<string>? Tags, IReadOnlyList<Guid>? CatalogItemIds, string? ChangeNote,
    // Desktop sync: version uploads are based on BaseETag (412 when the server copy changed), ContentHash (SHA-256 hex) is verified
    // once all chunks are assembled, and FailOnConflict answers 409 instead of auto-renaming "Tên (1).ext".
    string? BaseETag = null, string? ContentHash = null, bool FailOnConflict = false);

public sealed record UploadResult(Guid FileId, Guid VersionId, int VersionNo, string Name, ApprovalStatus ApprovalStatus);

/// <summary>Pre-flight checks done before any byte is accepted (tus OnBeforeCreate): rights, type, size, quota.</summary>
public sealed record ValidateUploadCommand(UploadMetadata Meta, long Length) : IRequest<Unit>;

public sealed class ValidateUploadHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms,
    IFileTypeInspector types, ISettingsService settings, QuotaService quota, Libraries.Libraries libraries) : IRequestHandler<ValidateUploadCommand, Unit>
{
    public async Task<Unit> Handle(ValidateUploadCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await UploadRules.CheckAsync(db, uid, cmd.Meta, cmd.Length, auth, perms, types, settings, quota, libraries, ct);
        return Unit.Value;
    }
}

public static class UploadRules
{
    public static async Task CheckAsync(IAppDbContext db, Guid uid, UploadMetadata meta, long length, IResourceAuthorizationService auth, IPermissionService perms,
        IFileTypeInspector types, ISettingsService settings, QuotaService quota, Libraries.Libraries libraries, CancellationToken ct)
    {
        if (!await perms.HasAsync(uid, Perm.DocUpload, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền tải lên tư liệu.");
        var name = FileNameSanitizer.Sanitize(meta.FileName);
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (types.IsBlockedExtension(ext)) throw new AppException($"Không cho phép tải lên tệp thực thi/kịch bản (.{ext}).", 415);
        var allowed = await settings.GetAsync(SettingKeys.UploadAllowedExtensions, SettingKeys.DefaultAllowedExtensions, ct);
        if (!allowed.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new AppException($"Định dạng .{(ext.Length == 0 ? "(không có phần mở rộng)" : ext)} chưa được phép tải lên. Các định dạng cho phép: {string.Join(", ", allowed)}.", 415);
        var maxMb = await settings.GetAsync(SettingKeys.UploadMaxSizeMb, 5120, ct);
        if (maxMb > 0 && length > maxMb * 1024L * 1024L) throw new AppException($"Tệp vượt quá kích thước tối đa cho phép ({maxMb} MB).", 413);

        if (meta.TargetFileId is { } target)
        {
            var a = await auth.EnsureAsync(uid, ResourceType.File, target, AccessLevel.Edit, ct);
            var f = await db.Files.AsNoTracking().FirstAsync(x => x.Id == target, ct);
            if (!string.IsNullOrWhiteSpace(meta.BaseETag) && !Sync.SyncIds.Matches(meta.BaseETag, f.Revision)) throw new PreconditionFailedException(null);
            if (!string.Equals(f.Extension, ext, StringComparison.OrdinalIgnoreCase)) throw new AppException($"Phiên bản mới phải cùng định dạng .{f.Extension} với tệp hiện tại.");
            await libraries.EnsureMayChangeContentAsync(f, uid, ct);
            await libraries.CheckUploadAsync(f.WorkspaceId, f.FolderId, ext, length, ct);
            await quota.EnsureAvailableAsync(f.WorkspaceId, length, ct);
            return;
        }
        await auth.EnsureCanUploadAsync(uid, meta.WorkspaceId, meta.FolderId, ct);
        if (meta.FolderId.HasValue && !await db.Folders.AnyAsync(x => x.Id == meta.FolderId && x.WorkspaceId == meta.WorkspaceId && !x.IsDeleted, ct))
            throw new NotFoundException("Không tìm thấy thư mục đích.");
        await libraries.CheckUploadAsync(meta.WorkspaceId, meta.FolderId, ext, length, ct);
        if (meta.FailOnConflict && string.IsNullOrEmpty(meta.RelativePath) && await NameTakenAsync(db, meta.WorkspaceId, meta.FolderId, name, ct))
            throw new ConflictException($"Đã có tệp \"{name}\" trong thư mục này.");
        await quota.EnsureAvailableAsync(meta.WorkspaceId, length, ct);
    }

    public static Task<bool> NameTakenAsync(IAppDbContext db, Guid workspaceId, Guid? folderId, string name, CancellationToken ct)
    {
        var lower = name.ToLower();
        return db.Files.AnyAsync(f => f.WorkspaceId == workspaceId && f.FolderId == folderId && !f.IsDeleted && f.Name.ToLower() == lower, ct);
    }
}

/// <summary>Turns a fully received temp file into a stored file/version: magic bytes, encryption, approval, quota, audit.</summary>
public sealed record CompleteUploadCommand(string TempFilePath, UploadMetadata Meta) : IRequest<UploadResult>;

public sealed class CompleteUploadHandler(
    IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IFileTypeInspector types, ISettingsService settings,
    QuotaService quota, IFileContentService content, IBackgroundJobs jobs, IMalwareScanPolicy scan, IUserNotifier notify, IAuditLogger audit, TimeProvider clock,
    Libraries.Libraries libraries)
    : IRequestHandler<CompleteUploadCommand, UploadResult>
{
    public async Task<UploadResult> Handle(CompleteUploadCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var length = new FileInfo(cmd.TempFilePath).Length;
        await UploadRules.CheckAsync(db, uid, cmd.Meta, length, auth, perms, types, settings, quota, libraries, ct);

        var name = FileNameSanitizer.Sanitize(cmd.Meta.FileName);
        var header = new byte[64];
        int read;
        await using (var probe = File.OpenRead(cmd.TempFilePath)) read = await probe.ReadAsync(header, ct);
        if (types.ValidateContent(name, header.AsSpan(0, read)) is { } error) throw new AppException(error, 415);
        var info = types.FromName(name);
        var now = clock.GetUtcNow().UtcDateTime;

        StoredContent stored;
        await using (var src = new FileStream(cmd.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
            stored = await content.SaveAsync(src, "files", ct);
        if (!string.IsNullOrWhiteSpace(cmd.Meta.ContentHash) && !string.Equals(cmd.Meta.ContentHash.Trim(), stored.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Blobs may be shared (copy-on-write): only remove one that no version references.
            if (!await db.FileVersions.AnyAsync(v => v.StorageKey == stored.StorageKey, ct)) await content.DeleteAsync(stored.StorageKey, ct);
            throw new AppException("Nội dung nhận được không khớp mã băm SHA-256 của tệp gửi lên. Vui lòng tải lên lại.", 422);
        }

        StoredFile file;
        FileVersion version;
        if (cmd.Meta.TargetFileId is { } targetId)
        {
            file = await db.Files.FirstAsync(f => f.Id == targetId, ct);
            var next = await db.FileVersions.Where(v => v.FileId == file.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;
            version = NewVersion(file.Id, next + 1, stored, uid, now, cmd.Meta.ChangeNote);
            db.FileVersions.Add(version);
            await libraries.StampAsync(file, version, publish: false, ct);
            file.SizeBytes = stored.SizeBytes;
            file.ChecksumSha256 = stored.Sha256;
            file.ThumbnailKey = null;
            file.PreviewKey = null;
            file.ScanStatus = scan.InitialStatus(stored.SizeBytes);
            await db.SaveChangesAsync(ct);
            await libraries.PruneAsync(file, ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Upload, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { version = Libraries.Libraries.Label(version), size = stored.SizeBytes, newVersion = true }), ct);
        }
        else
        {
            var folder = await FolderChain.EnsureAsync(db, cmd.Meta.WorkspaceId, cmd.Meta.FolderId, Path.GetDirectoryName(cmd.Meta.RelativePath ?? "")?.Replace('\\', '/'), ct);
            var access = folder is not null ? await auth.GetAccessAsync(uid, ResourceType.Folder, folder.Id, ct) : await auth.GetAccessAsync(uid, ResourceType.Workspace, cmd.Meta.WorkspaceId, ct);
            // Content approval: the folder's flag, or the library's at its top (spec 1.4).
            var approvalHere = folder?.RequiresApproval == true || (folder is null && (await libraries.ForAsync(cmd.Meta.WorkspaceId, null, ct)).ContentApproval);
            var needsApproval = approvalHere && !access.CanApprove && !await perms.HasAsync(uid, Perm.DocApprove, ct);
            file = new StoredFile
            {
                WorkspaceId = cmd.Meta.WorkspaceId, FolderId = folder?.Id,
                Name = cmd.Meta.FailOnConflict
                    ? (await UploadRules.NameTakenAsync(db, cmd.Meta.WorkspaceId, folder?.Id, name, ct) ? throw new ConflictException($"Đã có tệp \"{name}\" trong thư mục này.") : name)
                    : await StorageNames.UniqueFileNameAsync(db, cmd.Meta.WorkspaceId, folder?.Id, name, null, ct),
                Extension = info.Extension, MimeType = info.MimeType, MediaType = info.MediaType, SizeBytes = stored.SizeBytes, ChecksumSha256 = stored.Sha256,
                Title = string.IsNullOrWhiteSpace(cmd.Meta.Title) ? null : cmd.Meta.Title.Trim(), Description = string.IsNullOrWhiteSpace(cmd.Meta.Description) ? null : cmd.Meta.Description.Trim(),
                Tags = cmd.Meta.Tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList() ?? [],
                ApprovalStatus = needsApproval ? ApprovalStatus.Pending : ApprovalStatus.Approved, ScanStatus = scan.InitialStatus(stored.SizeBytes),
                UploaderOrganizationId = await db.Users.Where(u => u.Id == uid).Select(u => u.OrganizationId).FirstAsync(ct),
            };
            version = NewVersion(file.Id, 1, stored, uid, now, cmd.Meta.ChangeNote ?? "Tải lên lần đầu");
            await libraries.StampAsync(file, version, publish: false, ct);
            file.Versions.Add(version);
            if (cmd.Meta.CatalogItemIds is { Count: > 0 })
                foreach (var ci in await db.CatalogItems.Where(i => cmd.Meta.CatalogItemIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct))
                    file.CatalogItems.Add(new FileCatalogItem { FileId = file.Id, CatalogItemId = ci });
            db.Files.Add(file);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Upload, ResourceType.File, file.Id, file.Name, file.WorkspaceId,
                new { size = stored.SizeBytes, folder = folder?.Id, relativePath = cmd.Meta.RelativePath, approval = file.ApprovalStatus }), ct);
            if (needsApproval) await notify.NotifyPermissionAsync(Perm.DocApprove, "approval", $"Tư liệu \"{file.Name}\" chờ phê duyệt", null, "/approvals", ct);
        }
        await quota.AddUsageAsync(file.WorkspaceId, stored.SizeBytes, ct);
        await RecentTracker.TouchAsync(db, uid, file.Id, "upload", now, ct);
        jobs.EnqueueFileProcessing(file.Id, version.Id);
        return new UploadResult(file.Id, version.Id, version.VersionNo, file.Name, file.ApprovalStatus);
    }

    private static FileVersion NewVersion(Guid fileId, int no, StoredContent s, Guid uid, DateTime now, string? note) => new()
    {
        FileId = fileId, VersionNo = no, StorageKey = s.StorageKey, SizeBytes = s.SizeBytes, Checksum = s.Sha256, CreatedBy = uid, CreatedAt = now,
        ChangeNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), IsEncrypted = s.IsEncrypted, WrappedKey = s.WrappedKey,
    };
}
