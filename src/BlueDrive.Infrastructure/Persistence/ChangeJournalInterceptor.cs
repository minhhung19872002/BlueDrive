using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlueDrive.Infrastructure.Persistence;

/// <summary>
/// Writes the desktop sync change journal from the tracked changes of a SaveChanges, so journal rows are committed
/// in the same transaction as the business change (no drift). Only changes that matter to a synced copy are recorded
/// (create, rename, move, delete/restore, new content, approval visibility, permissions) and bump the item revision
/// (the sync ETag); worker-side updates such as thumbnails or malware scan status do not.
/// Bulk ExecuteUpdate/ExecuteDelete statements bypass the tracker: callers add rows themselves (see TrashPurger).
/// </summary>
public sealed class ChangeJournalInterceptor(ICurrentUser currentUser, TimeProvider clock) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } ctx) await RecordAsync(ctx, ct);
        return await base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } ctx) RecordAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    private async Task RecordAsync(DbContext ctx, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = new List<ChangeJournalEntry>();
        ChangeJournalEntry Row(Guid ws, Guid id, ChangeItemType type, ChangeOp op) => new()
        {
            WorkspaceId = ws, ItemId = id, ItemType = type, Op = op, ModifiedAt = now, OccurredAt = now,
            ModifiedBy = currentUser.UserId, DeviceId = currentUser.DeviceId,
        };

        foreach (var e in ctx.ChangeTracker.Entries<StoredFile>().ToList())
        {
            var f = e.Entity;
            var ops = new List<ChangeOp>();
            switch (e.State)
            {
                case EntityState.Added when !f.IsDeleted: ops.Add(ChangeOp.Create); break;
                case EntityState.Deleted: ops.Add(ChangeOp.Purge); break;
                case EntityState.Modified:
                    if (Changed(e, nameof(StoredFile.IsDeleted))) ops.Add(f.IsDeleted ? ChangeOp.Delete : ChangeOp.Restore);
                    else if (!f.IsDeleted)
                    {
                        if (Changed(e, nameof(StoredFile.FolderId))) ops.Add(ChangeOp.Move);
                        else if (Changed(e, nameof(StoredFile.Name))) ops.Add(ChangeOp.Rename);
                        if (Changed(e, nameof(StoredFile.CurrentVersionId))) ops.Add(ChangeOp.UpdateContent);
                        if (Changed(e, nameof(StoredFile.ApprovalStatus))) ops.Add(ChangeOp.UpdateMeta);
                    }
                    break;
            }
            if (ops.Count == 0) continue;
            if (e.State == EntityState.Modified) f.Revision++;
            foreach (var op in ops)
            {
                var r = Row(f.WorkspaceId, f.Id, ChangeItemType.File, op);
                r.ParentId = f.FolderId; r.Name = f.Name; r.VersionId = f.CurrentVersionId; r.Revision = f.Revision;
                r.SizeBytes = f.SizeBytes; r.ContentHash = f.ChecksumSha256;
                rows.Add(r);
            }
        }

        foreach (var e in ctx.ChangeTracker.Entries<Folder>().ToList())
        {
            var f = e.Entity;
            var ops = new List<ChangeOp>();
            switch (e.State)
            {
                case EntityState.Added when !f.IsDeleted: ops.Add(ChangeOp.Create); break;
                case EntityState.Deleted: ops.Add(ChangeOp.Purge); break;
                case EntityState.Modified:
                    if (Changed(e, nameof(Folder.IsDeleted))) ops.Add(f.IsDeleted ? ChangeOp.Delete : ChangeOp.Restore);
                    else if (!f.IsDeleted)
                    {
                        if (Changed(e, nameof(Folder.ParentId))) ops.Add(ChangeOp.Move);
                        else if (Changed(e, nameof(Folder.Name))) ops.Add(ChangeOp.Rename);
                        if (Changed(e, nameof(Folder.BreakInheritance))) ops.Add(ChangeOp.PermissionChanged);
                    }
                    break;
            }
            if (ops.Count == 0) continue;
            if (e.State == EntityState.Modified) f.Revision++;
            foreach (var op in ops)
            {
                var r = Row(f.WorkspaceId, f.Id, ChangeItemType.Folder, op);
                r.ParentId = f.ParentId; r.Name = f.Name; r.Path = f.Path; r.Revision = f.Revision;
                rows.Add(r);
            }
        }

        // A deleted workspace removes the whole sync root.
        foreach (var e in ctx.ChangeTracker.Entries<Workspace>().Where(x => x.State == EntityState.Deleted))
            rows.Add(Row(e.Entity.Id, e.Entity.Id, ChangeItemType.Workspace, ChangeOp.Purge));

        // Permission changes: the client re-checks access to the item (or the whole root for a workspace).
        var grants = new List<(ResourceType Type, Guid Id)>();
        foreach (var e in ctx.ChangeTracker.Entries<Share>().Where(x => x.State is EntityState.Added or EntityState.Deleted or EntityState.Modified))
            grants.Add((e.Entity.ResourceType, e.Entity.ResourceId));
        foreach (var e in ctx.ChangeTracker.Entries<ResourcePermission>().Where(x => x.State is EntityState.Added or EntityState.Deleted or EntityState.Modified))
            grants.Add((e.Entity.ResourceType, e.Entity.ResourceId));
        foreach (var e in ctx.ChangeTracker.Entries<WorkspaceMember>().Where(x => x.State is EntityState.Added or EntityState.Deleted or EntityState.Modified))
            grants.Add((ResourceType.Workspace, e.Entity.WorkspaceId));
        foreach (var (type, id) in grants.Distinct())
        {
            var ws = await WorkspaceOfAsync(ctx, type, id, ct);
            if (ws is null) continue;
            var r = Row(ws.Value, id, type switch { ResourceType.File => ChangeItemType.File, ResourceType.Folder => ChangeItemType.Folder, _ => ChangeItemType.Workspace },
                ChangeOp.PermissionChanged);
            rows.Add(r);
        }

        if (rows.Count > 0) ctx.Set<ChangeJournalEntry>().AddRange(rows);
    }

    private static bool Changed<T>(EntityEntry<T> e, string property) where T : class
    {
        var p = e.Property(property);
        return p.IsModified && !Equals(p.OriginalValue, p.CurrentValue);
    }

    private static async Task<Guid?> WorkspaceOfAsync(DbContext ctx, ResourceType type, Guid id, CancellationToken ct)
    {
        switch (type)
        {
            case ResourceType.Workspace:
                return id;
            case ResourceType.Folder:
                var tf = ctx.ChangeTracker.Entries<Folder>().FirstOrDefault(x => x.Entity.Id == id)?.Entity;
                return tf?.WorkspaceId ?? await ctx.Set<Folder>().AsNoTracking().IgnoreQueryFilters().Where(f => f.Id == id).Select(f => (Guid?)f.WorkspaceId).FirstOrDefaultAsync(ct);
            default:
                var tfi = ctx.ChangeTracker.Entries<StoredFile>().FirstOrDefault(x => x.Entity.Id == id)?.Entity;
                return tfi?.WorkspaceId ?? await ctx.Set<StoredFile>().AsNoTracking().IgnoreQueryFilters().Where(f => f.Id == id).Select(f => (Guid?)f.WorkspaceId).FirstOrDefaultAsync(ct);
        }
    }
}
