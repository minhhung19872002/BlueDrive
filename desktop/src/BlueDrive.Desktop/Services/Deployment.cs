using System.Text.Json;
using Microsoft.Win32;

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Mass deployment settings (spec 11 / 13A), strongest first:
/// <list type="number">
/// <item>Group Policy / Intune registry values under <c>HKLM\SOFTWARE\Policies\BlueDrive</c> (or HKCU): <c>Server</c>,
/// <c>InstallOnlyOffice</c> (DWORD 0/1), <c>UpdateChannel</c> (stable/beta), <c>DisableUpdates</c> (DWORD 1).</item>
/// <item>Installer arguments, forwarded by Velopack's Setup after <c>--</c>: <c>BlueDriveSetup.exe --silent -- --server=https://… --with-onlyoffice=1</c>
/// (the <c>/server=</c> and <c>/with-onlyoffice=</c> spellings work too). They only reach the first start, so they are saved in settings.</item>
/// <item><c>deploy.json</c> next to BlueDrive.exe: <c>{ "server": "…", "withOnlyOffice": true, "channel": "stable" }</c>.</item>
/// </list>
/// </summary>
public sealed record Deployment(string? Server, bool? InstallOnlyOffice, string? Channel, bool UpdatesDisabled)
{
    /// <summary>Certificate pinning for an internal server (SPKI SHA-256, base64): policy <c>CertificatePins</c> (REG_MULTI_SZ) or deploy.json
    /// <c>"certificatePins": [...]</c>. Not part of equality.</summary>
    public IReadOnlyList<string> CertificatePins { get; init; } = [];

    public bool Equals(Deployment? other) => other is not null && (Server, InstallOnlyOffice, Channel, UpdatesDisabled) == (other.Server, other.InstallOnlyOffice, other.Channel, other.UpdatesDisabled);

    public override int GetHashCode() => HashCode.Combine(Server, InstallOnlyOffice, Channel, UpdatesDisabled);

    public const string PolicyKey = @"SOFTWARE\Policies\BlueDrive";

    public static string? Arg(IEnumerable<string> args, string name)
    {
        foreach (var a in args)
            foreach (var prefix in new[] { "--", "/" })
                if (a.StartsWith(prefix + name + "=", StringComparison.OrdinalIgnoreCase)) return a[(prefix.Length + name.Length + 1)..].Trim('"');
        return null;
    }

    private static bool? Flag(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" => true,
        "0" or "false" or "no" => false,
        _ => null,
    };

    public static string? NormalizeChannel(string? c) => c?.Trim().ToLowerInvariant() is "beta" ? "beta" : c is null ? null : "stable";

    /// <summary>Reads arguments and deploy.json (no registry: tests and other platforms).</summary>
    public static Deployment FromArgs(IReadOnlyList<string> args, string? appDir = null)
    {
        string? server = Arg(args, "server"), channel = Arg(args, "channel");
        var oo = Flag(Arg(args, "with-onlyoffice"));
        bool? off = null;
        var pins = new List<string>();
        foreach (var file in new[] { Path.Combine(appDir ?? AppContext.BaseDirectory, "deploy.json"), MachineFile }.OfType<string>())
        {
            if (!File.Exists(file)) continue;
            try
            {
                var root = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                if (server is null && root.TryGetProperty("server", out var s)) server = s.GetString();
                if (oo is null && root.TryGetProperty("withOnlyOffice", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False) oo = w.GetBoolean();
                if (channel is null && root.TryGetProperty("channel", out var c)) channel = c.GetString();
                if (off is null && root.TryGetProperty("disableUpdates", out var d) && d.ValueKind is JsonValueKind.True) off = true;
                if (pins.Count == 0 && root.TryGetProperty("certificatePins", out var p) && p.ValueKind == JsonValueKind.Array)
                    pins.AddRange(p.EnumerateArray().Select(x => x.GetString()).OfType<string>());
            }
            catch (JsonException) { }
        }
        return new(Clean(server), oo, NormalizeChannel(channel), off ?? false) { CertificatePins = pins };
    }

    /// <summary>
    /// macOS / Linux mass deployment (no registry policies): a deploy.json the administrator (MDM, Ansible…) puts for the whole computer:
    /// <c>/Library/Application Support/BlueDrive/deploy.json</c>, <c>/etc/bluedrive/deploy.json</c>. Overridable in tests.
    /// </summary>
    public static string? MachineFile { get; set; } =
        OperatingSystem.IsMacOS() ? "/Library/Application Support/BlueDrive/deploy.json" : OperatingSystem.IsLinux() ? "/etc/bluedrive/deploy.json" : null;

    private static string? Clean(string? server) =>
        Uri.TryCreate(server?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.ToString().TrimEnd('/') : null;

    /// <summary>Everything that applies on this computer: policy over arguments over deploy.json.</summary>
    public static Deployment Current(IReadOnlyList<string> args)
    {
        var d = FromArgs(args);
        if (!OperatingSystem.IsWindows()) return d;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(PolicyKey);
            if (key is null) continue;
            if (Clean(key.GetValue("Server") as string) is { } s) d = d with { Server = s };
            if (key.GetValue("InstallOnlyOffice") is int oo) d = d with { InstallOnlyOffice = oo != 0 };
            if (key.GetValue("UpdateChannel") is string c) d = d with { Channel = NormalizeChannel(c) };
            if (key.GetValue("DisableUpdates") is int off && off != 0) d = d with { UpdatesDisabled = true };
            if (key.GetValue("CertificatePins") switch { string[] many => many, string one => one.Split(';'), _ => null } is { Length: > 0 } pins)
                d = d with { CertificatePins = pins };
            break; // machine policy wins over user policy
        }
        return d;
    }

    /// <summary>Installer arguments only reach the first start: keep them in settings (true when something changed).</summary>
    public bool SaveInto(AppSettings settings)
    {
        var changed = false;
        if (Server is not null && settings.DeploymentServer != Server) { settings.DeploymentServer = Server; changed = true; }
        if (InstallOnlyOffice is { } oo && settings.InstallOnlyOffice != oo) { settings.InstallOnlyOffice = oo; changed = true; }
        if (Channel is not null && settings.UpdateChannel != Channel) { settings.UpdateChannel = Channel; changed = true; }
        return changed;
    }
}
