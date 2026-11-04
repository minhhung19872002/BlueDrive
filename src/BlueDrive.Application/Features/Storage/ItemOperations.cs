using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

public sealed record ItemRef(string Type, Guid Id)
{
    public bool IsFile => Type == "file";
}

public static class ItemNameRules
{
    public static IRuleBuilderOptions<T, string> ValidItemName<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty().WithMessage("Vui lòng nhập tên.")
        .MaximumLength(200).WithMessage("Tên tối đa 200 ký tự.")
        .Must(n => n is null || n.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0).WithMessage("Tên không được chứa các ký tự / \\ : * ? \" < > |")
        .Must(n => n is null || (n.Trim() != "." && n.Trim() != "..")).WithMessage("Tên không hợp lệ.");
}

// ---------- Folders ----------
public sealed record CreateFolderCommand(Guid WorkspaceId, Guid? ParentId, string Name) : IRequest<DriveItemDto>;

public sealed class CreateFolderValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderValidator() => RuleFor(x => x.Name).ValidItemName();
}

public sealed class CreateFolderHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit)
    : IRequestHandler<CreateFolderCommand, DriveItemDto>
{
    public async Task<DriveItemDto> Handle(CreateFolderCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.DocUpload, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền tạo mới tư liệu.");
        await auth.EnsureCanUploadAsync(uid, cmd.WorkspaceId, cmd.ParentId, ct);
        Folder? parent = null;
        if (cmd.ParentId.HasValue)
            parent = await db.Folders.FirstOrDefaultAsync(f => f.Id == cmd.ParentId && f.WorkspaceId == cmd.WorkspaceId && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy thư mục cha.");
        var name = FileNameSanitizer.Sanitize(cmd.Name);
        if (await db.Folders.AnyAsync(f => f.WorkspaceId == cmd.WorkspaceId && f.ParentId == cmd.ParentId && !f.IsDeleted && f.Name.ToLower() == name.ToLower(), ct))
            throw new ConflictException($"Thư mục \"{name}\" đã tồn tại ở vị trí này.");
        var folder = new Folder { WorkspaceId = cmd.WorkspaceId, ParentId = cmd.ParentId, Name = name, RequiresApproval = parent?.RequiresApproval ?? false };
        folder.Path = StorageNames.FolderPath(parent, folder.Id);
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Create, ResourceType.Folder, folder.Id, folder.Name, folder.WorkspaceId), ct);
        return await DriveProjection.Folders(db, db.Folders.Where(f => f.Id == folder.Id), uid).FirstAsync(ct);
    }
}

/// <summary>Ensures "A/B/C" exists under the parent (folder upload, #22/#40). Reuses same-named folders. Saves.</summary>
public static class FolderChain
{
    public static async Task<Folder?> EnsureAsync(IAppDbContext db, Guid workspaceId, Guid? parentId, string? relativeDir, CancellationToken ct)
    {
        Folder? current = parentId is { } pid ? await db.Folders.FirstAsync(f => f.Id == pid, ct) : null;
        if (string.IsNullOrWhiteSpace(relativeDir)) return current;
        var parts = relativeDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p is not ("." or "..")).Select(FileNameSanitizer.Sanitize).Take(32).ToList();
        if (parts.Count == 0) return current;
        // Serialize concurrent uploads of the same folder tree within a workspace.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({workspaceId.ToString()}))", ct);
        foreach (var name in parts)
        {
            var pid2 = current?.Id;
            var existing = await db.Folders.FirstOrDefaultAsync(f => f.WorkspaceId == workspaceId && f.ParentId == pid2 && !f.IsDeleted && f.Name.ToLower() == name.ToLower(), ct);
            if (existing is null)
            {
                existing = new Folder { WorkspaceId = workspaceId, ParentId = pid2, Name = name, RequiresApproval = current?.RequiresApproval ?? false };
                existing.Path = StorageNames.FolderPath(current, existing.Id);
                db.Folders.Add(existing);
                await db.SaveChangesAsync(ct);
            }
            current = existing;
        }
        await tx.CommitAsync(ct);
        return current;
    }
}

public sealed record UpdateFolderCommand(Guid Id, string? Name, bool? RequiresApproval) : IRequest<Unit>;

