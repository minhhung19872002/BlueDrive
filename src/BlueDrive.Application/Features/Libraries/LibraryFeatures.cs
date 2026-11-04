using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Libraries;

/// <summary>
/// Document libraries (spec 1.4): which library a place belongs to, and what its settings make of versions — major only (1, 2, 3…) or major
/// and minor (0.1, 0.2 → "Xuất bản" 1.0): a minor version is a draft that only people allowed to see drafts get; limits of versions kept.
/// </summary>
public sealed class Libraries(IAppDbContext db, IFileContentService content, QuotaService quota, TimeProvider clock)
{
    /// <summary>The library of a place: the top folder when it is a library of its own, otherwise the Space's default library.</summary>
    public async Task<LibrarySettings> ForAsync(Guid workspaceId, Guid? folderId, CancellationToken ct)
    {
        if (folderId is { } fid)
        {
            var path = await db.Folders.AsNoTracking().Where(f => f.Id == fid).Select(f => f.Path).FirstOrDefaultAsync(ct);
            var top = path is null ? (Guid?)null : LegacyResourceAuthorizationService.PathIds(path).FirstOrDefault();
            if (top is { } t && await db.Folders.AnyAsync(f => f.Id == t && f.IsLibrary, ct))
                return await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == t, ct) ?? Default(t, workspaceId, false);
        }
        var id = PermissionProjection.LibraryOf(workspaceId);
        return await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct) ?? Default(id, workspaceId, true);
    }

    public static LibrarySettings Default(Guid id, Guid workspaceId, bool isDefault) =>
        new() { Id = id, WorkspaceId = workspaceId, IsDefault = isDefault, Name = isDefault ? "Tài liệu" : "Thư viện" };

    public static string Label(FileVersion v) => v.Minor == 0 ? $"{v.Major}.0" : $"{v.Major}.{v.Minor}";

    /// <summary>
    /// Numbers a new version by the library's versioning and makes it current; a major version (or any in a major-only library) is also the
    /// published one. Then the library's limits are applied.
    /// </summary>
    public async Task StampAsync(StoredFile file, FileVersion version, bool publish, CancellationToken ct)
    {
        var library = await ForAsync(file.WorkspaceId, file.FolderId, ct);
        var previous = await db.FileVersions.AsNoTracking().Where(v => v.FileId == file.Id && v.Id != version.Id).OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);
        if (library.Versioning == VersioningMode.Major || publish)
        {
            var lastMajor = await db.FileVersions.Where(v => v.FileId == file.Id && v.Id != version.Id && v.IsPublished).MaxAsync(v => (int?)v.Major, ct) ?? 0;
            version.Major = library.Versioning == VersioningMode.Major ? version.VersionNo : lastMajor + 1;
            version.Minor = 0;
            version.IsPublished = true;
            file.PublishedVersionId = version.Id;
        }
        else
        {
            version.Major = previous?.Major ?? 0;
            version.Minor = (previous?.Minor ?? 0) + 1;
            version.IsPublished = false;
        }
        file.CurrentVersionId = version.Id;
    }

    /// <summary>"Xuất bản": the current draft becomes the next major version, what everyone gets.</summary>
    public async Task<FileVersion> PublishAsync(StoredFile file, CancellationToken ct)
    {
        var current = await db.FileVersions.FirstOrDefaultAsync(v => v.Id == file.CurrentVersionId, ct) ?? throw new NotFoundException("Tệp chưa có phiên bản.");
        if (current.IsPublished) throw new ConflictException("Phiên bản hiện tại đã được xuất bản.");
        var lastMajor = await db.FileVersions.Where(v => v.FileId == file.Id && v.IsPublished).MaxAsync(v => (int?)v.Major, ct) ?? 0;
        current.Major = lastMajor + 1;
        current.Minor = 0;
        current.IsPublished = true;
        file.PublishedVersionId = current.Id;
        await db.SaveChangesAsync(ct);
        return current;
    }

    /// <summary>
    /// Versions kept (spec 1.4): at most N major versions, M minor versions after each major one, none older than D days — never the current
    /// nor the published version. Their content goes when no version uses it any more; the space they took is given back.
    /// </summary>
    public async Task<int> PruneAsync(StoredFile file, CancellationToken ct)
    {
        var library = await ForAsync(file.WorkspaceId, file.FolderId, ct);
        var versions = await db.FileVersions.Where(v => v.FileId == file.Id).OrderByDescending(v => v.VersionNo).ToListAsync(ct);
        var keep = new HashSet<Guid> { file.CurrentVersionId ?? Guid.Empty, file.PublishedVersionId ?? Guid.Empty };
        var drop = new List<FileVersion>();
        var majors = versions.Where(v => v.IsPublished).ToList();
        if (library.MajorLimit > 0) drop.AddRange(majors.Skip(library.MajorLimit));
        if (library.MinorLimit >= 0)
            foreach (var group in versions.Where(v => !v.IsPublished).GroupBy(v => v.Major))
                drop.AddRange(group.Skip(library.MinorLimit));
        if (library.DeleteVersionsOlderThanDays is { } days && days > 0)
        {
            var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-days);
            drop.AddRange(versions.Where(v => v.CreatedAt < cutoff));
        }
        var gone = drop.Where(v => !keep.Contains(v.Id)).DistinctBy(v => v.Id).ToList();
        if (gone.Count == 0) return 0;
        db.FileVersions.RemoveRange(gone);
        await db.SaveChangesAsync(ct);
        foreach (var key in gone.Select(v => v.StorageKey).Distinct())
            if (!await db.FileVersions.AnyAsync(v => v.StorageKey == key, ct)) await content.DeleteAsync(key, ct);
        await quota.AddUsageAsync(file.WorkspaceId, -gone.Sum(v => v.SizeBytes), ct);
        return gone.Count;
    }

    /// <summary>
    /// The version someone gets: the current one for who may see drafts (or edit), otherwise the latest published one (null = nothing published
    /// yet, the file is not theirs to read).
    /// </summary>
    public static Guid? VersionFor(StoredFile file, AccessRight rights) =>
        file.CurrentVersionId == file.PublishedVersionId || rights.Has(AccessRight.ViewDrafts) || rights.Has(AccessRight.EditItems)
            ? file.CurrentVersionId : file.PublishedVersionId;

    /// <summary>Upload rules of the library (spec 1.4): its own file types and size, never wider than the system's.</summary>
    public async Task CheckUploadAsync(Guid workspaceId, Guid? folderId, string extension, long length, CancellationToken ct)
    {
        var library = await ForAsync(workspaceId, folderId, ct);
        if (library.AllowedExtensions.Count > 0 && !library.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new AppException($"Thư viện \"{library.Name}\" chỉ nhận các định dạng: {string.Join(", ", library.AllowedExtensions)}.", 415);
        if (library.MaxFileSizeMb is > 0 and var mb && length > mb * 1024L * 1024L)
            throw new AppException($"Thư viện \"{library.Name}\" chỉ nhận tệp tối đa {mb} MB.", 413);
    }

    /// <summary>Content of a file may change only when checked out by the person changing it, if the library requires it (spec 1.4).</summary>
    public async Task EnsureMayChangeContentAsync(StoredFile file, Guid uid, CancellationToken ct)
    {
        if (file.CheckedOutBy is { } who && who != uid) throw new AppException("Tệp đang được người khác check-out; chỉ người đó sửa được nội dung.", 423);
        if (file.CheckedOutBy is null && (await ForAsync(file.WorkspaceId, file.FolderId, ct)).RequireCheckout)
            throw new AppException("Thư viện yêu cầu check-out trước khi sửa nội dung tệp.", 423);
    }
}

