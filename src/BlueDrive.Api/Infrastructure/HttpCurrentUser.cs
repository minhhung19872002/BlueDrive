using System.Security.Claims;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;

namespace BlueDrive.Api.Infrastructure;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue("sub") ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? Username => Principal?.FindFirstValue("preferred_username") ?? Principal?.Identity?.Name;
    public Guid? SessionId => Guid.TryParse(Principal?.FindFirstValue("sid"), out var id) ? id : null;
    public string? Ip => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && UserId.HasValue;
    public Guid RequireUserId() => UserId ?? throw new UnauthorizedException();
}
