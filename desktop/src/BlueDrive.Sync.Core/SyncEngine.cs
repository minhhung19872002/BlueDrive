using System.Security.Cryptography;
using System.Text.Json;

namespace BlueDrive.Sync.Core;

public sealed class SyncEngineOptions
{
    /// <summary>Shown in conflict copy names ("Bản xung đột - &lt;Họ tên&gt; - &lt;Tên máy&gt; - …").</summary>
    public string UserDisplayName { get; init; } = "Người dùng";
    public string DeviceName { get; init; } = Environment.MachineName;
    /// <summary>Safe mass delete (spec 6.3): ask before deleting more than this many files on the server…</summary>
    public int MassDeleteCount { get; init; } = 100;
    /// <summary>…or more than this share of a root's files (when at least <see cref="MassDeleteMinimum"/> files).</summary>
    public double MassDeleteShare { get; init; } = 0.30;
    public int MassDeleteMinimum { get; init; } = 10;
    /// <summary>Plan / execute rounds per pass; each round re-scans so operations always act on fresh state.</summary>
    public int MaxRounds { get; init; } = 12;
    /// <summary>Converts the local time used in conflict names (tests pass a fixed zone).</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    /// <summary>Where large files are snapshotted before upload (null = in memory, tests).</summary>
    public string? SnapshotDirectory { get; init; }
    /// <summary>Files up to this size are snapshotted in memory.</summary>
    public long MemorySnapshotLimit { get; init; } = 32L * 1024 * 1024;

    /// <summary>
    /// "Số luồng tải song song" (spec 8.3): full sync mode downloads the content of up to this many files at once before the steps run
    /// (one after the other, as always). 1 = no prefetch (default; the app sets it from Cài đặt → Mạng). The remote store must accept
    /// concurrent downloads (HttpRemoteStore does).
    /// </summary>
    public int ParallelDownloads { get; init; } = 1;

    /// <summary>Only files up to this size are prefetched (larger ones stream straight into place), at most <see cref="PrefetchBudget"/> per round.</summary>
    public long PrefetchMaxFile { get; init; } = 64L * 1024 * 1024;
    public long PrefetchBudget { get; init; } = 1024L * 1024 * 1024;
}

/// <summary>Write-through stream that hashes what passes (downloads are verified without a second read).</summary>
public sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public string Hash() => Convert.ToHexStringLower(_sha.GetCurrentHash());
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _sha.AppendData(buffer, offset, count);
        inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _sha.AppendData(buffer.Span);
        await inner.WriteAsync(buffer, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sha.Dispose(); // the destination belongs to the caller
        base.Dispose(disposing);
    }
}

public sealed record SyncPassResult(int Operations, int MassDeletePending, IReadOnlyList<string> Errors);

