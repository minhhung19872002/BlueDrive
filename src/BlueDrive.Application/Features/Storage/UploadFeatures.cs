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
    string? Title, string? Description, IReadOnlyList<string>? Tags, IReadOnlyList<Guid>? CatalogItemIds, string? ChangeNote);

public sealed record UploadResult(Guid FileId, Guid VersionId, int VersionNo, string Name, ApprovalStatus ApprovalStatus);

/// <summary>Pre-flight checks done before any byte is accepted (tus OnBeforeCreate): rights, type, size, quota.</summary>
public sealed record ValidateUploadCommand(UploadMetadata Meta, long Length) : IRequest<Unit>;

public sealed class ValidateUploadHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms,
    IFileTypeInspector types, ISettingsService settings, QuotaService quota) : IRequestHandler<ValidateUploadCommand, Unit>
{
    public async Task<Unit> Handle(ValidateUploadCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await UploadRules.CheckAsync(db, uid, cmd.Meta, cmd.Length, auth, perms, types, settings, quota, ct);
        return Unit.Value;
    }
}

public static class UploadRules
{
    public static async Task CheckAsync(IAppDbContext db, Guid uid, UploadMetadata meta, long length, IResourceAuthorizationService auth, IPermissionService perms,
        IFileTypeInspector types, ISettingsService settings, QuotaService quota, CancellationToken ct)
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
            var f = await db.Files.AsNoTracking().Where(x => x.Id == target).Select(x => new { x.WorkspaceId, x.Extension }).FirstAsync(ct);
            if (!string.Equals(f.Extension, ext, StringComparison.OrdinalIgnoreCase)) throw new AppException($"Phiên bản mới phải cùng định dạng .{f.Extension} với tệp hiện tại.");
            await quota.EnsureAvailableAsync(f.WorkspaceId, length, ct);
            return;
        }
        await auth.EnsureCanUploadAsync(uid, meta.WorkspaceId, meta.FolderId, ct);
        if (meta.FolderId.HasValue && !await db.Folders.AnyAsync(x => x.Id == meta.FolderId && x.WorkspaceId == meta.WorkspaceId && !x.IsDeleted, ct))
            throw new NotFoundException("Không tìm thấy thư mục đích.");
        await quota.EnsureAvailableAsync(meta.WorkspaceId, length, ct);
    }
}

/// <summary>Turns a fully received temp file into a stored file/version: magic bytes, encryption, approval, quota, audit.</summary>
public sealed record CompleteUploadCommand(string TempFilePath, UploadMetadata Meta) : IRequest<UploadResult>;

public sealed class CompleteUploadHandler(
    IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IFileTypeInspector types, ISettingsService settings,
    QuotaService quota, IFileContentService content, IBackgroundJobs jobs, IUserNotifier notify, IAuditLogger audit, TimeProvider clock) : IRequestHandler<CompleteUploadCommand, UploadResult>
{
    public async Task<UploadResult> Handle(CompleteUploadCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var length = new FileInfo(cmd.TempFilePath).Length;
        await UploadRules.CheckAsync(db, uid, cmd.Meta, length, auth, perms, types, settings, quota, ct);

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

        StoredFile file;
        FileVersion version;
        if (cmd.Meta.TargetFileId is { } targetId)
        {
            file = await db.Files.FirstAsync(f => f.Id == targetId, ct);
            var next = await db.FileVersions.Where(v => v.FileId == file.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;
            version = NewVersion(file.Id, next + 1, stored, uid, now, cmd.Meta.ChangeNote);
            db.FileVersions.Add(version);
            file.CurrentVersionId = version.Id;
            file.SizeBytes = stored.SizeBytes;
            file.ChecksumSha256 = stored.Sha256;
            file.ThumbnailKey = null;
            file.PreviewKey = null;
            file.ScanStatus = ScanStatus.Pending;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Upload, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { version = version.VersionNo, size = stored.SizeBytes, newVersion = true }), ct);
        }
        else
        {
            var folder = await FolderChain.EnsureAsync(db, cmd.Meta.WorkspaceId, cmd.Meta.FolderId, Path.GetDirectoryName(cmd.Meta.RelativePath ?? "")?.Replace('\\', '/'), ct);
            var access = folder is not null ? await auth.GetAccessAsync(uid, ResourceType.Folder, folder.Id, ct) : await auth.GetAccessAsync(uid, ResourceType.Workspace, cmd.Meta.WorkspaceId, ct);
            var needsApproval = folder?.RequiresApproval == true && !access.CanApprove && !await perms.HasAsync(uid, Perm.DocApprove, ct);
            file = new StoredFile
            {
                WorkspaceId = cmd.Meta.WorkspaceId, FolderId = folder?.Id,
                Name = await StorageNames.UniqueFileNameAsync(db, cmd.Meta.WorkspaceId, folder?.Id, name, null, ct),
                Extension = info.Extension, MimeType = info.MimeType, MediaType = info.MediaType, SizeBytes = stored.SizeBytes, ChecksumSha256 = stored.Sha256,
                Title = string.IsNullOrWhiteSpace(cmd.Meta.Title) ? null : cmd.Meta.Title.Trim(), Description = string.IsNullOrWhiteSpace(cmd.Meta.Description) ? null : cmd.Meta.Description.Trim(),
                Tags = cmd.Meta.Tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList() ?? [],
                ApprovalStatus = needsApproval ? ApprovalStatus.Pending : ApprovalStatus.Approved,
                UploaderOrganizationId = await db.Users.Where(u => u.Id == uid).Select(u => u.OrganizationId).FirstAsync(ct),
            };
            version = NewVersion(file.Id, 1, stored, uid, now, cmd.Meta.ChangeNote ?? "Tải lên lần đầu");
            file.CurrentVersionId = version.Id;
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
