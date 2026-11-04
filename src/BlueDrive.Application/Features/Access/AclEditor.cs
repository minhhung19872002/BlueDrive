using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Changes to ACLs in the new model (spec 4.1): break / restore inheritance, grant, revoke. Every change bumps the scope's
/// <see cref="AclScope.AclVersion"/> (cache key) and rewrites its <see cref="ScopeGrant"/> rows in the same save. The subtree of a node is
/// re-pointed with one UPDATE on the ltree path (PostgreSQL), never a recursive walk in code.
/// </summary>
public sealed class AclEditor(IAppDbContext db, TimeProvider clock)
{
    /// <summary>
    /// "Ngắt kế thừa": the node gets its own ACL — a copy of the inherited one (default) or only the people who manage it
    /// (<paramref name="copy"/> = false) — and every node below that used the old scope uses this one.
    /// </summary>
    public async Task BreakInheritanceAsync(Guid nodeId, bool copy, Guid? by, CancellationToken ct)
    {
        var node = await db.Securables.FirstOrDefaultAsync(s => s.Id == nodeId, ct) ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        if (node.HasUniqueAcl) return;
        var oldScope = node.AclScopeId;
        var inherited = await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == oldScope && e.AppliesToChildren).ToListAsync(ct);
        if (!copy)
        {
            var managers = await db.PermissionLevels.AsNoTracking().ToListAsync(ct);
            var managing = managers.Where(l => l.Permissions.Has(AccessRight.ManagePermissions)).Select(l => l.Id).ToHashSet();
            inherited = inherited.Where(e => managing.Contains(e.PermissionLevelId)).ToList();
        }
        var now = clock.GetUtcNow().UtcDateTime;
        db.AclEntries.AddRange(inherited.Select(e => new AclEntry
        {
            ScopeId = node.Id, PrincipalKind = e.PrincipalKind, PrincipalId = e.PrincipalId, PermissionLevelId = e.PermissionLevelId,
            ExpiresAt = e.ExpiresAt, GrantedBy = e.GrantedBy, GrantedAt = e.GrantedAt,
            Source = e.Source == AclSource.Direct ? AclSource.Inherited : e.Source,
        }));
        node.HasUniqueAcl = true;
        node.AclScopeId = node.Id;
        db.AclScopes.Add(new AclScope { ScopeId = node.Id, AclVersion = NewVersion() });
        await db.SaveChangesAsync(ct);
        await RepointSubtreeAsync(node, from: oldScope, to: node.Id, ct);
        await RefreshGrantsAsync(node.Id, ct);
    }

    /// <summary>
    /// "Khôi phục kế thừa": the node's own ACL is deleted and it (with the nodes below that used it) inherits again; with
    /// <paramref name="recursive"/>, every node below with its own ACL too (SharePoint "Delete unique permissions").
    /// </summary>
    public async Task RestoreInheritanceAsync(Guid nodeId, bool recursive, CancellationToken ct)
    {
        var node = await db.Securables.FirstOrDefaultAsync(s => s.Id == nodeId, ct) ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        if (!node.HasUniqueAcl) return;
        if (node.ParentId is not { } parentId) throw new ConflictException("Space và Drive của tôi luôn có quyền riêng.");
        var parentScope = await db.Securables.Where(s => s.Id == parentId).Select(s => s.AclScopeId).FirstAsync(ct);
        var scopes = new List<Guid> { node.Id };
        if (recursive)
            scopes.AddRange(await Subtree(node).Where(s => s.HasUniqueAcl && s.Id != node.Id).Select(s => s.Id).ToListAsync(ct));
        foreach (var scope in scopes) await RepointSubtreeAsync(node, from: scope, to: parentScope, ct);
        await db.AclEntries.Where(e => scopes.Contains(e.ScopeId)).ExecuteDeleteAsync(ct);
        await db.ScopeGrants.Where(g => scopes.Contains(g.ScopeId)).ExecuteDeleteAsync(ct);
        await db.AclScopes.Where(s => scopes.Contains(s.ScopeId)).ExecuteDeleteAsync(ct);
        await db.Securables.Where(s => scopes.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.HasUniqueAcl, false), ct);
        await BumpAsync(parentScope, ct);
    }

    /// <summary>
    /// Grants a level to a principal on the node. Like SharePoint ("sharepoint_scope" mode), a node without its own ACL first breaks
    /// inheritance, copying the inherited entries.
    /// </summary>
    public async Task<AclEntry> GrantAsync(Guid nodeId, PrincipalKind kind, Guid principalId, Guid levelId, DateTime? expiresAt, Guid? by, CancellationToken ct,
        string? message = null)
    {
        var node = await db.Securables.FirstOrDefaultAsync(s => s.Id == nodeId, ct) ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        if (!node.HasUniqueAcl) await BreakInheritanceAsync(nodeId, copy: true, by, ct);
        var entry = new AclEntry
        {
            ScopeId = nodeId, PrincipalKind = kind, PrincipalId = principalId, PermissionLevelId = levelId, ExpiresAt = expiresAt,
            GrantedBy = by, GrantedAt = clock.GetUtcNow().UtcDateTime, Source = AclSource.Direct, Message = message,
        };
        db.AclEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        await BumpAsync(nodeId, ct);
        return entry;
    }

    public async Task RevokeAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await db.AclEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct) ?? throw new NotFoundException("Không tìm thấy quyền.");
        db.AclEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        await BumpAsync(entry.ScopeId, ct);
    }

    // ------------------------------------------------------------------------------------------------ parts

    /// <summary>A principal that no longer exists (a group or role deleted): its entries go, the scopes it was on get new versions.</summary>
    public async Task ForgetPrincipalAsync(Guid principalId, CancellationToken ct)
    {
        var scopes = await db.AclEntries.Where(e => e.PrincipalId == principalId).Select(e => e.ScopeId).Distinct().ToListAsync(ct);
        await db.AclEntries.Where(e => e.PrincipalId == principalId).ExecuteDeleteAsync(ct);
        foreach (var scope in scopes) await BumpAsync(scope, ct);
    }

    /// <summary>The entries of a scope were changed directly: new version, grants rewritten.</summary>
    public Task TouchAsync(Guid scopeId, CancellationToken ct) => BumpAsync(scopeId, ct);

    private IQueryable<Securable> Subtree(Securable node) =>
        db.Securables.Where(s => s.Id == node.Id || s.Path.StartsWith(node.Path + "."));

    private async Task RepointSubtreeAsync(Securable node, Guid from, Guid to, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE securables SET acl_scope_id = {to} WHERE path <@ {node.Path}::ltree AND acl_scope_id = {from}", ct);
        else
            await Subtree(node).Where(s => s.AclScopeId == from).ExecuteUpdateAsync(u => u.SetProperty(s => s.AclScopeId, to), ct);
    }

    private async Task BumpAsync(Guid scopeId, CancellationToken ct)
    {
        var scope = await db.AclScopes.FirstOrDefaultAsync(s => s.ScopeId == scopeId, ct);
        if (scope is null) db.AclScopes.Add(new AclScope { ScopeId = scopeId, AclVersion = NewVersion() });
        else scope.AclVersion = Math.Max(scope.AclVersion + 1, NewVersion());
        await db.SaveChangesAsync(ct);
        await RefreshGrantsAsync(scopeId, ct);
    }

    /// <summary>scope_grants of one scope = OR of its live entries per principal.</summary>
    public async Task RefreshGrantsAsync(Guid scopeId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var live = await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == scopeId && e.AppliesToChildren && (e.ExpiresAt == null || e.ExpiresAt > now))
            .Join(db.PermissionLevels, e => e.PermissionLevelId, l => l.Id, (e, l) => new { e.PrincipalId, l.Permissions }).ToListAsync(ct);
        var wanted = live.GroupBy(x => x.PrincipalId).ToDictionary(g => g.Key, g => g.Aggregate(AccessRight.None, (m, x) => m | x.Permissions));
        var rows = await db.ScopeGrants.Where(g => g.ScopeId == scopeId).ToListAsync(ct);
        foreach (var row in rows)
            if (wanted.Remove(row.PrincipalId, out var rights)) row.Permissions = rights;
            else db.ScopeGrants.Remove(row);
        db.ScopeGrants.AddRange(wanted.Select(w => new ScopeGrant { ScopeId = scopeId, PrincipalId = w.Key, Permissions = w.Value }));
        await db.SaveChangesAsync(ct);
    }

    private long NewVersion() => AclVersions.Next(clock);
}

/// <summary>
/// ACL versions (cache keys of the permission engine) only grow and are never handed out twice — a scope deleted and created again must
/// not get a version a cache still holds: the clock's ticks, or one more than the last version given in this process.
/// </summary>
public static class AclVersions
{
    private static long _last;

    public static long Next(TimeProvider clock)
    {
        var ticks = clock.GetUtcNow().UtcTicks;
        while (true)
        {
            var last = Interlocked.Read(ref _last);
            var next = Math.Max(ticks, last + 1);
            if (Interlocked.CompareExchange(ref _last, next, last) == last) return next;
        }
    }
}
