using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;

namespace BlueDrive.Desktop.Services;

/// <summary>Current transfer shown in the flyout ("Đang tải lên báo cáo.docx — 45%").</summary>
public sealed record TransferInfo(string Name, bool Upload, long Done, long Total)
{
    public int Percent => Total > 0 ? (int)Math.Clamp(Done * 100 / Total, 0, 100) : 0;
}

/// <summary>One signed-in account while the app runs: its config, agent, and a read-only view of its state for the UI.</summary>
public sealed class AccountSession : IAsyncDisposable
{
    private readonly AccountStore _store;
    private readonly object _readGate = new();
    private SyncStateDb? _reader;

    public AccountConfig Config { get; private set; }
    public SyncAgent Agent { get; }
    public IOnDemandPlatform? Platform { get; private set; }
    public TransferInfo? Transfer { get; private set; }

    /// <summary>Raised (on a background thread) whenever status, activity or transfer progress changed.</summary>
    public event Action<AccountSession>? Changed;
    /// <summary>(title, text) worth a Windows notification: new conflict, error needing action, sign-in needed, almost out of space.</summary>
    public event Action<string, string>? Notify;

    public AccountSession(AccountStore store, AccountConfig config, AppSettings settings, BandwidthLimiter up, BandwidthLimiter down, string appVersion)
    {
        _store = store;
        Config = config;
        var transfer = new RemoteStoreOptions
        {
            UploadLimit = up, DownloadLimit = down,
            Progress = (name, upload, done, total) =>
            {
                Transfer = done >= total && total > 0 ? null : new TransferInfo(name, upload, done, total);
                Changed?.Invoke(this);
            },
        };
        Agent = new SyncAgent(new AgentOptions
        {
            Server = new Uri(config.ServerUrl), SyncFolder = config.SyncFolder, StateDbPath = store.StateDbPath(config.Id), Tokens = store.Tokens(config.Id),
            UserDisplayName = config.DisplayName, AppVersion = appVersion, AccountId = config.Id, Transfer = transfer,
            OnDemand = config.OnDemand && CloudFiles.Supported(config.SyncFolder) ? ctx => Platform = NewPlatform(ctx, appVersion) : null,
        });
        var lastConflicts = -1;
        var lastState = AgentState.Starting;
        var quotaWarned = false;
        Agent.StatusChanged += s =>
        {
            if (s.State != AgentState.Syncing) Transfer = null;
            var conflicts = Conflicts().Count;
            if (lastConflicts >= 0 && conflicts > lastConflicts)
                Notify?.Invoke("Có xung đột mới", "Một tệp đã bị sửa ở cả máy này và nơi khác. BlueDrive đã giữ cả hai bản — mở BlueDrive để chọn bản giữ lại.");
            lastConflicts = conflicts;
            if (s.State != lastState && s.State is AgentState.NeedsSignIn or AgentState.Revoked or AgentState.UpdateRequired or AgentState.Error)
                Notify?.Invoke(StateTitle(s.State), s.Message);
            // Out of space (413): the changes stay on this computer and go up once space is freed (spec 6.5).
            var full = s.Problems.FirstOrDefault(p => p.Contains("Không đủ dung lượng", StringComparison.Ordinal));
            if (full is not null && !quotaWarned)
                Notify?.Invoke("Hết dung lượng BlueDrive", "Một số tệp chưa tải lên được vì hết dung lượng. Thay đổi vẫn được giữ trên máy và sẽ tự tải lên khi có chỗ trống.");
            quotaWarned = full is not null;
            if (s.State == AgentState.Idle && QuotaPercent >= 95 && lastState == AgentState.Syncing)
                Notify?.Invoke("Sắp hết dung lượng", $"Bạn đã dùng {QuotaPercent}% dung lượng BlueDrive.");
            lastState = s.State;
            Changed?.Invoke(this);
        };
        Agent.Activity += (_, _) => Changed?.Invoke(this);
        Agent.StatusChanged += s =>
        {
            if (s.State == AgentState.Idle) _ = CheckSharesAsync();
        };
        if (config.Paused) Agent.Pause(true);
    }

    private IOnDemandPlatform NewPlatform(OnDemandContext ctx, string version) =>
        CloudFiles.NewPlatform(ctx, version, (path, message) => Notify?.Invoke("BlueDrive", $"{path}: {message}"));

    public Task StartAsync() => Agent.StartAsync();

    // Only what arrives while the app runs; at most once a minute.
    private DateTime _sharesSince = DateTime.UtcNow;
    private DateTime _sharesChecked = DateTime.MinValue;
    private int _checkingShares;

