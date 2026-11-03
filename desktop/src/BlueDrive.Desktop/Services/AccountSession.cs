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
            ParallelTransfers = settings.ParallelTransfers,
            LanSync = settings.LanSync,
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
            if (s.State == AgentState.Idle)
            {
                _ = CheckSharesAsync();
                _ = RefreshEditingAsync();
            }
        };
        _editingTimer = new Timer(_ => _ = RefreshEditingAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
        WatchOfficeLocks();
        if (config.Paused) Agent.Pause(true);
    }

    private IOnDemandPlatform NewPlatform(OnDemandContext ctx, string version) =>
        CloudFiles.NewPlatform(ctx, version, (path, message) => Notify?.Invoke("BlueDrive", $"{path}: {message}"));

    public Task StartAsync() => Agent.StartAsync();

    // ---------------------------------------------------------------- co-editing (spec 13A)

    private readonly Timer _editingTimer;
    private FileSystemWatcher? _locks;
    private int _refreshingEditing;

    /// <summary>Documents of this account being co-edited online now (remote id → name, editors).</summary>
    public IReadOnlyDictionary<string, BlueDriveApi.EditingFile> Editing { get; private set; } = new Dictionary<string, BlueDriveApi.EditingFile>();

    /// <summary>A document being co-edited online was opened from this computer in Word / Excel / PowerPoint: (local path, editors).</summary>
    public event Action<string, IReadOnlyList<string>>? CoEditWarning;

    private async Task RefreshEditingAsync()
    {
        if (Interlocked.Exchange(ref _refreshingEditing, 1) == 1) return;
        try
        {
            var list = await Agent.Api.EditingAsync();
            var next = list.ToDictionary(e => e.FileId.ToString(), e => e);
            if (!next.Keys.ToHashSet().SetEquals(Editing.Keys) || next.Any(e => !Editing.TryGetValue(e.Key, out var old) || !old.Editors.SequenceEqual(e.Value.Editors)))
            {
                Editing = next;
                Changed?.Invoke(this);
            }
            await ApplyExplorerStatesAsync();
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException or RemoteNotFoundException or SignInRequiredException) { }
        finally { Interlocked.Exchange(ref _refreshingEditing, 0); }
    }

    private Dictionary<string, string> _explorerStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explorer shows BlueDrive's own state next to the sync status (spec 7.2): "Đang được … chỉnh sửa trực tuyến" for documents being
    /// co-edited, "Có xung đột" on conflict copies. Files On-Demand accounts (registered sync root) only; only the changes are written.
    /// </summary>
    private async Task ApplyExplorerStatesAsync()
    {
        if (Platform is null) return;
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, e) in Editing)
            if (Read(db => LocalIndex.AbsPathOf(db, Config.SyncFolder, id), null) is { } path)
                wanted[path] = $"Đang được {string.Join(", ", e.Editors.Take(3))} chỉnh sửa trực tuyến";
        foreach (var c in Conflicts()) wanted[Agent.LocalPathOf(c.RootId, c.ConflictPath)] = "Có xung đột — mở BlueDrive để chọn bản giữ lại";
        foreach (var (path, text) in wanted.Where(w => !_explorerStates.TryGetValue(w.Key, out var old) || old != w.Value)
                     .Concat(_explorerStates.Where(o => !wanted.ContainsKey(o.Key)).Select(o => new KeyValuePair<string, string>(o.Key, null!))))
        {
            try { await CloudFiles.SetStatusAsync(path, text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException
                                           or FileNotFoundException) { }
        }
        _explorerStates = wanted;
    }

    /// <summary>
    /// Soft lock: Office creates its owner file "~$…" next to a document when it opens it. If that document is being co-edited online, the
    /// local edits would end as a conflict copy: warn and offer the co-editing mode.
    /// </summary>
    private void WatchOfficeLocks()
    {
        if (!Directory.Exists(Config.SyncFolder)) return;
        try
        {
            _locks = new FileSystemWatcher(Config.SyncFolder, "~$*") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
            _locks.Created += (_, e) => _ = OnOfficeLockAsync(e.FullPath);
            _locks.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    /// <summary>The document an Office owner file belongs to: Word drops the first two characters of long names ("~$o cáo.docx"), Excel keeps them.</summary>
    public static string? DocumentForLock(string lockPath)
    {
        var dir = Path.GetDirectoryName(lockPath);
        var tail = Path.GetFileName(lockPath)[2..];
        if (dir is null || tail.Length == 0 || !Directory.Exists(dir)) return null;
        var exact = Path.Combine(dir, tail);
        if (File.Exists(exact)) return exact;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)
                                                               && Path.GetFileName(f).EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                                                               && Path.GetFileName(f).Length == tail.Length + 2);
    }

    private async Task OnOfficeLockAsync(string lockPath)
    {
        if (DocumentForLock(lockPath) is not { } doc || FindItem(doc) is not { } hit) return;
        await RefreshEditingAsync();
        if (Editing.TryGetValue(hit.Item.RemoteId, out var e) && e.Editors.Count > 0) CoEditWarning?.Invoke(doc, e.Editors);
    }

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
        _locks?.Dispose();
        await _editingTimer.DisposeAsync();
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

