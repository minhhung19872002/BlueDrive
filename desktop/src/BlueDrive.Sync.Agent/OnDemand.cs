using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Agent;

/// <summary>What a Files On-Demand platform (it opens its own read connection to <see cref="StateDbPath"/>: callbacks run on other threads) (Windows Cloud Files, later macOS File Provider) gets from the agent.</summary>
public sealed record OnDemandContext(string SyncFolder, Guid AccountId, string DisplayName, Uri Server, IRemoteRangeReader Content, string StateDbPath)
{
    /// <summary>Root folders (relative) whose workspace is "online-only" by policy: files there are never kept on the disk.</summary>
    public HashSet<string> OnlineOnlyFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Placeholder-based sync folder: the agent's file system, placeholder operations, and the OS registration (sync root).</summary>
public interface IOnDemandPlatform : IAsyncDisposable
{
    PhysicalFileSystem FileSystem { get; }
    IPlaceholderProvider Placeholders { get; }

    /// <summary>A file in the sync folder was closed after being written: worth a sync round soon.</summary>
    event Action<string>? FileClosed;

    /// <summary>Registers the sync folder with the OS (Explorer navigation pane, status icons) and starts serving hydration requests.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Set by <see cref="StartAsync"/>: the folder had to be registered again although BlueDrive had registered it (the OS dropped the registration
    /// and with it the placeholders of files not on the computer). The next pass then downloads missing items back instead of deleting them.
    /// </summary>
    bool RegistrationLost => false;

    /// <summary>Removes the OS registration (sign out, remote wipe). Placeholders left behind become plain empty files.</summary>
    Task UnregisterAsync();
}