public sealed class UpdateFolderHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IAuditLogger audit) : IRequestHandler<UpdateFolderCommand, Unit>
{
    public async Task<Unit> Handle(UpdateFolderCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == cmd.Id && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy thư mục.");
        if (cmd.Name is not null)
        {
            await auth.EnsureAsync(uid, ResourceType.Folder, folder.Id, AccessLevel.Edit, ct);
            var name = FileNameSanitizer.Sanitize(cmd.Name);
            if (await db.Folders.AnyAsync(f => f.WorkspaceId == folder.WorkspaceId && f.ParentId == folder.ParentId && !f.IsDeleted && f.Id != folder.Id && f.Name.ToLower() == name.ToLower(), ct))
                throw new ConflictException($"Thư mục \"{name}\" đã tồn tại ở vị trí này.");
            var old = folder.Name;
            folder.Name = name;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Rename, ResourceType.Folder, folder.Id, folder.Name, folder.WorkspaceId, new { from = old, to = name }), ct);
        }
        if (cmd.RequiresApproval is { } ra)
        {
            await auth.EnsureAsync(uid, ResourceType.Folder, folder.Id, AccessLevel.Manage, ct);
            folder.RequiresApproval = ra;
            await db.SaveChangesAsync(ct);
            // Apply to sub-folders so the rule covers the whole subtree.
            await db.Folders.Where(f => f.Path.StartsWith(folder.Path)).ExecuteUpdateAsync(s => s.SetProperty(f => f.RequiresApproval, ra), ct);
            await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Folder, folder.Id, folder.Name, folder.WorkspaceId, new { requiresApproval = ra }), ct);
        }
        return Unit.Value;
    }
}

// ---------- Files: rename + metadata ----------
public sealed record RenameFileCommand(Guid Id, string Name) : IRequest<Unit>;

public sealed class RenameFileValidator : AbstractValidator<RenameFileCommand>
{
    public RenameFileValidator() => RuleFor(x => x.Name).ValidItemName();
}

public sealed class RenameFileHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IFileTypeInspector types, IAuditLogger audit) : IRequestHandler<RenameFileCommand, Unit>
{
    public async Task<Unit> Handle(RenameFileCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await auth.EnsureAsync(uid, ResourceType.File, cmd.Id, AccessLevel.Edit, ct);
        var file = await db.Files.FirstAsync(f => f.Id == cmd.Id, ct);
        var name = FileNameSanitizer.Sanitize(cmd.Name);
        // Keep the extension: content type must not change through a rename.
        if (!string.Equals(Path.GetExtension(name).TrimStart('.'), file.Extension, StringComparison.OrdinalIgnoreCase) && file.Extension.Length > 0)
            name = $"{name}.{file.Extension}";
        if (types.IsBlockedExtension(Path.GetExtension(name))) throw new AppException("Tên tệp có phần mở rộng không được phép.");
        var unique = await StorageNames.UniqueFileNameAsync(db, file.WorkspaceId, file.FolderId, name, file.Id, ct);
        var old = file.Name;
        file.Name = unique;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Rename, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { from = old, to = unique }), ct);
        return Unit.Value;
    }
}

/// <summary>#39: title, description, tags, catalog items — for one or many files ("áp dụng hàng loạt").</summary>
public sealed record UpdateFileMetadataCommand(IReadOnlyList<Guid> FileIds, string? Title, string? Description, IReadOnlyList<string>? Tags,
    IReadOnlyList<Guid>? CatalogItemIds, bool AppendTags = false) : IRequest<int>;

public sealed class UpdateFileMetadataValidator : AbstractValidator<UpdateFileMetadataCommand>
{
    public UpdateFileMetadataValidator()
    {
        RuleFor(x => x.FileIds).NotEmpty().WithMessage("Vui lòng chọn tệp.");
        RuleFor(x => x.Title).MaximumLength(500).WithMessage("Tiêu đề tối đa 500 ký tự.");
        RuleFor(x => x.Description).MaximumLength(5000).WithMessage("Mô tả tối đa 5000 ký tự.");
        RuleForEach(x => x.Tags).MaximumLength(100).WithMessage("Mỗi thẻ tối đa 100 ký tự.");
    }
}

