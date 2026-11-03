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
    /// <summary>Pause on a metered connection / on battery below 20 % (spec 6.5).</summary>
    public bool PauseOnMetered { get; set; } = true;
    public bool PauseOnLowBattery { get; set; } = true;
    /// <summary>Server given by the installer (/server=…) or deploy.json: pre-filled in the wizard (see <see cref="Deployment"/>).</summary>
    public string? DeploymentServer { get; set; }
    /// <summary>"Cài ONLYOFFICE Desktop Editors để cùng soạn thảo tài liệu" (default on; /with-onlyoffice=0 turns it off).</summary>
    public bool InstallOnlyOffice { get; set; } = true;
    /// <summary>ONLYOFFICE was installed by BlueDrive: uninstalling BlueDrive offers to remove it too (default: keep).</summary>
    public bool OnlyOfficeInstalledByBlueDrive { get; set; }
    /// <summary>The automatic ONLYOFFICE installation was done or declined (not offered again; Settings → Giới thiệu still can).</summary>
    public bool OnlyOfficeSetupDone { get; set; }
    /// <summary>Update channel: "stable" or "beta".</summary>
    public string UpdateChannel { get; set; } = "stable";

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
        if (PackageInfo.IsPackaged)
        {
            // MSIX: the package's StartupTask (Settings → Apps → Startup), not the Run key.
            PackageInfo.SetStartupTask(enabled);
            return;
        }
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

/// <summary>Running from the MSIX package (package identity) instead of the Velopack install.</summary>
public static class PackageInfo
{
    public const string StartupTaskId = "BlueDriveStartup";
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return false;
            var len = 0;
            return GetCurrentPackageFullName(ref len, null) != AppModelErrorNoPackage;
        }
    }

    /// <summary>Started by Windows at sign-in through the package's StartupTask (start hidden, like --background).</summary>
    public static bool LaunchedAtStartup()
    {
        if (!IsPackaged || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return false;
        try { return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind == Windows.ApplicationModel.Activation.ActivationKind.StartupTask; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    public static void SetStartupTask(bool enabled)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        try
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            if (enabled && task.State == Windows.ApplicationModel.StartupTaskState.Disabled) task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            else if (!enabled && task.State == Windows.ApplicationModel.StartupTaskState.Enabled) task.Disable();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException) { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);
}
