using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BlueDrive.Infrastructure.Security;

public sealed class JwtOptions
{
    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "bluedrive";
    public string Audience { get; set; } = "bluedrive";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    public SymmetricSecurityKey SigningKey()
    {
        if (Secret.Length < 32) throw new InvalidOperationException("Jwt:Secret phải có tối thiểu 32 ký tự (đặt JWT_SECRET trong .env).");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
    }
}

public sealed class TokenService(IOptions<JwtOptions> options, IAppDbContext db, ISettingsService settings, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _opt = options.Value;

    public async Task<IssuedTokens> IssueAsync(User user, UserSession session, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var accessExp = now.AddMinutes(_opt.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("preferred_username", user.Username),
            new("name", user.FullName),
            new("sid", session.Id.ToString()),
            new("stamp", user.SecurityStamp.ToString()),
            new("pwd_change", user.MustChangePassword ? "1" : "0"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        // Desktop sync sessions carry their device id (audit, revocation) and a long-lived refresh token.
        if (session.DeviceId is { } deviceId) claims.Add(new Claim("did", deviceId.ToString()));
        var token = new JwtSecurityToken(_opt.Issuer, _opt.Audience, claims, now, accessExp,
            new SigningCredentials(_opt.SigningKey(), SecurityAlgorithms.HmacSha256));
        var access = new JwtSecurityTokenHandler().WriteToken(token);

        var refresh = GenerateOpaqueToken();
        var refreshExp = session.DeviceId is null
            ? now.AddDays(_opt.RefreshTokenDays)
            : now.AddDays(await settings.GetAsync(SettingKeys.SyncDeviceRefreshDays, 90, ct));
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            SessionId = session.Id,
            TokenHash = HashToken(refresh),
            CreatedAt = now,
            ExpiresAt = refreshExp,
        });
        return new IssuedTokens(access, accessExp, refresh, refreshExp);
    }

    public string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public string GenerateOpaqueToken(int bytes = 32) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));
}
