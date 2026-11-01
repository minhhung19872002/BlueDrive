using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.Infrastructure.Security;

/// <summary>
/// Access-token blacklist: revoked sessions are cached until the token would expire anyway; other checks hit the DB
/// at most every 20 seconds per session. Also enforces the idle timeout and bumps last_seen_at.
/// </summary>
public sealed class SessionValidator(IMemoryCache cache, IServiceScopeFactory scopes, TimeProvider clock) : ISessionValidator
{
    private static string RevokedKey(Guid sid) => $"session:revoked:{sid}";
    private static string OkKey(Guid sid) => $"session:ok:{sid}";
    private static string UserKey(Guid uid) => $"session:user:{uid}";

    public async Task<bool> IsValidAsync(Guid userId, Guid sessionId, int securityStamp, CancellationToken ct = default)
    {
        if (cache.TryGetValue(RevokedKey(sessionId), out _)) return false;
        var userVersion = cache.GetOrCreate(UserKey(userId), _ => Guid.NewGuid());
        if (cache.TryGetValue(OkKey(sessionId), out Guid cachedVersion) && cachedVersion == userVersion) return true;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var now = clock.GetUtcNow().UtcDateTime;

        var row = await db.UserSessions.Where(s => s.Id == sessionId && s.UserId == userId)
            .Select(s => new { s.RevokedAt, s.LastSeenAt, s.User!.IsActive, s.User.IsLocked, s.User.LockoutEnd, s.User.SecurityStamp })
            .FirstOrDefaultAsync(ct);
        var idle = await settings.GetAsync(SettingKeys.SessionIdleMinutes, 30, ct);
        var valid = row is not null && row.RevokedAt is null && row.IsActive && !row.IsLocked
                    && (row.LockoutEnd is null || row.LockoutEnd < now) && row.SecurityStamp == securityStamp
                    && (idle <= 0 || now - row.LastSeenAt < TimeSpan.FromMinutes(idle));
        if (!valid)
        {
            MarkRevoked(sessionId, TimeSpan.FromMinutes(20));
            return false;
        }
        if (now - row!.LastSeenAt > TimeSpan.FromMinutes(1))
            await db.UserSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), ct);
        cache.Set(OkKey(sessionId), userVersion, TimeSpan.FromSeconds(20));
        return true;
    }

    public void MarkRevoked(Guid sessionId, TimeSpan forHowLong)
    {
        cache.Set(RevokedKey(sessionId), true, forHowLong);
        cache.Remove(OkKey(sessionId));
    }

    public void InvalidateUser(Guid userId) => cache.Set(UserKey(userId), Guid.NewGuid());
}
