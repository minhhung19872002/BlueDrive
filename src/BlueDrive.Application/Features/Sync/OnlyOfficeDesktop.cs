using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Auth;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlueDrive.Application.Features.Sync;

/// <summary>
/// Co-editing from ONLYOFFICE Desktop Editors (spec 13A). BlueDrive Desktop (already signed in) asks for a one-time ticket; ONLYOFFICE Desktop
/// Editors opens "/oo-desktop/open?ticket=…", the web page redeems it for a normal web session and opens the document editor — the user does not
/// sign in again. Tickets reuse the OAuth code table: client "onlyoffice-desktop", 2 minutes, single use, hashed.
/// </summary>
public static class OnlyOfficeDesktop
{
    public const string ClientId = "onlyoffice-desktop";
    public const string ProviderId = "bluedrive";
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
}

public sealed record OnlyOfficeEditorLinkDto(string Url, string BrowserUrl, bool CanEdit);

public sealed record CreateEditorTicketCommand(Guid FileId) : IRequest<OnlyOfficeEditorLinkDto>;

public sealed class CreateEditorTicketHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, ITokenService tokens,
    IConfiguration config, TimeProvider clock) : IRequestHandler<CreateEditorTicketCommand, OnlyOfficeEditorLinkDto>
{
    public async Task<OnlyOfficeEditorLinkDto> Handle(CreateEditorTicketCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var access = await auth.GetAccessAsync(uid, ResourceType.File, cmd.FileId, ct);
        if (!access.CanView) throw new NotFoundException("Không tìm thấy tài liệu hoặc bạn không có quyền truy cập.");
        var ticket = tokens.GenerateOpaqueToken(32);
        var now = clock.GetUtcNow().UtcDateTime;
        db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCode
        {
            CodeHash = tokens.HashToken(ticket), UserId = uid, ClientId = OnlyOfficeDesktop.ClientId, RedirectUri = $"/files/{cmd.FileId}/edit",
            CodeChallenge = "", DeviceName = me.DeviceId?.ToString(), CreatedAt = now, ExpiresAt = now + OnlyOfficeDesktop.TicketLifetime,
        });
        await db.SaveChangesAsync(ct);
        var baseUrl = (config["App:PublicBaseUrl"] ?? "").TrimEnd('/');
        return new OnlyOfficeEditorLinkDto($"{baseUrl}/oo-desktop/open?ticket={Uri.EscapeDataString(ticket)}", $"{baseUrl}/files/{cmd.FileId}/edit",
            access.CanEdit && !access.WorkspaceLocked);
    }
}

public sealed record RedeemEditorTicketCommand(string Ticket) : IRequest<RedeemEditorTicketResult>;

public sealed record RedeemEditorTicketResult(AuthResult Auth, string Next);

public sealed class RedeemEditorTicketHandler(IAppDbContext db, ICurrentUser caller, ITokenService tokens, IPermissionService perms,
    ISensitiveDataProtector pii, IAuditLogger audit, TimeProvider clock) : IRequestHandler<RedeemEditorTicketCommand, RedeemEditorTicketResult>
{
    public async Task<RedeemEditorTicketResult> Handle(RedeemEditorTicketCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = tokens.HashToken(cmd.Ticket ?? "");
        var code = await db.OAuthAuthorizationCodes.FirstOrDefaultAsync(c => c.CodeHash == hash && c.ClientId == OnlyOfficeDesktop.ClientId, ct);
        if (code is null || code.UsedAt is not null || code.ExpiresAt < now)
            throw new UnauthorizedException("Liên kết mở tài liệu đã hết hạn hoặc đã được dùng. Hãy mở lại từ BlueDrive Desktop.");
        code.UsedAt = now;
        var user = await db.Users.FirstAsync(u => u.Id == code.UserId, ct);
        if (!user.IsActive || user.IsLocked || user.LockoutEnd > now) throw new UnauthorizedException("Tài khoản đã bị khóa hoặc vô hiệu hóa.");
        var session = new UserSession
        {
            UserId = user.Id, Ip = caller.Ip, UserAgent = caller.UserAgent is { Length: > 500 } ua ? ua[..500] : caller.UserAgent,
            Device = "ONLYOFFICE Desktop Editors", CreatedAt = now, LastSeenAt = now,
        };
        db.UserSessions.Add(session);
        var issued = await tokens.IssueAsync(user, session, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Login,
            Detail: new { source = "onlyoffice_desktop", device = session.Device, sessionId = session.Id, viaDevice = code.DeviceName }, UserId: user.Id,
            Username: user.Username), ct);
        var profile = await AuthHelpers.BuildProfileAsync(db, perms, pii, user.Id, ct);
        return new RedeemEditorTicketResult(new AuthResult(issued.AccessToken, issued.AccessTokenExpiresAt, issued.RefreshToken, issued.RefreshTokenExpiresAt, profile),
            code.RedirectUri);
    }
}
