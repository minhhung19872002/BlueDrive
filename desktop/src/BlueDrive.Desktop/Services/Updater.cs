using Serilog;
using Velopack;
using Velopack.Sources;

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Automatic updates (spec 11) with Velopack: the BlueDrive server publishes the releases under <c>/downloads/desktop/</c>
/// (<c>releases.stable.json</c>, <c>releases.beta.json</c> and their packages, plus the summary <c>releases.json</c> for the web page).
/// The app checks at start and every 6 hours, downloads in the background and applies the update when the user restarts it or quits.
/// Only an installed copy (Setup.exe) updates itself; a development build or the MSIX package (updated by App Installer) does not.
/// </summary>
public sealed class Updater(Func<string?> server, Func<string> channel, Func<bool> disabled)
{
    public const string FeedPath = "downloads/desktop";

    private UpdateManager? _mgr;
    private UpdateInfo? _pending;

    /// <summary>Version downloaded and waiting for a restart, if any.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();
    public string? LastError { get; private set; }
    public DateTime? LastCheck { get; private set; }

    public event Action? Changed;

    public static string FeedUrl(string server) => $"{server.TrimEnd('/')}/{FeedPath}/";

    public static bool IsInstalled
    {
        get
        {
            try { return new UpdateManager(new SimpleWebSource("https://localhost/")).IsInstalled; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>True when a newer version was downloaded.</summary>
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        if (disabled() || server() is not { } s) return false;
        try
        {
            var ch = channel();
            // Switching beta → stable installs the (older) stable version.
            _mgr = new UpdateManager(new SimpleWebSource(FeedUrl(s)), new UpdateOptions { ExplicitChannel = ch, AllowVersionDowngrade = ch == "stable" });
            if (!_mgr.IsInstalled) return false;
            LastCheck = DateTime.Now;
            var info = await _mgr.CheckForUpdatesAsync();
            LastError = null;
            if (info is null) return false;
            await _mgr.DownloadUpdatesAsync(info, cancelToken: ct);
            _pending = info;
            Log.Information("Đã tải bản cập nhật BlueDrive {Version} ({Channel})", PendingVersion, ch);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = "Không kiểm tra được bản cập nhật. Sẽ thử lại sau.";
            Log.Warning(ex, "Kiểm tra cập nhật");
            return false;
        }
        finally { Changed?.Invoke(); }
    }

    /// <summary>"Khởi động lại để cập nhật": the process exits and the new version starts.</summary>
    public void ApplyAndRestart()
    {
        if (_mgr is not null && _pending is not null) _mgr.ApplyUpdatesAndRestart(_pending, ["--background"]);
    }

    /// <summary>On quit: the downloaded update is installed after the app has exited (no restart).</summary>
    public void ApplyOnExit()
    {
        if (_mgr is null || _pending is null) return;
        try { _mgr.WaitExitThenApplyUpdates(_pending, silent: true, restart: false); }
        catch (Exception ex) { Log.Warning(ex, "Cập nhật khi thoát"); }
    }
}
