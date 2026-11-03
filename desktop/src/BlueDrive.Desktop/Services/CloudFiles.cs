using BlueDrive.Sync.Agent;
#if WINDOWS
using BlueDrive.Sync.Windows;
#endif

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Files On-Demand for the app (Windows Cloud Files API, BlueDrive.Sync.Windows). macOS and Linux builds (Sync-Phase 10/11) run the full sync
/// mode only: every call is a no-op there and <see cref="Supported"/> is false.
/// </summary>
public static class CloudFiles
{
    /// <summary>Files On-Demand possible for this folder: Windows 10 1809+ and an NTFS volume.</summary>
    public static bool Supported(string folder)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return false;
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).DriveFormat == "NTFS"; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
#else
        return false;
#endif
    }

    public static IOnDemandPlatform NewPlatform(OnDemandContext ctx, string version, Action<string, string> problem)
    {
#if WINDOWS
        var p = new CloudFilesPlatform(ctx, Environment.ProcessPath is { } exe ? $"{exe},0" : null, version);
        p.Problem += problem;
        return p;
#else
        throw new PlatformNotSupportedException("Tệp theo yêu cầu chỉ có trên Windows.");
#endif
    }

    /// <summary>A placeholder whose content is not on this computer.</summary>
    public static bool IsDehydrated(string path)
    {
#if WINDOWS
        return CloudPlaceholders.IsDehydrated(path);
#else
        return false;
#endif
    }

    /// <summary>"Luôn giữ trên thiết bị".</summary>
    public static bool IsPinned(FileAttributes attributes)
    {
#if WINDOWS
        return attributes.HasFlag(CloudNative.Pinned);
#else
        return false;
#endif
    }

    public static void Hydrate(string path)
    {
#if WINDOWS
        CloudPlaceholders.Hydrate(path);
#endif
    }

    public static void Dehydrate(string path)
    {
#if WINDOWS
        CloudPlaceholders.Dehydrate(path);
#endif
    }

    /// <summary>Removes the sync root registration (the folder leaves Explorer's navigation pane).</summary>
    public static void Unregister(string folder, Guid accountId, string version)
    {
#if WINDOWS
        SyncRootRegistrar.Unregister(new SyncRootInfo(folder, accountId, "", version, "", null));
#endif
    }
}
