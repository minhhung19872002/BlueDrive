using BlueDrive.Domain.Entities;

namespace BlueDrive.Application.Security;

public interface IPasswordService
{
    string Hash(string password);
    bool Verify(string hash, string password);
    /// <summary>Returns Vietnamese error messages when the password violates the configured policy.</summary>
    Task<IReadOnlyList<string>> ValidatePolicyAsync(string password, Guid? userId, CancellationToken ct = default);
    /// <summary>Sets a new password, records history and clears must_change flags. Does not save.</summary>
    Task SetPasswordAsync(User user, string newPassword, bool mustChange, CancellationToken ct = default);
}

public sealed record IssuedTokens(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt);

public interface ITokenService
{
    /// <summary>Creates an access token for the session and a new refresh token (stored hashed). Does not save.</summary>
    Task<IssuedTokens> IssueAsync(User user, UserSession session, CancellationToken ct = default);
    string HashToken(string token);
    string GenerateOpaqueToken(int bytes = 32);
}

/// <summary>Validates that the session behind an access token is still alive (revocation, idle timeout, security stamp).</summary>
public interface ISessionValidator
{
    Task<bool> IsValidAsync(Guid userId, Guid sessionId, int securityStamp, CancellationToken ct = default);
    void MarkRevoked(Guid sessionId, TimeSpan forHowLong);
    void InvalidateUser(Guid userId);
}
