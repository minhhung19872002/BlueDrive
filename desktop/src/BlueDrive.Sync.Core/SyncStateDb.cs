using Dapper;
using Microsoft.Data.Sqlite;

namespace BlueDrive.Sync.Core;

/// <summary>Last synced state of one item: the "base" of the 3-way merge. Local and remote content were equal (<see cref="Hash"/>).</summary>
public sealed class BaseItem
{
    public long LocalId { get; set; }
    public string RootId { get; set; } = "";
    public string RemoteId { get; set; } = "";
    public string? ParentRemoteId { get; set; }
    /// <summary>Name on disk (Windows-safe, NFC).</summary>
    public string Name { get; set; } = "";
    /// <summary>Exact name on the server (may contain characters Windows forbids).</summary>
    public string RemoteName { get; set; } = "";
    public bool IsFolder { get; set; }
    public long RemoteRevision { get; set; }
    public string? Hash { get; set; }
    public long Size { get; set; }
    public string? LocalFileId { get; set; }
    public long LocalSize { get; set; }
    public DateTime LocalMtime { get; set; }
    public string HydrationState { get; set; } = "full";
    public string PinState { get; set; } = "unspecified";
    public string SyncState { get; set; } = "in_sync";
    public DateTime? LastSyncedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
}

public sealed class RootState
{
    public string RootId { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public bool Enabled { get; init; }
    public long Cursor { get; init; }
    public DateTime? LastFullSyncAt { get; init; }
    public int PendingMassDelete { get; init; }
    public string? MassDeleteDecision { get; init; }
}

public sealed class PendingOperation
{
    public long OpId { get; init; }
    public string RootId { get; init; } = "";
    public string Type { get; init; } = "";
    public string ItemKey { get; init; } = "";
    public string? Payload { get; init; }
    public string Status { get; init; } = "";
    public int Attempts { get; init; }
    public string? TusUploadUrl { get; init; }
}

public sealed class ActivityEntry
{
    public long Id { get; init; }
    public DateTime At { get; init; }
    public string Action { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Detail { get; init; }
}

public sealed class ConflictEntry
{
    public long Id { get; init; }
    public string RootId { get; init; } = "";
    public string Path { get; init; } = "";
    public string ConflictPath { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public bool Resolved { get; init; }
}

/// <summary>
/// Local sync state (%LOCALAPPDATA%\BlueDrive\&lt;account&gt;\state.db): SQLite in WAL mode. Every mutation of the base and of the
/// operation queue is a short transaction, so a crash at any point leaves a consistent state that the next pass reconciles.
/// </summary>
public sealed class SyncStateDb : IDisposable
{
    public const int SchemaVersion = 1;
    private readonly SqliteConnection _db;

    static SyncStateDb()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
    }

