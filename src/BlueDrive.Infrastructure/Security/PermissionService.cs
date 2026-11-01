using BlueDrive.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BlueDrive.Infrastructure.Security;

/// <summary>Resolves functional (RBAC) permissions of a user from roles, cached for 5 minutes.</summary>
public sealed class PermissionService(IAppDbContext db, IMemoryCache cache) : IPermissionService
{
    private const string VersionKey = "perm:version";

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var version = cache.GetOrCreate(VersionKey, _ => Guid.NewGuid());
        var key = $"perm:{version}:{userId}";
        if (cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null) return cached;

        var codes = await (from ur in db.UserRoles
                           join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                           where ur.UserId == userId
                           select rp.PermissionCode).Distinct().ToListAsync(ct);
        IReadOnlySet<string> set = codes.ToHashSet(StringComparer.Ordinal);
        cache.Set(key, set, TimeSpan.FromMinutes(5));
        return set;
    }

    public async Task<bool> HasAsync(Guid userId, string permission, CancellationToken ct = default)
        => (await GetPermissionsAsync(userId, ct)).Contains(permission);

    /// <summary>Any role/permission change invalidates every cached set (cheap: bumps a version).</summary>
    public void Invalidate(Guid? userId = null) => cache.Set(VersionKey, Guid.NewGuid());
}
