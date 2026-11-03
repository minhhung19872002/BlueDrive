using System.Security.Cryptography;
using System.Text;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Auth;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sync;

/// <summary>OAuth 2.0 error (RFC 6749 §5.2): returned as 400 { error, error_description }.</summary>
public sealed class OAuthException(string error, string description) : AppException(description, 400)
{
    public string Error { get; } = error;
}

public static class SyncClients
{
    /// <summary>The only public client: BlueDrive Desktop (no secret, PKCE mandatory, loopback redirect).</summary>
    public const string DesktopClientId = "bluedrive-desktop";

    /// <summary>RFC 8252 §7.3: loopback IP redirect with any port (and the "localhost" alias).</summary>
    public static bool IsLoopbackRedirect(string redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && !u.IsDefaultPort && u.Port > 1023
        && (u.Host == "127.0.0.1" || u.Host == "[::1]" || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment);

    public static string S256(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// ---------------------------------------------------------------- authorize (web page, signed-in user)

public sealed record AuthorizeDesktopCommand(string ClientId, string RedirectUri, string CodeChallenge, string CodeChallengeMethod, string? State, string? DeviceName)
    : IRequest<AuthorizeDesktopResult>;

public sealed record AuthorizeDesktopResult(string RedirectUrl);

public sealed class AuthorizeDesktopHandler(IAppDbContext db, ICurrentUser me, ITokenService tokens, SyncPolicyService policy, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<AuthorizeDesktopCommand, AuthorizeDesktopResult>
{
    public async Task<AuthorizeDesktopResult> Handle(AuthorizeDesktopCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (cmd.ClientId != SyncClients.DesktopClientId) throw new AppException("Ứng dụng yêu cầu đăng nhập không hợp lệ.");
        if (!SyncClients.IsLoopbackRedirect(cmd.RedirectUri)) throw new AppException("Địa chỉ quay lại không hợp lệ (chỉ chấp nhận ứng dụng trên chính máy này).");
        if (cmd.CodeChallengeMethod != "S256" || cmd.CodeChallenge.Length is < 43 or > 128) throw new AppException("Yêu cầu đăng nhập thiếu mã PKCE hợp lệ.");
        await policy.EnsureAllowedAsync(uid, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var code = tokens.GenerateOpaqueToken(32);
        db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCode
        {
            CodeHash = tokens.HashToken(code), UserId = uid, ClientId = cmd.ClientId, RedirectUri = cmd.RedirectUri, CodeChallenge = cmd.CodeChallenge,
            DeviceName = cmd.DeviceName?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 200)] : null, CreatedAt = now, ExpiresAt = now.AddMinutes(2),
        });
        await db.OAuthAuthorizationCodes.Where(c => c.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Login, Detail: new { step = "desktop_authorize", device = cmd.DeviceName, source = "desktop_sync" }), ct);
        var sep = cmd.RedirectUri.Contains('?') ? '&' : '?';
        var url = $"{cmd.RedirectUri}{sep}code={Uri.EscapeDataString(code)}" + (string.IsNullOrEmpty(cmd.State) ? "" : $"&state={Uri.EscapeDataString(cmd.State)}");
        return new AuthorizeDesktopResult(url);
    }
}

// ---------------------------------------------------------------- token (anonymous, the desktop app)

public sealed record DesktopTokenCommand(
    string GrantType, string? Code, string? CodeVerifier, string? RedirectUri, string? ClientId, string? RefreshToken,
    string? DeviceName, string? DeviceOs, string? AppVersion, string? MachineId) : IRequest<DesktopTokenResult>;

/// <summary>OAuth token response (snake_case on the wire) plus the device registration.</summary>
public sealed record DesktopTokenResult(
    string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, DateTime RefreshTokenExpiresAt,
    Guid DeviceId, string? DeviceSecret, UserProfileDto User);

public sealed class DesktopTokenHandler(
    IAppDbContext db, ITokenService tokens, ISender mediator, SyncPolicyService policy, IPermissionService perms, ISensitiveDataProtector pii,
    IAuditLogger audit, ICurrentUser current, TimeProvider clock) : IRequestHandler<DesktopTokenCommand, DesktopTokenResult>
{
    public async Task<DesktopTokenResult> Handle(DesktopTokenCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (cmd.GrantType == "refresh_token")
        {
            if (string.IsNullOrWhiteSpace(cmd.RefreshToken)) throw new OAuthException("invalid_request", "Thiếu refresh_token.");
            var hash = tokens.HashToken(cmd.RefreshToken);
            var device = await db.RefreshTokens.Where(t => t.TokenHash == hash)
                .Join(db.UserSessions, t => t.SessionId, s => s.Id, (t, s) => s.DeviceId)
                .Join(db.Devices, d => d, d => d.Id, (_, d) => d).FirstOrDefaultAsync(ct);
            if (device is null) throw new OAuthException("invalid_grant", "Phiên đăng nhập của thiết bị không hợp lệ. Vui lòng đăng nhập lại.");
            if (device.Status is DeviceStatus.Revoked or DeviceStatus.Wiped)
                throw new OAuthException("invalid_grant", "Thiết bị này đã bị thu hồi quyền truy cập. Vui lòng đăng nhập lại.");
            AuthResult refreshed;
            try { refreshed = await mediator.Send(new RefreshCommand(cmd.RefreshToken), ct); }
            catch (UnauthorizedException ex) { throw new OAuthException("invalid_grant", ex.Message); }
            await policy.EnsureAllowedAsync(device.UserId, ct);
            device.LastSeenAt = now;
            device.LastIp = current.Ip;
            await db.SaveChangesAsync(ct);
            return Result(refreshed, device.Id, null, now);
        }

        if (cmd.GrantType != "authorization_code") throw new OAuthException("unsupported_grant_type", "Kiểu cấp quyền không được hỗ trợ.");
        if (string.IsNullOrWhiteSpace(cmd.Code) || string.IsNullOrWhiteSpace(cmd.CodeVerifier) || string.IsNullOrWhiteSpace(cmd.RedirectUri))
            throw new OAuthException("invalid_request", "Thiếu code, code_verifier hoặc redirect_uri.");
        var codeHash = tokens.HashToken(cmd.Code);
        var code = await db.OAuthAuthorizationCodes.FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);
        if (code is null || code.UsedAt is not null || code.ExpiresAt < now)
            throw new OAuthException("invalid_grant", "Mã đăng nhập không hợp lệ hoặc đã hết hạn. Vui lòng đăng nhập lại.");
        code.UsedAt = now; // single use, even when the checks below fail
        await db.SaveChangesAsync(ct);
        if (code.ClientId != (cmd.ClientId ?? SyncClients.DesktopClientId) || code.RedirectUri != cmd.RedirectUri)
            throw new OAuthException("invalid_grant", "Mã đăng nhập không thuộc ứng dụng này.");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(SyncClients.S256(cmd.CodeVerifier)), Encoding.ASCII.GetBytes(code.CodeChallenge)))
            throw new OAuthException("invalid_grant", "Mã xác minh PKCE không khớp.");

        var user = await db.Users.FirstAsync(u => u.Id == code.UserId, ct);
        if (!user.IsActive || user.IsLocked || user.LockoutEnd > now) throw new OAuthException("invalid_grant", "Tài khoản đã bị khóa hoặc vô hiệu hóa.");
        await policy.EnsureAllowedAsync(user.Id, ct);

        // One device per installation: re-login on the same machine reuses its active registration.
        var machineHash = string.IsNullOrWhiteSpace(cmd.MachineId) ? null : tokens.HashToken(cmd.MachineId.Trim());
        var dev = machineHash is null ? null
            : await db.Devices.FirstOrDefaultAsync(d => d.UserId == user.Id && d.MachineIdHash == machineHash && d.Status == DeviceStatus.Active, ct);
        if (dev is null)
        {
            dev = new SyncDevice { UserId = user.Id, MachineIdHash = machineHash, CreatedAt = now };
            db.Devices.Add(dev);
        }
        dev.Name = Trim(cmd.DeviceName ?? code.DeviceName, 200) ?? "Máy tính";
        dev.Os = Trim(cmd.DeviceOs, 200) ?? "";
        dev.AppVersion = Trim(cmd.AppVersion, 50) ?? "";
        dev.LastSeenAt = now;
        dev.LastIp = current.Ip;
        var secret = tokens.GenerateOpaqueToken(32);
        dev.SecretProtected = pii.Protect(secret);

        var session = new UserSession
        {
            UserId = user.Id, DeviceId = dev.Id, Device = $"BlueDrive Desktop – {dev.Name}", Ip = current.Ip, UserAgent = Trim(current.UserAgent, 500),
            CreatedAt = now, LastSeenAt = now,
        };
        db.UserSessions.Add(session);
        user.LastLoginAt = now;
        var issued = await tokens.IssueAsync(user, session, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Login, Detail: new { device = session.Device, sessionId = session.Id, deviceId = dev.Id, source = "desktop_sync" },
            UserId: user.Id, Username: user.Username), ct);
        var profile = await AuthHelpers.BuildProfileAsync(db, perms, pii, user.Id, ct);
        return Result(new AuthResult(issued.AccessToken, issued.AccessTokenExpiresAt, issued.RefreshToken, issued.RefreshTokenExpiresAt, profile), dev.Id, secret, now);
    }

    private static DesktopTokenResult Result(AuthResult r, Guid deviceId, string? secret, DateTime now) =>
        new(r.AccessToken, "Bearer", (int)Math.Max(1, (r.AccessTokenExpiresAt - now).TotalSeconds), r.RefreshToken, r.RefreshTokenExpiresAt, deviceId, secret, r.User);

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];
}
