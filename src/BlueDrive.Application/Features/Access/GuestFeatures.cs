using System.Net;
using System.Security.Cryptography;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Auth;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Guests from outside (spec 2, 5.4): invited by email when sharing (if the policy of every tier allows guests), sign in with a one-time code
/// sent to that email (no password), see only what is shared with them directly, cannot look up people, and their account ends after N days
/// without use.
/// </summary>
public sealed class GuestService(IAppDbContext db, ISettingsService settings, SharingPolicy policy, ISensitiveDataProtector pii, IPasswordService passwords,
    IEmailSender email, IAuditLogger audit, IConfiguration config, TimeProvider clock)
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>The user an email stands for when sharing: an internal user with that email, an existing guest, or a guest invited now.</summary>
    public async Task<(Guid UserId, bool IsGuest, bool Invited)> ResolveAsync(string rawEmail, Guid itemId, Guid invitedBy, CancellationToken ct)
    {
        var address = Normalize(rawEmail);
        if (!System.Net.Mail.MailAddress.TryCreate(address, out _) || !address.Contains('@')) throw new AppException($"Email \"{rawEmail}\" không hợp lệ.");
        var hash = pii.Hash(address);
        var known = await db.Users.FirstOrDefaultAsync(u => u.EmailHash == hash, ct);
        if (known is { IsGuest: false })
        {
            if (!known.IsActive) throw new AppException($"Tài khoản của {address} đã bị vô hiệu hóa.");
            return (known.Id, false, false);
        }
        if (!await settings.GetAsync(SettingKeys.GuestEnabled, true, ct)) throw new ForbiddenException("Quản trị hệ thống chưa cho phép mời khách bên ngoài.");
        var domain = address[(address.IndexOf('@') + 1)..];
        var allowed = await settings.GetAsync(SettingKeys.GuestAllowedDomains, Array.Empty<string>(), ct);
        var blocked = await settings.GetAsync(SettingKeys.GuestBlockedDomains, Array.Empty<string>(), ct);
        bool Matches(string d) => domain == d.Trim().TrimStart('@').ToLowerInvariant() || domain.EndsWith("." + d.Trim().TrimStart('@').ToLowerInvariant());
        if ((allowed.Length > 0 && !allowed.Any(Matches)) || blocked.Any(Matches))
            throw new ForbiddenException($"Không được mời khách có email thuộc tên miền {domain}.");
        var p = await policy.EffectiveAsync(itemId, ct);
        var need = known is null ? ExternalSharing.NewAndExistingGuests : ExternalSharing.ExistingGuests;
        if (p.External < need)
            throw new ForbiddenException($"Chính sách chia sẻ ({p.ExternalSource}: {SharingPolicy.Label(p.External)}) không cho phép "
                                         + (known is null ? "mời khách mới." : "chia sẻ cho khách."));
        var now = clock.GetUtcNow().UtcDateTime;
        if (known is not null)
        {
            if (!known.IsActive) throw new AppException($"Tài khoản khách {address} đã hết hạn hoặc bị vô hiệu hóa; quản trị kích hoạt lại ở mục Khách bên ngoài.");
            return (known.Id, true, false);
        }
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Code == Perm.Roles.Guest, ct) ?? throw new AppException("Chưa có vai trò Khách bên ngoài.");
        var user = new User
        {
            Username = "guest:" + address, Email = pii.Protect(address), EmailHash = hash, FullName = address[..address.IndexOf('@')],
            // No password: guests sign in with a one-time code only.
            PasswordHash = passwords.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), IsGuest = true, PasswordChangedAt = now,
        };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Guests.Add(new Guest { UserId = user.Id, InvitedBy = invitedBy, InvitedAt = now, ExpiresAt = now.AddDays(await InactiveDaysAsync(ct)) });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.GuestInvited, ResourceName: address, Detail: new { guest = user.Id, item = itemId }), ct);
        return (user.Id, true, true);
    }

    /// <summary>The invitation (or "shared with you") email to a guest: where to sign in with a code.</summary>
    public async Task SendInvitationAsync(Guid guestId, string sharer, string itemName, string next, string? message, CancellationToken ct)
    {
        var to = pii.Unprotect(await db.Users.Where(u => u.Id == guestId).Select(u => u.Email).FirstAsync(ct));
        if (string.IsNullOrWhiteSpace(to)) return;
        var baseUrl = (config["App:PublicBaseUrl"] ?? "http://localhost").TrimEnd('/');
        var link = $"{baseUrl}/guest-login?email={Uri.EscapeDataString(to)}&next={Uri.EscapeDataString(next)}";
        await email.SendAsync(to, $"[BlueDrive] {sharer} đã chia sẻ \"{itemName}\" với bạn",
            $"""
            <p>Xin chào,</p>
            <p><b>{WebUtility.HtmlEncode(sharer)}</b> đã chia sẻ <b>{WebUtility.HtmlEncode(itemName)}</b> với bạn trên BlueDrive.</p>
            {(string.IsNullOrWhiteSpace(message) ? "" : $"<p><i>{WebUtility.HtmlEncode(message)}</i></p>")}
            <p><a href="{link}">Mở tư liệu</a> — bạn đăng nhập bằng mã gồm 6 chữ số gửi tới email này, không cần mật khẩu.</p>
            """, ct);
    }

    public async Task<int> InactiveDaysAsync(CancellationToken ct) => Math.Max(1, await settings.GetAsync(SettingKeys.GuestInactiveDays, 90, ct));

    /// <summary>Guest accounts not used for N days end (the job runs it).</summary>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = await db.Guests.Where(g => g.ExpiresAt <= now).Select(g => g.UserId).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        var n = await db.Users.Where(u => ids.Contains(u.Id) && u.IsActive).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false).SetProperty(x => x.SecurityStamp, x => x.SecurityStamp + 1), ct);
        await db.UserSessions.Where(s => ids.Contains(s.UserId) && s.RevokedAt == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now), ct);
        return n;
    }
}

