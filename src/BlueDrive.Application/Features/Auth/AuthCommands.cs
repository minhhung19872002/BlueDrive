using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlueDrive.Application.Features.Auth;

public sealed record UserProfileDto(
    Guid Id, string Username, string FullName, string? Email, string? Phone, Guid? OrganizationId, string? OrganizationName,
    string? Position, Guid? AvatarFileId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, bool MustChangePassword,
    long QuotaBytes, long UsedBytes, DateTime? LastLoginAt, Guid? PersonalWorkspaceId);

public sealed record AuthResult(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt, UserProfileDto User);

public static class AuthHelpers
{
    public static string DescribeDevice(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "Không xác định";
        var os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
            : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : "Khác";
        var app = ua.Contains("BlueDriveMobile") ? "Ứng dụng BlueDrive" : ua.Contains("Edg/") ? "Edge" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "Trình duyệt khác";
        return $"{app} trên {os}";
    }

    public static async Task<UserProfileDto> BuildProfileAsync(IAppDbContext db, IPermissionService perms, ISensitiveDataProtector pii, Guid userId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Include(x => x.Organization).FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw new UnauthorizedException();
        var roles = await db.UserRoles.Where(r => r.UserId == userId).Select(r => r.Role!.Name).ToListAsync(ct);
        var permissions = await perms.GetPermissionsAsync(userId, ct);
        var personalWs = await db.Workspaces.Where(w => w.OwnerId == userId && w.Type == WorkspaceType.Personal).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        return new UserProfileDto(u.Id, u.Username, u.FullName, pii.Unprotect(u.Email), pii.Unprotect(u.Phone), u.OrganizationId, u.Organization?.Name,
            u.Position, u.AvatarFileId, roles, permissions.OrderBy(p => p).ToList(), u.MustChangePassword, u.QuotaBytes, u.UsedBytes, u.LastLoginAt, personalWs);
    }
}

// ---------- Login ----------
public sealed record LoginCommand(string Username, string Password) : IRequest<AuthResult>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Username).NotEmpty().WithMessage("Vui lòng nhập tên đăng nhập.").MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().WithMessage("Vui lòng nhập mật khẩu.").MaximumLength(256);
    }
}

public sealed class LoginHandler(
    IAppDbContext db, IPasswordService passwords, ITokenService tokens, ISettingsService settings, IAuditLogger audit,
    IAlertService alerts, IPermissionService perms, ISensitiveDataProtector pii, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<LoginCommand, AuthResult>
{
    private const string InvalidCredentials = "Tên đăng nhập hoặc mật khẩu không đúng.";

    public async Task<AuthResult> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var username = cmd.Username.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            // Burn comparable CPU time so response timing does not reveal whether the account exists.
            passwords.Verify("AQAAAAIAAYagAAAAEFakeHashFakeHashFakeHashFakeHashFakeHashFakeHashFakeHashFakeHash==", cmd.Password);
            await audit.LogAsync(new AuditEntry(AuditAction.LoginFailed, Username: username, Detail: new { reason = "Tài khoản không tồn tại" }), ct);
            throw new AppException(InvalidCredentials, 401);
        }
        if (!user.IsActive) await Fail(user, "Tài khoản đã bị vô hiệu hóa.", "inactive", ct);
        if (user.IsLocked) await Fail(user, "Tài khoản đã bị khóa. Vui lòng liên hệ quản trị viên.", "locked", ct);
        if (user.LockoutEnd > now)
        {
            var minutes = Math.Ceiling((user.LockoutEnd.Value - now).TotalMinutes);
            await Fail(user, $"Tài khoản đang bị tạm khóa do đăng nhập sai nhiều lần. Vui lòng thử lại sau {minutes} phút.", "lockout", ct);
        }

        if (!passwords.Verify(user.PasswordHash, cmd.Password))
        {
            var threshold = await settings.GetAsync(SettingKeys.LockoutThreshold, 5, ct);
            var lockMinutes = await settings.GetAsync(SettingKeys.LockoutMinutes, 15, ct);
            user.FailedLoginCount++;
            var attempts = user.FailedLoginCount;
            var locked = threshold > 0 && attempts >= threshold;
            if (locked)
            {
                user.LockoutEnd = now.AddMinutes(lockMinutes);
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(new AuditEntry(AuditAction.LoginFailed,
                Detail: new { reason = "Sai mật khẩu", attempts, locked }, UserId: user.Id, Username: user.Username), ct);
            if (locked)
            {
                await alerts.RaiseAsync(AlertType.BruteForce, AlertSeverity.Critical,
                    $"Tài khoản '{user.Username}' đăng nhập sai {attempts} lần liên tiếp từ IP {caller.Ip ?? "không xác định"}. Đã khóa tạm {lockMinutes} phút.",
                    user.Id, ct: ct);
                throw new AppException($"Bạn đã nhập sai mật khẩu {attempts} lần. Tài khoản bị tạm khóa {lockMinutes} phút.", 401);
            }
            var remaining = threshold - attempts;
            throw new AppException(threshold > 0 ? $"{InvalidCredentials} Còn {remaining} lần thử trước khi tài khoản bị tạm khóa." : InvalidCredentials, 401);
        }

        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = now;
        var expiry = await settings.GetAsync(SettingKeys.PasswordExpiryDays, 90, ct);
        if (expiry > 0 && user.PasswordChangedAt is { } changed && changed.AddDays(expiry) < now) user.MustChangePassword = true;

        var session = new UserSession
        {
            UserId = user.Id,
            Ip = caller.Ip,
            UserAgent = caller.UserAgent is { Length: > 500 } ua ? ua[..500] : caller.UserAgent,
            Device = AuthHelpers.DescribeDevice(caller.UserAgent),
            CreatedAt = now,
            LastSeenAt = now,
        };
        db.UserSessions.Add(session);
        var issued = await tokens.IssueAsync(user, session, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Login, Detail: new { device = session.Device, sessionId = session.Id }, UserId: user.Id, Username: user.Username), ct);

        var profile = await AuthHelpers.BuildProfileAsync(db, perms, pii, user.Id, ct);
        return new AuthResult(issued.AccessToken, issued.AccessTokenExpiresAt, issued.RefreshToken, issued.RefreshTokenExpiresAt, profile);
    }

    private async Task Fail(User user, string message, string reason, CancellationToken ct)
    {
        await audit.LogAsync(new AuditEntry(AuditAction.LoginFailed, Detail: new { reason }, UserId: user.Id, Username: user.Username), ct);
        throw new AppException(message, 401);
    }
}

