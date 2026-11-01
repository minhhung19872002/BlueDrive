using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Infrastructure.Security;

/// <summary>PBKDF2 (ASP.NET Core Identity v3 format) hashing + configurable password policy and history.</summary>
public sealed class PasswordService(IAppDbContext db, ISettingsService settings, TimeProvider clock) : IPasswordService
{
    private static readonly PasswordHasher<User> Hasher = new();

    public string Hash(string password) => Hasher.HashPassword(null!, password);

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try { return Hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed; }
        catch (FormatException) { return false; }
    }

    public async Task<IReadOnlyList<string>> ValidatePolicyAsync(string password, Guid? userId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var min = await settings.GetAsync(SettingKeys.PasswordMinLength, 8, ct);
        if (string.IsNullOrEmpty(password) || password.Length < min) errors.Add($"Mật khẩu phải có tối thiểu {min} ký tự.");
        password ??= "";
        if (await settings.GetAsync(SettingKeys.PasswordRequireUpper, true, ct) && !password.Any(char.IsUpper)) errors.Add("Mật khẩu phải có ít nhất một chữ hoa.");
        if (await settings.GetAsync(SettingKeys.PasswordRequireLower, true, ct) && !password.Any(char.IsLower)) errors.Add("Mật khẩu phải có ít nhất một chữ thường.");
        if (await settings.GetAsync(SettingKeys.PasswordRequireDigit, true, ct) && !password.Any(char.IsDigit)) errors.Add("Mật khẩu phải có ít nhất một chữ số.");
        if (await settings.GetAsync(SettingKeys.PasswordRequireSpecial, true, ct) && password.All(char.IsLetterOrDigit)) errors.Add("Mật khẩu phải có ít nhất một ký tự đặc biệt.");

        if (userId.HasValue)
        {
            var historyCount = await settings.GetAsync(SettingKeys.PasswordHistory, 5, ct);
            if (historyCount > 0)
            {
                var recent = await db.PasswordHistories.Where(h => h.UserId == userId).OrderByDescending(h => h.CreatedAt)
                    .Take(historyCount).Select(h => h.PasswordHash).ToListAsync(ct);
                if (recent.Any(h => Verify(h, password))) errors.Add($"Mật khẩu mới không được trùng với {historyCount} mật khẩu gần nhất.");
            }
        }
        return errors;
    }

    public Task SetPasswordAsync(User user, string newPassword, bool mustChange, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        user.PasswordHash = Hash(newPassword);
        user.PasswordChangedAt = now;
        user.MustChangePassword = mustChange;
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        db.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, PasswordHash = user.PasswordHash, CreatedAt = now });
        return Task.CompletedTask;
    }
}