public sealed class UpdateFileMetadataHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IAuditLogger audit) : IRequestHandler<UpdateFileMetadataCommand, int>
{
    public async Task<int> Handle(UpdateFileMetadataCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        foreach (var id in cmd.FileIds.Distinct()) await auth.EnsureAsync(uid, ResourceType.File, id, AccessLevel.Edit, ct);
        var validItems = cmd.CatalogItemIds is null ? null : await db.CatalogItems.Where(i => cmd.CatalogItemIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
        var files = await db.Files.Include(f => f.CatalogItems).Where(f => cmd.FileIds.Contains(f.Id)).ToListAsync(ct);
        var tags = cmd.Tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var f in files)
        {
            if (cmd.Title is not null) f.Title = cmd.Title.Trim().Length == 0 ? null : cmd.Title.Trim();
            if (cmd.Description is not null) f.Description = cmd.Description.Trim().Length == 0 ? null : cmd.Description.Trim();
            if (tags is not null) f.Tags = cmd.AppendTags ? f.Tags.Union(tags, StringComparer.CurrentCultureIgnoreCase).ToList() : tags;
            if (validItems is not null)
            {
                f.CatalogItems.RemoveAll(c => !validItems.Contains(c.CatalogItemId));
                foreach (var ci in validItems.Where(ci => f.CatalogItems.All(c => c.CatalogItemId != ci))) f.CatalogItems.Add(new FileCatalogItem { FileId = f.Id, CatalogItemId = ci });
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var f in files)
            await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { metadata = true, cmd.Title, tags, catalogItems = validItems }), ct);
        return files.Count;
    }
}

// ---------- Move / copy / delete (#24, #46, #48) ----------
public sealed record MoveItemsCommand(IReadOnlyList<ItemRef> Items, Guid TargetWorkspaceId, Guid? TargetFolderId) : IRequest<int>;

