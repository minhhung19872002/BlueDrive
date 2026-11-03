using System.Net;
using System.Text.Json;
using Microsoft.Win32;

namespace BlueDrive.Desktop.Services;

public enum ThemeChoice { System, Light, Dark }

public enum ProxyChoice { System, None, Manual }

/// <summary>Application settings (no secrets): %LOCALAPPDATA%\BlueDrive\settings.json.</summary>
public sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>KB/s, 0 = không giới hạn.</summary>
    public int UploadLimitKBps { get; set; }
    public int DownloadLimitKBps { get; set; }
    public ProxyChoice Proxy { get; set; } = ProxyChoice.System;
    public string? ProxyAddress { get; set; }
    /// <summary>Files On-Demand: free the space of files not used for this many days (0 = never), like Storage Sense.</summary>
    public int FreeUpAfterDays { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static AppSettings Load(string dir)
    {
        var path = Path.Combine(dir, "settings.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static readonly IWebProxy SystemProxy = HttpClient.DefaultProxy;

    /// <summary>Proxy for every connection of the app (API, uploads, SignalR).</summary>
    public void ApplyProxy()
    {
        HttpClient.DefaultProxy = Proxy switch
        {
            ProxyChoice.None => new WebProxy(),
            ProxyChoice.Manual when Uri.TryCreate(ProxyAddress, UriKind.Absolute, out var u) => new WebProxy(u) { UseDefaultCredentials = true },
            _ => SystemProxy,
        };
    }
}

/// <summary>"Khởi động cùng Windows": HKCU\...\Run (per user, no admin rights).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BlueDrive";

    public static void Set(bool enabled, string? exePath = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, $"\"{exePath ?? Environment.ProcessPath}\" --background");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    public static bool IsSet()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string;
    }
}