    public SyncStateDb(string path)
    {
        if (path != ":memory:" && File.Exists(path))
        {
            // Back up the database before a schema migration.
            using var probe = new SqliteConnection($"Data Source={path};Pooling=False");
            probe.Open();
            var version = probe.ExecuteScalar<long>("PRAGMA user_version");
            if (version > 0 && version < SchemaVersion) File.Copy(path, $"{path}.v{version}.bak", true);
        }
        _db = new SqliteConnection($"Data Source={path};Pooling=False");
        _db.Open();
        _db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private void Migrate()
    {
        var version = _db.ExecuteScalar<long>("PRAGMA user_version");
        if (version >= SchemaVersion) return;
        using var tx = _db.BeginTransaction();
        _db.Execute("""
            CREATE TABLE IF NOT EXISTS account (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS roots (
                root_id TEXT PRIMARY KEY, local_path TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, cursor INTEGER NOT NULL DEFAULT 0,
                last_full_sync_at TEXT, pending_mass_delete INTEGER NOT NULL DEFAULT 0, mass_delete_decision TEXT);
            CREATE TABLE IF NOT EXISTS remote_items (
                root_id TEXT NOT NULL, id TEXT PRIMARY KEY, parent_id TEXT, name TEXT NOT NULL, is_folder INTEGER NOT NULL, revision INTEGER NOT NULL,
                hash TEXT, size INTEGER NOT NULL, modified_at TEXT NOT NULL, can_edit INTEGER NOT NULL, can_download INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_remote_items_root ON remote_items(root_id);
            CREATE TABLE IF NOT EXISTS items (
                local_id INTEGER PRIMARY KEY AUTOINCREMENT, root_id TEXT NOT NULL, remote_id TEXT NOT NULL UNIQUE, parent_remote_id TEXT,
                name TEXT NOT NULL, remote_name TEXT NOT NULL, is_folder INTEGER NOT NULL, remote_revision INTEGER NOT NULL, hash TEXT,
                size INTEGER NOT NULL, local_file_id TEXT, local_size INTEGER NOT NULL, local_mtime TEXT NOT NULL,
                hydration_state TEXT NOT NULL DEFAULT 'full', pin_state TEXT NOT NULL DEFAULT 'unspecified', sync_state TEXT NOT NULL DEFAULT 'in_sync',
                last_synced_at TEXT, error_code TEXT, error_message TEXT, retry_count INTEGER NOT NULL DEFAULT 0, next_retry_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_items_root ON items(root_id);
            CREATE TABLE IF NOT EXISTS operations (
                op_id INTEGER PRIMARY KEY AUTOINCREMENT, root_id TEXT NOT NULL, type TEXT NOT NULL, item_key TEXT NOT NULL, payload TEXT,
                status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, tus_upload_url TEXT);
            CREATE TABLE IF NOT EXISTS conflicts (
                id INTEGER PRIMARY KEY AUTOINCREMENT, root_id TEXT NOT NULL, path TEXT NOT NULL, conflict_path TEXT NOT NULL, kind TEXT NOT NULL,
                created_at TEXT NOT NULL, resolved INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS activity_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, action TEXT NOT NULL, path TEXT NOT NULL, detail TEXT);
            """, transaction: tx);
        _db.Execute($"PRAGMA user_version = {SchemaVersion}", transaction: tx);
        tx.Commit();
    }

    // ---------------------------------------------------------------- account / roots

    public string? GetAccount(string key) => _db.ExecuteScalar<string?>("SELECT value FROM account WHERE key = @key", new { key });

    public void SetAccount(string key, string? value) =>
        _db.Execute("INSERT INTO account(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", new { key, value });

    public RootState? GetRoot(string rootId) => _db.QueryFirstOrDefault<RootState>(
        "SELECT root_id, local_path, enabled, cursor, last_full_sync_at, pending_mass_delete, mass_delete_decision FROM roots WHERE root_id = @rootId", new { rootId });

    public IReadOnlyList<RootState> Roots() => _db.Query<RootState>(
        "SELECT root_id, local_path, enabled, cursor, last_full_sync_at, pending_mass_delete, mass_delete_decision FROM roots").ToList();

    public void UpsertRoot(string rootId, string localPath, bool enabled) => _db.Execute("""
        INSERT INTO roots(root_id, local_path, enabled) VALUES (@rootId, @localPath, @enabled)
        ON CONFLICT(root_id) DO UPDATE SET local_path = excluded.local_path, enabled = excluded.enabled
        """, new { rootId, localPath, enabled });

    public void SetCursor(string rootId, long cursor, bool fullSync = false) => _db.Execute(
        fullSync ? "UPDATE roots SET cursor = @cursor, last_full_sync_at = @now WHERE root_id = @rootId" : "UPDATE roots SET cursor = @cursor WHERE root_id = @rootId",
        new { rootId, cursor, now = DateTime.UtcNow });

    public void SetMassDelete(string rootId, int pending, string? decision) =>
        _db.Execute("UPDATE roots SET pending_mass_delete = @pending, mass_delete_decision = @decision WHERE root_id = @rootId", new { rootId, pending, decision });

    // ---------------------------------------------------------------- remote tree (applied snapshot + changes)

    public Dictionary<string, RemoteItem> RemoteTree(string rootId) => _db.Query<RemoteRow>(
        "SELECT id, parent_id, name, is_folder, revision, hash, size, modified_at, can_edit, can_download FROM remote_items WHERE root_id = @rootId", new { rootId })
        .Select(r => r.ToItem()).ToDictionary(i => i.Id);

    public RemoteItem? GetRemote(string id) => _db.QueryFirstOrDefault<RemoteRow>(
        "SELECT id, parent_id, name, is_folder, revision, hash, size, modified_at, can_edit, can_download FROM remote_items WHERE id = @id", new { id })?.ToItem();

    public void ReplaceRemoteTree(string rootId, IEnumerable<RemoteItem> items)
    {
        using var tx = _db.BeginTransaction();
        _db.Execute("DELETE FROM remote_items WHERE root_id = @rootId", new { rootId }, tx);
        foreach (var i in items) UpsertRemote(rootId, i, tx);
        tx.Commit();
    }

    public void UpsertRemote(string rootId, RemoteItem i, SqliteTransaction? tx = null) => _db.Execute("""
        INSERT INTO remote_items(root_id, id, parent_id, name, is_folder, revision, hash, size, modified_at, can_edit, can_download)
        VALUES (@rootId, @Id, @ParentId, @Name, @IsFolder, @Revision, @Hash, @Size, @ModifiedAt, @CanEdit, @CanDownload)
        ON CONFLICT(id) DO UPDATE SET root_id = excluded.root_id, parent_id = excluded.parent_id, name = excluded.name, is_folder = excluded.is_folder,
            revision = excluded.revision, hash = excluded.hash, size = excluded.size, modified_at = excluded.modified_at,
            can_edit = excluded.can_edit, can_download = excluded.can_download
        """, new { rootId, i.Id, i.ParentId, i.Name, i.IsFolder, i.Revision, i.Hash, i.Size, i.ModifiedAt, i.CanEdit, i.CanDownload }, tx);

    public void DeleteRemote(IEnumerable<string> ids)
    {
        using var tx = _db.BeginTransaction();
        foreach (var id in ids) _db.Execute("DELETE FROM remote_items WHERE id = @id", new { id }, tx);
        tx.Commit();
    }

    // ---------------------------------------------------------------- base items

    public List<BaseItem> Items(string rootId) => _db.Query<BaseItem>("SELECT * FROM items WHERE root_id = @rootId", new { rootId }).ToList();

    public void SaveItem(BaseItem i)
    {
        if (i.LocalId == 0)
            i.LocalId = _db.ExecuteScalar<long>("""
                INSERT INTO items(root_id, remote_id, parent_remote_id, name, remote_name, is_folder, remote_revision, hash, size, local_file_id,
                    local_size, local_mtime, hydration_state, pin_state, sync_state, last_synced_at, error_code, error_message, retry_count)
                VALUES (@RootId, @RemoteId, @ParentRemoteId, @Name, @RemoteName, @IsFolder, @RemoteRevision, @Hash, @Size, @LocalFileId,
                    @LocalSize, @LocalMtime, @HydrationState, @PinState, @SyncState, @LastSyncedAt, @ErrorCode, @ErrorMessage, @RetryCount)
                ON CONFLICT(remote_id) DO UPDATE SET parent_remote_id = excluded.parent_remote_id, name = excluded.name, remote_name = excluded.remote_name,
                    remote_revision = excluded.remote_revision, hash = excluded.hash, size = excluded.size, local_file_id = excluded.local_file_id,
                    local_size = excluded.local_size, local_mtime = excluded.local_mtime, sync_state = excluded.sync_state, last_synced_at = excluded.last_synced_at
                RETURNING local_id
                """, i);
        else
            _db.Execute("""
                UPDATE items SET remote_id = @RemoteId, parent_remote_id = @ParentRemoteId, name = @Name, remote_name = @RemoteName, is_folder = @IsFolder,
                    remote_revision = @RemoteRevision, hash = @Hash, size = @Size, local_file_id = @LocalFileId, local_size = @LocalSize,
                    local_mtime = @LocalMtime, hydration_state = @HydrationState, pin_state = @PinState, sync_state = @SyncState,
                    last_synced_at = @LastSyncedAt, error_code = @ErrorCode, error_message = @ErrorMessage, retry_count = @RetryCount
                WHERE local_id = @LocalId
                """, i);
    }

    public void DeleteItem(long localId) => _db.Execute("DELETE FROM items WHERE local_id = @localId", new { localId });

    /// <summary>Items moved on the server to another root (workspace): their base rows follow.</summary>
    public void MoveItemsToRoot(IEnumerable<long> localIds, string rootId)
    {
        using var tx = _db.BeginTransaction();
        foreach (var id in localIds) _db.Execute("UPDATE items SET root_id = @rootId WHERE local_id = @id", new { rootId, id }, tx);
        tx.Commit();
    }

    // ---------------------------------------------------------------- operation queue

    public long Enqueue(string rootId, string type, string itemKey, string? payload) => _db.ExecuteScalar<long>("""
        INSERT INTO operations(root_id, type, item_key, payload, status, created_at) VALUES (@rootId, @type, @itemKey, @payload, 'pending', @now)
        RETURNING op_id
        """, new { rootId, type, itemKey, payload, now = DateTime.UtcNow });

    public void CompleteOperation(long opId) => _db.Execute("DELETE FROM operations WHERE op_id = @opId", new { opId });

    public void FailOperation(long opId, string status) =>
        _db.Execute("UPDATE operations SET status = @status, attempts = attempts + 1 WHERE op_id = @opId", new { opId, status });

    public void SetTusUrl(long opId, string? url) => _db.Execute("UPDATE operations SET tus_upload_url = @url WHERE op_id = @opId", new { opId, url });

    public IReadOnlyList<PendingOperation> PendingOperations(string rootId) => _db.Query<PendingOperation>(
        "SELECT op_id, root_id, type, item_key, payload, status, attempts, tus_upload_url FROM operations WHERE root_id = @rootId ORDER BY op_id", new { rootId }).ToList();

    public void ClearOperations(string rootId) => _db.Execute("DELETE FROM operations WHERE root_id = @rootId", new { rootId });

    // ---------------------------------------------------------------- conflicts / activity

    public void AddConflict(string rootId, string path, string conflictPath, string kind) => _db.Execute(
        "INSERT INTO conflicts(root_id, path, conflict_path, kind, created_at) VALUES (@rootId, @path, @conflictPath, @kind, @now)",
        new { rootId, path, conflictPath, kind, now = DateTime.UtcNow });

    public IReadOnlyList<ConflictEntry> Conflicts(bool unresolvedOnly = true) => _db.Query<ConflictEntry>(
        "SELECT id, root_id, path, conflict_path, kind, created_at, resolved FROM conflicts" + (unresolvedOnly ? " WHERE resolved = 0" : "") + " ORDER BY id DESC").ToList();

    public void Log(string action, string path, string? detail = null)
    {
        _db.Execute("INSERT INTO activity_log(at, action, path, detail) VALUES (@now, @action, @path, @detail)", new { now = DateTime.UtcNow, action, path, detail });
        _db.Execute("DELETE FROM activity_log WHERE id <= (SELECT MAX(id) - 500 FROM activity_log)");
    }

    public IReadOnlyList<ActivityEntry> Activity(int take = 50) =>
        _db.Query<ActivityEntry>("SELECT id, at, action, path, detail FROM activity_log ORDER BY id DESC LIMIT @take", new { take }).ToList();

    internal string Diagnostics() =>
        $"remote_items={_db.ExecuteScalar<long>("SELECT COUNT(*) FROM remote_items")} roots={string.Join(",", _db.Query<string>("SELECT DISTINCT root_id FROM remote_items"))} cursor={GetAccount("cursor")}";

    public void ResolveConflict(long id) => _db.Execute("UPDATE conflicts SET resolved = 1 WHERE id = @id", new { id });

    /// <summary>Selective sync: a root no longer synced is forgotten (its base, remote tree and queue); the cursor is kept.</summary>
    public void ForgetRoot(string rootId) => _db.Execute("""
        DELETE FROM items WHERE root_id = @rootId; DELETE FROM remote_items WHERE root_id = @rootId; DELETE FROM operations WHERE root_id = @rootId;
        """, new { rootId });

    /// <summary>Remote wipe: forgets everything about the account (trees, base, queue, conflicts, activity, cursor).</summary>
    public void ClearAll() => _db.Execute("""
        DELETE FROM operations; DELETE FROM conflicts; DELETE FROM activity_log; DELETE FROM items; DELETE FROM remote_items;
        DELETE FROM roots; DELETE FROM account;
        """);

    public void Dispose() => _db.Dispose();

    private sealed class RemoteRow
    {
        public string Id { get; init; } = "";
        public string? ParentId { get; init; }
        public string Name { get; init; } = "";
        public bool IsFolder { get; init; }
        public long Revision { get; init; }
        public string? Hash { get; init; }
        public long Size { get; init; }
        public DateTime ModifiedAt { get; init; }
        public bool CanEdit { get; init; }
        public bool CanDownload { get; init; }
        public RemoteItem ToItem() => new(Id, ParentId, Name, IsFolder, Revision, Hash, Size, ModifiedAt, CanEdit, CanDownload);
    }

    private sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateTime value) =>
            parameter.Value = (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime()).ToString("O");

        // Values are always written in UTC; text without an offset is read back as UTC (never as local time).
        public override DateTime Parse(object value)
        {
            var d = DateTime.Parse((string)value, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
        }
    }
}