public sealed class MoveItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, QuotaService quota, IPermissionService perms, IAuditLogger audit,
    Access.AclEditor acl, Access.IItemPermissionService engine)
    : IRequestHandler<MoveItemsCommand, int>
{
    public Task<int> Handle(MoveItemsCommand cmd, CancellationToken ct) => MoveAsync(me.RequireUserId(), cmd, trusted: false, ct);

    /// <summary>
    /// The move itself. <paramref name="trusted"/>: a move the system makes on someone's behalf after its own checks (an ownership transfer
    /// accepted), without the mover's rights on source and target.
    /// </summary>
    public async Task<int> MoveAsync(Guid uid, MoveItemsCommand cmd, bool trusted, CancellationToken ct)
    {
        if (cmd.Items.Count == 0) throw new AppException("Vui lòng chọn mục cần di chuyển.");
        // Moving puts items into the target like an upload does (#24, #46): same role right as copy.
        if (!trusted && !await perms.HasAsync(uid, Perm.DocUpload, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền đưa tư liệu vào vị trí khác.");
        if (!trusted) await auth.EnsureCanUploadAsync(uid, cmd.TargetWorkspaceId, cmd.TargetFolderId, ct);
        Folder? target = cmd.TargetFolderId is { } tid
            ? await db.Folders.FirstOrDefaultAsync(f => f.Id == tid && f.WorkspaceId == cmd.TargetWorkspaceId && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy thư mục đích.")
            : null;
        var moved = 0;
        // What the item gets from its new place (spec 1.1): its new parent in the tree.
        var newParent = cmd.TargetFolderId ?? Access.PermissionProjection.LibraryOf(cmd.TargetWorkspaceId);
        foreach (var item in cmd.Items)
        {
            if (item.IsFile)
            {
                if (!trusted) await auth.EnsureAsync(uid, ResourceType.File, item.Id, AccessLevel.Edit, ct);
                var f = await db.Files.FirstAsync(x => x.Id == item.Id, ct);
                if (f.WorkspaceId == cmd.TargetWorkspaceId && f.FolderId == cmd.TargetFolderId) continue;
                if (!trusted) await MoveRules.EnsureMayLeaveAsync(db, engine, perms, uid, f.WorkspaceId, cmd.TargetWorkspaceId, ct);
                var bytes = await db.FileVersions.Where(v => v.FileId == f.Id).SumAsync(v => v.SizeBytes, ct);
                var fromWs = f.WorkspaceId;
                var fromFolder = f.FolderId;
                if (fromWs != cmd.TargetWorkspaceId) await quota.EnsureAvailableAsync(cmd.TargetWorkspaceId, bytes, ct);
                f.Name = await StorageNames.UniqueFileNameAsync(db, cmd.TargetWorkspaceId, cmd.TargetFolderId, f.Name, f.Id, ct);
                f.WorkspaceId = cmd.TargetWorkspaceId;
                f.FolderId = cmd.TargetFolderId;
                await db.SaveChangesAsync(ct);
                if (fromWs != cmd.TargetWorkspaceId)
                {
                    await quota.AddUsageAsync(fromWs, -bytes, ct);
                    await quota.AddUsageAsync(cmd.TargetWorkspaceId, bytes, ct);
                }
                await acl.RebaseAfterMoveAsync(f.Id, newParent, uid, ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Move, ResourceType.File, f.Id, f.Name, cmd.TargetWorkspaceId,
                    new { fromWorkspace = fromWs, fromFolder, toWorkspace = cmd.TargetWorkspaceId, toFolder = cmd.TargetFolderId }), ct);
            }
            else
            {
                if (!trusted) await auth.EnsureAsync(uid, ResourceType.Folder, item.Id, AccessLevel.Edit, ct);
                var folder = await db.Folders.FirstAsync(x => x.Id == item.Id, ct);
                if (target is not null && target.Path.StartsWith(folder.Path)) throw new AppException($"Không thể di chuyển thư mục \"{folder.Name}\" vào chính nó hoặc thư mục con của nó.");
                if (folder.WorkspaceId == cmd.TargetWorkspaceId && folder.ParentId == cmd.TargetFolderId) continue;
                if (!trusted) await MoveRules.EnsureMayLeaveAsync(db, engine, perms, uid, folder.WorkspaceId, cmd.TargetWorkspaceId, ct);
                var fromWs = folder.WorkspaceId;
                var subtreeFolderIds = await db.Folders.Where(x => x.Path.StartsWith(folder.Path)).Select(x => x.Id).ToListAsync(ct);
                var bytes = await db.FileVersions.Where(v => v.File!.FolderId != null && subtreeFolderIds.Contains(v.File.FolderId.Value)).SumAsync(v => v.SizeBytes, ct);
                if (fromWs != cmd.TargetWorkspaceId) await quota.EnsureAvailableAsync(cmd.TargetWorkspaceId, bytes, ct);
                var oldPath = folder.Path;
                folder.Name = await StorageNames.UniqueFolderNameAsync(db, cmd.TargetWorkspaceId, cmd.TargetFolderId, folder.Name, folder.Id, ct);
                folder.ParentId = cmd.TargetFolderId;
                folder.WorkspaceId = cmd.TargetWorkspaceId;
                folder.Path = StorageNames.FolderPath(target, folder.Id);
                await db.SaveChangesAsync(ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE folders SET path = {folder.Path} || substr(path, {oldPath.Length + 1}), workspace_id = {cmd.TargetWorkspaceId} WHERE path LIKE {oldPath + "%"} AND id <> {folder.Id}", ct);
                if (fromWs != cmd.TargetWorkspaceId)
                {
                    await db.Files.Where(x => x.FolderId != null && subtreeFolderIds.Contains(x.FolderId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkspaceId, cmd.TargetWorkspaceId), ct);
                    await quota.AddUsageAsync(fromWs, -bytes, ct);
                    await quota.AddUsageAsync(cmd.TargetWorkspaceId, bytes, ct);
                }
                await acl.RebaseAfterMoveAsync(folder.Id, newParent, uid, ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Move, ResourceType.Folder, folder.Id, folder.Name, cmd.TargetWorkspaceId,
                    new { fromWorkspace = fromWs, toWorkspace = cmd.TargetWorkspaceId, toFolder = cmd.TargetFolderId }), ct);
            }
            moved++;
        }
        return moved;
    }
}

/// <summary>
/// Spec 1.1 moving between Drive của tôi and a Space: out of a drive only by its owner (the Space then owns it); out of a Space only by who
/// manages it, and only if the Space allows items to leave.
/// </summary>
public static class MoveRules
{
    public static async Task EnsureMayLeaveAsync(IAppDbContext db, Access.IItemPermissionService engine, IPermissionService perms, Guid uid, Guid fromWs, Guid toWs,
        CancellationToken ct)
    {
        if (fromWs == toWs) return;
        var from = await db.Workspaces.AsNoTracking().Where(w => w.Id == fromWs).Select(w => new { w.Type, w.OwnerId, w.AllowMoveOut, w.Name }).FirstAsync(ct);
        if (from.Type == WorkspaceType.Personal)
        {
            if (from.OwnerId != uid) throw new ForbiddenException("Chỉ chủ sở hữu được chuyển tư liệu ra khỏi Drive của họ.");
            return;
        }
        if (await perms.HasAsync(uid, Perm.WorkspaceAdmin, ct)) return;
        if (!Domain.Permissions.AccessRights.Has(await engine.GetEffectiveAsync(uid, fromWs, ct), Domain.Permissions.AccessRight.ManageSpace))
            throw new ForbiddenException($"Chỉ Quản lý Space \"{from.Name}\" được chuyển tư liệu ra khỏi Space.");
        if (!from.AllowMoveOut) throw new ForbiddenException($"Space \"{from.Name}\" không cho phép chuyển tư liệu ra ngoài.");
    }
}

/// <summary>Before a move: who will lose access (what came from the old place) and who will get it (from the new place); direct grants stay.</summary>
public sealed record MovePreviewQuery(IReadOnlyList<ItemRef> Items, Guid TargetWorkspaceId, Guid? TargetFolderId) : IRequest<MovePreviewDto>;

public sealed record MovePreviewDto(IReadOnlyList<string> Lose, IReadOnlyList<string> Gain, bool OwnershipToSpace, bool Blocked, string? BlockedReason);

public sealed class MovePreviewHandler(IAppDbContext db, ICurrentUser me, Access.IItemPermissionService engine, IPermissionService perms)
    : IRequestHandler<MovePreviewQuery, MovePreviewDto>
{
    public async Task<MovePreviewDto> Handle(MovePreviewQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var parent = q.TargetFolderId ?? Access.PermissionProjection.LibraryOf(q.TargetWorkspaceId);
        var parentScope = await db.Securables.Where(s => s.Id == parent).Select(s => (Guid?)s.AclScopeId).FirstOrDefaultAsync(ct);
        var gaining = parentScope is null ? []
            : await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == parentScope && e.AppliesToChildren && e.PrincipalId != uid).Select(e => e.PrincipalId).Distinct().ToListAsync(ct);
        var lose = new HashSet<Guid>();
        var gain = new HashSet<Guid>();
        var toSpace = false;
        string? blocked = null;
        foreach (var item in q.Items.Take(50))
        {
            var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == item.Id, ct);
            if (node is null) continue;
            await engine.DemandAsync(uid, node.Id, Domain.Permissions.AccessRight.ViewItems, ct);
            var fromWs = node.SpaceId ?? node.PersonalDriveId ?? Guid.Empty;
            if (node.PersonalDriveId is not null && fromWs != q.TargetWorkspaceId) toSpace = true;
            try { await MoveRules.EnsureMayLeaveAsync(db, engine, perms, uid, fromWs, q.TargetWorkspaceId, ct); }
            catch (ForbiddenException e) { blocked ??= e.Message; }
            var entries = await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == node.AclScopeId && e.PrincipalId != uid).ToListAsync(ct);
            var direct = entries.Where(e => e.ScopeId == node.Id && (e.Source == AclSource.Direct || e.Source == AclSource.Link)).Select(e => e.PrincipalId).ToHashSet();
            var now = entries.Select(e => e.PrincipalId).ToHashSet();
            foreach (var p in now.Where(p => !direct.Contains(p) && !gaining.Contains(p))) lose.Add(p);
            foreach (var p in gaining.Where(p => !now.Contains(p))) gain.Add(p);
        }
        var names = await Access.PrincipalDisplay.ResolveAsync(db, lose.Concat(gain), ct);
        string Name(Guid id) => names.GetValueOrDefault(id, ("(không xác định)", null)).Name;
        return new MovePreviewDto(lose.Select(Name).Order().ToList(), gain.Select(Name).Order().ToList(),
            toSpace && await db.Workspaces.AnyAsync(w => w.Id == q.TargetWorkspaceId && w.Type != WorkspaceType.Personal, ct), blocked is not null, blocked);
    }
}

