using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueDrive.Sync.Client;

namespace BlueDrive.Sync.Agent;

/// <summary>One BlueDrive account on this computer: its server, sync folder, and (in <see cref="AccountStore.AccountDir"/>) state database + tokens.</summary>
public sealed record AccountConfig(Guid Id, string ServerUrl, string SyncFolder, string Username, string DisplayName, Guid DeviceId)
{
    public bool Paused { get; init; }
}

/// <summary>
/// %LOCALAPPDATA%\BlueDrive: accounts.json (no secrets) + one folder per account (state.db, tokens.dat). Several accounts / servers
/// can live side by side, each with its own sync folder.
/// </summary>
public sealed class AccountStore(string? baseDir = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string BaseDir { get; } = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueDrive");
    private string File => Path.Combine(BaseDir, "accounts.json");

    public string AccountDir(Guid id) => Path.Combine(BaseDir, id.ToString("N"));
    public string StateDbPath(Guid id) => Path.Combine(AccountDir(id), "state.db");
    public ITokenStore Tokens(Guid id) => new ProtectedFileTokenStore(Path.Combine(AccountDir(id), "tokens.dat"));

    public IReadOnlyList<AccountConfig> Load()
    {
        if (!System.IO.File.Exists(File)) return [];
        return JsonSerializer.Deserialize<List<AccountConfig>>(System.IO.File.ReadAllText(File), Json) ?? [];
    }

    public void Save(AccountConfig account)
    {
        var list = Load().Where(a => a.Id != account.Id).Append(account).ToList();
        Write(list);
    }

    /// <summary>Removes the account, its state and its tokens (the synced files stay unless the caller deletes them).</summary>
    public void Remove(Guid id)
    {
        Write(Load().Where(a => a.Id != id).ToList());
        Tokens(id).Clear();
        if (Directory.Exists(AccountDir(id))) Directory.Delete(AccountDir(id), recursive: true);
    }

    private void Write(List<AccountConfig> list)
    {
        Directory.CreateDirectory(BaseDir);
        var tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        System.IO.File.Move(tmp, File, overwrite: true);
    }
}

/// <summary>
/// Device tokens encrypted for the current Windows user (DPAPI, CurrentUser scope + app entropy): another user of the computer, or the
/// file copied elsewhere, cannot read them. Never written in clear, never logged.
/// </summary>
public sealed class ProtectedFileTokenStore(string path) : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlueDrive.Desktop.tokens.v1");

    public DeviceTokens? Load()
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var json = Unprotect(System.IO.File.ReadAllBytes(path));
            return JsonSerializer.Deserialize<StoredTokens>(json)?.ToTokens();
        }
        catch (CryptographicException) { return null; } // another user / machine: sign in again
    }

    public void Save(DeviceTokens tokens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllBytes(tmp, Protect(JsonSerializer.SerializeToUtf8Bytes(StoredTokens.From(tokens))));
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public void Clear()
    {
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
    }

    private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException("Lưu thông tin đăng nhập trên hệ điều hành này dùng Keychain (macOS, Sync-Phase 10).");

    private static byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException("Lưu thông tin đăng nhập trên hệ điều hành này dùng Keychain (macOS, Sync-Phase 10).");

    // Keeps the access token expiry (the record's JSON shape is the server's token response).
    private sealed record StoredTokens(string AccessToken, string RefreshToken, int ExpiresIn, Guid DeviceId, string? DeviceSecret, DateTime AccessExpiresAt)
    {
        public static StoredTokens From(DeviceTokens t) => new(t.AccessToken, t.RefreshToken, t.ExpiresIn, t.DeviceId, t.DeviceSecret, t.AccessExpiresAt);
        public DeviceTokens ToTokens() => new(AccessToken, RefreshToken, ExpiresIn, DeviceId, DeviceSecret) { AccessExpiresAt = AccessExpiresAt };
    }
}
