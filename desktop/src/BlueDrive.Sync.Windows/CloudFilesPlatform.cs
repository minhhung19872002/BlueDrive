using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Windows;

/// <summary>
/// Files On-Demand on Windows (spec 7.1–7.2): the BlueDrive folder is a registered sync root; server files are placeholders downloaded
/// when opened; "Luôn giữ trên thiết bị" downloads and keeps, "Giải phóng dung lượng" frees the space; deletes / renames of view-only items
/// are refused by the filter; workspaces that are online-only by policy cannot be pinned and are freed again after use.
/// </summary>
public sealed class CloudFilesPlatform : IOnDemandPlatform
{
    private readonly OnDemandContext _ctx;
    private readonly SyncRootInfo _info;
    private readonly CloudProvider _provider;
    private readonly SyncStateDb _reader;
    private readonly object _readerGate = new();
    private FileSystemWatcher? _pins;
    private bool _started;

    public PhysicalFileSystem FileSystem { get; }
    public IPlaceholderProvider Placeholders { get; }
    /// <summary>True when Explorer shows the folder in its navigation pane (shell registration), false when only the cloud filter is registered.</summary>
    public bool ShellRegistered { get; private set; }
    public int Hydrations => _provider.Hydrations;
    public bool RegistrationLost { get; private set; }

    public event Action<string>? FileClosed;
    /// <summary>(relative path, message) — hydration failures, refused pins.</summary>
    public event Action<string, string>? Problem;

    public CloudFilesPlatform(OnDemandContext ctx, string? iconResource = null, string version = "1.0.0")
    {
        _ctx = ctx;
        _info = new SyncRootInfo(ctx.SyncFolder, ctx.AccountId, $"BlueDrive - {ctx.DisplayName}", version,
            iconResource ?? @"%SystemRoot%\system32\imageres.dll,-1043", new Uri(ctx.Server, "trash"));
        _reader = new SyncStateDb(ctx.StateDbPath);
        FileSystem = new CloudFileSystem(ctx.SyncFolder);
        Placeholders = new CloudPlaceholders(ctx.SyncFolder);
        _provider = new CloudProvider(ctx.SyncFolder, ctx.Content, CanModify);
        _provider.FileClosed += OnClosed;
        _provider.HydrationFailed += (p, m) => Problem?.Invoke(p, $"Không tải được tệp: {m}");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        var wasRegistered = SyncRootRegistrar.IsRegistered(_ctx.AccountId);
        ShellRegistered = await SyncRootRegistrar.RegisterAsync(_info);
        // Only a shell registration can be checked; a brand-new account has nothing in its state yet, so restoring is harmless there.
        RegistrationLost = ShellRegistered && !wasRegistered;
        _provider.Connect();
        _pins = new FileSystemWatcher(_ctx.SyncFolder)
        {
            IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.Attributes,
        };
        _pins.Changed += (_, e) => _ = Task.Run(() => OnAttributes(e.FullPath));
        _pins.EnableRaisingEvents = true;
        _started = true;
    }

    /// <summary>The filter asks before a delete / rename made by another program: view-only items stay.</summary>
    private bool CanModify(string remoteId)
    {
        if (remoteId.Length == 0) return true;
        RemoteItem? item;
        lock (_readerGate) item = _reader.GetRemote(remoteId);
        return item is null || item.CanEdit;
    }

    private string Rel(string full) => Path.GetRelativePath(_ctx.SyncFolder, full).Replace('\\', '/');

    private bool OnlineOnly(string rel)
    {
        var top = rel.Split('/')[0];
        return _ctx.OnlineOnlyFolders.Contains(top);
    }

    /// <summary>Explorer changed the pin state ("Luôn giữ trên thiết bị" / "Giải phóng dung lượng").</summary>
    private void OnAttributes(string full)
    {
        // Right after a download (or while another program still holds the file) the change can fail for a moment: try again a few times.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ApplyAttributes(full);
                return;
            }
            catch (IOException) when (attempt < 5) { Thread.Sleep(300 * attempt); }
            catch (IOException ex)
            {
                Problem?.Invoke(Rel(full), ex.Message);
                return;
            }
        }
    }

    private void ApplyAttributes(string full)
    {
        try
        {
            if (!File.Exists(full) && !Directory.Exists(full)) return;
            var attrs = File.GetAttributes(full);
            var rel = Rel(full);
            if (attrs.HasFlag(CloudNative.Pinned))
            {
                if (OnlineOnly(rel))
                {
                    CloudPlaceholders.Pin(full, false);
                    Problem?.Invoke(rel, "Không gian này chỉ xem trực tuyến theo chính sách, không thể giữ tệp trên máy.");
                }
                else if (CloudPlaceholders.IsDehydrated(full)) CloudPlaceholders.Hydrate(full);
            }
            else if (attrs.HasFlag(CloudNative.Unpinned) && File.Exists(full) && !CloudPlaceholders.IsDehydrated(full))
                CloudPlaceholders.Dehydrate(full);
        }
        catch (UnauthorizedAccessException) { }
    }

    private void OnClosed(string rel)
    {
        FileClosed?.Invoke(rel);
        // Online-only workspaces: the content goes away again once the file is closed (and already on the server).
        if (!OnlineOnly(rel)) return;
        var full = Path.Combine(_ctx.SyncFolder, rel.Replace('/', '\\'));
        try
        {
            if (File.Exists(full) && !CloudPlaceholders.IsDehydrated(full)) CloudPlaceholders.Dehydrate(full);
        }
        catch (IOException) { } // not in sync yet (edited): freed after the upload, on the next close
    }

    public Task UnregisterAsync()
    {
        _pins?.Dispose();
        _pins = null;
        _provider.Dispose();
        SyncRootRegistrar.Unregister(_info);
        _started = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _pins?.Dispose();
        _provider.Dispose();
        lock (_readerGate) _reader.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The sync folder when it holds placeholders: a file that was never downloaded is removed for good (no Recycle Bin round trip).</summary>
public sealed class CloudFileSystem(string root) : PhysicalFileSystem(root)
{
    public override void Delete(string path)
    {
        var abs = Abs(path);
        if (CloudPlaceholders.IsDehydrated(abs))
        {
            File.Delete(abs);
            return;
        }
        base.Delete(path);
    }
}