public sealed record CopyItemsCommand(IReadOnlyList<ItemRef> Items, Guid TargetWorkspaceId, Guid? TargetFolderId) : IRequest<int>;

public sealed class CopyItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, QuotaService quota, IPermissionService perms, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<CopyItemsCommand, int>
{
    public async Task<int> Handle(CopyItemsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (cmd.Items.Count == 0) throw new AppException("Vui lòng chọn mục cần sao chép.");
        if (!await perms.HasAsync(uid, Perm.DocUpload, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền tạo mới tư liệu.");
        await auth.EnsureCanUploadAsync(uid, cmd.TargetWorkspaceId, cmd.TargetFolderId, ct);
        var target = cmd.TargetFolderId is { } tid ? await db.Folders.FirstAsync(f => f.Id == tid, ct) : null;

        // Plan first (so quota is checked once for everything), then copy.
        var plannedFiles = new List<(StoredFile Source, Guid? NewFolderPlaceholder)>();
        long totalBytes = 0;
        foreach (var item in cmd.Items)
        {
            if (item.IsFile)
            {
                await auth.EnsureAsync(uid, ResourceType.File, item.Id, AccessLevel.Download, ct);
                totalBytes += await db.Files.Where(f => f.Id == item.Id).Select(f => f.SizeBytes).FirstAsync(ct);
            }
            else
            {
                await auth.EnsureAsync(uid, ResourceType.Folder, item.Id, AccessLevel.Download, ct);
                var path = await db.Folders.Where(f => f.Id == item.Id).Select(f => f.Path).FirstAsync(ct);
                if (target is not null && target.Path.StartsWith(path)) throw new AppException("Không thể sao chép thư mục vào chính nó hoặc thư mục con của nó.");
                totalBytes += await db.Files.Where(f => !f.IsDeleted && f.Folder != null && f.Folder.Path.StartsWith(path)).SumAsync(f => f.SizeBytes, ct);
            }
        }
        await quota.EnsureAvailableAsync(cmd.TargetWorkspaceId, totalBytes, ct);

        var copied = 0;
        foreach (var item in cmd.Items)
        {
            if (item.IsFile)
            {
                var src = await db.Files.AsNoTracking().Include(f => f.CatalogItems).FirstAsync(f => f.Id == item.Id, ct);
                var copy = await CopyFileAsync(src, cmd.TargetWorkspaceId, cmd.TargetFolderId, uid, ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Copy, ResourceType.File, copy.Id, copy.Name, cmd.TargetWorkspaceId, new { sourceId = src.Id }), ct);
            }
            else
            {
                var src = await db.Folders.AsNoTracking().FirstAsync(f => f.Id == item.Id, ct);
                var newRoot = await CopyFolderAsync(src, cmd.TargetWorkspaceId, target, uid, ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Copy, ResourceType.Folder, newRoot.Id, newRoot.Name, cmd.TargetWorkspaceId, new { sourceId = src.Id }), ct);
            }
            copied++;
        }
        await quota.AddUsageAsync(cmd.TargetWorkspaceId, totalBytes, ct);
        return copied;
    }