// ---------- Refresh (rotation + reuse detection + idle timeout) ----------
public sealed record RefreshCommand(string RefreshToken) : IRequest<AuthResult>;

public sealed class RefreshHandler(
    IAppDbContext db, ITokenService tokens, ISettingsService settings, SessionRevoker revoker, IPermissionService perms,
    ISensitiveDataProtector pii, IAuditLogger audit, TimeProvider clock) : IRequestHandler<RefreshCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RefreshCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RefreshToken)) throw new UnauthorizedException();
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = tokens.HashToken(cmd.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw new UnauthorizedException();
        if (token.RevokedAt is not null)
        {
            // A rotated token was presented again: assume theft and kill the whole session.
            await revoker.RevokeAsync([token.SessionId], ct);
            await audit.LogAsync(new AuditEntry(AuditAction.SessionRevoke, Detail: new { reason = "Phát hiện dùng lại refresh token", sessionId = token.SessionId }, UserId: token.UserId), ct);
            throw new UnauthorizedException("Phiên đăng nhập đã bị thu hồi. Vui lòng đăng nhập lại.");
        }
        if (token.ExpiresAt < now) throw new UnauthorizedException("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");

        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == token.SessionId, ct);
        if (session is null || session.RevokedAt is not null) throw new UnauthorizedException("Phiên đăng nhập đã bị thu hồi. Vui lòng đăng nhập lại.");
        var idle = await settings.GetAsync(SettingKeys.SessionIdleMinutes, 30, ct);
        if (idle > 0 && now - session.LastSeenAt > TimeSpan.FromMinutes(idle))
        {
            await revoker.RevokeAsync([session.Id], ct);
            throw new UnauthorizedException("Phiên đăng nhập đã hết hạn do không hoạt động. Vui lòng đăng nhập lại.");
        }
        var user = await db.Users.FirstAsync(u => u.Id == token.UserId, ct);
        if (!user.IsActive || user.IsLocked || user.LockoutEnd > now) throw new UnauthorizedException("Tài khoản đã bị khóa hoặc vô hiệu hóa.");

        token.RevokedAt = now;
        session.LastSeenAt = now;
        var issued = await tokens.IssueAsync(user, session, ct);
        await db.SaveChangesAsync(ct);
        var newId = await db.RefreshTokens.Where(t => t.TokenHash == tokens.HashToken(issued.RefreshToken)).Select(t => t.Id).FirstAsync(ct);
        token.ReplacedById = newId;
        await db.SaveChangesAsync(ct);
        var profile = await AuthHelpers.BuildProfileAsync(db, perms, pii, user.Id, ct);
        return new AuthResult(issued.AccessToken, issued.AccessTokenExpiresAt, issued.RefreshToken, issued.RefreshTokenExpiresAt, profile);
    }
}

