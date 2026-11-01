using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Auth;

public sealed record SessionDto(Guid Id, Guid UserId, string Username, string FullName, string? Device, string? Ip, string? UserAgent,
    DateTime CreatedAt, DateTime LastSeenAt, DateTime? RevokedAt, bool IsCurrent);

public sealed record ListMySessionsQuery(bool IncludeRevoked = false) : IRequest<IReadOnlyList<SessionDto>>;

public sealed class ListMySessionsHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListMySessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<IReadOnlyList<SessionDto>> Handle(ListMySessionsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var sid = me.SessionId;
        return await db.UserSessions.AsNoTracking().Where(s => s.UserId == uid && (q.IncludeRevoked || s.RevokedAt == null))
            .OrderByDescending(s => s.LastSeenAt).Take(100)
            .Select(s => new SessionDto(s.Id, s.UserId, s.User!.Username, s.User.FullName, s.Device, s.Ip, s.UserAgent, s.CreatedAt, s.LastSeenAt, s.RevokedAt, s.Id == sid))
            .ToListAsync(ct);
    }
}

public sealed record ListAllSessionsQuery(Guid? UserId, bool ActiveOnly = true) : PageQuery, IRequest<PagedResult<SessionDto>>;

public sealed class ListAllSessionsHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListAllSessionsQuery, PagedResult<SessionDto>>
{
    public Task<PagedResult<SessionDto>> Handle(ListAllSessionsQuery q, CancellationToken ct)
    {
        var sid = me.SessionId;
        var query = db.UserSessions.AsNoTracking();
        if (q.UserId.HasValue) query = query.Where(s => s.UserId == q.UserId);
        if (q.ActiveOnly) query = query.Where(s => s.RevokedAt == null);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(s => s.User!.Username.Contains(term) || DbFn.Unaccent(s.User.FullName).Contains(term) || (s.Ip != null && s.Ip.Contains(term)));
        }
        return query.OrderByDescending(s => s.LastSeenAt)
            .Select(s => new SessionDto(s.Id, s.UserId, s.User!.Username, s.User.FullName, s.Device, s.Ip, s.UserAgent, s.CreatedAt, s.LastSeenAt, s.RevokedAt, s.Id == sid))
            .ToPagedAsync(q, ct);
    }
}

/// <summary>Revoke one session. Users may revoke their own; session.admin may revoke anyone's.</summary>
public sealed record RevokeSessionCommand(Guid SessionId) : IRequest<Unit>;

public sealed class RevokeSessionHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, SessionRevoker revoker, IAuditLogger audit)
    : IRequestHandler<RevokeSessionCommand, Unit>
{
    public async Task<Unit> Handle(RevokeSessionCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var s = await db.UserSessions.AsNoTracking().Include(x => x.User).FirstOrDefaultAsync(x => x.Id == cmd.SessionId, ct) ?? throw new NotFoundException("Không tìm thấy phiên đăng nhập.");
        if (s.UserId != uid && !await perms.HasAsync(uid, Perm.SessionAdmin, ct)) throw new ForbiddenException();
        await revoker.RevokeAsync([s.Id], ct);
        await audit.LogAsync(new AuditEntry(AuditAction.SessionRevoke, ResourceName: s.User?.Username, Detail: new { sessionId = s.Id, targetUserId = s.UserId, s.Device, s.Ip }), ct);
        return Unit.Value;
    }
}

/// <summary>Sign out every other device of the current user.</summary>
public sealed record RevokeMyOtherSessionsCommand : IRequest<int>;

public sealed class RevokeMyOtherSessionsHandler(ICurrentUser me, SessionRevoker revoker, IAuditLogger audit) : IRequestHandler<RevokeMyOtherSessionsCommand, int>
{
    public async Task<int> Handle(RevokeMyOtherSessionsCommand cmd, CancellationToken ct)
    {
        var count = await revoker.RevokeAllForUserAsync(me.RequireUserId(), me.SessionId ?? Guid.Empty, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.SessionRevoke, Detail: new { scope = "other_sessions", count }), ct);
        return count;
    }
}

/// <summary>Admin: revoke all access of a user (all sessions + outstanding access tokens).</summary>
public sealed record RevokeUserAccessCommand(Guid UserId) : IRequest<int>;

public sealed class RevokeUserAccessHandler(IAppDbContext db, SessionRevoker revoker, IAuditLogger audit) : IRequestHandler<RevokeUserAccessCommand, int>
{
    public async Task<int> Handle(RevokeUserAccessCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        var count = await revoker.RevokeAllForUserAsync(user.Id, null, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.SessionRevoke, ResourceName: user.Username, Detail: new { scope = "all_sessions", targetUserId = user.Id, count }), ct);
        return count;
    }
}
