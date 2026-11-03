using System.Security.Cryptography;
using System.Text;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Agent;

public enum AgentState { Starting, Idle, Syncing, Offline, Paused, NeedsSignIn, UpdateRequired, Error, Revoked, Wiped }

public sealed record AgentStatus(AgentState State, string Message, DateTime? LastSyncAt, int PendingMassDelete, IReadOnlyList<string> Problems);

public sealed class AgentOptions
{
    public required Uri Server { get; init; }
    public required string SyncFolder { get; init; }
    public required string StateDbPath { get; init; }
    public required ITokenStore Tokens { get; init; }
    public string UserDisplayName { get; init; } = "Người dùng";
    public string DeviceName { get; init; } = Environment.MachineName;
    public string AppVersion { get; init; } = typeof(SyncAgent).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    /// <summary>Local edits are synced once the folder has been quiet this long (debounce + "file stable", spec 6.1).</summary>
    public TimeSpan LocalQuiet { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>A folder that never goes quiet (a growing log file…) is synced anyway after this long.</summary>
    public TimeSpan LocalMaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Periodic full pass (scan + change feed), also catches anything the watcher / push missed (spec 6.1: 15 minutes).</summary>
    public TimeSpan PeriodicPass { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(30);
    public bool UseRecycleBin { get; init; } = true;
    public RemoteStoreOptions Transfer { get; init; } = new();
    /// <summary>Tests: in-memory API handler (and SignalR over long polling).</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; init; }
    /// <summary>Identifies the account's sync root to the OS (Files On-Demand).</summary>
    public Guid AccountId { get; init; } = Guid.NewGuid();
    /// <summary>Files On-Demand (Windows Cloud Files API); null = full sync mode (every file downloaded).</summary>
    public Func<OnDemandContext, IOnDemandPlatform>? OnDemand { get; init; }
}

/// <summary>
/// The background sync of one account in full sync mode (spec 7.3): runs <see cref="SyncEngine"/> passes when the server pushes a
/// change, when the sync folder changes (debounced), periodically, or on demand; backs off while offline; stops on revocation, honours a
/// signed remote wipe, and never runs a pass when the sync folder (or a root folder) has disappeared, so a removed disk or a deleted
/// folder is never mirrored to the server as mass deletion.
/// </summary>
public sealed class SyncAgent : IAsyncDisposable
{
    private const string InitializedKey = "sync_folder";
    private readonly AgentOptions _o;
    private readonly SyncStateDb _db;
    private readonly HttpClient _http;
    private readonly HttpRemoteStore _remote;
    private readonly PhysicalFileSystem _fs;
    private readonly SyncEngine _engine;
    private readonly DesktopAuthenticator _auth;
    private readonly IOnDemandPlatform? _platform;
    private readonly OnDemandContext? _onDemand;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private SyncNotifications? _notes;
    private LocalChangeWatcher? _watcher;
    private Task? _loop;
    private DateTime? _remoteDue, _localDue, _localFirst, _retryDue;
    private TimeSpan _backoff = TimeSpan.Zero;
    private bool _paused;

    public AgentStatus Status { get; private set; } = new(AgentState.Starting, "Đang khởi động…", null, 0, []);
    public event Action<AgentStatus>? StatusChanged;
    /// <summary>Activity feed for the UI (action, path).</summary>
    public event Action<string, string>? Activity;

    public SyncEngine Engine => _engine;
    public SyncStateDb State => _db;
    public HttpRemoteStore Remote => _remote;

    public SyncAgent(AgentOptions o)
    {
        _o = o;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.StateDbPath))!);
        _db = new SyncStateDb(o.StateDbPath);
        var server = BlueDriveHttp.Normalize(o.Server);
        _auth = new DesktopAuthenticator(server, o.HandlerFactory?.Invoke());
        _http = BlueDriveHttp.Create(server, o.Tokens, _auth, o.HandlerFactory?.Invoke());
        _remote = new HttpRemoteStore(_http, o.Transfer);
        if (o.OnDemand is not null)
        {
            _onDemand = new OnDemandContext(Path.GetFullPath(o.SyncFolder), o.AccountId, o.UserDisplayName, server, _remote, Path.GetFullPath(o.StateDbPath));
            _platform = o.OnDemand(_onDemand);
            _platform.FileClosed += _ => RequestLocal();
        }
        _fs = _platform?.FileSystem ?? new PhysicalFileSystem(o.SyncFolder, o.UseRecycleBin);
        _fs.Skipped += (path, why) => Activity?.Invoke("Bỏ qua", $"{path}: {why}");
        _engine = new SyncEngine(_db, _remote, _fs, SystemClock.Instance,
            new SyncEngineOptions { UserDisplayName = o.UserDisplayName, DeviceName = o.DeviceName }, _platform?.Placeholders);
        _engine.Activity += (action, path) => Activity?.Invoke(action, path);
    }

    // ---------------------------------------------------------------- control

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_db.GetAccount(InitializedKey) is null)
        {
            // First start: the folder is created (or adopted when it already holds files: they are matched by path + hash).
            Directory.CreateDirectory(_fs.Root);
            _db.SetAccount(InitializedKey, _fs.Root);
        }
        if (_platform is not null && Directory.Exists(_fs.Root)) await _platform.StartAsync(ct);
        if (Directory.Exists(_fs.Root)) _watcher = NewWatcher();
        _notes = new SyncNotifications(BlueDriveHttp.Normalize(_o.Server), () => Task.FromResult(_o.Tokens.Load()?.AccessToken), _o.HandlerFactory)
        {
            PollInterval = _o.PeriodicPass,
        };
        _notes.SyncRequested += _ => RequestRemote();
        _notes.DeviceStatusChanged += _ => RequestRemote();
        await _notes.StartAsync(ct);
        RequestRemote();
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    private LocalChangeWatcher NewWatcher()
    {
        var w = new LocalChangeWatcher(_fs.Root);
        w.Changed += (_, overflow) =>
        {
            if (overflow) RequestRemote();
            else RequestLocal();
        };
        return w;
    }

    /// <summary>"Đồng bộ ngay".</summary>
    public void SyncNow() => RequestRemote();

    public void Pause(bool paused)
    {
        _paused = paused;
        if (paused) SetStatus(AgentState.Paused, "Đã tạm dừng đồng bộ.");
        else RequestRemote();
    }

    /// <summary>Answer to "Bạn vừa xóa N tệp. Xóa cả trên BlueDrive?" for every root waiting for it.</summary>
    public void ResolveMassDelete(bool deleteOnServer)
    {
        foreach (var r in _db.Roots().Where(r => r.PendingMassDelete > 0)) _engine.ResolveMassDelete(r.RootId, deleteOnServer);
        RequestRemote();
    }

    private void RequestRemote()
    {
        lock (_gate) _remoteDue = DateTime.UtcNow;
        _wake.Release();
    }

    private void RequestLocal()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _localFirst ??= now;
            _localDue = Min(now + _o.LocalQuiet, _localFirst.Value + _o.LocalMaxDelay);
        }
        _wake.Release();
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Waits until the next pass is due; null when stopping.</summary>
    private async Task<bool> WaitDueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTime? due;
            lock (_gate)
            {
                due = new[] { _remoteDue, _localDue, _retryDue }.Where(d => d is not null).Min();
                if (due is not null && due <= DateTime.UtcNow)
                {
                    _remoteDue = _localDue = _localFirst = _retryDue = null;
                    return true;
                }
            }
            var wait = due is null ? _o.PeriodicPass : due.Value - DateTime.UtcNow;
            try
            {
                if (!await _wake.WaitAsync(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, ct) && due is null) return true; // periodic pass
            }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (await WaitDueAsync(ct))
        {
            if (_paused) continue;
            if (Status.State is AgentState.NeedsSignIn or AgentState.Revoked or AgentState.Wiped or AgentState.UpdateRequired) continue;
            try
            {
                await PassAsync(ct);
                _backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (SignInRequiredException ex)
            {
                SetStatus(AgentState.NeedsSignIn, $"Cần đăng nhập lại. {ex.Message}");
            }
            catch (RemoteTransientException ex)
            {
                RetryLater(AgentState.Offline, $"Không kết nối được máy chủ BlueDrive. {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RemotePermanentException)
            {
                RetryLater(AgentState.Error, ex.Message);
            }
        }
    }

    private void RetryLater(AgentState state, string message)
    {
        // Exponential backoff with jitter: 1 s → 30 min.
        _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(_o.MaxBackoff.Ticks, _backoff.Ticks * 2));
        var delay = _backoff * (0.8 + Random.Shared.NextDouble() * 0.4);
        lock (_gate) _retryDue = DateTime.UtcNow + delay;
        SetStatus(state, $"{message} Thử lại sau {Math.Ceiling(delay.TotalSeconds)} giây.");
    }

    // ---------------------------------------------------------------- one pass

    private async Task PassAsync(CancellationToken ct)
    {
        if (!await DeviceCheckAsync(ct)) return;
        if (!Directory.Exists(_fs.Root))
        {
            SetStatus(AgentState.Error, $"Không tìm thấy thư mục đồng bộ \"{_fs.Root}\" (đã bị xóa, đổi tên hoặc ổ đĩa đã rút ra). Đồng bộ tạm dừng, không có gì bị xóa trên BlueDrive.");
            return;
        }
        _watcher ??= NewWatcher();
        await RefreshRootsAsync(ct);
        foreach (var r in _db.Roots().Where(r => r.Enabled))
            if (!Directory.Exists(_fs.Abs(r.LocalPath)) && _db.Items(r.RootId).Count > 0)
            {
                SetStatus(AgentState.Error, $"Thư mục \"{r.LocalPath}\" không còn trong thư mục BlueDrive. Đồng bộ tạm dừng để không xóa dữ liệu trên máy chủ; hãy khôi phục thư mục (Thùng rác) rồi đồng bộ lại.");
                return;
            }

        SetStatus(AgentState.Syncing, "Đang đồng bộ…");
        var result = await _engine.SyncAsync(ct);
        var problems = result.Errors.ToList();
        var msg = result.MassDeletePending > 0
            ? $"Bạn vừa xóa {result.MassDeletePending} tệp. Xóa cả trên BlueDrive?"
            : problems.Count > 0 ? $"Đã đồng bộ, {problems.Count} mục chưa đồng bộ được." : "Đã đồng bộ.";
        Status = new AgentStatus(AgentState.Idle, msg, DateTime.UtcNow, result.MassDeletePending, problems);
        StatusChanged?.Invoke(Status);
    }

    /// <summary>Folders for the roots: "Tài liệu của tôi", one per workspace, "Được chia sẻ với tôi". A root keeps its folder once chosen.</summary>
    private async Task RefreshRootsAsync(CancellationToken ct)
    {
        var roots = await _remote.RootsAsync(ct);
        var known = _db.Roots().ToDictionary(r => r.RootId);
        var used = new HashSet<string>(known.Values.Select(r => r.LocalPath), StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots)
        {
            // Online-only workspaces (policy): placeholders only with Files On-Demand; not synced at all in full mode.
            var enabled = !r.OnlineOnly || _platform is not null;
            string folder;
            if (known.TryGetValue(r.RootId, out var existing))
            {
                folder = existing.LocalPath;
                if (existing.Enabled != enabled) _db.UpsertRoot(r.RootId, folder, enabled);
            }
            else
            {
                folder = NameMapper.NextFree(NameMapper.ToLocal(r.Name), n => used.Contains(n) || IgnoreRules.IsIgnored(n) || n == IgnoreRules.NotSyncedFolder);
                used.Add(folder);
                _db.UpsertRoot(r.RootId, folder, enabled);
            }
            if (_onDemand is not null)
            {
                if (r.OnlineOnly) _onDemand.OnlineOnlyFolders.Add(folder);
                else _onDemand.OnlineOnlyFolders.Remove(folder);
            }
        }
    }

    // ---------------------------------------------------------------- device: revocation, remote wipe, minimum version

    private async Task<bool> DeviceCheckAsync(CancellationToken ct)
    {
        var d = await _remote.DeviceStatusAsync(_o.AppVersion, ct);
        if (d.Status == "revoked" || d.Status == "wiped")
        {
            _o.Tokens.Clear();
            SetStatus(AgentState.Revoked, "Máy tính này đã bị thu hồi quyền đồng bộ. Dữ liệu trên máy được giữ nguyên; hãy đăng nhập lại nếu được cấp lại quyền.");
            return false;
        }
        if (d.Wipe is { } wipe)
        {
            if (VerifyWipe(d.DeviceId, wipe))
            {
                await WipeAsync(ct);
                return false;
            }
            Activity?.Invoke("Cảnh báo", "Lệnh xóa dữ liệu từ xa có chữ ký không hợp lệ đã bị bỏ qua.");
        }
        if (d.UpdateRequired)
        {
            SetStatus(AgentState.UpdateRequired, $"Cần cập nhật BlueDrive Desktop lên phiên bản {d.MinClientVersion} trở lên để tiếp tục đồng bộ.");
            return false;
        }
        return true;
    }

    internal bool VerifyWipe(Guid deviceId, HttpRemoteStore.WipeCommand wipe)
    {
        var secret = _o.Tokens.Load()?.DeviceSecret;
        if (string.IsNullOrEmpty(secret)) return false;
        var at = DateTime.SpecifyKind(wipe.RequestedAt.ToUniversalTime(), DateTimeKind.Utc);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"wipe:{deviceId}:{at:O}"));
        byte[] given;
        try { given = Convert.FromHexString(wipe.Signature); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>Remote wipe (spec 3.4): the synced files and the local state are deleted for good, the server is told, the tokens go.</summary>
    private async Task WipeAsync(CancellationToken ct)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_platform is not null) await _platform.UnregisterAsync();
        if (Directory.Exists(_fs.Root))
            foreach (var r in _db.Roots())
            {
                var dir = _fs.Abs(r.LocalPath);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        var notSynced = _fs.Abs(IgnoreRules.NotSyncedFolder);
        if (Directory.Exists(notSynced)) Directory.Delete(notSynced, recursive: true);
        _db.ClearAll();
        await _remote.ConfirmWipedAsync(ct);
        _o.Tokens.Clear();
        SetStatus(AgentState.Wiped, "Dữ liệu BlueDrive trên máy tính này đã được xóa theo yêu cầu của quản trị viên.");
    }

    private void SetStatus(AgentState state, string message)
    {
        Status = Status with { State = state, Message = message };
        StatusChanged?.Invoke(Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _wake.Release();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _watcher?.Dispose();
        if (_notes is not null) await _notes.DisposeAsync();
        if (_platform is not null) await _platform.DisposeAsync();
        _http.Dispose();
        _db.Dispose();
        _stop.Dispose();
    }
}