// ---------- Logout ----------
public sealed record LogoutCommand : IRequest<Unit>;

public sealed class LogoutHandler(ICurrentUser user, SessionRevoker revoker, IAuditLogger audit) : IRequestHandler<LogoutCommand, Unit>
{
    public async Task<Unit> Handle(LogoutCommand cmd, CancellationToken ct)
    {
        if (user.SessionId is { } sid) await revoker.RevokeAsync([sid], ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Logout, Detail: new { sessionId = user.SessionId }), ct);
        return Unit.Value;
    }
}

// ---------- Me ----------
public sealed record GetMeQuery : IRequest<UserProfileDto>;

public sealed class GetMeHandler(IAppDbContext db, ICurrentUser user, IPermissionService perms, ISensitiveDataProtector pii) : IRequestHandler<GetMeQuery, UserProfileDto>
{
    public Task<UserProfileDto> Handle(GetMeQuery q, CancellationToken ct) => AuthHelpers.BuildProfileAsync(db, perms, pii, user.RequireUserId(), ct);
}

// ---------- Change password ----------
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword, bool SignOutOtherSessions = true) : IRequest<AuthResult>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Vui lòng nhập mật khẩu hiện tại.");
        RuleFor(x => x.NewPassword).NotEmpty().WithMessage("Vui lòng nhập mật khẩu mới.").MaximumLength(256)
            .NotEqual(x => x.CurrentPassword).WithMessage("Mật khẩu mới phải khác mật khẩu hiện tại.");
    }
}

public sealed class ChangePasswordHandler(
    IAppDbContext db, ICurrentUser caller, IPasswordService passwords, ITokenService tokens, SessionRevoker revoker,
    IAuditLogger audit, IPermissionService perms, ISensitiveDataProtector pii, TimeProvider clock) : IRequestHandler<ChangePasswordCommand, AuthResult>
{
    public async Task<AuthResult> Handle(ChangePasswordCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == caller.RequireUserId(), ct);
        if (!passwords.Verify(user.PasswordHash, cmd.CurrentPassword))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["currentPassword"] = ["Mật khẩu hiện tại không đúng."] });
        var errors = await passwords.ValidatePolicyAsync(cmd.NewPassword, user.Id, ct);
        if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { ["newPassword"] = [.. errors] });

        await passwords.SetPasswordAsync(user, cmd.NewPassword, mustChange: false, ct);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == caller.SessionId, ct)
                      ?? throw new UnauthorizedException();
        session.LastSeenAt = clock.GetUtcNow().UtcDateTime;
        // Revoke the old refresh tokens of this session; a new pair is issued below (clears the pwd_change claim).
        await db.RefreshTokens.Where(t => t.SessionId == session.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, session.LastSeenAt), ct);
        var issued = await tokens.IssueAsync(user, session, ct);
        await db.SaveChangesAsync(ct);
        if (cmd.SignOutOtherSessions) await revoker.RevokeAllForUserAsync(user.Id, session.Id, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PasswordChange, UserId: user.Id, Username: user.Username), ct);
        var profile = await AuthHelpers.BuildProfileAsync(db, perms, pii, user.Id, ct);
        return new AuthResult(issued.AccessToken, issued.AccessTokenExpiresAt, issued.RefreshToken, issued.RefreshTokenExpiresAt, profile);
    }
}

// ---------- Forgot / reset password ----------
public sealed record ForgotPasswordCommand(string UsernameOrEmail) : IRequest<Unit>;

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() => RuleFor(x => x.UsernameOrEmail).NotEmpty().WithMessage("Vui lòng nhập tên đăng nhập hoặc email.").MaximumLength(255);
}