    private async Task<StoredFile> CopyFileAsync(StoredFile src, Guid wsId, Guid? folderId, Guid uid, CancellationToken ct)
    {
        var current = await db.FileVersions.AsNoTracking().FirstAsync(v => v.Id == src.CurrentVersionId, ct);
        var copy = new StoredFile
        {
            WorkspaceId = wsId, FolderId = folderId, Name = await StorageNames.UniqueFileNameAsync(db, wsId, folderId, src.Name, null, ct),
            Extension = src.Extension, MimeType = src.MimeType, MediaType = src.MediaType, SizeBytes = src.SizeBytes, ChecksumSha256 = src.ChecksumSha256,
            Title = src.Title, Description = src.Description, Tags = [.. src.Tags], ApprovalStatus = src.ApprovalStatus == ApprovalStatus.Approved ? ApprovalStatus.Approved : ApprovalStatus.Draft,
            ScanStatus = src.ScanStatus, Width = src.Width, Height = src.Height, DurationSeconds = src.DurationSeconds, ThumbnailKey = src.ThumbnailKey,
            PreviewKey = src.PreviewKey, UploaderOrganizationId = await db.Users.Where(u => u.Id == uid).Select(u => u.OrganizationId).FirstAsync(ct),
        };
        // The blob is shared (copy-on-write): purge deletes it only when no version references it anymore.
        var v = new FileVersion
        {
            FileId = copy.Id, VersionNo = 1, StorageKey = current.StorageKey, SizeBytes = current.SizeBytes, Checksum = current.Checksum,
            ChangeNote = "Bản sao", CreatedBy = uid, CreatedAt = clock.GetUtcNow().UtcDateTime, IsEncrypted = current.IsEncrypted, WrappedKey = current.WrappedKey,
        };
        copy.CurrentVersionId = v.Id;
        copy.Versions.Add(v);
        foreach (var c in src.CatalogItems) copy.CatalogItems.Add(new FileCatalogItem { FileId = copy.Id, CatalogItemId = c.CatalogItemId });
        db.Files.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    private async Task<Folder> CopyFolderAsync(Folder src, Guid wsId, Folder? parent, Guid uid, CancellationToken ct)
    {
        var folder = new Folder { WorkspaceId = wsId, ParentId = parent?.Id, Name = await StorageNames.UniqueFolderNameAsync(db, wsId, parent?.Id, src.Name, null, ct), RequiresApproval = src.RequiresApproval };
        folder.Path = StorageNames.FolderPath(parent, folder.Id);
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        foreach (var f in await db.Files.AsNoTracking().Include(x => x.CatalogItems).Where(x => x.FolderId == src.Id && !x.IsDeleted).ToListAsync(ct))
            await CopyFileAsync(f, wsId, folder.Id, uid, ct);
        foreach (var child in await db.Folders.AsNoTracking().Where(x => x.ParentId == src.Id && !x.IsDeleted).ToListAsync(ct))
            await CopyFolderAsync(child, wsId, folder, uid, ct);
        return folder;
    }
}

/// <summary>Moves items to the trash (soft delete). Space is released only on permanent deletion.</summary>
public sealed record DeleteItemsCommand(IReadOnlyList<ItemRef> Items) : IRequest<int>;

public sealed class DeleteItemsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<DeleteItemsCommand, int>
{
    public async Task<int> Handle(DeleteItemsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.DocDelete, ct)) throw new ForbiddenException("Vai trò của bạn không có quyền xóa tư liệu.");
        var now = clock.GetUtcNow().UtcDateTime;
        var count = 0;
        foreach (var item in cmd.Items)
        {
            if (item.IsFile)
            {
                await auth.EnsureAsync(uid, ResourceType.File, item.Id, AccessLevel.Edit, ct);
                var f = await db.Files.FirstAsync(x => x.Id == item.Id, ct);
                f.IsDeleted = true; f.IsTrashRoot = true; f.DeletedAt = now; f.DeletedBy = uid; f.OriginalFolderId = f.FolderId;
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceType.File, f.Id, f.Name, f.WorkspaceId), ct);
            }
            else
            {
                await auth.EnsureAsync(uid, ResourceType.Folder, item.Id, AccessLevel.Edit, ct);
                var folder = await db.Folders.FirstAsync(x => x.Id == item.Id, ct);
                folder.IsDeleted = true; folder.IsTrashRoot = true; folder.DeletedAt = now; folder.DeletedBy = uid; folder.OriginalParentId = folder.ParentId;
                await db.SaveChangesAsync(ct);
                var path = folder.Path;
                await db.Folders.Where(x => x.Path.StartsWith(path) && x.Id != folder.Id && !x.IsDeleted)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, true).SetProperty(x => x.DeletedAt, now).SetProperty(x => x.DeletedBy, uid), ct);
                await db.Files.Where(x => x.Folder != null && x.Folder.Path.StartsWith(path) && !x.IsDeleted)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, true).SetProperty(x => x.DeletedAt, now).SetProperty(x => x.DeletedBy, uid), ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceType.Folder, folder.Id, folder.Name, folder.WorkspaceId), ct);
            }
            count++;
        }
        return count;
    }
}