// ---------- Sign-in with a one-time code ----------

public sealed record RequestGuestCodeCommand(string Email) : IRequest<Unit>;

public sealed record VerifyGuestCodeCommand(string Email, string Code) : IRequest<AuthResult>;

public sealed class GuestCodeValidators : AbstractValidator<VerifyGuestCodeCommand>
{
    public GuestCodeValidators()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Vui lòng nhập email.").MaximumLength(255);
        RuleFor(x => x.Code).NotEmpty().WithMessage("Vui lòng nhập mã.").Matches("^[0-9]{6}$").WithMessage("Mã gồm 6 chữ số.");
    }
}

public sealed class GuestSignInHandlers(IAppDbContext db, ISensitiveDataProtector pii, ITokenService tokens, IEmailSender email, IPermissionService perms,
    IAuditLogger audit, ICurrentUser caller, GuestService guests, TimeProvider clock)
    : IRequestHandler<RequestGuestCodeCommand, Unit>, IRequestHandler<VerifyGuestCodeCommand, AuthResult>
{
    private const string Invalid = "Mã không đúng hoặc đã hết hạn. Vui lòng yêu cầu mã mới.";
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 5;

    private async Task<User?> GuestAsync(string address, CancellationToken ct)
    {
        var hash = pii.Hash(GuestService.Normalize(address));
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.Users.FirstOrDefaultAsync(u => u.EmailHash == hash && u.IsGuest && u.IsActive && !u.IsLocked
                                                       && db.Guests.Any(g => g.UserId == u.Id && g.ExpiresAt > now), ct);
    }

    public async Task<Unit> Handle(RequestGuestCodeCommand cmd, CancellationToken ct)
    {
        // Always "sent" from the caller's point of view (no account enumeration).
        var user = await GuestAsync(cmd.Email, ct);
        if (user is null) return Unit.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        if (await db.GuestOtps.CountAsync(o => o.UserId == user.Id && o.CreatedAt > now.AddMinutes(-15), ct) >= 5) return Unit.Value;
        await db.GuestOtps.Where(o => o.UserId == user.Id && o.UsedAt == null).ExecuteUpdateAsync(u => u.SetProperty(o => o.UsedAt, now), ct);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.GuestOtps.Add(new GuestOtp { UserId = user.Id, CodeHash = tokens.HashToken($"{user.Id}:{code}"), CreatedAt = now, ExpiresAt = now + CodeLife });
        await db.SaveChangesAsync(ct);
        await email.SendAsync(pii.Unprotect(user.Email)!, "[BlueDrive] Mã đăng nhập của bạn",
            $"<p>Mã đăng nhập BlueDrive của bạn là <b style=\"font-size:20px;letter-spacing:4px\">{code}</b>.</p><p>Mã dùng một lần, hết hạn sau 10 phút. Nếu bạn không yêu cầu, hãy bỏ qua email này.</p>", ct);
        return Unit.Value;
    }

    public async Task<AuthResult> Handle(VerifyGuestCodeCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await GuestAsync(cmd.Email, ct) ?? throw new AppException(Invalid, 401);
        var otp = await db.GuestOtps.Where(o => o.UserId == user.Id && o.UsedAt == null && o.ExpiresAt > now).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        if (otp is null || otp.Attempts >= MaxAttempts) throw new AppException(Invalid, 401);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(otp.CodeHash), Convert.FromHexString(tokens.HashToken($"{user.Id}:{cmd.Code.Trim()}"))))
        {
            otp.Attempts++;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(new AuditEntry(AuditAction.LoginFailed, Detail: new { reason = "Sai mã khách", attempts = otp.Attempts }, UserId: user.Id, Username: user.Username), ct);
            throw new AppException(Invalid, 401);
        }
        otp.UsedAt = now;
        user.LastLoginAt = now;
        var days = await guests.InactiveDaysAsync(ct);
        await db.Guests.Where(g => g.UserId == user.Id).ExecuteUpdateAsync(u => u.SetProperty(g => g.ExpiresAt, now.AddDays(days)), ct);
        var session = new UserSession
        {
            UserId = user.Id, Ip = caller.Ip, UserAgent = caller.UserAgent is { Length: > 500 } ua ? ua[..500] : caller.UserAgent,
            Device = AuthHelpers.DescribeDevice(caller.UserAgent), CreatedAt = now, LastSeenAt = now,
        };
        db.UserSessions.Add(session);
        var issued = await tokens.IssueAsync(user, session, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.GuestLogin, Detail: new { device = session.Device, sessionId = session.Id }, UserId: user.Id, Username: user.Username), ct);
        var profile = await AuthHelpers.BuildProfileAsync(db, perms, pii, user.Id, ct);
        return new AuthResult(issued.AccessToken, issued.AccessTokenExpiresAt, issued.RefreshToken, issued.RefreshTokenExpiresAt, profile);
    }
}