    /// <summary>"Chia sẻ mới cho bạn" (spec 8.2): new share notifications of the server become system notifications.</summary>
    private async Task CheckSharesAsync()
    {
        if (DateTime.UtcNow - _sharesChecked < TimeSpan.FromMinutes(1) || Interlocked.Exchange(ref _checkingShares, 1) == 1) return;
        try
        {
            _sharesChecked = DateTime.UtcNow;
            var fresh = (await Agent.Api.UnreadNotificationsAsync())
                .Where(n => n.Type == "share" && DateTime.SpecifyKind(n.CreatedAt, DateTimeKind.Utc) > _sharesSince).OrderBy(n => n.CreatedAt).ToList();
            if (fresh.Count == 0) return;
            _sharesSince = DateTime.SpecifyKind(fresh[^1].CreatedAt, DateTimeKind.Utc);
            foreach (var n in fresh.TakeLast(3)) Notify?.Invoke("Chia sẻ mới cho bạn", n.Title);
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException or RemoteNotFoundException or SignInRequiredException) { }
        finally { Interlocked.Exchange(ref _checkingShares, 0); }
    }

    public static string StateTitle(AgentState s) => s switch
    {
        AgentState.NeedsSignIn or AgentState.Revoked => "Cần đăng nhập lại",
        AgentState.UpdateRequired => "Cần cập nhật BlueDrive",
        AgentState.Error => "BlueDrive gặp lỗi",
        _ => "BlueDrive",
    };

    /// <summary>The personal root's quota (Tài liệu của tôi): "Đã dùng 1,2 GB / 10 GB".</summary>
    public HttpRemoteStore.RootDto? Personal => Agent.Roots.FirstOrDefault(r => r.Kind == "personal");

    public int QuotaPercent => Personal is { QuotaBytes: > 0 } p ? (int)Math.Min(100, p.UsedBytes * 100 / p.QuotaBytes) : 0;

    // ---------------------------------------------------------------- read-only state for the UI (own connection: the engine runs elsewhere)

    private T Read<T>(Func<SyncStateDb, T> read, T fallback)
    {
        var path = _store.StateDbPath(Config.Id);
        if (!File.Exists(path)) return fallback;
        lock (_readGate)
        {
            try
            {
                _reader ??= new SyncStateDb(path);
                return read(_reader);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { return fallback; }
        }
    }

    /// <summary>The BlueDrive item at a path of this account's folder (context menu commands), or null when not synced (yet).</summary>
    public LocalIndex.Hit? FindItem(string absPath) => Read(db => LocalIndex.Find(db, Config.SyncFolder, absPath), null);

    public bool Owns(string absPath)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(Config.SyncFolder), Path.GetFullPath(absPath));
        return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
    }

    public IReadOnlyList<ActivityEntry> Activity(int take = 30) => Read(db => db.Activity(take), []);

    public IReadOnlyList<ConflictEntry> Conflicts() => Read(db => db.Conflicts(), []);

    public IReadOnlyList<RootState> RootStates() => Read(db => db.Roots(), []);

    /// <summary>Folders of a root (selective sync tree) and the ones left out of this computer.</summary>
    public IReadOnlyList<RemoteItem> FoldersOf(string rootId) => Read(db => db.RemoteTree(rootId).Values.Where(i => i.IsFolder).ToList(), []);

    public IReadOnlySet<string> ExcludedFoldersOf(string rootId) => Read(db =>
        db.GetAccount(SyncEngine.ExcludedFoldersKey(rootId)) is { } json
            ? System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(json) ?? []
            : new HashSet<string>(), (IReadOnlySet<string>)new HashSet<string>());

    /// <summary>Absolute path of an activity / conflict path (stored relative to its root, or to the sync folder).</summary>
    public string Locate(string rootIdOrEmpty, string path)
    {
        if (rootIdOrEmpty.Length > 0) return Agent.LocalPathOf(rootIdOrEmpty, path);
        var direct = Path.Combine(Agent.SyncFolder, path.Replace('/', '\\'));
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;
        foreach (var r in RootStates())
        {
            var p = Agent.LocalPathOf(r.RootId, path);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        return direct;
    }

    public void SetPaused(TimeSpan? duration)
    {
        Agent.PauseFor(duration);
        Config = Config with { Paused = duration == Timeout.InfiniteTimeSpan };
        _store.Save(Config);
        Changed?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        await Agent.DisposeAsync();
        lock (_readGate) _reader?.Dispose();
    }
}

/// <summary>Files On-Demand needs Windows 10 1809+ and an NTFS volume (FAT32 / exFAT / network drives: full sync, spec 7.3).</summary>
/// <summary>Kept for callers: Files On-Demand availability (see <see cref="CloudFiles.Supported"/>).</summary>
public static class OnDemandSupport
{
    public static bool IsSupported(string folder) => CloudFiles.Supported(folder);
}