// ---------- API ----------

public sealed record LibraryDto(Guid Id, Guid WorkspaceId, string Name, bool IsDefault, Guid? FolderId, VersioningMode Versioning, int MajorLimit, int MinorLimit,
    int? DeleteVersionsOlderThanDays, bool RequireCheckout, bool ContentApproval, IReadOnlyList<string> AllowedExtensions, int? MaxFileSizeMb, bool CanManage);

public sealed record ListLibrariesQuery(Guid WorkspaceId) : IRequest<IReadOnlyList<LibraryDto>>;

public sealed record CreateLibraryCommand(Guid WorkspaceId, string Name) : IRequest<Guid>;

public sealed record SaveLibrarySettingsCommand(Guid Id, Guid WorkspaceId, string Name, VersioningMode Versioning, int MajorLimit, int MinorLimit, int? DeleteVersionsOlderThanDays,
    bool RequireCheckout, bool ContentApproval, IReadOnlyList<string>? AllowedExtensions, int? MaxFileSizeMb) : IRequest<Unit>;

public sealed class SaveLibrarySettingsValidator : AbstractValidator<SaveLibrarySettingsCommand>
{
    public SaveLibrarySettingsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên thư viện.").MaximumLength(255);
        RuleFor(x => x.MajorLimit).InclusiveBetween(1, 50_000).WithMessage("Số phiên bản chính giữ lại từ 1 đến 50.000.");
        RuleFor(x => x.MinorLimit).InclusiveBetween(0, 1_000).WithMessage("Số phiên bản phụ giữ lại từ 0 đến 1.000.");
        RuleFor(x => x.DeleteVersionsOlderThanDays).InclusiveBetween(1, 36_500).When(x => x.DeleteVersionsOlderThanDays.HasValue).WithMessage("Số ngày không hợp lệ.");
        RuleFor(x => x.MaxFileSizeMb).InclusiveBetween(1, 1_000_000).When(x => x.MaxFileSizeMb.HasValue).WithMessage("Dung lượng tối đa không hợp lệ.");
    }
}

