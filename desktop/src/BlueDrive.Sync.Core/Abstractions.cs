namespace BlueDrive.Sync.Core;

/// <summary>An item on the server, as returned by the snapshot / change feed. <see cref="ParentId"/> null = top level of the root.</summary>
public sealed record RemoteItem(
    string Id, string? ParentId, string Name, bool IsFolder, long Revision, string? Hash, long Size, DateTime ModifiedAt,
    bool CanEdit = true, bool CanDownload = true);

/// <summary>One page of a full listing; <see cref="Cursor"/> is the change feed position when the listing started.</summary>
public sealed record RemoteSnapshotPage(IReadOnlyList<RemoteItem> Items, string? NextPageToken, long Cursor);

public enum RemoteChangeKind { Upsert, Delete, AccessRevoked, PermissionChanged }

/// <summary>A change feed entry: Upsert carries the current item; Delete / AccessRevoked remove the item and its subtree.</summary>
public sealed record RemoteChange(long Seq, RemoteChangeKind Kind, string ItemId, string? RootId, RemoteItem? Item, string? DeviceId);

public sealed record RemoteChangePage(IReadOnlyList<RemoteChange> Changes, long NextCursor, bool HasMore);

/// <summary>Server side of the sync (HTTP client in BlueDrive.Sync.Client, in-memory fake in tests).</summary>
public interface IRemoteStore
{
    Task<RemoteSnapshotPage> SnapshotAsync(string rootId, string? pageToken, string? folderId, CancellationToken ct);

    /// <exception cref="RemoteCursorExpiredException">The cursor is older than the retained journal (410).</exception>
    Task<RemoteChangePage> ChangesAsync(long cursor, CancellationToken ct);

    Task DownloadAsync(string itemId, Stream destination, CancellationToken ct);

    /// <exception cref="RemoteNameConflictException">An item with this name already exists (409).</exception>
    Task<RemoteItem> UploadNewAsync(string rootId, string? parentId, string name, Stream content, string hash, CancellationToken ct);

    /// <exception cref="RemotePreconditionFailedException">The server copy changed since <paramref name="baseRevision"/> (412).</exception>
    Task<RemoteItem> UploadVersionAsync(string itemId, long baseRevision, Stream content, string hash, CancellationToken ct);

    Task<RemoteItem> CreateFolderAsync(string rootId, string? parentId, string name, CancellationToken ct);

    /// <summary>Rename and / or move in one call (If-Match <paramref name="revision"/>).</summary>
    Task<RemoteItem> MoveAsync(string itemId, long revision, string rootId, string? newParentId, string newName, CancellationToken ct);

    /// <summary>Moves the item to the server trash (If-Match <paramref name="revision"/>).</summary>
    Task DeleteAsync(string itemId, long revision, CancellationToken ct);
}

public sealed record LocalEntry(string Path, bool IsFolder, string FileId, long Size, DateTime MTime);

/// <summary>The sync folder on disk. Paths are relative to the root, "/" separated, NFC.</summary>
public interface ILocalFileSystem
{
    /// <summary>Every file and folder under <paramref name="path"/> (recursive), symbolic links excluded.</summary>
    IReadOnlyList<LocalEntry> Scan(string path);

    LocalEntry? Stat(string path);

    Stream OpenRead(string path);

    /// <summary>Writes through a temporary file then replaces <paramref name="path"/> atomically; keeps <paramref name="mtime"/>.</summary>
    Task WriteAtomicAsync(string path, Func<Stream, Task> write, DateTime mtime, CancellationToken ct);

    void CreateDirectory(string path);

    /// <summary>Moves a file or folder; the parent of <paramref name="to"/> must exist.</summary>
    void Move(string from, string to);

    /// <summary>Deletes a file or (recursively) a folder; sync deletions go to the recycle bin when the platform has one.</summary>
    void Delete(string path);

    void Copy(string from, string to);

    /// <summary>Read-only attribute for files the user may only view (spec 7.2); no-op where the platform has none.</summary>
    void SetReadOnly(string path, bool readOnly);
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Files On-Demand integration (Cloud Files API on Windows). The full-sync mode uses <see cref="NoPlaceholders"/>.</summary>
public interface IPlaceholderProvider
{
    /// <summary>True when remote files are materialized as placeholders and downloaded on demand.</summary>
    bool OnDemand { get; }

    Task CreatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct);

    Task UpdatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct);

    /// <summary>The item at <paramref name="path"/> matches the server copy <paramref name="remoteId"/> (status icon "đã đồng bộ").</summary>
    Task MarkInSyncAsync(string path, string remoteId, CancellationToken ct);
}

/// <summary>Reads part of a server file (Files On-Demand hydration streams the requested range to the file system).</summary>
public interface IRemoteRangeReader
{
    Task ReadRangeAsync(string itemId, long offset, long length, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink, CancellationToken ct);
}

public sealed class NoPlaceholders : IPlaceholderProvider
{
    public static readonly NoPlaceholders Instance = new();
    public bool OnDemand => false;
    public Task CreatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct) => Task.CompletedTask;
    public Task UpdatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct) => Task.CompletedTask;
    public Task MarkInSyncAsync(string path, string remoteId, CancellationToken ct) => Task.CompletedTask;
}

// ---------------------------------------------------------------- errors

/// <summary>Network / server hiccup: retried with backoff.</summary>
public class RemoteTransientException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Permanent refusal (403, 413, blocked type, quota): not retried, shown in "Vấn đề".</summary>
public class RemotePermanentException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class RemotePreconditionFailedException(RemoteItem? current) : Exception("Tài liệu đã thay đổi trên máy chủ.")
{
    public RemoteItem? Current { get; } = current;
}

public sealed class RemoteNameConflictException(string name) : Exception($"Đã có \"{name}\" trên máy chủ.");

/// <summary>404: <see cref="Id"/> is the item that is really missing (the target itself, or the destination folder).</summary>
public sealed class RemoteNotFoundException(string id) : Exception($"Không tìm thấy {id} trên máy chủ.")
{
    public string Id { get; } = id;
}

public sealed class RemoteCursorExpiredException() : Exception("Mốc đồng bộ đã quá cũ.");