public sealed class ForgotPasswordHandler(
    IAppDbContext db, ITokenService tokens, IEmailSender email, ISensitiveDataProtector pii, IAuditLogger audit,
    IConfiguration config, TimeProvider clock) : IRequestHandler<ForgotPasswordCommand, Unit>
{
    public async Task<Unit> Handle(ForgotPasswordCommand cmd, CancellationToken ct)
    {
        var key = cmd.UsernameOrEmail.Trim();
        var emailHash = pii.Hash(key);
        var user = await db.Users.FirstOrDefaultAsync(u => u.IsActive && (u.Username == key.ToLower() || u.EmailHash == emailHash), ct);
        var to = pii.Unprotect(user?.Email);
        // Always succeed from the caller's point of view (no account enumeration).
        if (user is null || string.IsNullOrWhiteSpace(to)) return Unit.Value;

        var now = clock.GetUtcNow().UtcDateTime;
        await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.UsedAt, now), ct);
        var raw = tokens.GenerateOpaqueToken();
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = tokens.HashToken(raw), CreatedAt = now, ExpiresAt = now.AddMinutes(30) });
        await db.SaveChangesAsync(ct);

        var baseUrl = (config["App:PublicBaseUrl"] ?? "http://localhost").TrimEnd('/');
        var link = $"{baseUrl}/reset-password?token={Uri.EscapeDataString(raw)}";
        await email.SendAsync(to, "[BlueDrive] Khôi phục mật khẩu",
            $"""
            <p>Xin chào {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>
            <p>Hệ thống nhận được yêu cầu khôi phục mật khẩu cho tài khoản <b>{user.Username}</b>.</p>
            <p><a href="{link}">Bấm vào đây để đặt lại mật khẩu</a>. Liên kết chỉ dùng được một lần và hết hạn sau 30 phút.</p>
            <p>Nếu bạn không yêu cầu, vui lòng bỏ qua email này.</p>
            """, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PasswordReset, Detail: new { step = "request" }, UserId: user.Id, Username: user.Username), ct);
        return Unit.Value;
    }
}

public sealed record ResetPasswordCommand(string Token, string NewPassword) : IRequest<Unit>;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).NotEmpty().WithMessage("Liên kết khôi phục không hợp lệ.");
        RuleFor(x => x.NewPassword).NotEmpty().WithMessage("Vui lòng nhập mật khẩu mới.").MaximumLength(256);
    }
}

public sealed class ResetPasswordHandler(
    IAppDbContext db, ITokenService tokens, IPasswordService passwords, SessionRevoker revoker, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<ResetPasswordCommand, Unit>
{
    public async Task<Unit> Handle(ResetPasswordCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = tokens.HashToken(cmd.Token);
        var token = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < now)
            throw new AppException("Liên kết khôi phục mật khẩu không hợp lệ, đã được sử dụng hoặc đã hết hạn.");
        var user = await db.Users.FirstAsync(u => u.Id == token.UserId, ct);
        var errors = await passwords.ValidatePolicyAsync(cmd.NewPassword, user.Id, ct);
        if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { ["newPassword"] = [.. errors] });
        await passwords.SetPasswordAsync(user, cmd.NewPassword, mustChange: false, ct);
        token.UsedAt = now;
        await db.SaveChangesAsync(ct);
        await revoker.RevokeAllForUserAsync(user.Id, null, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PasswordReset, Detail: new { step = "completed" }, UserId: user.Id, Username: user.Username), ct);
        return Unit.Value;
    }
}

// ---------- Password policy (public, for the UI hint) ----------
public sealed record PasswordPolicyDto(int MinLength, bool RequireUpper, bool RequireLower, bool RequireDigit, bool RequireSpecial, int ExpiryDays, int HistoryCount);

public sealed record GetPasswordPolicyQuery : IRequest<PasswordPolicyDto>;

public sealed class GetPasswordPolicyHandler(ISettingsService s) : IRequestHandler<GetPasswordPolicyQuery, PasswordPolicyDto>
{
    public async Task<PasswordPolicyDto> Handle(GetPasswordPolicyQuery q, CancellationToken ct) => new(
        await s.GetAsync(SettingKeys.PasswordMinLength, 8, ct), await s.GetAsync(SettingKeys.PasswordRequireUpper, true, ct),
        await s.GetAsync(SettingKeys.PasswordRequireLower, true, ct), await s.GetAsync(SettingKeys.PasswordRequireDigit, true, ct),
        await s.GetAsync(SettingKeys.PasswordRequireSpecial, true, ct), await s.GetAsync(SettingKeys.PasswordExpiryDays, 90, ct),
        await s.GetAsync(SettingKeys.PasswordHistory, 5, ct));
}
