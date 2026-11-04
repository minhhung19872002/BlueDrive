using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

// Spec 1.2: shortcuts. A shortcut points at a file or folder; it carries no rights — who opens it needs access to the target.

public sealed record CreateShortcutCommand(Guid TargetId, Guid? WorkspaceId = null, Guid? FolderId = null) : IRequest<Guid>;

public sealed record DeleteShortcutCommand(Guid Id) : IRequest<Unit>;

public sealed class ShortcutHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<CreateShortcutCommand, Guid>, IRequestHandler<DeleteShortcutCommand, Unit>
{
    public async Task<Guid> Handle(CreateShortcutCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var target = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == cmd.TargetId && !s.IsDeleted && (s.Kind == SecurableKind.File || s.Kind == SecurableKind.Folder), ct)
                     ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        // Only something one may see; added where one may add (Drive của tôi by default).
        await engine.DemandAsync(uid, target.Id, AccessRight.ViewItems, ct);
        var wsId = cmd.WorkspaceId ?? await db.Workspaces.Where(w => w.OwnerId == uid && w.Type == WorkspaceType.Personal).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct)
                   ?? throw new AppException("Tài khoản chưa có Drive của tôi.");
        if (cmd.FolderId is { } folder && !await db.Folders.AnyAsync(f => f.Id == folder && f.WorkspaceId == wsId && !f.IsDeleted, ct))
            throw new NotFoundException("Không tìm thấy thư mục đích.");
        await engine.DemandAsync(uid, cmd.FolderId ?? wsId, AccessRight.AddItems, ct);
        if (await db.Shortcuts.AnyAsync(s => s.WorkspaceId == wsId && s.FolderId == cmd.FolderId && s.TargetId == target.Id, ct))
            throw new ConflictException("Đã có lối tắt tới tư liệu này ở đây.");
        var shortcut = new Shortcut
        {
            WorkspaceId = wsId, FolderId = cmd.FolderId, TargetId = target.Id, TargetType = target.Kind == SecurableKind.File ? "file" : "folder", Name = target.Name,
            CreatedBy = uid, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Shortcuts.Add(shortcut);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Create, target.Kind == SecurableKind.File ? ResourceType.File : ResourceType.Folder, target.Id, target.Name, wsId,
            new { shortcut = shortcut.Id, folder = cmd.FolderId }), ct);
        return shortcut.Id;
    }

    public async Task<Unit> Handle(DeleteShortcutCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var s = await db.Shortcuts.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy lối tắt.");
        // Whoever made it, or may delete in that place. The target stays.
        if (s.CreatedBy != uid) await engine.DemandAsync(uid, s.FolderId ?? s.WorkspaceId, AccessRight.DeleteItems, ct);
        db.Shortcuts.Remove(s);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, s.TargetType == "file" ? ResourceType.File : ResourceType.Folder, s.TargetId, s.Name, s.WorkspaceId,
            new { shortcut = s.Id }), ct);
        return Unit.Value;
    }
}

public static class ShortcutListing
{
    /// <summary>
    /// The shortcuts of a folder as list rows: the target's id and type (so opening goes to it), the shortcut's own id, and its state for this
    /// user — ok, no_access (they would see "Bạn cần quyền truy cập") or deleted.
    /// </summary>
    public static async Task<List<DriveItemDto>> RowsAsync(IAppDbContext db, IItemPermissionService engine, Guid uid, Guid workspaceId, Guid? folderId, CancellationToken ct)
    {
        var shortcuts = await db.Shortcuts.AsNoTracking().Where(s => s.WorkspaceId == workspaceId && s.FolderId == folderId).OrderBy(s => s.Name).ToListAsync(ct);
        var rows = new List<DriveItemDto>();
        foreach (var s in shortcuts)
        {
            var node = await db.Securables.AsNoTracking().Where(n => n.Id == s.TargetId).Select(n => new { n.IsDeleted, n.Name, Ws = n.SpaceId ?? n.PersonalDriveId }).FirstOrDefaultAsync(ct);
            var deleted = node is null || node.IsDeleted
                          || (s.TargetType == "file" ? !await db.Files.AnyAsync(f => f.Id == s.TargetId && !f.IsDeleted, ct) : !await db.Folders.AnyAsync(f => f.Id == s.TargetId && !f.IsDeleted, ct));
            var state = deleted ? "deleted" : await engine.HasAsync(uid, s.TargetId, AccessRight.ViewItems, ct) ? "ok" : "no_access";
            // The target's current name only for who may see it.
            var name = state == "ok" ? node!.Name : s.Name;
            rows.Add(new DriveItemDto(s.TargetId, s.TargetType, name, state == "ok" ? node!.Ws ?? workspaceId : workspaceId, folderId, 0, null,
                s.TargetType == "file" ? Path.GetExtension(name).TrimStart('.').ToLowerInvariant() : null, null, s.CreatedAt, null, s.CreatedBy, null, false, false, null, null, null, null,
                ShortcutId: s.Id, ShortcutState: state));
        }
        return rows;
    }
}
