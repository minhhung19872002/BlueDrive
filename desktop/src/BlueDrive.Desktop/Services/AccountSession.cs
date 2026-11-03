using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;
using BlueDrive.Sync.Windows;

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
    public CloudFilesPlatform? Platform { get; private set; }
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
            OnDemand = config.OnDemand && OnDemandSupport.IsSupported(config.SyncFolder) ? ctx => Platform = NewPlatform(ctx, appVersion) : null,
        });
        var lastConflicts = -1;
        var lastState = AgentState.Starting;
        Agent.StatusChanged += s =>
        {
            if (s.State != AgentState.Syncing) Transfer = null;
            var conflicts = Conflicts().Count;
            if (lastConflicts >= 0 && conflicts > lastConflicts)
                Notify?.Invoke("Có xung đột mới", "Một tệp đã bị sửa ở cả máy này và nơi khác. BlueDrive đã giữ cả hai bản — mở BlueDrive để chọn bản giữ lại.");
            lastConflicts = conflicts;
            if (s.State != lastState && s.State is AgentState.NeedsSignIn or AgentState.Revoked or AgentState.UpdateRequired or AgentState.Error)
                Notify?.Invoke(StateTitle(s.State), s.Message);
            if (s.State == AgentState.Idle && QuotaPercent >= 95 && lastState == AgentState.Syncing)
                Notify?.Invoke("Sắp hết dung lượng", $"Bạn đã dùng {QuotaPercent}% dung lượng BlueDrive.");
            lastState = s.State;
            Changed?.Invoke(this);
        };
        Agent.Activity += (_, _) => Changed?.Invoke(this);
        if (config.Paused) Agent.Pause(true);
    }

    private CloudFilesPlatform NewPlatform(OnDemandContext ctx, string version)
    {
        var p = new CloudFilesPlatform(ctx, Environment.ProcessPath is { } exe ? $"{exe},0" : null, version);
        p.Problem += (path, message) => Notify?.Invoke("BlueDrive", $"{path}: {message}");
        return p;
    }

    public Task StartAsync() => Agent.StartAsync();

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

    public IReadOnlyList<ActivityEntry> Activity(int take = 30) => Read(db => db.Activity(take), []);

    public IReadOnlyList<ConflictEntry> Conflicts() => Read(db => db.Conflicts(), []);

    public IReadOnlyList<RootState> RootStates() => Read(db => db.Roots(), []);

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
public static class OnDemandSupport
{
    public static bool IsSupported(string folder)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return false;
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).DriveFormat == "NTFS"; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }
}
