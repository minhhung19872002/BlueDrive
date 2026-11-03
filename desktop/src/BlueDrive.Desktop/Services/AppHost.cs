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

    /// <summary>Why <paramref name="target"/> cannot become the account's folder, or null (spec 8.3 "đổi vị trí thư mục").</summary>
    public string? MoveProblem(AccountSession s, string target)
    {
        string to;
        try { to = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Đường dẫn không hợp lệ."; }
        var from = Path.GetFullPath(s.Config.SyncFolder).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(to, from, StringComparison.OrdinalIgnoreCase)) return "Đây đã là thư mục BlueDrive của tài khoản này.";
        if (Path.GetPathRoot(to) == to + Path.DirectorySeparatorChar || Path.GetPathRoot(to) == to) return "Không dùng trực tiếp thư mục gốc của ổ đĩa.";
        if (Inside(to, from) || Inside(from, to)) return "Không thể chuyển thư mục vào bên trong chính nó (hoặc ngược lại).";
        if (Store.Load().Any(a => a.Id != s.Config.Id && (Inside(to, a.SyncFolder) || Inside(a.SyncFolder, to))))
            return "Thư mục này đang được một tài khoản BlueDrive khác dùng.";
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return "Hãy chọn một thư mục trống (hoặc chưa có).";
        return null;

        static bool Inside(string path, string folder) =>
            (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves the account's BlueDrive folder (spec 8.3): the account stops, files not on this computer are left on BlueDrive (their
    /// placeholders removed, downloaded ones become ordinary files), the folder is moved (same drive) or copied then removed (another drive),
    /// and the account starts again there. Nothing is deleted on the server: unchanged files are recognised by path and content, and a
    /// sync root registered anew puts missing items back instead of treating them as deleted.
    /// </summary>
    public async Task<AccountSession> MoveSyncFolderAsync(AccountSession s, string target, IProgress<string>? progress = null)
    {
        if (MoveProblem(s, target) is { } problem) throw new IOException(problem);
        var from = Path.GetFullPath(s.Config.SyncFolder);
        var to = Path.GetFullPath(target);
        progress?.Report("Đang dừng đồng bộ…");
        Sessions.Remove(s);
        await s.DisposeAsync();
        var onDemand = s.Config.OnDemand;
        if (onDemand)
        {
            foreach (var f in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                         .Where(CloudFiles.IsDehydrated).ToList())
            {
                try { File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Không xóa được tệp giữ chỗ {Path}", f); }
            }
            CloudFiles.Unregister(from, s.Config.Id, AppVersion);
        }
        progress?.Report("Đang chuyển tệp sang vị trí mới…");
        try
        {
            await Task.Run(() =>
            {
            if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(to)) Directory.Delete(to); // empty (checked above)
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
            }
            else CopyTree(from, to, progress); // the old folder goes only once the account runs from the copy
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file open in another program, no space…: the account carries on where it was (a new registration puts placeholders back).
            Log.Warning(ex, "Không chuyển được thư mục BlueDrive {From} → {To}", from, to);
            if (Directory.Exists(from)) await StartSessionAsync(s.Config);
            throw new IOException($"Không chuyển được thư mục: {ex.Message} Đồng bộ tiếp tục ở vị trí cũ.", ex);
        }
        var acc = s.Config with { SyncFolder = to, OnDemand = onDemand && OnDemandSupport.IsSupported(to) };
        Store.Save(acc);
        if (Directory.Exists(from))
        {
            try { Uninstaller.DeleteTree(from); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Chưa xóa được thư mục cũ {From}", from); }
        }
        Log.Information("Đã chuyển thư mục BlueDrive của {Account}: {From} → {To}", acc.Username, from, to);
        RegisterShell();
        return await StartSessionAsync(acc);
    }

    private static void CopyTree(string from, string to, IProgress<string>? progress)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, files[i]));
            progress?.Report($"Đang chép {i + 1}/{files.Count}: {Path.GetFileName(files[i])}");
            File.Copy(files[i], target);
            var attributes = File.GetAttributes(files[i]);
            File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly); // view-only files are read-only: the time first
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(files[i]));
            File.SetAttributes(target, attributes);
        }
    }

    /// <summary>Explorer context menu for the current sync folders (removed with the last account).</summary>
    private void RegisterShell()
    {
        if (Environment.ProcessPath is not { } exe || !exe.EndsWith("BlueDrive.exe", StringComparison.OrdinalIgnoreCase)) return; // tests, tools
        var folders = Sessions.Select(x => x.Config.SyncFolder).ToList();
        try
        {
            // MSIX: started through its app execution alias (an exe inside the package folder cannot be started by path).
            var launcher = PackageInfo.IsPackaged
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "BlueDrive.exe")
                : exe;
            if (Settings.CoEditOnDoubleClick) OfficeDoubleClick.Register(Store.BaseDir, folders, launcher);
            else OfficeDoubleClick.Unregister(Store.BaseDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warning(ex, "Không đăng ký được tùy chọn nhấp đúp tệp Office");
        }
        if (PackageInfo.IsPackaged) return; // MSIX: the package's Windows 11 menu (BlueDrive.Shell.dll) instead of the classic one
        try { ExplorerMenu.Register(folders, exe); }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Không đăng ký được menu chuột phải"); }
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        settings.Save(Store.BaseDir);
        _confirmed = true;
        ApplySettings();
        if (Sessions.Count > 0) RegisterShell(); // "nhấp đúp tệp Office" may have changed
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
