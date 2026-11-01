using System.Security.Cryptography;
using System.Text;
using BlueDrive.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace BlueDrive.Infrastructure.Security;

/// <summary>Encrypts PII columns (email, phone) with ASP.NET Data Protection; hashes emails for lookups.</summary>
public sealed class SensitiveDataProtector(IDataProtectionProvider provider) : ISensitiveDataProtector
{
    private const string Prefix = "dp:";
    private readonly IDataProtector _protector = provider.CreateProtector("BlueDrive.PII.v1");

    public string? Protect(string? plain) => string.IsNullOrWhiteSpace(plain) ? null : Prefix + _protector.Protect(plain.Trim());

    public string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        if (!cipher.StartsWith(Prefix, StringComparison.Ordinal)) return cipher; // legacy/plain value
        try { return _protector.Unprotect(cipher[Prefix.Length..]); }
        catch (CryptographicException) { return null; }
    }

    public string? Hash(string? plain) => string.IsNullOrWhiteSpace(plain)
        ? null
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain.Trim().ToLowerInvariant())));
}
