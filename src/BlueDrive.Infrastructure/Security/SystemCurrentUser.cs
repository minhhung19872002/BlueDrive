using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;

namespace BlueDrive.Infrastructure.Security;

/// <summary>Current user for background jobs (no HTTP context). Can be scoped to act as a user.</summary>
public sealed class SystemCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
    public string? Username { get; set; } = "system";
    public Guid? SessionId => null;
    public string? Ip => null;
    public string? UserAgent => "BlueDrive.Worker";
    public bool IsAuthenticated => UserId.HasValue;
    public Guid RequireUserId() => UserId ?? throw new UnauthorizedException();
}