public sealed record PublishFileCommand(Guid FileId) : IRequest<string>;

/// <summary>check_out, check_in (with a note; publish = make it a major version), discard (undo the check-out), force_check_in (who manages).</summary>
public sealed record CheckOutCommand(Guid FileId, string Action, string? Note = null, bool Publish = false) : IRequest<Unit>;

public sealed class LibraryHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, Libraries libraries, ISettingsService settings, IAuditLogger audit,
    IUserNotifier notify, TimeProvider clock)
    : IRequestHandler<ListLibrariesQuery, IReadOnlyList<LibraryDto>>, IRequestHandler<CreateLibraryCommand, Guid>, IRequestHandler<SaveLibrarySettingsCommand, Unit>,
      IRequestHandler<PublishFileCommand, string>, IRequestHandler<CheckOutCommand, Unit>
{
    public async Task<IReadOnlyList<LibraryDto>> Handle(ListLibrariesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, q.WorkspaceId, AccessRight.ViewItems, ct);
        var defaultId = PermissionProjection.LibraryOf(q.WorkspaceId);
        var folders = await db.Folders.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && f.IsLibrary && !f.IsDeleted).OrderBy(f => f.Name).ToListAsync(ct);
        var stored = await db.LibrarySettings.AsNoTracking().Where(l => l.WorkspaceId == q.WorkspaceId).ToDictionaryAsync(l => l.Id, ct);
        var list = new List<LibraryDto>();
        async Task Add(LibrarySettings l, Guid? folder)
        {
            var can = (await engine.GetEffectiveAsync(uid, folder ?? q.WorkspaceId, ct)).Has(AccessRight.ManageLibrarySettings);
            if (folder is { } fid && !await engine.HasAsync(uid, fid, AccessRight.ViewItems, ct)) return;
            var name = folder is { } id ? folders.First(x => x.Id == id).Name : l.Name;
            list.Add(new LibraryDto(l.Id, q.WorkspaceId, name, l.IsDefault, folder, l.Versioning, l.MajorLimit, l.MinorLimit,
                l.DeleteVersionsOlderThanDays, l.RequireCheckout, l.ContentApproval, l.AllowedExtensions, l.MaxFileSizeMb, can));
        }
        await Add(stored.GetValueOrDefault(defaultId) ?? Libraries.Default(defaultId, q.WorkspaceId, true), null);
        foreach (var f in folders) await Add(stored.GetValueOrDefault(f.Id) ?? Libraries.Default(f.Id, q.WorkspaceId, false), f.Id);
        return list;
    }

    public async Task<Guid> Handle(CreateLibraryCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId && w.Type != WorkspaceType.Personal, ct)
                 ?? throw new NotFoundException("Thư viện riêng chỉ tạo được trong Space.");
        if (!(await engine.GetEffectiveAsync(uid, ws.Id, ct)).Has(AccessRight.ManageSpace)) throw new ForbiddenException("Chỉ Quản lý Space tạo được thư viện.");
        var name = cmd.Name.Trim();
        if (name.Length == 0) throw new AppException("Vui lòng nhập tên thư viện.");
        var folder = new Folder { WorkspaceId = ws.Id, IsLibrary = true };
        folder.Name = await StorageNames.UniqueFolderNameAsync(db, ws.Id, null, name, folder.Id, ct);
        folder.Path = StorageNames.FolderPath(null, folder.Id);
        db.Folders.Add(folder);
        db.LibrarySettings.Add(new LibrarySettings { Id = folder.Id, WorkspaceId = ws.Id, Name = folder.Name, CreatedAt = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Create, ResourceType.Folder, folder.Id, folder.Name, ws.Id, new { library = true }), ct);
        return folder.Id;
    }

    public async Task<Unit> Handle(SaveLibrarySettingsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var isDefault = cmd.Id == PermissionProjection.LibraryOf(cmd.WorkspaceId);
        Folder? folder = null;
        if (!isDefault)
            folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == cmd.Id && f.WorkspaceId == cmd.WorkspaceId && f.IsLibrary && !f.IsDeleted, ct)
                     ?? throw new NotFoundException("Không tìm thấy thư viện.");
        if (!(await engine.GetEffectiveAsync(uid, folder?.Id ?? cmd.WorkspaceId, ct)).Has(AccessRight.ManageLibrarySettings))
            throw new ForbiddenException("Bạn không có quyền thay đổi cài đặt thư viện.");
        // Never wider than the system's file types.
        var system = await settings.GetAsync(BlueDrive.Application.Settings.SettingKeys.UploadAllowedExtensions, BlueDrive.Application.Settings.SettingKeys.DefaultAllowedExtensions, ct);
        var types = (cmd.AllowedExtensions ?? []).Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        var outside = types.Where(e => !system.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        if (outside.Count > 0) throw new AppException($"Các định dạng không được hệ thống cho phép: {string.Join(", ", outside)}.");
        var row = await db.LibrarySettings.FirstOrDefaultAsync(l => l.Id == cmd.Id, ct);
        if (row is null) db.LibrarySettings.Add(row = new LibrarySettings { Id = cmd.Id, WorkspaceId = cmd.WorkspaceId, IsDefault = isDefault, CreatedAt = clock.GetUtcNow().UtcDateTime });
        row.Name = cmd.Name.Trim();
        row.Versioning = cmd.Versioning;
        row.MajorLimit = cmd.MajorLimit;
        row.MinorLimit = cmd.MinorLimit;
        row.DeleteVersionsOlderThanDays = cmd.DeleteVersionsOlderThanDays;
        row.RequireCheckout = cmd.RequireCheckout;
        row.ContentApproval = cmd.ContentApproval;
        row.AllowedExtensions = types;
        row.MaxFileSizeMb = cmd.MaxFileSizeMb;
        if (folder is not null) folder.Name = await StorageNames.UniqueFolderNameAsync(db, cmd.WorkspaceId, null, row.Name, folder.Id, ct);
        // Content approval is the existing approval flow ("Cần phê duyệt khi tải lên") on every folder of the library.
        var folders = folder is null
            ? db.Folders.Where(f => f.WorkspaceId == cmd.WorkspaceId && !db.Folders.Any(t => t.IsLibrary && t.WorkspaceId == cmd.WorkspaceId && f.Path.StartsWith(t.Path)))
            : db.Folders.Where(f => f.Path.StartsWith(folder.Path));
        await db.SaveChangesAsync(ct);
        await folders.ExecuteUpdateAsync(u => u.SetProperty(f => f.RequiresApproval, cmd.ContentApproval), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Folder, cmd.Id, row.Name, cmd.WorkspaceId, new { library = cmd }), ct);
        return Unit.Value;
    }

    public async Task<string> Handle(PublishFileCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, cmd.FileId, AccessRight.EditItems, ct);
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == cmd.FileId && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tệp.");
        if (file.CheckedOutBy is { } who && who != uid) throw new AppException("Tệp đang được người khác check-out.", 423);
        var v = await libraries.PublishAsync(file, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { published = Libraries.Label(v) }), ct);
        return Libraries.Label(v);
    }

    public async Task<Unit> Handle(CheckOutCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == cmd.FileId && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tệp.");
        var rights = await engine.DemandAsync(uid, file.Id, AccessRight.ViewItems, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        switch (cmd.Action)
        {
            case "check_out":
                if (file.CheckedOutBy is { } other && other != uid) throw new ConflictException("Tệp đang được người khác check-out.");
                if (!rights.Has(AccessRight.EditItems)) throw new ForbiddenException("Bạn không có quyền sửa tệp này.");
                file.CheckedOutBy = uid;
                file.CheckedOutAt = now;
                break;
            case "check_in" or "discard":
                if (file.CheckedOutBy != uid) throw new ConflictException("Bạn không check-out tệp này.");
                file.CheckedOutBy = null;
                file.CheckedOutAt = null;
                if (cmd.Action == "check_in")
                {
                    if (!string.IsNullOrWhiteSpace(cmd.Note) && await db.FileVersions.FirstOrDefaultAsync(v => v.Id == file.CurrentVersionId, ct) is { } current && current.CreatedBy == uid)
                        current.ChangeNote = cmd.Note.Trim();
                    await db.SaveChangesAsync(ct);
                    if (cmd.Publish && await db.FileVersions.AnyAsync(v => v.Id == file.CurrentVersionId && !v.IsPublished, ct)) await libraries.PublishAsync(file, ct);
                }
                break;
            case "force_check_in":
                if (!rights.Has(AccessRight.CheckOutOverride) && !rights.Has(AccessRight.ManageLibrarySettings))
                    throw new ForbiddenException("Chỉ người quản lý thư viện được ép check-in.");
                if (file.CheckedOutBy is not { } holder) throw new ConflictException("Tệp không bị check-out.");
                file.CheckedOutBy = null;
                file.CheckedOutAt = null;
                if (holder != uid)
                    await notify.NotifyUserAsync(holder, "checkout", $"Tệp \"{file.Name}\" đã được người quản lý check-in thay bạn", null, $"/files/{file.Id}", ct);
                break;
            default:
                throw new AppException("Thao tác không hợp lệ.");
        }
        file.Revision++;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { checkout = cmd.Action }), ct);
        return Unit.Value;
    }
}
