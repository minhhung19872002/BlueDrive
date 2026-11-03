using System.Collections.ObjectModel;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using Serilog;

namespace BlueDrive.Desktop.Services;

/// <summary>Everything the app runs: accounts (one agent each), settings, notifications, the "free up space" job.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AccountStore Store { get; }
    public AppSettings Settings { get; private set; }
    public string LogsDir { get; }
    public string AppVersion { get; } = typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public ObservableCollection<AccountSession> Sessions { get; } = [];
    public BandwidthLimiter UploadLimit { get; } = new();
    public BandwidthLimiter DownloadLimit { get; } = new();

    /// <summary>(title, text) to show as a Windows notification (when enabled in settings).</summary>
    public event Action<string, string>? Notification;
    /// <summary>A document being co-edited online was opened locally in Office: (local path, editors).</summary>
    public event Action<string, IReadOnlyList<string>>? CoEditWarning;
    /// <summary>Any session changed (status, activity, transfer): the tray icon and the flyout refresh.</summary>
    public event Action? Changed;

    private readonly Timer _freeUp;
    private bool _confirmed;

    public AppHost(string? baseDir = null)
    {
        Store = new AccountStore(baseDir);
        LogsDir = Path.Combine(Store.BaseDir, "logs");
        // Before the first run's wizard there is no settings file: nothing is written to the registry until the user confirms.
        _confirmed = File.Exists(Path.Combine(Store.BaseDir, "settings.json"));
        Settings = AppSettings.Load(Store.BaseDir);
        ApplySettings();
        _freeUp = new Timer(_ => FreeUpUnused(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));
    }

    private ConditionsMonitor? _conditions;
    private string? _autoPause;

    /// <summary>Starts watching metered network / battery (not in tests: they never call it).</summary>
    public void WatchConditions() => _conditions ??= new ConditionsMonitor(() => Settings, reason =>
    {
        _autoPause = reason;
        foreach (var s in Sessions.ToList()) s.Agent.SetAutoPause(reason);
        if (reason is not null) Log.Information("{Reason}", reason);
        Changed?.Invoke();
    });

    public async Task StartAsync()
    {
        foreach (var acc in Store.Load()) await StartSessionAsync(acc);
    }

    private async Task<AccountSession> StartSessionAsync(AccountConfig acc)
    {
        var s = new AccountSession(Store, acc, Settings, UploadLimit, DownloadLimit, AppVersion);
        s.Changed += _ => Changed?.Invoke();
        s.Notify += (title, text) =>
        {
            Log.Information("{Title}: {Text}", title, text);
            if (Settings.Notifications) Notification?.Invoke(title, text);
        };
        s.Agent.Activity += (action, path) => Log.Information("[{Account}] {Action}: {Path}", acc.Username, action, path);
        s.CoEditWarning += (path, editors) =>
        {
            Log.Information("Mở bản trên máy của tài liệu đang cùng soạn: {Path}", path);
            CoEditWarning?.Invoke(path, editors);
        };
        Sessions.Add(s);
        RegisterShell();
        if (_autoPause is not null) s.Agent.SetAutoPause(_autoPause);
        try { await s.StartAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Log.Warning(ex, "Không khởi động được đồng bộ cho {Account}", acc.Username);
        }
        Changed?.Invoke();
        return s;
    }

    /// <summary>Wizard finished: the account is saved (tokens already in its store) and starts syncing.</summary>
    public async Task<AccountSession> AddAccountAsync(AccountConfig acc, IReadOnlyCollection<string> excludedRoots)
    {
        Store.Save(acc);
        if (excludedRoots.Count > 0)
        {
            Directory.CreateDirectory(Store.AccountDir(acc.Id));
            using var db = new Sync.Core.SyncStateDb(Store.StateDbPath(acc.Id));
            db.SetAccount("excluded_roots", System.Text.Json.JsonSerializer.Serialize(excludedRoots));
        }
        return await StartSessionAsync(acc);
    }

    /// <summary>"Hủy liên kết máy này": stop, remove the sync root and the account; the files stay unless <paramref name="deleteFiles"/>.</summary>
    public async Task RemoveAccountAsync(AccountSession s, bool deleteFiles)
    {
        Sessions.Remove(s);
        var dehydrated = s.Config.OnDemand && Directory.Exists(s.Config.SyncFolder)
            ? Directory.EnumerateFiles(s.Config.SyncFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(CloudFiles.IsDehydrated).ToList()
            : [];
        await s.DisposeAsync();
        if (s.Config.OnDemand)
        {
            // Like OneDrive: files that were never downloaded are only on BlueDrive; their empty placeholders are removed.
            foreach (var f in dehydrated)
            {
                try { File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Không xóa được tệp giữ chỗ {Path}", f); }
            }
            CloudFiles.Unregister(s.Config.SyncFolder, s.Config.Id, AppVersion);
        }
        Store.Remove(s.Config.Id);
        RegisterShell();
        if (deleteFiles) Uninstaller.DeleteTree(s.Config.SyncFolder);
        Changed?.Invoke();
    }

    /// <summary>
    /// Files On-Demand on / off. Off: every placeholder is downloaded first (so nothing is lost), then the sync root is removed and the
    /// account restarts in full sync mode.
    /// </summary>
    public async Task<AccountSession> SetOnDemandAsync(AccountSession s, bool onDemand, IProgress<string>? progress = null)
    {
        if (s.Config.OnDemand == onDemand) return s;
        if (!onDemand && s.Platform is not null)
        {
            var files = Directory.EnumerateFiles(s.Config.SyncFolder, "*", SearchOption.AllDirectories).Where(CloudFiles.IsDehydrated).ToList();
            for (var i = 0; i < files.Count; i++)
            {
                progress?.Report($"Đang tải về máy {i + 1}/{files.Count}: {Path.GetFileName(files[i])}");
                await Task.Run(() => CloudFiles.Hydrate(files[i]));
            }
        }
        Sessions.Remove(s);
        await s.DisposeAsync();
        if (!onDemand) CloudFiles.Unregister(s.Config.SyncFolder, s.Config.Id, AppVersion);
        var acc = s.Config with { OnDemand = onDemand };
        Store.Save(acc);
        return await StartSessionAsync(acc);
    }

    /// <summary>Explorer context menu for the current sync folders (removed with the last account).</summary>
    private void RegisterShell()
    {
        if (Environment.ProcessPath is not { } exe || !exe.EndsWith("BlueDrive.exe", StringComparison.OrdinalIgnoreCase)) return; // tests, tools
        if (PackageInfo.IsPackaged) return; // MSIX: the package's Windows 11 menu (BlueDrive.Shell.dll) instead of the classic one
        try { ExplorerMenu.Register(Sessions.Select(x => x.Config.SyncFolder).ToList(), exe); }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Không đăng ký được menu chuột phải"); }
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        settings.Save(Store.BaseDir);
        _confirmed = true;
        ApplySettings();
        _conditions?.Evaluate();
        Changed?.Invoke();
    }

    private void ApplySettings()
    {
        UploadLimit.BytesPerSecond = Settings.UploadLimitKBps * 1024L;
        DownloadLimit.BytesPerSecond = Settings.DownloadLimitKBps * 1024L;
        Settings.ApplyProxy();
        if (!_confirmed) return;
        try { StartupRegistration.Set(Settings.StartWithWindows); }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Không ghi được khởi động cùng Windows"); }
    }

    /// <summary>"Giải phóng dung lượng cho tệp không dùng sau N ngày" (Files On-Demand accounts, files on the server and not kept on the device).</summary>
    public int FreeUpUnused()
    {
        if (Settings.FreeUpAfterDays <= 0) return 0;
        var limit = DateTime.UtcNow.AddDays(-Settings.FreeUpAfterDays);
        var freed = 0;
        foreach (var s in Sessions.ToList().Where(x => x.Platform is not null))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(s.Config.SyncFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var info = new FileInfo(f);
                    if (CloudFiles.IsDehydrated(f) || CloudFiles.IsPinned(info.Attributes)) continue;
                    var used = info.LastAccessTimeUtc > info.LastWriteTimeUtc ? info.LastAccessTimeUtc : info.LastWriteTimeUtc;
                    if (used > limit) continue;
                    try
                    {
                        CloudFiles.Dehydrate(f);
                        freed++;
                    }
                    catch (IOException) { } // not synced yet / open: next time
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Giải phóng dung lượng"); }
        }
        if (freed > 0) Log.Information("Đã giải phóng dung lượng của {Count} tệp lâu không dùng", freed);
        return freed;
    }

    public async ValueTask DisposeAsync()
    {
        await _freeUp.DisposeAsync();
        _conditions?.Dispose();
        foreach (var s in Sessions.ToList()) await s.DisposeAsync();
    }
}