// ---------- Administration ("Khách bên ngoài") ----------

public sealed record GuestDto(Guid Id, string Email, string Name, string? InvitedByName, DateTime InvitedAt, DateTime? LastLoginAt, DateTime ExpiresAt, bool IsActive,
    int SharedItems);

public sealed record ListGuestsQuery(string? Q) : IRequest<IReadOnlyList<GuestDto>>;

/// <summary>Enable (the account lives N more days) or disable a guest.</summary>
public sealed record SetGuestActiveCommand(Guid Id, bool Active) : IRequest<Unit>;

/// <summary>Removes a guest and every access they were given (spec 8.4 "xóa khách").</summary>
public sealed record DeleteGuestCommand(Guid Id) : IRequest<Unit>;

public sealed class GuestAdminHandlers(IAppDbContext db, ISensitiveDataProtector pii, AclEditor editor, GuestService guests, IAuditLogger audit, TimeProvider clock,
    SessionRevoker sessions)
    : IRequestHandler<ListGuestsQuery, IReadOnlyList<GuestDto>>, IRequestHandler<SetGuestActiveCommand, Unit>, IRequestHandler<DeleteGuestCommand, Unit>
{
    public async Task<IReadOnlyList<GuestDto>> Handle(ListGuestsQuery q, CancellationToken ct)
    {
        var rows = await db.Guests.AsNoTracking().Join(db.Users, g => g.UserId, u => u.Id, (g, u) => new { g, u }).OrderByDescending(x => x.g.InvitedAt).Take(1000).ToListAsync(ct);
        var inviters = rows.Select(r => r.g.InvitedBy ?? Guid.Empty).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => inviters.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var ids = rows.Select(r => r.u.Id).ToList();
        var counts = await db.AclEntries.AsNoTracking().Where(e => ids.Contains(e.PrincipalId)).GroupBy(e => e.PrincipalId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var list = rows.Select(r => new GuestDto(r.u.Id, pii.Unprotect(r.u.Email) ?? "", r.u.FullName, r.g.InvitedBy is { } i ? names.GetValueOrDefault(i) : null, r.g.InvitedAt,
            r.u.LastLoginAt, r.g.ExpiresAt, r.u.IsActive, counts.GetValueOrDefault(r.u.Id))).ToList();
        if (!string.IsNullOrWhiteSpace(q.Q)) list = list.Where(g => g.Email.Contains(q.Q.Trim(), StringComparison.OrdinalIgnoreCase) || g.Name.Contains(q.Q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return list;
    }

    public async Task<Unit> Handle(SetGuestActiveCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.Id && u.IsGuest, ct) ?? throw new NotFoundException("Không tìm thấy khách.");
        var now = clock.GetUtcNow().UtcDateTime;
        user.IsActive = cmd.Active;
        if (!cmd.Active)
        {
            user.SecurityStamp++;
            await sessions.RevokeAllForUserAsync(user.Id, null, ct);
        }
        else
        {
            var days = await guests.InactiveDaysAsync(ct);
            await db.Guests.Where(g => g.UserId == user.Id).ExecuteUpdateAsync(u => u.SetProperty(g => g.ExpiresAt, now.AddDays(days)), ct);
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(cmd.Active ? AuditAction.UserUnlock : AuditAction.UserLock, ResourceName: user.Username, Detail: new { guest = user.Id, active = cmd.Active }), ct);
        return Unit.Value;
    }

    public async Task<Unit> Handle(DeleteGuestCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.Id && u.IsGuest, ct) ?? throw new NotFoundException("Không tìm thấy khách.");
        await editor.ForgetPrincipalAsync(user.Id, ct);
        await db.ShareLinkMembers.Where(m => m.UserId == user.Id).ExecuteDeleteAsync(ct);
        await sessions.RevokeAllForUserAsync(user.Id, null, ct);
        await db.GuestOtps.Where(o => o.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.Guests.Where(g => g.UserId == user.Id).ExecuteDeleteAsync(ct);
        // The account itself stays (disabled) so audit entries keep a name; it no longer has any access.
        user.IsActive = false;
        user.SecurityStamp++;
        user.EmailHash = null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: user.Username, Detail: new { guest = user.Id }), ct);
        return Unit.Value;
    }
}
