using System.Security.Cryptography;
using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Testing;

public sealed class FakeClock(DateTime start) : IClock
{
    public DateTime Now = start;
    public DateTime UtcNow => Now;
    public DateTime Tick() => Now = Now.AddSeconds(1);
}

public static class Content
{
    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public static byte[] Of(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

/// <summary>
/// In-memory sync folder (Windows semantics: case-insensitive, file ids kept across renames). Calls through
/// <see cref="ILocalFileSystem"/> are the engine's; the User* methods are what the person at the computer does.
/// <see cref="Destroyed"/> receives the hash of every content the ENGINE deletes or overwrites (data-loss invariant).
/// </summary>
public sealed class FakeLocalFileSystem(FakeClock clock) : ILocalFileSystem
{
    private sealed class Node
    {
        public required string Path;
        public bool IsFolder;
        public required string FileId;
        public byte[] Data = [];
        public DateTime MTime;
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private long _ids;
    public Action<string, string>? Destroyed; // (path, hash)
    public int EngineWrites;

    private string NewId() => $"F{++_ids}";

    public IReadOnlyList<LocalEntry> Scan(string path) => _nodes.Values
        .Where(n => path.Length == 0 || NameMapper.IsUnder(n.Path, path))
        .OrderBy(n => n.Path, StringComparer.Ordinal)
        .Select(Entry).ToList();

    private static LocalEntry Entry(Node n) => new(n.Path, n.IsFolder, n.FileId, n.IsFolder ? 0 : n.Data.Length, n.IsFolder ? default : n.MTime);

    public LocalEntry? Stat(string path) => path.Length == 0 ? new LocalEntry("", true, "root", 0, default) : _nodes.TryGetValue(path, out var n) ? Entry(n) : null;

    public Stream OpenRead(string path) =>
        _nodes.TryGetValue(path, out var n) && !n.IsFolder ? new MemoryStream(n.Data, false) : throw new FileNotFoundException(path);

    public async Task WriteAtomicAsync(string path, Func<Stream, Task> write, DateTime mtime, CancellationToken ct)
    {
        RequireParent(path);
        var ms = new MemoryStream();
        await write(ms);
        if (_nodes.TryGetValue(path, out var old))
        {
            if (old.IsFolder) throw new IOException($"Là thư mục: {path}");
            Destroyed?.Invoke(path, Content.Hash(old.Data));
        }
        EngineWrites++;
        // Like ReplaceFile: a new file takes the path.
        _nodes[path] = new Node { Path = old?.Path ?? path, FileId = NewId(), Data = ms.ToArray(), MTime = mtime };
    }

    public void CreateDirectory(string path)
    {
        if (path.Length == 0) return;
        if (_nodes.TryGetValue(path, out var n))
        {
            if (!n.IsFolder) throw new IOException($"Là tệp: {path}");
            return;
        }
        if (NameMapper.ParentOf(path) is { } p) CreateDirectory(p);
        _nodes[path] = new Node { Path = path, IsFolder = true, FileId = NewId() };
    }

    public void Move(string from, string to) => MoveCore(from, to);

    public void Delete(string path)
    {
        if (!_nodes.TryGetValue(path, out var n)) return;
        foreach (var x in _nodes.Values.Where(x => x == n || NameMapper.IsUnder(x.Path, n.Path)).ToList())
        {
            if (!x.IsFolder) Destroyed?.Invoke(x.Path, Content.Hash(x.Data));
            _nodes.Remove(x.Path);
        }
    }

    public void Copy(string from, string to)
    {
        RequireParent(to);
        var src = _nodes[from];
        if (_nodes.ContainsKey(to)) throw new IOException($"Đã tồn tại: {to}");
        _nodes[to] = new Node { Path = to, FileId = NewId(), Data = src.Data.ToArray(), MTime = clock.Tick() };
    }

    private void RequireParent(string path)
    {
        if (NameMapper.ParentOf(path) is { } p && !(_nodes.TryGetValue(p, out var pn) && pn.IsFolder))
            throw new DirectoryNotFoundException($"Thiếu thư mục cha: {p}");
    }

    private void MoveCore(string from, string to)
    {
        if (!_nodes.TryGetValue(from, out var n)) throw new FileNotFoundException(from);
        RequireParent(to);
        if (_nodes.TryGetValue(to, out var existing) && existing != n) throw new IOException($"Đã tồn tại: {to}");
        if (NameMapper.IsUnder(to, n.Path)) throw new IOException("Không thể chuyển thư mục vào chính nó.");
        var moving = _nodes.Values.Where(x => x == n || NameMapper.IsUnder(x.Path, n.Path)).ToList();
        var prefix = n.Path.Length;
        foreach (var x in moving) _nodes.Remove(x.Path);
        foreach (var x in moving)
        {
            x.Path = to + x.Path[prefix..];
            _nodes[x.Path] = x;
        }
    }

    // ---------------------------------------------------------------- the user

    /// <summary>Files the engine marked read-only (view-only on the server).</summary>
    public HashSet<string> ReadOnly { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetReadOnly(string path, bool readOnly)
    {
        if (!_nodes.ContainsKey(path)) return;
        if (readOnly) ReadOnly.Add(path);
        else ReadOnly.Remove(path);
    }

    public bool Exists(string path) => _nodes.ContainsKey(path);
    public bool IsFolder(string path) => _nodes.TryGetValue(path, out var n) && n.IsFolder;
    public byte[] Read(string path) => _nodes[path].Data;
    public IEnumerable<string> Files => _nodes.Values.Where(n => !n.IsFolder).Select(n => n.Path);
    public IEnumerable<string> Folders => _nodes.Values.Where(n => n.IsFolder).Select(n => n.Path);

    public void UserWrite(string path, byte[] data)
    {
        if (NameMapper.ParentOf(path) is { } p) CreateDirectory(p);
        if (_nodes.TryGetValue(path, out var n))
        {
            if (n.IsFolder) return;
            n.Data = data;
            n.MTime = clock.Tick();
        }
        else _nodes[path] = new Node { Path = path, FileId = NewId(), Data = data, MTime = clock.Tick() };
    }

    /// <summary>Office-style save: write a temp file, delete the original, rename the temp (the file id changes).</summary>
    public void UserSaveAsNewFile(string path, byte[] data)
    {
        if (!_nodes.TryGetValue(path, out var n) || n.IsFolder) return;
        _nodes[path] = new Node { Path = n.Path, FileId = NewId(), Data = data, MTime = clock.Tick() };
    }

    public void UserMkdir(string path) => CreateDirectory(path);

    public bool UserMove(string from, string to)
    {
        try { MoveCore(from, to); clock.Tick(); return true; }
        catch (IOException) { return false; } // includes FileNotFoundException / DirectoryNotFoundException
    }

    public void UserDelete(string path)
    {
        foreach (var x in _nodes.Values.Where(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase) || NameMapper.IsUnder(x.Path, path)).ToList())
            _nodes.Remove(x.Path);
    }
}

/// <summary>
/// In-memory BlueDrive server with the semantics the sync client relies on: revisions (ETag), 412 / 409, trash instead of delete,
/// version history, a change journal with paging and retention (410), and injectable network failures — before the request
/// reaches the server, or after the server performed it (lost response).
/// </summary>
public sealed class FakeRemoteStore(FakeClock clock) : IRemoteStore
{
    private sealed class Node
    {
        public required string Id;
        public string? ParentId;
        public required string Name;
        public bool IsFolder;
        public long Revision = 1;
        public byte[] Data = [];
        public bool Deleted;
        public bool CanEdit = true;
        public bool CanDownload = true;
        public readonly List<byte[]> Versions = [];
        public DateTime Modified;
    }

    private readonly Dictionary<string, Node> _nodes = [];
    private readonly List<(long Seq, string Id, bool Delete, string? Device)> _journal = [];
    private long _seq;
    private long _minSeq = 1;
    private long _ids;
    public const string Root = "root";
    public Random? Chaos;
    public double FailureRate;
    public string? CurrentDevice;
    public int Requests;

    /// <summary>Every content the server ever held (current, versions, trash): what "never lose data" is checked against.</summary>
    public HashSet<string> EverStored { get; } = [];

    private void MaybeFail(bool after)
    {
        if (Chaos is not null && FailureRate > 0 && Chaos.NextDouble() < FailureRate / 2)
            throw new RemoteTransientException(after ? "Mất kết nối (máy chủ đã xử lý)" : "Mất kết nối");
    }

    private T Call<T>(Func<T> op)
    {
        Requests++;
        MaybeFail(false);
        var r = op();
        MaybeFail(true);
        return r;
    }

    private void Journal(Node n, bool delete) => _journal.Add((++_seq, n.Id, delete, CurrentDevice));

    private RemoteItem Item(Node n) => new(n.Id, n.ParentId, n.Name, n.IsFolder, n.Revision, n.IsFolder ? null : Content.Hash(n.Data), n.Data.Length, n.Modified, n.CanEdit, n.CanDownload);

    private IEnumerable<Node> Live => _nodes.Values.Where(n => !n.Deleted);

    private bool Taken(string? parent, string name, string? except = null) =>
        Live.Any(n => n.ParentId == parent && n.Id != except && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    private Node Get(string id) => _nodes.TryGetValue(id, out var n) && !n.Deleted ? n : throw new RemoteNotFoundException(id);

    private void RequireFolder(string? parentId)
    {
        if (parentId is not null && (!_nodes.TryGetValue(parentId, out var p) || p.Deleted || !p.IsFolder)) throw new RemoteNotFoundException(parentId);
        if (parentId is not null && !_nodes[parentId].CanEdit) throw new RemotePermanentException("Bạn không có quyền thêm vào thư mục này.", 403);
    }

    // ---------------------------------------------------------------- IRemoteStore

    public Task<RemoteSnapshotPage> SnapshotAsync(string rootId, string? pageToken, string? folderId, CancellationToken ct) => Task.FromResult(Call(() =>
    {
        var items = Live.Where(n => folderId is null || n.Id == folderId || IsUnder(n, folderId)).OrderBy(n => Depth(n)).ThenBy(n => n.Id).Select(Item).ToList();
        const int page = 7; // small pages to exercise paging
        var cursor = pageToken is null ? _seq : long.Parse(pageToken.Split(':')[1]);
        var skip = pageToken is null ? 0 : int.Parse(pageToken.Split(':')[0]);
        var slice = items.Skip(skip).Take(page).ToList();
        var next = skip + page < items.Count ? $"{skip + page}:{cursor}" : null;
        return new RemoteSnapshotPage(slice, next, cursor);
    }));

    public Task<RemoteChangePage> ChangesAsync(long cursor, CancellationToken ct) => Task.FromResult(Call(() =>
    {
        if (cursor + 1 < _minSeq) throw new RemoteCursorExpiredException();
        var rows = _journal.Where(j => j.Seq > cursor).OrderBy(j => j.Seq).Take(5).ToList();
        var changes = rows.Select(j =>
        {
            var n = _nodes[j.Id];
            return n.Deleted || j.Delete
                ? new RemoteChange(j.Seq, RemoteChangeKind.Delete, j.Id, Root, null, j.Device)
                : new RemoteChange(j.Seq, RemoteChangeKind.Upsert, j.Id, Root, Item(n), j.Device);
        }).ToList();
        var next = rows.Count > 0 ? rows[^1].Seq : cursor;
        return new RemoteChangePage(changes, next, _journal.Any(j => j.Seq > next));
    }));

    public async Task DownloadAsync(string itemId, Stream destination, CancellationToken ct)
    {
        var data = Call(() => Get(itemId).Data.ToArray());
        await destination.WriteAsync(data, ct);
    }

    public async Task<RemoteItem> UploadNewAsync(string rootId, string? parentId, string name, Stream content, string hash, CancellationToken ct)
    {
        var data = await ReadAll(content);
        return Call(() =>
        {
            if (Content.Hash(data) != hash) throw new RemotePermanentException("Sai mã băm", 422);
            RequireFolder(parentId);
            if (Taken(parentId, name)) throw new RemoteNameConflictException(name);
            var n = new Node { Id = $"R{++_ids}", ParentId = parentId, Name = name, Data = data, Modified = clock.Tick() };
            n.Versions.Add(data);
            EverStored.Add(hash);
            _nodes[n.Id] = n;
            Journal(n, false);
            return Item(n);
        });
    }

    public async Task<RemoteItem> UploadVersionAsync(string itemId, long baseRevision, Stream content, string hash, CancellationToken ct)
    {
        var data = await ReadAll(content);
        return Call(() =>
        {
            var n = Get(itemId);
            if (n.Revision != baseRevision) throw new RemotePreconditionFailedException(Item(n));
            if (!n.CanEdit) throw new RemotePermanentException("Không có quyền chỉnh sửa", 403);
            n.Data = data;
            n.Versions.Add(data);
            EverStored.Add(hash);
            n.Revision++;
            n.Modified = clock.Tick();
            Journal(n, false);
            return Item(n);
        });
    }

    public Task<RemoteItem> CreateFolderAsync(string rootId, string? parentId, string name, CancellationToken ct) => Task.FromResult(Call(() =>
    {
        RequireFolder(parentId);
        if (Taken(parentId, name)) throw new RemoteNameConflictException(name);
        var n = new Node { Id = $"R{++_ids}", ParentId = parentId, Name = name, IsFolder = true, Modified = clock.Tick() };
        _nodes[n.Id] = n;
        Journal(n, false);
        return Item(n);
    }));

    public Task<RemoteItem> MoveAsync(string itemId, long revision, string rootId, string? newParentId, string newName, CancellationToken ct) => Task.FromResult(Call(() =>
    {
        var n = Get(itemId);
        if (n.Revision != revision) throw new RemotePreconditionFailedException(Item(n));
        RequireFolder(newParentId);
        if (newParentId is not null && (newParentId == n.Id || IsUnder(_nodes[newParentId], n.Id))) throw new RemotePermanentException("Không thể chuyển vào chính nó", 400);
        if (Taken(newParentId, newName, n.Id)) throw new RemoteNameConflictException(newName);
        n.ParentId = newParentId;
        n.Name = newName;
        n.Revision++;
        Journal(n, false);
        return Item(n);
    }));

    public Task DeleteAsync(string itemId, long revision, CancellationToken ct) => Task.FromResult(Call(() =>
    {
        var n = Get(itemId);
        if (n.Revision != revision) throw new RemotePreconditionFailedException(Item(n));
        foreach (var x in Live.Where(x => x == n || IsUnder(x, n.Id)).ToList()) x.Deleted = true; // to the trash, content kept
        Journal(n, true);
        return true;
    }));

    private static async Task<byte[]> ReadAll(Stream s)
    {
        var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    private bool IsUnder(Node n, string ancestorId)
    {
        var cur = n.ParentId;
        for (var guard = 0; cur is not null && guard < 256; guard++)
        {
            if (cur == ancestorId) return true;
            cur = _nodes[cur].ParentId;
        }
        return false;
    }

    private int Depth(Node n)
    {
        var d = 0;
        for (var cur = n.ParentId; cur is not null; cur = _nodes[cur].ParentId) d++;
        return d;
    }

    // ---------------------------------------------------------------- the web (other users) and the operator

    public string? FindPath(string path)
    {
        string? parent = null;
        Node? cur = null;
        foreach (var seg in path.Split('/'))
        {
            cur = Live.FirstOrDefault(n => n.ParentId == parent && string.Equals(n.Name, seg, StringComparison.OrdinalIgnoreCase));
            if (cur is null) return null;
            parent = cur.Id;
        }
        return cur?.Id;
    }

    public string PathOf(string id)
    {
        var parts = new List<string>();
        for (var cur = _nodes[id]; ; cur = _nodes[cur.ParentId!])
        {
            parts.Add(cur.Name);
            if (cur.ParentId is null) break;
        }
        parts.Reverse();
        return string.Join('/', parts);
    }

    public IReadOnlyDictionary<string, string> Tree() =>
        Live.ToDictionary(n => PathOf(n.Id), n => n.IsFolder ? "<dir>" : Content.Hash(n.Data), StringComparer.OrdinalIgnoreCase);

    public long Seq => _seq;
    public string Ids() => string.Join(" ", Live.Select(n => $"{n.Id}:{PathOf(n.Id)}{(n.IsFolder ? "/" : "")}"));
    public string JournalText() => string.Join(" ", _journal.Select(j => $"{j.Seq}:{j.Id}{(j.Delete ? "x" : "")}@{j.Device ?? "web"}"));
    public int VersionCount(string id) => _nodes[id].Versions.Count;
    public byte[] Data(string id) => _nodes[id].Data;

    public string WebCreateFolder(string? parentPath, string name)
    {
        var parent = parentPath is null ? null : FindPath(parentPath);
        if (parentPath is not null && parent is null) return "";
        if (Taken(parent, name)) return "";
        var n = new Node { Id = $"R{++_ids}", ParentId = parent, Name = name, IsFolder = true, Modified = clock.Tick() };
        _nodes[n.Id] = n;
        CurrentDevice = null;
        Journal(n, false);
        return n.Id;
    }

    public string WebUpload(string? parentPath, string name, byte[] data)
    {
        var parent = parentPath is null ? null : FindPath(parentPath);
        if (parentPath is not null && parent is null) return "";
        var existing = Live.FirstOrDefault(n => n.ParentId == parent && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is { IsFolder: true }) return "";
        CurrentDevice = null;
        if (existing is not null)
        {
            existing.Data = data;
            existing.Versions.Add(data);
            existing.Revision++;
            existing.Modified = clock.Tick();
            EverStored.Add(Content.Hash(data));
            Journal(existing, false);
            return existing.Id;
        }
        var n = new Node { Id = $"R{++_ids}", ParentId = parent, Name = name, Data = data, Modified = clock.Tick() };
        n.Versions.Add(data);
        EverStored.Add(Content.Hash(data));
        _nodes[n.Id] = n;
        Journal(n, false);
        return n.Id;
    }

    public bool WebRename(string path, string newName)
    {
        var id = FindPath(path);
        if (id is null) return false;
        var n = _nodes[id];
        if (Taken(n.ParentId, newName, id)) return false;
        n.Name = newName;
        n.Revision++;
        CurrentDevice = null;
        Journal(n, false);
        return true;
    }

    public bool WebMove(string path, string? newParentPath)
    {
        var id = FindPath(path);
        var parent = newParentPath is null ? null : FindPath(newParentPath);
        if (id is null || (newParentPath is not null && parent is null)) return false;
        var n = _nodes[id];
        if (parent is not null && (parent == id || IsUnder(_nodes[parent], id) || !_nodes[parent].IsFolder)) return false;
        if (Taken(parent, n.Name, id)) return false;
        n.ParentId = parent;
        n.Revision++;
        CurrentDevice = null;
        Journal(n, false);
        return true;
    }

    public bool WebDelete(string path)
    {
        var id = FindPath(path);
        if (id is null) return false;
        foreach (var x in Live.Where(x => x.Id == id || IsUnder(x, id)).ToList()) x.Deleted = true;
        CurrentDevice = null;
        Journal(_nodes[id], true);
        return true;
    }

    /// <summary>Retention job: drop journal rows up to <paramref name="seq"/> (older cursors get 410).</summary>
    public void ExpireJournal(long seq)
    {
        _journal.RemoveAll(j => j.Seq <= seq);
        _minSeq = seq + 1;
    }

    public void SetCanEdit(string path, bool canEdit)
    {
        if (FindPath(path) is { } id) { _nodes[id].CanEdit = canEdit; _nodes[id].Revision++; Journal(_nodes[id], false); }
    }
}
