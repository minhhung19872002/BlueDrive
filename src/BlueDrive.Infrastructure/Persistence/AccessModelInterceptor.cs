using BlueDrive.Application.Features.Access;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.Infrastructure.Persistence;

/// <summary>
/// Keeps the new permission model (securables, ACL scopes, memberships) in step with every change of the current model, so the
/// permission engine never reads stale data (Perm-Phase 3). Frequent changes are applied in the same save: a new folder or file gets its
/// node, inheriting its parent's scope; a rename or a deleted file updates its node. Changes that move rights around — grants, shares,
/// break inheritance, moves, workspace members, folders deleted or restored — project the workspace again right after the save
/// (<see cref="PermissionProjection.ProjectAsync"/>, same transaction when there is one); group, role and unit changes recompute the
/// memberships of the users concerned.
/// </summary>
public sealed class AccessModelInterceptor(IServiceProvider services) : SaveChangesInterceptor, IAccessModelSync
{
    /// <summary>Workspaces projected in this unit of work: projected once more when the request ends (bulk updates after the save).</summary>
    private readonly HashSet<Guid> _touched = [];
    private readonly HashSet<Guid> _workspaces = [];
    private readonly HashSet<Guid> _users = [];
    private bool _principals;
    private bool _running;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } ctx && !_running) await CollectAsync(ctx, ct);
        return await base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } ctx && !_running) CollectAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await base.SavedChangesAsync(eventData, result, ct);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _workspaces.Clear();
        _users.Clear();
        _principals = false;
        base.SaveChangesFailed(eventData);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_running || (_workspaces.Count == 0 && _users.Count == 0 && !_principals)) return;
        var workspaces = _workspaces.ToList();
        _touched.UnionWith(workspaces);
        var users = _users.ToList();
        _workspaces.Clear();
        _users.Clear();
        _principals = false;
        _running = true;
        try { await services.GetRequiredService<PermissionProjection>().ProjectAsync(workspaces, users, ct); }
        finally { _running = false; }
    }

    public async Task FlushTouchedAsync(CancellationToken ct)
    {
        if (_running || _touched.Count == 0) return;
        var workspaces = _touched.ToList();
        _touched.Clear();
        _running = true;
        try { await services.GetRequiredService<PermissionProjection>().ProjectAsync(workspaces, [], ct); }
        finally { _running = false; }
    }

    private static bool Changed(EntityEntry e, string property) => e.Property(property).IsModified;

    private async Task CollectAsync(DbContext ctx, CancellationToken ct)
    {
        var nodes = ctx.Set<Securable>();

        foreach (var e in ctx.ChangeTracker.Entries<Workspace>().ToList())
            if (e.State is EntityState.Added or EntityState.Deleted
                || (e.State == EntityState.Modified && (Changed(e, nameof(Workspace.OwnerId)) || Changed(e, nameof(Workspace.Name)) || Changed(e, nameof(Workspace.Type)))))
                _workspaces.Add(e.Entity.Id);

        foreach (var e in ctx.ChangeTracker.Entries<Folder>().ToList())
        {
            var f = e.Entity;
            if (e.State == EntityState.Added && !f.IsDeleted && await AddNodeAsync(ctx, f.Id, SecurableKind.Folder, f.WorkspaceId, f.ParentId, f.Name, ct)) continue;
            if (e.State is EntityState.Added or EntityState.Deleted
                || (e.State == EntityState.Modified && (Changed(e, nameof(Folder.ParentId)) || Changed(e, nameof(Folder.WorkspaceId)) || Changed(e, nameof(Folder.IsDeleted))
                                                        || Changed(e, nameof(Folder.BreakInheritance)))))
            {
                _workspaces.Add(f.WorkspaceId);
                if (e.State == EntityState.Modified && e.Property(nameof(Folder.WorkspaceId)).OriginalValue is Guid before) _workspaces.Add(before);
            }
            else if (e.State == EntityState.Modified && Changed(e, nameof(Folder.Name)) && await nodes.FindAsync([f.Id], ct) is { } node) node.Name = f.Name;
        }

        foreach (var e in ctx.ChangeTracker.Entries<StoredFile>().ToList())
        {
            var f = e.Entity;
            if (e.State == EntityState.Added && !f.IsDeleted && await AddNodeAsync(ctx, f.Id, SecurableKind.File, f.WorkspaceId, f.FolderId, f.Name, ct)) continue;
            if (e.State == EntityState.Modified && !Changed(e, nameof(StoredFile.FolderId)) && !Changed(e, nameof(StoredFile.WorkspaceId))
                && !Changed(e, nameof(StoredFile.BreakInheritance)) && await nodes.FindAsync([f.Id], ct) is { } node)
            {
                if (Changed(e, nameof(StoredFile.Name))) node.Name = f.Name;
                if (Changed(e, nameof(StoredFile.IsDeleted)))
                {
                    if (f.IsDeleted) node.IsDeleted = true;
                    else _workspaces.Add(f.WorkspaceId); // restored: maybe under a folder that was restored too
                }
                continue;
            }
            if (e.State is EntityState.Added or EntityState.Deleted or EntityState.Modified
                && (e.State != EntityState.Modified || Changed(e, nameof(StoredFile.FolderId)) || Changed(e, nameof(StoredFile.WorkspaceId))
                    || Changed(e, nameof(StoredFile.BreakInheritance)) || Changed(e, nameof(StoredFile.IsDeleted))))
            {
                _workspaces.Add(f.WorkspaceId);
                if (e.State == EntityState.Modified && e.Property(nameof(StoredFile.WorkspaceId)).OriginalValue is Guid before) _workspaces.Add(before);
            }
        }

        foreach (var e in ctx.ChangeTracker.Entries<ResourcePermission>().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
            if (await WorkspaceOfAsync(ctx, e.Entity.ResourceType, e.Entity.ResourceId, ct) is { } ws) _workspaces.Add(ws);
        foreach (var e in ctx.ChangeTracker.Entries<Share>().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
            if (await WorkspaceOfAsync(ctx, e.Entity.ResourceType, e.Entity.ResourceId, ct) is { } ws) _workspaces.Add(ws);
        foreach (var e in ctx.ChangeTracker.Entries<WorkspaceMember>().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            _workspaces.Add(e.Entity.WorkspaceId);
            _users.Add(e.Entity.UserId);
        }

        foreach (var e in ctx.ChangeTracker.Entries<WorkspaceGroupMember>().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            _workspaces.Add(e.Entity.WorkspaceId);
            // The people of a group or unit (removed now or just added) get their memberships recomputed.
            foreach (var u in await ctx.Set<PrincipalMembership>().AsNoTracking().Where(m => m.PrincipalId == e.Entity.PrincipalId).Select(m => m.UserId).ToListAsync(ct))
                _users.Add(u);
        }

        foreach (var e in ctx.ChangeTracker.Entries<UserGroupMember>().Where(x => x.State is EntityState.Added or EntityState.Deleted).ToList()) _users.Add(e.Entity.UserId);
        foreach (var e in ctx.ChangeTracker.Entries<UserRole>().Where(x => x.State is EntityState.Added or EntityState.Deleted).ToList()) _users.Add(e.Entity.UserId);
        foreach (var e in ctx.ChangeTracker.Entries<User>().ToList())
            if (e.State == EntityState.Added || (e.State == EntityState.Modified && (Changed(e, nameof(User.OrganizationId)) || Changed(e, nameof(User.FullName)))))
                _users.Add(e.Entity.Id);
        if (ctx.ChangeTracker.Entries<UserGroup>().Any(x => x.State is EntityState.Added or EntityState.Deleted or EntityState.Modified)
            || ctx.ChangeTracker.Entries<Role>().Any(x => x.State is EntityState.Added or EntityState.Deleted or EntityState.Modified))
            _principals = true;
        // A unit moved or renamed: everyone in it or below it counts differently.
        foreach (var e in ctx.ChangeTracker.Entries<Organization>().Where(x => x.State is EntityState.Added or EntityState.Deleted or EntityState.Modified).ToList())
        {
            _principals = true;
            var path = e.Entity.Path;
            var orgIds = await ctx.Set<Organization>().AsNoTracking().Where(o => o.Path.StartsWith(path)).Select(o => o.Id).ToListAsync(ct);
            foreach (var u in await ctx.Set<User>().AsNoTracking().Where(u => u.OrganizationId != null && orgIds.Contains(u.OrganizationId.Value)).Select(u => u.Id).ToListAsync(ct))
                _users.Add(u);
        }
    }

    /// <summary>A new folder or file inherits its parent's scope; false when the parent's node is not known (the workspace is projected instead).</summary>
    private static async Task<bool> AddNodeAsync(DbContext ctx, Guid id, SecurableKind kind, Guid workspaceId, Guid? parentId, string name, CancellationToken ct)
    {
        var parent = await ctx.Set<Securable>().FindAsync([parentId ?? PermissionProjection.LibraryOf(workspaceId)], ct);
        if (parent is null || parent.IsDeleted) return false;
        if (await ctx.Set<Securable>().FindAsync([id], ct) is not null) return true;
        ctx.Set<Securable>().Add(new Securable
        {
            Id = id, Kind = kind, ParentId = parent.Id, LibraryId = parent.LibraryId ?? parent.Id, SpaceId = parent.SpaceId,
            PersonalDriveId = parent.PersonalDriveId, OwnerUserId = parent.OwnerUserId, Path = $"{parent.Path}.{PermissionProjection.Label(id)}", Name = name,
            AclScopeId = parent.AclScopeId,
        });
        return true;
    }

    private static async Task<Guid?> WorkspaceOfAsync(DbContext ctx, ResourceType type, Guid id, CancellationToken ct) => type switch
    {
        ResourceType.Workspace => id,
        ResourceType.Folder => ctx.Set<Folder>().Local.FirstOrDefault(f => f.Id == id)?.WorkspaceId
                               ?? await ctx.Set<Folder>().AsNoTracking().Where(f => f.Id == id).Select(f => (Guid?)f.WorkspaceId).FirstOrDefaultAsync(ct),
        _ => ctx.Set<StoredFile>().Local.FirstOrDefault(f => f.Id == id)?.WorkspaceId
             ?? await ctx.Set<StoredFile>().AsNoTracking().Where(f => f.Id == id).Select(f => (Guid?)f.WorkspaceId).FirstOrDefaultAsync(ct),
    };
}
