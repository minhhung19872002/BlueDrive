using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Auth;

/// <summary>Revokes sessions and their refresh tokens; access tokens are blacklisted until they expire.</summary>
public sealed class SessionRevoker(IAppDbContext db, ISessionValidator validator, ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(20);

    public async Task<int> RevokeAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
    {
        if (sessionIds.Count == 0) return 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var by = currentUser.UserId;
        var count = await db.UserSessions.Where(s => sessionIds.Contains(s.Id) && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedBy, by), ct);
        await db.RefreshTokens.Where(t => sessionIds.Contains(t.SessionId) && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now), ct);
        foreach (var id in sessionIds) validator.MarkRevoked(id, AccessTokenLifetime);
        return count;
    }

    /// <summary>"Thu hồi toàn bộ quyền truy cập": every session + bump the security stamp.</summary>
    public async Task<int> RevokeAllForUserAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct)
    {
        var ids = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != exceptSessionId)
            .Select(s => s.Id).ToListAsync(ct);
        var count = await RevokeAsync(ids, ct);
        if (exceptSessionId is null)
        {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, x => x.SecurityStamp + 1), ct);
            validator.InvalidateUser(userId);
        }
        return count;
    }
}