/// <summary>
/// Two-way sync of one account: remote tree (snapshot + change feed) × local tree (scan) × base (SQLite), reconciled item by item with
/// <see cref="Reconciler"/>. Every operation re-checks its preconditions right before running and records the new base in the same
/// step, so the engine can be killed at any point: the next pass sees either the old or the new state and converges without loss.
/// </summary>
public sealed class SyncEngine(
    SyncStateDb state, IRemoteStore remote, ILocalFileSystem fs, IClock clock, SyncEngineOptions? options = null, IPlaceholderProvider? placeholders = null)
{
    private const string CursorKey = "cursor";
    private const string RevokedKey = "revoked";
    private readonly SyncEngineOptions _o = options ?? new();
    private readonly IPlaceholderProvider _placeholders = placeholders ?? NoPlaceholders.Instance;
    /// <summary>Content downloaded ahead in parallel: remote id → (verified temporary file, revision).</summary>
    private readonly Dictionary<string, (string Path, long Revision)> _prefetched = [];

    /// <summary>Operations executed since the engine was created (tests check that a converged state stays quiet).</summary>
    public int TotalOperations { get; private set; }

    public event Action<string, string>? Activity;

    /// <summary>Diagnostics: the last planned steps and whether each one ran (tests and the diagnostic package).</summary>
    /// <summary>Plan of the last pass (diagnostics, tests); capped so a first sync of 100 000 files does not keep 200 000 strings.</summary>
    internal List<string> LastPlan { get; } = [];
    private const int LastPlanLimit = 2000;

    private void Plan(string line)
    {
        if (LastPlan.Count < LastPlanLimit) LastPlan.Add(line);
    }

    // ================================================================= remote tree

    /// <summary>Brings the stored remote trees up to date: full snapshot the first time (or after 410), then the change feed.</summary>
    public async Task RefreshRemoteAsync(CancellationToken ct)
    {
        var cursorText = state.GetAccount(CursorKey);
        if (cursorText is null || !long.TryParse(cursorText, out var cursor))
        {
            await FullSnapshotAsync(ct);
            return;
        }
        var revoked = Revoked();
        while (true)
        {
            RemoteChangePage page;
            try { page = await remote.ChangesAsync(cursor, ct); }
            catch (RemoteCursorExpiredException)
            {
                await FullSnapshotAsync(ct);
                return;
            }
            foreach (var c in page.Changes) await ApplyChangeAsync(c, revoked, ct);
            cursor = page.NextCursor;
            if (!page.HasMore) PruneOrphans(revoked);
            state.SetAccount(CursorKey, cursor.ToString());
            state.SetAccount(RevokedKey, JsonSerializer.Serialize(revoked));
            if (!page.HasMore) break;
        }
    }

    /// <summary>
    /// Deletes only remove the item itself while a batch of changes is applied: a child may have been moved out of a deleted
    /// folder by a later change of the same batch. Once the batch is complete, items whose parent chain is broken are gone.
    /// </summary>
    private void PruneOrphans(HashSet<string> revoked)
    {
        foreach (var root in state.Roots().Where(r => r.Enabled))
        {
            var tree = state.RemoteTree(root.RootId);
            var orphans = new List<string>();
            foreach (var item in tree.Values)
            {
                var cur = item.ParentId;
                var revokedAncestor = false;
                var broken = false;
                for (var guard = 0; cur is not null && guard < 256; guard++)
                {
                    if (revoked.Contains(cur)) revokedAncestor = true;
                    if (!tree.TryGetValue(cur, out var p)) { broken = true; break; }
                    cur = p.ParentId;
                }
                if (!broken) continue;
                orphans.Add(item.Id);
                if (revokedAncestor) revoked.Add(item.Id);
            }
            if (orphans.Count > 0)
            {
                if (Diagnostics) state.Log("Gỡ mục mồ côi", root.RootId, string.Join(",", orphans));
                state.DeleteRemote(orphans);
            }
        }
    }

    private async Task FullSnapshotAsync(CancellationToken ct)
    {
        long? cursor = null;
        foreach (var root in state.Roots().Where(r => r.Enabled))
        {
            var items = new List<RemoteItem>();
            string? token = null;
            do
            {
                var page = await remote.SnapshotAsync(root.RootId, token, null, ct);
                items.AddRange(page.Items);
                cursor = cursor is null ? page.Cursor : Math.Min(cursor.Value, page.Cursor);
                token = page.NextPageToken;
            } while (token is not null);
            state.ReplaceRemoteTree(root.RootId, items.Where(i => i.CanDownload || i.IsFolder));
            state.SetCursor(root.RootId, cursor ?? 0, fullSync: true);
        }
        state.SetAccount(CursorKey, (cursor ?? 0).ToString());
    }

    private async Task ApplyChangeAsync(RemoteChange c, HashSet<string> revoked, CancellationToken ct)
    {
        if (Diagnostics) state.Log("Thay đổi", $"{c.Seq}:{c.Kind}:{c.ItemId}", c.Item is { } it ? $"parent={it.ParentId} name={it.Name}" : null);
        var roots = state.Roots().Where(r => r.Enabled).ToList();
        var owner = roots.FirstOrDefault(r => state.RemoteTree(r.RootId).ContainsKey(c.ItemId));
        switch (c.Kind)
        {
            case RemoteChangeKind.Upsert when c.Item is { } item:
            {
                var rootId = c.RootId ?? owner?.RootId;
                if (rootId is null || roots.All(r => r.RootId != rootId)) return;
                if (!item.CanDownload && !item.IsFolder)
                {
                    // Downgraded to view only: never kept on the computer.
                    RemoveSubtree(rootId, c.ItemId);
                    revoked.Add(c.ItemId);
                    return;
                }
                if (owner is not null && owner.RootId != rootId) RemoveSubtree(owner.RootId, c.ItemId);
                state.UpsertRemote(rootId, item);
                revoked.Remove(c.ItemId);
                return;
            }
            case RemoteChangeKind.Delete:
                if (owner is not null) state.DeleteRemote([c.ItemId]); // descendants: see PruneOrphans
                return;
            case RemoteChangeKind.AccessRevoked:
                if (owner is not null)
                {
                    revoked.Add(c.ItemId);
                    state.DeleteRemote([c.ItemId]);
                }
                return;
            case RemoteChangeKind.PermissionChanged:
            {
                var rootId = c.RootId ?? owner?.RootId;
                if (rootId is null || roots.All(r => r.RootId != rootId)) return;
                // Re-list the subtree: items may have appeared, disappeared or changed rights.
                var listed = new List<RemoteItem>();
                string? token = null;
                do
                {
                    var page = await remote.SnapshotAsync(rootId, token, c.ItemId, ct);
                    listed.AddRange(page.Items);
                    token = page.NextPageToken;
                } while (token is not null);
                var before = Subtree(state.RemoteTree(rootId), c.ItemId).ToHashSet();
                var keep = listed.Where(i => i.CanDownload || i.IsFolder).ToList();
                foreach (var gone in before.Except(keep.Select(i => i.Id))) revoked.Add(gone);
                state.DeleteRemote(before);
                foreach (var i in keep) state.UpsertRemote(rootId, i);
                return;
            }
        }
    }

    private void RemoveSubtree(string rootId, string id)
    {
        var ids = Subtree(state.RemoteTree(rootId), id).ToList();
        if (Diagnostics) state.Log("Gỡ khỏi cây", id, string.Join(",", ids));
        state.DeleteRemote(ids);
    }

    /// <summary>Verbose activity entries for tests / the diagnostic package.</summary>
    internal bool Diagnostics { get; set; }

    private static IEnumerable<string> Subtree(Dictionary<string, RemoteItem> tree, string id)
    {
        var stack = new Stack<string>([id]);
        var children = tree.Values.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            foreach (var ch in children[cur]) stack.Push(ch.Id);
        }
    }

    private HashSet<string> Revoked() =>
        state.GetAccount(RevokedKey) is { } json ? JsonSerializer.Deserialize<HashSet<string>>(json) ?? [] : [];

    // ================================================================= sync pass

    /// <summary>Refreshes the remote trees and reconciles every enabled root until nothing is left to do.</summary>
    public async Task<SyncPassResult> SyncAsync(CancellationToken ct)
    {
        LastPlan.Clear();
        var started = clock.UtcNow;
        await RefreshRemoteAsync(ct);
        var total = 0;
        var errors = new List<string>();
        var pendingMass = 0;
        foreach (var root in state.Roots().Where(r => r.Enabled))
        {
            fs.CreateDirectory(root.LocalPath);
            for (var round = 0; round < _o.MaxRounds; round++)
            {
                var (ops, mass) = await RoundAsync(root.RootId, errors, ct);
                total += ops;
                pendingMass = Math.Max(pendingMass, mass);
                if (ops == 0) break;
            }
            if (_placeholders.OnDemand) await MarkSyncedAsync(root, started, ct);
            MarkReadOnly(root);
            // Clear processed revocations: the items are gone from the base now.
            var revoked = Revoked();
            var known = state.Items(root.RootId).Select(i => i.RemoteId).ToHashSet();
            revoked.RemoveWhere(id => !known.Contains(id));
            state.SetAccount(RevokedKey, JsonSerializer.Serialize(revoked));
            state.ClearOperations(root.RootId);
        }
        ClearPrefetch();
        return new SyncPassResult(total, pendingMass, errors.Distinct().ToList());
    }

    /// <summary>
    /// View-only files are read-only on the disk (spec 7.2); the attribute follows permission changes. Only the files whose state changes are
    /// touched (the set marked last time is kept in the state database).
    /// </summary>
    private void MarkReadOnly(RootState root)
    {
        var key = "readonly:" + root.RootId;
        var marked = state.GetAccount(key) is { } json ? JsonSerializer.Deserialize<HashSet<string>>(json) ?? [] : [];
        var tree = state.RemoteTree(root.RootId);
        var items = state.Items(root.RootId);
        var byRemote = items.ToDictionary(i => i.RemoteId);
        var target = items.Where(b => !b.IsFolder && tree.TryGetValue(b.RemoteId, out var r) && !r.CanEdit).Select(b => b.RemoteId).ToHashSet();
        foreach (var id in target.Union(marked))
        {
            var want = target.Contains(id);
            if (want == marked.Contains(id) || !byRemote.TryGetValue(id, out var b)) continue;
            var parts = new List<string> { b.Name };
            var cur = b.ParentRemoteId;
            for (var guard = 0; cur is not null && guard < 256 && byRemote.TryGetValue(cur, out var p); guard++)
            {
                parts.Add(p.Name);
                cur = p.ParentRemoteId;
            }
            parts.Reverse();
            try { fs.SetReadOnly(NameMapper.Combine(root.LocalPath.Length == 0 ? null : root.LocalPath, string.Join('/', parts)), want); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { target.Remove(id); } // retried next pass
        }
        if (target.Count > 0 || marked.Count > 0) state.SetAccount(key, JsonSerializer.Serialize(target));
    }

    /// <summary>Files On-Demand: everything recorded during this pass is shown as synced in Explorer (status icon).</summary>
    private async Task MarkSyncedAsync(RootState root, DateTime since, CancellationToken ct)
    {
        var items = state.Items(root.RootId);
        var byRemote = items.ToDictionary(i => i.RemoteId);
        foreach (var b in items.Where(i => i.SyncState == "in_sync" && i.LastSyncedAt >= since))
        {
            var parts = new List<string> { b.Name };
            var cur = b.ParentRemoteId;
            for (var guard = 0; cur is not null && guard < 256 && byRemote.TryGetValue(cur, out var p); guard++)
            {
                parts.Add(p.Name);
                cur = p.ParentRemoteId;
            }
            parts.Reverse();
            var path = NameMapper.Combine(root.LocalPath.Length == 0 ? null : root.LocalPath, string.Join('/', parts));
            if (!fs.Exists(path, out _)) continue;
            try { await _placeholders.MarkInSyncAsync(path, b.RemoteId, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // open elsewhere: next pass
        }
    }

    /// <summary>The user answered the mass delete question: delete on BlueDrive too, or restore the files.</summary>
    public void ResolveMassDelete(string rootId, bool deleteOnServer) => state.SetMassDelete(rootId, 0, deleteOnServer ? "confirm" : "restore");

    // ----------------------------------------------------------------- one round: snapshot of all three trees, plan, execute

    private sealed class Ctx
    {
        public required string RootId;
        public required string Dir;
        public required Dictionary<string, RemoteItem> Tree;
        public required Dictionary<string, string> RemoteLocalName; // remote id → local name (mapped, case collisions resolved)
        public required List<BaseItem> Base;
        public required Dictionary<string, BaseItem> BaseByRemote;
        public required List<LocalEntry> Local;
        public required Dictionary<long, LocalEntry?> Match; // base local id → local entry (null = deleted locally)
        public required HashSet<string> Revoked;
        public readonly Dictionary<string, string> HashCache = new();
        /// <summary>Local files that could not be read while planning: left alone this round.</summary>
        public readonly HashSet<string> Busy = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(int Ops, int MassPending)> RoundAsync(string rootId, List<string> errors, CancellationToken ct)
    {
        var root = state.GetRoot(rootId)!;
        var c = Load(root);
        var plan = Plan(c);
        if (c.Busy.Count > 0)
        {
            plan = plan.Select(p => p.LocalEntry is { } l && c.Busy.Contains(l.Path) ? p with { Decision = new(SyncAction.None, "Tệp đang được mở.") } : p).ToList();
            foreach (var path in c.Busy) errors.Add($"{path}: Tệp đang được mở hoặc bị khóa, sẽ đồng bộ khi tệp được đóng.");
        }

        // Safe mass delete: pause server deletions and ask the user.
        var deletes = plan.Where(p => p.Decision.Has(SyncAction.DeleteRemote) && p.Base is { IsFolder: false }).ToList();
        var baseFiles = c.Base.Count(b => !b.IsFolder);
        var tooMany = deletes.Count > _o.MassDeleteCount || (deletes.Count >= _o.MassDeleteMinimum && deletes.Count > _o.MassDeleteShare * baseFiles);
        var massPending = 0;
        if (tooMany && root.MassDeleteDecision != "confirm")
        {
            if (root.MassDeleteDecision == "restore")
            {
                plan = plan.Select(p => p.Decision.Has(SyncAction.DeleteRemote) ? p with { Decision = new(SyncAction.RestoreLocal, "Người dùng chọn khôi phục các tệp đã xóa.") } : p).ToList();
                state.SetMassDelete(rootId, 0, null);
            }
            else
            {
                massPending = deletes.Count;
                state.SetMassDelete(rootId, deletes.Count, null);
                plan = plan.Where(p => !p.Decision.Has(SyncAction.DeleteRemote)).ToList();
            }
        }
        else if (root.MassDeleteDecision is not null && deletes.Count == 0) state.SetMassDelete(rootId, 0, null);

        await PrefetchAsync(plan, ct);
        var ops = 0;
        var stuckMoves = new List<Step>();
        Plan($"-- vòng {rootId}: {plan.Count} mục, {plan.Count(p => p.Decision.Actions != SyncAction.None)} cần xử lý");
        foreach (var step in Order(plan))
        {
            ct.ThrowIfCancellationRequested();
            if (step.Decision.Actions == SyncAction.None) continue;
            var opId = state.Enqueue(rootId, step.Decision.Actions.ToString(), step.Base?.RemoteId ?? step.Remote?.Id ?? step.LocalEntry?.Path ?? "", step.Decision.Reason);
            Plan($".. {step.Decision.Actions} {step.Path} [{step.Base?.RemoteId}/{step.Remote?.Id}]");
            try
            {
                var ran = await ExecuteAsync(c, step, ct);
                if (ran) ops++;
                else if (step.Decision.Has(SyncAction.MoveRemote) || (step.Decision.Has(SyncAction.MoveLocal) && step.LocalEntry is not null && step.Base is not null
                         && step.LocalEntry.Path != BasePath(c, step.Base))) stuckMoves.Add(step);
                Plan($"{(ran ? "OK " : "-- ")}{step.Decision.Actions} {step.Path} [{step.Base?.RemoteId}/{step.Remote?.Id}]");
                state.CompleteOperation(opId);
            }
            catch (RemotePermanentException ex) when (ex.Status == 403 && step.Base is null && step.Remote is null && step.LocalEntry is { } created)
            {
                // Created where the user may not add anything (a view-only folder, the top of "Được chia sẻ với tôi"): moved aside, not retried forever.
                state.CompleteOperation(opId);
                QuarantineNew(c, created, ex.Message);
                ops++;
            }
            catch (RemotePermanentException ex)
            {
                state.FailOperation(opId, "failed");
                errors.Add($"{step.Path}: {ex.Message}");
                state.Log("Lỗi", step.Path, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file is open in an editor, locked, or changed under us: skip it this time. Every step is safe to stop at
                // any point (as after a crash), and the next pass plans again from fresh scans.
                state.FailOperation(opId, "retry");
                var why = ex is UnauthorizedAccessException || IsSharingViolation(ex)
                    ? "Tệp đang được mở hoặc bị khóa, sẽ đồng bộ khi tệp được đóng."
                    : ex.Message;
                errors.Add($"{step.Path}: {why}");
                state.Log("Chờ", step.Path, why);
            }
            catch (RemoteNotFoundException ex)
            {
                // Gone on the server meanwhile (the item, or the destination folder): forget exactly that one, re-plan next round.
                state.CompleteOperation(opId);
                if (c.Tree.ContainsKey(ex.Id))
                {
                    RemoveSubtree(rootId, ex.Id);
                    ops++;
                }
                else errors.Add($"{step.Path}: {ex.Message}");
            }
            catch (RemotePreconditionFailedException ex)
            {
                // Progress only when the server taught us something new; otherwise the same 412 would repeat forever.
                state.CompleteOperation(opId);
                if (ex.Current is { } cur && (!c.Tree.TryGetValue(cur.Id, out var known) || known.Revision != cur.Revision))
                {
                    state.UpsertRemote(rootId, cur);
                    ops++;
                }
                else errors.Add($"{step.Path}: máy chủ từ chối thay đổi (412), sẽ thử lại ở lần đồng bộ sau.");
            }
        }
        // Deadlock between a local move and a server move (e.g. each side moved a folder into the other): the server wins,
        // the local move is undone so the server layout can be applied next round.
        if (ops == 0)
            foreach (var step in stuckMoves.OrderBy(x => x.Depth))
            {
                var l = step.LocalEntry!;
                var target = step.Remote is { } r ? RemotePath(c, r.Id) : step.Base is { } b ? BasePath(c, b) : null;
                if (target is null || string.Equals(target, l.Path, StringComparison.OrdinalIgnoreCase) || fs.Stat(Abs(c, l.Path)) is null) continue;
                if (fs.Stat(Abs(c, target)) is not null || NameMapper.IsUnder(target, l.Path)) continue;
                var parent = NameMapper.ParentOf(target);
                if (parent is not null && fs.Stat(Abs(c, parent)) is not { IsFolder: true }) continue;
                fs.Move(Abs(c, l.Path), Abs(c, target));
                Log("Hoàn tác", l.Path, $"Đổi tên/di chuyển trên máy xung đột với máy chủ, trả về {target}");
                ops++;
                break; // one at a time, then re-plan
            }
        TotalOperations += ops;
        return (ops, massPending);
    }

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33) on Windows.
    private static bool IsSharingViolation(Exception ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Selective sync (folder level): folders of this root the user chose not to have on this computer.</summary>
    public static string ExcludedFoldersKey(string rootId) => "excluded_folders:" + rootId;

    public HashSet<string> ExcludedFolders(string rootId) =>
        state.GetAccount(ExcludedFoldersKey(rootId)) is { } json ? JsonSerializer.Deserialize<HashSet<string>>(json) ?? [] : [];

    private Ctx Load(RootState root)
    {
        var tree = state.RemoteTree(root.RootId);
        var names = LocalNames(tree);
        var items = state.Items(root.RootId);
        // Excluded folders (with everything inside) are left out of all three trees: never downloaded, never seen as deleted.
        var excludedPaths = new List<string>();
        var excluded = ExcludedFolders(root.RootId);
        if (excluded.Count > 0)
        {
            var gone = new HashSet<string>();
            foreach (var id in excluded.Where(tree.ContainsKey))
            {
                var parts = new List<string>();
                for (var (cur, guard) = ((string?)id, 0); cur is not null && guard < 256 && tree.TryGetValue(cur, out var t); cur = t.ParentId, guard++)
                    parts.Add(names.GetValueOrDefault(t.Id, t.Name));
                parts.Reverse();
                excludedPaths.Add(string.Join('/', parts));
                gone.UnionWith(Subtree(tree, id));
            }
            tree = tree.Where(kv => !gone.Contains(kv.Key)).ToDictionary();
            names = LocalNames(tree);
            items = items.Where(i => !gone.Contains(i.RemoteId)).ToList();
        }
        var local = fs.Scan(root.LocalPath)
            .Select(e => e with { Path = Rel(root.LocalPath, e.Path) })
            .Where(e => !IsExcludedPath(e.Path))
            .Where(e => !excludedPaths.Any(x => string.Equals(e.Path, x, StringComparison.OrdinalIgnoreCase) || NameMapper.IsUnder(e.Path, x)))
            .ToList();
        var c = new Ctx
        {
            RootId = root.RootId, Dir = root.LocalPath, Tree = tree, RemoteLocalName = names, Base = items,
            BaseByRemote = items.ToDictionary(i => i.RemoteId), Local = local, Match = [], Revoked = Revoked(),
        };
        // Match base ↔ local: file id first (survives renames / moves), then the base path (editors that save through a new file).
        var byId = local.Where(l => !string.IsNullOrEmpty(l.FileId)).GroupBy(l => l.FileId).ToDictionary(g => g.Key, g => g.First());
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in items)
            if (b.LocalFileId is { } id && byId.TryGetValue(id, out var l) && l.IsFolder == b.IsFolder && used.Add(l.Path)) c.Match[b.LocalId] = l;
        var byPath = local.Where(l => !used.Contains(l.Path)).ToDictionary(l => l.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var b in items.Where(b => !c.Match.ContainsKey(b.LocalId)))
        {
            var path = BasePath(c, b);
            if (path is not null && byPath.TryGetValue(path, out var l) && l.IsFolder == b.IsFolder && used.Add(l.Path)) c.Match[b.LocalId] = l;
            else c.Match[b.LocalId] = null;
        }
        return c;
    }

    private static bool IsExcludedPath(string path) =>
        path.Split('/').Any(seg => IgnoreRules.IsIgnored(seg)) || path == IgnoreRules.NotSyncedFolder || path.StartsWith(IgnoreRules.NotSyncedFolder + "/", StringComparison.Ordinal);

    private static string Rel(string rootDir, string path) =>
        string.IsNullOrEmpty(rootDir) ? path : path.StartsWith(rootDir + "/", StringComparison.Ordinal) ? path[(rootDir.Length + 1)..] : path;

    private static string Abs(Ctx c, string rel) => string.IsNullOrEmpty(c.Dir) ? rel : c.Dir + "/" + rel;

    /// <summary>Windows-safe names; a sibling that differs only in case gets a suffix (spec 6.4).</summary>
    private static Dictionary<string, string> LocalNames(Dictionary<string, RemoteItem> tree)
    {
        var result = new Dictionary<string, string>();
        foreach (var group in tree.Values.GroupBy(i => i.ParentId ?? ""))
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in group.OrderBy(i => i.Id, StringComparer.Ordinal))
            {
                var name = NameMapper.ToLocal(i.Name);
                if (!taken.Add(name))
                {
                    name = NameMapper.NextFree(NameMapper.CaseCollisionName(name), n => taken.Contains(n));
                    taken.Add(name);
                }
                result[i.Id] = name;
            }
        }
        return result;
    }

    /// <summary>Local name of a server item (items added during the round get the plain mapped name).</summary>
    private static string LocalName(Ctx c, RemoteItem i) => c.RemoteLocalName.GetValueOrDefault(i.Id) ?? NameMapper.ToLocal(i.Name);

    private static string? RemotePath(Ctx c, string id)
    {
        var parts = new List<string>();
        var cur = id;
        for (var guard = 0; cur is not null && guard < 256; guard++)
        {
            if (!c.Tree.TryGetValue(cur, out var i)) return null;
            parts.Add(LocalName(c, i));
            cur = i.ParentId;
        }
        parts.Reverse();
        return string.Join('/', parts);
    }

    private static string? BasePath(Ctx c, BaseItem b)
    {
        var parts = new List<string> { b.Name };
        var cur = b.ParentRemoteId;
        for (var guard = 0; cur is not null && guard < 256; guard++)
        {
            if (!c.BaseByRemote.TryGetValue(cur, out var p)) return null;
            parts.Add(p.Name);
            cur = p.ParentRemoteId;
        }
        parts.Reverse();
        return string.Join('/', parts);
    }

    // ----------------------------------------------------------------- planning

    private sealed record Step(string Path, int Depth, SyncDecision Decision, BaseItem? Base, RemoteItem? Remote, LocalEntry? LocalEntry, string? NewParentRemoteId);

    private List<Step> Plan(Ctx c)
    {
        var steps = new List<Step>();
        // Local path → base item matched there (to resolve the parent of local entries).
        var baseAtLocalPath = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in c.Base)
            if (c.Match[b.LocalId] is { } l) baseAtLocalPath[l.Path] = b;
        var matchedPaths = baseAtLocalPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? LocalParentRemote(string path, out bool parentKnown)
        {
            var parent = NameMapper.ParentOf(path);
            parentKnown = parent is null || baseAtLocalPath.ContainsKey(parent);
            return parent is not null && baseAtLocalPath.TryGetValue(parent, out var p) ? p.RemoteId : null;
        }

        var restoredFolders = new HashSet<string>();
        // Files first, then folders deepest first (a folder's delete depends on what happens to its children).
        foreach (var b in c.Base.OrderBy(b => b.IsFolder).ThenByDescending(b => Depth(BasePath(c, b) ?? b.Name)))
        {
            var l = c.Match[b.LocalId];
            c.Tree.TryGetValue(b.RemoteId, out var r);
            var localDelta = l is null ? LocalDelta.Deleted : b.IsFolder ? LocalDelta.Unchanged : LocalFileDelta(c, b, l);
            var remoteDelta = r is null
                ? c.Revoked.Contains(b.RemoteId) ? RemoteDelta.AccessRevoked : RemoteDelta.Deleted
                : !b.IsFolder && r.Hash != b.Hash ? RemoteDelta.Modified : RemoteDelta.Unchanged;
            var localParent = l is null ? null : LocalParentRemote(l.Path, out var known) is var lp && known ? lp : "\0new";
            var localMoved = l is not null && (localParent != b.ParentRemoteId || NameMapper.NameOf(l.Path) != b.Name);
            var remoteMoved = r is not null && (r.ParentId != b.ParentRemoteId || r.Name != b.RemoteName);
            if (r is not null && remoteDelta == RemoteDelta.Unchanged && !remoteMoved && r.Revision != b.RemoteRevision)
            {
                // Metadata-only change on the server (moved there and back, rights…): adopt the revision so If-Match stays valid.
                b.RemoteRevision = r.Revision;
                state.SaveItem(b);
            }
            var sameLocation = localMoved && remoteMoved && r is not null && localParent == r.ParentId && NameMapper.NameOf(l!.Path) == LocalName(c, r);
            var sameContent = localDelta == LocalDelta.Modified && remoteDelta == RemoteDelta.Modified && r is not null && Hash(c, l!) == r.Hash;
            var keepsChildren = b.IsFolder && localDelta == LocalDelta.Deleted && r is not null &&
                (Children(c, r.Id).Any(ch => !c.BaseByRemote.ContainsKey(ch.Id)) || restoredFolders.Contains(b.RemoteId)
                 || c.Base.Any(x => x.ParentRemoteId == b.RemoteId && c.Match[x.LocalId] is not null));
            var d = Reconciler.Decide(new ItemFacts(b.IsFolder, localDelta, localMoved, remoteDelta, remoteMoved, sameContent, sameLocation,
                r?.CanEdit ?? true, keepsChildren));
            if (d.Has(SyncAction.RestoreLocal) && b.ParentRemoteId is { } pid) restoredFolders.Add(pid);
            var path = l?.Path ?? BasePath(c, b) ?? b.Name;
            steps.Add(new Step(path, Depth(path), d, b, r, l, localParent));
        }

        // New on the server (not in base), paired with new local entries at the same path.
        var newLocal = c.Local.Where(l => !matchedPaths.Contains(l.Path)).ToDictionary(l => l.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Tree.Values.Where(r => !c.BaseByRemote.ContainsKey(r.Id)))
        {
            var path = RemotePath(c, r.Id);
            if (path is null) continue;
            newLocal.Remove(path, out var l);
            if (l is not null && l.IsFolder != r.IsFolder) l = null; // different kinds: handled as two separate items, local renamed on create
            var same = l is not null && (r.IsFolder || Hash(c, l) == r.Hash);
            var d = Reconciler.Decide(new ItemFacts(r.IsFolder, l is null ? LocalDelta.Absent : LocalDelta.New, false, RemoteDelta.New, false, same));
            steps.Add(new Step(path, Depth(path), d, null, r, l, r.ParentId));
        }
        foreach (var l in newLocal.Values)
        {
            var parent = LocalParentRemote(l.Path, out _);
            steps.Add(new Step(l.Path, Depth(l.Path), Reconciler.Decide(new ItemFacts(l.IsFolder, LocalDelta.New, false, RemoteDelta.Absent, false)), null, null, l, parent));
        }
        return steps;
    }

    private static int Depth(string path) => path.Count(ch => ch == '/');

    private static IEnumerable<RemoteItem> Children(Ctx c, string id) => c.Tree.Values.Where(i => i.ParentId == id);

    private LocalDelta LocalFileDelta(Ctx c, BaseItem b, LocalEntry l)
    {
        if (l.Size == b.LocalSize && l.MTime == b.LocalMtime && l.FileId == b.LocalFileId) return LocalDelta.Unchanged;
        if (Hash(c, l) == b.Hash)
        {
            // Touched or saved without changes: refresh the stored stats only.
            b.LocalSize = l.Size;
            b.LocalMtime = l.MTime;
            b.LocalFileId = l.FileId;
            state.SaveItem(b);
            return LocalDelta.Unchanged;
        }
        return LocalDelta.Modified;
    }

    /// <summary>SHA-256 of a local file; null when it cannot be read now (open in an editor, locked): the item then waits (<see cref="Ctx.Busy"/>).</summary>
    private string? Hash(Ctx c, LocalEntry l)
    {
        if (c.HashCache.TryGetValue(l.Path, out var h)) return h;
        try
        {
            using var s = fs.OpenRead(Abs(c, l.Path));
            h = Convert.ToHexStringLower(SHA256.HashData(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            c.Busy.Add(l.Path);
            return null;
        }
        c.HashCache[l.Path] = h;
        return h;
    }

    /// <summary>Folders before content, local moves before remote moves, deletions last (files, then deepest folders).</summary>
    private static IEnumerable<Step> Order(List<Step> plan)
    {
        static int Phase(Step s)
        {
            var a = s.Decision.Actions;
            var folder = s.Base?.IsFolder ?? s.Remote?.IsFolder ?? s.LocalEntry?.IsFolder ?? false;
            if (folder && (a.HasFlag(SyncAction.UploadNew) || a.HasFlag(SyncAction.CreateLocal) || a.HasFlag(SyncAction.Converge) || a.HasFlag(SyncAction.RestoreLocal))) return 0;
            if (a.HasFlag(SyncAction.MoveRemote)) return 1;
            if (a.HasFlag(SyncAction.MoveLocal)) return 2;
            if (a.HasFlag(SyncAction.DeleteRemote) || a.HasFlag(SyncAction.DeleteLocal) || a == SyncAction.ForgetBase) return folder ? 5 : 4;
            return 3;
        }
        return plan.OrderBy(Phase).ThenBy(s => Phase(s) == 5 ? -s.Depth : s.Depth);
    }

    // ----------------------------------------------------------------- execution

    private async Task<bool> ExecuteAsync(Ctx c, Step s, CancellationToken ct)
    {
        var a = s.Decision.Actions;
        var b = s.Base;
        if (a.HasFlag(SyncAction.Quarantine)) return await QuarantineAsync(c, s, ct);
        if (a.HasFlag(SyncAction.Conflict)) return await ConflictAsync(c, s, ct);
        if (a == SyncAction.ForgetBase || (a.HasFlag(SyncAction.ForgetBase) && !a.HasFlag(SyncAction.DeleteLocal) && !a.HasFlag(SyncAction.DeleteRemote)))
        {
            Forget(c, b!);
            return true;
        }
        if (a.HasFlag(SyncAction.DeleteRemote)) return await DeleteRemoteAsync(c, s, ct);
        if (a.HasFlag(SyncAction.DeleteLocal)) return DeleteLocal(c, s);
        var moves = a & (SyncAction.MoveRemote | SyncAction.MoveLocal);
        var content = a & (SyncAction.UploadVersion | SyncAction.Download);
        if (a.HasFlag(SyncAction.Converge))
        {
            if (content != SyncAction.None)
            {
                // Both sides moved to the same place: record the location only, the content step below still runs.
                if (b is null || s.Remote is null || s.LocalEntry is null) return false;
                b.ParentRemoteId = s.Remote.ParentId;
                b.RemoteName = s.Remote.Name;
                b.Name = NameMapper.NameOf(s.LocalEntry.Path);
                state.SaveItem(b);
            }
            else
            {
                // Same content on both sides: record it; a pending rename / move on one side still runs below.
                var converged = Converge(c, s, location: moves == SyncAction.None);
                if (moves == SyncAction.None) return converged;
            }
        }
        if (a.HasFlag(SyncAction.UploadNew)) return await UploadNewAsync(c, s.LocalEntry!, ct);
        if (a.HasFlag(SyncAction.CreateLocal)) return await CreateLocalAsync(c, s.Remote!, null, ct);
        if (a.HasFlag(SyncAction.RestoreLocal)) return await RestoreAsync(c, s, ct);
        if (a.HasFlag(SyncAction.ReuploadAsNew)) return await ReuploadAsync(c, s, ct);

        var done = false;
        if (a.HasFlag(SyncAction.MoveRemote)) done |= await MoveRemoteAsync(c, s, ct);
        if (a.HasFlag(SyncAction.MoveLocal)) done |= MoveLocal(c, s);
        if (done && (a & (SyncAction.UploadVersion | SyncAction.Download)) != 0) return true; // content next round, from the new location
        if (a.HasFlag(SyncAction.UploadVersion)) done |= await UploadVersionAsync(c, s, ct);
        if (a.HasFlag(SyncAction.Download)) done |= await DownloadOverAsync(c, s, ct);
        return done;
    }

    /// <summary>
    /// Records a synced state. <paramref name="location"/> false for content-only steps: the base keeps its parent / names so a
    /// rename on either side that is still pending is not forgotten.
    /// </summary>
    private void Record(BaseItem b, RemoteItem r, string localName, LocalEntry? l, bool location = true)
    {
        b.RemoteId = r.Id;
        if (location)
        {
            b.ParentRemoteId = r.ParentId;
            b.RemoteName = r.Name;
            b.Name = localName;
        }
        b.IsFolder = r.IsFolder;
        b.RemoteRevision = r.Revision;
        b.Hash = r.Hash;
        b.Size = r.Size;
        if (l is not null)
        {
            b.LocalFileId = l.FileId;
            b.LocalSize = l.Size;
            b.LocalMtime = l.MTime;
        }
        b.SyncState = "in_sync";
        b.LastSyncedAt = clock.UtcNow;
        state.SaveItem(b);
    }

    private void Log(string action, string path, string? detail = null)
    {
        state.Log(action, path, detail);
        Activity?.Invoke(action, path);
    }

    private void Forget(Ctx c, BaseItem b)
    {
        foreach (var x in c.Base.Where(x => x.RemoteId == b.RemoteId || IsBaseDescendant(c, x, b.RemoteId)).ToList())
        {
            state.DeleteItem(x.LocalId);
            c.Base.Remove(x);
            c.BaseByRemote.Remove(x.RemoteId);
        }
    }

    private static bool IsBaseDescendant(Ctx c, BaseItem x, string ancestor)
    {
        var cur = x.ParentRemoteId;
        for (var guard = 0; cur is not null && guard < 256; guard++)
        {
            if (cur == ancestor) return true;
            cur = c.BaseByRemote.TryGetValue(cur, out var p) ? p.ParentRemoteId : null;
        }
        return false;
    }

    private string? ParentRemoteIdForLocalPath(Ctx c, string path, out bool ok)
    {
        var parent = NameMapper.ParentOf(path);
        ok = true;
        if (parent is null) return null;
        foreach (var b in c.Base)
            if (b.IsFolder && string.Equals(BasePath(c, b), parent, StringComparison.OrdinalIgnoreCase)) return b.RemoteId;
        ok = false;
        return null;
    }

    private bool LocalUnchanged(Ctx c, BaseItem b, string path)
    {
        var now = fs.Stat(Abs(c, path));
        return now is not null && now.IsFolder == b.IsFolder && (b.IsFolder || (now.FileId == b.LocalFileId && now.Size == b.LocalSize && now.MTime == b.LocalMtime));
    }

    private bool SameAsScan(Ctx c, LocalEntry l)
    {
        var now = fs.Stat(Abs(c, l.Path));
        return now is not null && now.IsFolder == l.IsFolder && now.FileId == l.FileId && now.Size == l.Size && now.MTime == l.MTime;
    }

    private async Task<(Stream Data, string Hash)> ReadSnapshotAsync(Ctx c, string path, CancellationToken ct)
    {
        // Snapshot the content first: the user may keep editing while the upload runs. Large files go to a temporary file (deleted on
        // close) instead of memory; the SHA-256 is computed while copying.
        await using var s = fs.OpenRead(Abs(c, path));
        Stream copy = _o.SnapshotDirectory is { } dir && s.CanSeek && s.Length > _o.MemorySnapshotLimit
            ? NewSnapshotFile(dir)
            : new MemoryStream();
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = await s.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                await copy.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            copy.Position = 0;
            return (copy, Convert.ToHexStringLower(sha.GetHashAndReset()));
        }
        catch
        {
            await copy.DisposeAsync();
            throw;
        }
    }

    private static FileStream NewSnapshotFile(string dir)
    {
        Directory.CreateDirectory(dir);
        return new FileStream(Path.Combine(dir, $"{Guid.NewGuid():N}.snapshot"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    private async Task<bool> UploadNewAsync(Ctx c, LocalEntry l, CancellationToken ct)
    {
        if (!SameAsScan(c, l)) return false;
        var parentId = ParentRemoteIdForLocalPath(c, l.Path, out var ok);
        if (!ok) return false; // parent folder not on the server yet: next round
        var name = NameMapper.NameOf(l.Path);
        RemoteItem r;
        try
        {
            if (l.IsFolder) r = await remote.CreateFolderAsync(c.RootId, parentId, name, ct);
            else
            {
                var (data, hash) = await ReadSnapshotAsync(c, l.Path, ct);
                await using (data) r = await remote.UploadNewAsync(c.RootId, parentId, name, data, hash, ct);
            }
        }
        catch (RemoteNameConflictException)
        {
            // Same name on the server (created there meanwhile): keep ours under the next free name.
            var siblings = c.Local.Where(x => NameMapper.ParentOf(x.Path) == NameMapper.ParentOf(l.Path)).Select(x => NameMapper.NameOf(x.Path))
                .Concat(c.Tree.Values.Where(t => t.ParentId == parentId).Select(t => LocalName(c, t))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var free = NameMapper.NextFree(name, siblings.Contains);
            fs.Move(Abs(c, l.Path), Abs(c, NameMapper.Combine(NameMapper.ParentOf(l.Path), free)));
            Log("Đổi tên", l.Path, $"Trùng tên trên máy chủ, đổi thành \"{free}\"");
            return true;
        }
        state.UpsertRemote(c.RootId, r);
        c.Tree[r.Id] = r;
        c.RemoteLocalName[r.Id] = name;
        var b = new BaseItem { RootId = c.RootId };
        var after = fs.Stat(Abs(c, l.Path));
        Record(b, r, name, after is not null && after.FileId == l.FileId && after.Size == l.Size && after.MTime == l.MTime ? l : l with { MTime = DateTime.MinValue });
        c.Base.Add(b);
        c.BaseByRemote[r.Id] = b;
        Log("Đã tải lên", l.Path);
        return true;
    }

    private async Task<bool> UploadVersionAsync(Ctx c, Step s, CancellationToken ct)
    {
        var b = s.Base!;
        var l = s.LocalEntry!;
        if (!SameAsScan(c, l)) return false;
        var (data, hash) = await ReadSnapshotAsync(c, l.Path, ct);
        RemoteItem r;
        await using (data) r = await remote.UploadVersionAsync(b.RemoteId, b.RemoteRevision, data, hash, ct);
        state.UpsertRemote(c.RootId, r);
        c.Tree[r.Id] = r;
        // If the file changed during the upload, keep the old stats: the next pass uploads again.
        Record(b, r, b.Name, SameAsScan(c, l) ? l : null, location: false);
        Log("Đã tải lên", l.Path, "Phiên bản mới");
        return true;
    }

    /// <summary>Downloads the files this round will write, <see cref="SyncEngineOptions.ParallelDownloads"/> at a time, into verified temporary files.</summary>
    private async Task PrefetchAsync(List<Step> plan, CancellationToken ct)
    {
        ClearPrefetch();
        if (_placeholders.OnDemand || _o.ParallelDownloads <= 1) return;
        long budget = _o.PrefetchBudget;
        var wanted = new List<RemoteItem>();
        foreach (var r in plan.Where(p => (p.Decision.Has(SyncAction.CreateLocal) || p.Decision.Has(SyncAction.Download)) && !p.Decision.Has(SyncAction.Conflict))
                     .Select(p => p.Remote).OfType<RemoteItem>().Where(r => !r.IsFolder && r.CanDownload && r.Size <= _o.PrefetchMaxFile).DistinctBy(r => r.Id))
        {
            if (r.Size > budget) break;
            budget -= r.Size;
            wanted.Add(r);
        }
        if (wanted.Count < 2) return;
        var dir = Path.Combine(_o.SnapshotDirectory ?? Path.GetTempPath(), "prefetch");
        Directory.CreateDirectory(dir);
        var done = new System.Collections.Concurrent.ConcurrentDictionary<string, (string, long)>();
        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = _o.ParallelDownloads, CancellationToken = ct }, async (r, token) =>
        {
            var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            try
            {
                await using (var file = File.Create(tmp))
                await using (var hashing = new HashingStream(file))
                {
                    await remote.DownloadAsync(r.Id, hashing, token);
                    if (r.Hash is not null && hashing.Hash() != r.Hash) throw new IOException("hash");
                }
                done[r.Id] = (tmp, r.Revision);
            }
            catch (Exception ex) when (ex is IOException or RemoteTransientException or RemotePermanentException or RemoteNotFoundException or UnauthorizedAccessException)
            {
                TryDelete(tmp); // the step downloads it again (and reports the error) when it runs
            }
        });
        foreach (var (id, v) in done) _prefetched[id] = v;
    }

    private void ClearPrefetch()
    {
        foreach (var (path, _) in _prefetched.Values) TryDelete(path);
        _prefetched.Clear();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<bool> WriteRemoteAsync(Ctx c, RemoteItem r, string path, CancellationToken ct)
    {
        if (!_placeholders.OnDemand && _prefetched.Remove(r.Id, out var pre))
        {
            try
            {
                if (pre.Revision == r.Revision)
                {
                    await fs.WriteAtomicAsync(Abs(c, path), async dest =>
                    {
                        await using var src = File.OpenRead(pre.Path);
                        await src.CopyToAsync(dest, ct);
                    }, r.ModifiedAt, ct);
                    return true;
                }
            }
            finally { TryDelete(pre.Path); }
        }
        if (_placeholders.OnDemand)
        {
            await _placeholders.CreatePlaceholderAsync(Abs(c, path), r, ct);
            return true;
        }
        // Streamed straight into the temporary file of the atomic write; a wrong hash aborts the write (the old file stays).
        await fs.WriteAtomicAsync(Abs(c, path), async dest =>
        {
            await using var hashing = new HashingStream(dest);
            await remote.DownloadAsync(r.Id, hashing, ct);
            if (r.Hash is not null && hashing.Hash() != r.Hash) throw new RemoteTransientException("Nội dung tải xuống không khớp mã băm, sẽ thử lại.");
        }, r.ModifiedAt, ct);
        return true;
    }

    private async Task<bool> CreateLocalAsync(Ctx c, RemoteItem r, string? at, CancellationToken ct)
    {
        var path = at ?? RemotePath(c, r.Id);
        if (path is null) return false;
        var parent = NameMapper.ParentOf(path);
        if (parent is not null && !(fs.Exists(Abs(c, parent), out var parentIsFolder) && parentIsFolder)) return false; // parent first
        if (fs.Exists(Abs(c, path), out _))
        {
            // Something unrelated sits there (different kind): it is renamed, the server item takes the name.
            var existing = fs.Stat(Abs(c, path))!;
            var siblings = c.Local.Where(x => NameMapper.ParentOf(x.Path) == parent).Select(x => NameMapper.NameOf(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var free = NameMapper.NextFree(NameMapper.NameOf(path), n => siblings.Contains(n) || fs.Stat(Abs(c, NameMapper.Combine(parent, n))) is not null);
            fs.Move(Abs(c, path), Abs(c, NameMapper.Combine(parent, free)));
            Log("Đổi tên", path, $"Nhường tên cho mục trên máy chủ, đổi thành \"{free}\" ({(existing.IsFolder ? "thư mục" : "tệp")})");
            return true;
        }
        if (r.IsFolder) fs.CreateDirectory(Abs(c, path));
        else await WriteRemoteAsync(c, r, path, ct);
        var b = c.BaseByRemote.GetValueOrDefault(r.Id) ?? new BaseItem { RootId = c.RootId };
        Record(b, r, NameMapper.NameOf(path), fs.Stat(Abs(c, path)));
        if (!c.BaseByRemote.ContainsKey(r.Id)) { c.Base.Add(b); c.BaseByRemote[r.Id] = b; }
        Log(r.IsFolder ? "Đã tạo thư mục" : _placeholders.OnDemand ? "Đã thêm (tải khi mở)" : "Đã tải xuống", path);
        return true;
    }

    private async Task<bool> DownloadOverAsync(Ctx c, Step s, CancellationToken ct)
    {
        var b = s.Base!;
        var r = s.Remote!;
        var path = BasePath(c, b);
        if (path is null || !LocalUnchanged(c, b, path)) return false;
        if (_placeholders.OnDemand) await _placeholders.UpdatePlaceholderAsync(Abs(c, path), r, ct);
        else await WriteRemoteAsync(c, r, path, ct);
        Record(b, r, b.Name, fs.Stat(Abs(c, path)), location: false);
        Log(_placeholders.OnDemand ? "Có phiên bản mới" : "Đã tải xuống", path, "Phiên bản mới");
        return true;
    }

    private async Task<bool> RestoreAsync(Ctx c, Step s, CancellationToken ct)
    {
        var b = s.Base!;
        if (s.Remote is null) { Forget(c, b); return true; }
        var path = RemotePath(c, s.Remote.Id);
        if (path is null || fs.Stat(Abs(c, path)) is not null) { Forget(c, b); return true; }
        Forget(c, b);
        Log("Khôi phục", path, s.Decision.Reason);
        return await CreateLocalAsync(c, s.Remote, path, ct);
    }

    private async Task<bool> ReuploadAsync(Ctx c, Step s, CancellationToken ct)
    {
        Forget(c, s.Base!);
        Log("Tải lên lại", s.Path, s.Decision.Reason);
        return await UploadNewAsync(c, s.LocalEntry!, ct) || true;
    }

    private bool Converge(Ctx c, Step s, bool location = true)
    {
        if (s.LocalEntry is null || !SameAsScan(c, s.LocalEntry)) return false; // changed since the scan: next round
        if (s.Base is { } b)
        {
            if (s.Remote is null) return false;
            Record(b, s.Remote, NameMapper.NameOf(s.LocalEntry.Path), s.LocalEntry, location);
            Log("Đã khớp", s.LocalEntry.Path, "Nội dung giống máy chủ");
            return true;
        }
        // Linked by a case-insensitive path: take the server's spelling so both sides end up identical.
        var local = s.LocalEntry!;
        var serverName = LocalName(c, s.Remote!);
        if (NameMapper.NameOf(local.Path) != serverName)
        {
            var to = NameMapper.Combine(NameMapper.ParentOf(local.Path), serverName);
            fs.Move(Abs(c, local.Path), Abs(c, to));
            local = fs.Stat(Abs(c, to)) is { } moved ? moved with { Path = to } : local with { Path = to };
        }
        var nb = new BaseItem { RootId = c.RootId };
        Record(nb, s.Remote!, serverName, local);
        Log("Đã khớp", local.Path, "Có sẵn ở cả hai phía");
        c.Base.Add(nb);
        c.BaseByRemote[nb.RemoteId] = nb;
        return true;
    }

    private async Task<bool> MoveRemoteAsync(Ctx c, Step s, CancellationToken ct)
    {
        var b = s.Base!;
        var l = s.LocalEntry!;
        if (fs.Stat(Abs(c, l.Path)) is null) return false;
        var parentId = ParentRemoteIdForLocalPath(c, l.Path, out var ok);
        if (!ok) return false;
        var localName = NameMapper.NameOf(l.Path);
        var remoteName = localName == NameMapper.ToLocal(b.RemoteName) ? b.RemoteName : localName;
        RemoteItem r;
        // The server copy may have new content (handled after the move): the location did not change there, so the move is
        // guarded by the current revision, not the base one.
        var revision = s.Remote?.Revision ?? b.RemoteRevision;
        try { r = await remote.MoveAsync(b.RemoteId, revision, c.RootId, parentId, remoteName, ct); }
        catch (RemoteNameConflictException)
        {
            // The name is taken on the server: keep ours under the next free name, pushed next round.
            var parent = NameMapper.ParentOf(l.Path);
            var free = NameMapper.NextFree(localName, n => fs.Stat(Abs(c, NameMapper.Combine(parent, n))) is not null
                || c.Tree.Values.Any(t => t.ParentId == parentId && t.Id != b.RemoteId && string.Equals(LocalName(c, t), n, StringComparison.OrdinalIgnoreCase)));
            fs.Move(Abs(c, l.Path), Abs(c, NameMapper.Combine(parent, free)));
            Log("Đổi tên", l.Path, $"Trùng tên trên máy chủ, đổi thành \"{free}\"");
            return true;
        }
        catch (RemotePermanentException ex)
        {
            // Refused for good (e.g. it would put a folder inside itself after a move on the server): the server wins, the
            // local move is undone and the server's layout is applied next round.
            if (s.Remote is null || RemotePath(c, s.Remote.Id) is not { } back || fs.Stat(Abs(c, back)) is not null) throw;
            var backParent = NameMapper.ParentOf(back);
            if (backParent is not null && fs.Stat(Abs(c, backParent)) is not { IsFolder: true }) throw;
            if (backParent is not null && NameMapper.IsUnder(Abs(c, backParent), Abs(c, l.Path))) throw;
            fs.Move(Abs(c, l.Path), Abs(c, back));
            Log("Hoàn tác", l.Path, $"Máy chủ từ chối ({ex.Message}), trả về {back}");
            return true;
        }
        state.UpsertRemote(c.RootId, r);
        c.Tree[r.Id] = r;
        c.RemoteLocalName[r.Id] = localName;
        b.ParentRemoteId = r.ParentId;
        b.RemoteName = r.Name;
        b.Name = localName;
        // Keep the base revision when the content is still to be reconciled (download next round compares hashes).
        b.RemoteRevision = s.Remote is { } known && known.Hash != b.Hash ? b.RemoteRevision : r.Revision;
        state.SaveItem(b);
        Log("Đổi tên/di chuyển", l.Path);
        return true;
    }

    private bool MoveLocal(Ctx c, Step s)
    {
        var b = s.Base!;
        var r = s.Remote!;
        var from = s.LocalEntry?.Path ?? BasePath(c, b);
        if (from is null || fs.Stat(Abs(c, from)) is null) return false;
        // Target: under the parent's current local location.
        string? parentPath = null;
        if (r.ParentId is { } pid)
        {
            if (!c.BaseByRemote.TryGetValue(pid, out var pb)) return false;
            parentPath = BasePath(c, pb);
            if (parentPath is null || fs.Stat(Abs(c, parentPath)) is not { IsFolder: true }) return false;
        }
        var name = c.RemoteLocalName.GetValueOrDefault(r.Id) ?? NameMapper.ToLocal(r.Name);
        var to = NameMapper.Combine(parentPath, name);
        if (string.Equals(from, to, StringComparison.Ordinal)) { }
        else if (fs.Stat(Abs(c, to)) is not null && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false; // occupied: next round
        else fs.Move(Abs(c, from), Abs(c, to));
        b.ParentRemoteId = r.ParentId;
        b.RemoteName = r.Name;
        b.Name = name;
        b.RemoteRevision = r.Revision;
        state.SaveItem(b);
        Log("Đổi tên/di chuyển", to, $"Từ {from}");
        return true;
    }

    private async Task<bool> DeleteRemoteAsync(Ctx c, Step s, CancellationToken ct)
    {
        var b = s.Base!;
        var path = BasePath(c, b) ?? s.Path;
        if (fs.Stat(Abs(c, path)) is not null) return false; // came back meanwhile
        if (b.IsFolder && c.Base.Any(x => x.ParentRemoteId == b.RemoteId)) return false; // children first
        await remote.DeleteAsync(b.RemoteId, b.RemoteRevision, ct);
        RemoveSubtree(c.RootId, b.RemoteId);
        foreach (var id in Subtree(c.Tree, b.RemoteId).ToList()) c.Tree.Remove(id);
        Forget(c, b);
        Log("Đã xóa trên BlueDrive", path);
        return true;
    }

    private bool DeleteLocal(Ctx c, Step s)
    {
        var b = s.Base!;
        var l = s.LocalEntry;
        if (l is null) { Forget(c, b); return true; }
        if (b.IsFolder)
        {
            // Only an empty folder (children already handled) is removed; unsynced content keeps it, uploaded again next round.
            var inside = fs.Scan(Abs(c, l.Path)).Select(e => Rel(c.Dir, e.Path)).Where(p => !IsExcludedPath(p)).ToList();
            if (inside.Count > 0)
            {
                Forget(c, b);
                return true;
            }
        }
        else if (!LocalUnchanged(c, b, l.Path)) return false; // edited meanwhile: next round re-uploads it
        fs.Delete(Abs(c, l.Path));
        Forget(c, b);
        Log("Đã xóa trên máy", l.Path, s.Decision.Reason);
        return true;
    }

    private async Task<bool> ConflictAsync(Ctx c, Step s, CancellationToken ct)
    {
        var l = s.LocalEntry!;
        var r = s.Remote!;
        if (!SameAsScan(c, l)) return false;
        var parent = NameMapper.ParentOf(l.Path);
        var stamp = TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, _o.TimeZone);
        var name = NameMapper.ConflictName(NameMapper.NameOf(l.Path), _o.UserDisplayName, _o.DeviceName, stamp);
        name = NameMapper.NextFree(name, n => fs.Stat(Abs(c, NameMapper.Combine(parent, n))) is not null);
        var conflictPath = NameMapper.Combine(parent, name);
        // Keep the local edit under the conflict name (uploaded as a new file next round), take the server copy under the original name.
        fs.Move(Abs(c, l.Path), Abs(c, conflictPath));
        state.AddConflict(c.RootId, l.Path, conflictPath, "content");
        Log("Xung đột", l.Path, $"Bản trên máy được lưu thành \"{name}\"");
        if (s.Base is { } b)
        {
            var path = RemotePath(c, r.Id) ?? l.Path;
            var parent2 = NameMapper.ParentOf(path);
            if (fs.Stat(Abs(c, path)) is not null || (parent2 is not null && fs.Stat(Abs(c, parent2)) is not { IsFolder: true }))
            {
                // The server copy cannot be written yet: detach the base from the renamed local file (it must not follow it),
                // the server copy is restored next round.
                b.LocalFileId = null;
                b.LocalSize = -1;
                state.SaveItem(b);
                return true;
            }
            await WriteRemoteAsync(c, r, path, ct);
            Record(b, r, NameMapper.NameOf(path), fs.Stat(Abs(c, path)));
            return true;
        }
        return await CreateLocalAsync(c, r, null, ct); // the server's spelling of the name
    }

    private void QuarantineNew(Ctx c, LocalEntry l, string reason)
    {
        var target = NameMapper.Combine(IgnoreRules.NotSyncedFolder, l.Path);
        var targetParent = NameMapper.ParentOf(target)!;
        fs.CreateDirectory(Abs(c, targetParent));
        var free = NameMapper.NextFree(NameMapper.NameOf(target), n => fs.Stat(Abs(c, NameMapper.Combine(targetParent, n))) is not null);
        target = NameMapper.Combine(targetParent, free);
        fs.Move(Abs(c, l.Path), Abs(c, target));
        state.AddConflict(c.RootId, l.Path, target, "not_allowed");
        Log("Không đồng bộ được", l.Path, $"{reason} Đã chuyển vào: {target}");
    }

    private async Task<bool> QuarantineAsync(Ctx c, Step s, CancellationToken ct)
    {
        var l = s.LocalEntry;
        var b = s.Base!;
        if (l is null || l.IsFolder) { Forget(c, b); return true; }
        var target = NameMapper.Combine(IgnoreRules.NotSyncedFolder, l.Path);
        var targetParent = NameMapper.ParentOf(target)!;
        fs.CreateDirectory(Abs(c, targetParent));
        var free = NameMapper.NextFree(NameMapper.NameOf(target), n => fs.Stat(Abs(c, NameMapper.Combine(targetParent, n))) is not null);
        target = NameMapper.Combine(targetParent, free);
        state.AddConflict(c.RootId, l.Path, target, s.Remote is null ? "access_revoked" : "read_only");
        if (s.Decision.Has(SyncAction.Download) && s.Remote is { } r)
        {
            fs.Copy(Abs(c, l.Path), Abs(c, target));
            await WriteRemoteAsync(c, r, l.Path, ct);
            Record(b, r, b.Name, fs.Stat(Abs(c, l.Path)), location: false);
        }
        else
        {
            fs.Move(Abs(c, l.Path), Abs(c, target));
            Forget(c, b);
        }
        Log("Không đồng bộ được", l.Path, $"{s.Decision.Reason} Bản trên máy: {target}");
        return true;
    }
}
