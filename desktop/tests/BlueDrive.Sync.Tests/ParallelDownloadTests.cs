using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>"Số luồng tải song song" (spec 8.3): full sync mode downloads several files at once, the steps still run one by one.</summary>
public sealed class ParallelDownloadTests
{
    /// <summary>The fake server behind a lock (like HttpClient, the real store accepts concurrent calls); measures concurrent downloads.</summary>
    private sealed class ConcurrentRemote(FakeRemoteStore inner) : IRemoteStore
    {
        private readonly object _gate = new();
        private int _active;
        public int MaxActive;
        public int Downloads;

        public async Task DownloadAsync(string itemId, Stream destination, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _active);
            lock (_gate) MaxActive = Math.Max(MaxActive, now);
            Interlocked.Increment(ref Downloads);
            await Task.Delay(40, ct);
            var copy = new MemoryStream();
            lock (_gate) inner.DownloadAsync(itemId, copy, ct).GetAwaiter().GetResult();
            Interlocked.Decrement(ref _active);
            copy.Position = 0;
            await copy.CopyToAsync(destination, ct);
        }

        private T Locked<T>(Func<Task<T>> call)
        {
            lock (_gate) return call().GetAwaiter().GetResult();
        }

        public Task<RemoteSnapshotPage> SnapshotAsync(string rootId, string? pageToken, string? folderId, CancellationToken ct) =>
            Task.FromResult(Locked(() => inner.SnapshotAsync(rootId, pageToken, folderId, ct)));
        public Task<RemoteChangePage> ChangesAsync(long cursor, CancellationToken ct) => Task.FromResult(Locked(() => inner.ChangesAsync(cursor, ct)));
        public Task<RemoteItem> UploadNewAsync(string rootId, string? parentId, string name, Stream content, string hash, CancellationToken ct) =>
            Task.FromResult(Locked(() => inner.UploadNewAsync(rootId, parentId, name, content, hash, ct)));
        public Task<RemoteItem> UploadVersionAsync(string itemId, long baseRevision, Stream content, string hash, CancellationToken ct) =>
            Task.FromResult(Locked(() => inner.UploadVersionAsync(itemId, baseRevision, content, hash, ct)));
        public Task<RemoteItem> CreateFolderAsync(string rootId, string? parentId, string name, CancellationToken ct) =>
            Task.FromResult(Locked(() => inner.CreateFolderAsync(rootId, parentId, name, ct)));
        public Task<RemoteItem> MoveAsync(string itemId, long revision, string rootId, string? newParentId, string newName, CancellationToken ct) =>
            Task.FromResult(Locked(() => inner.MoveAsync(itemId, revision, rootId, newParentId, newName, ct)));
        public Task DeleteAsync(string itemId, long revision, CancellationToken ct)
        {
            lock (_gate) inner.DeleteAsync(itemId, revision, ct).GetAwaiter().GetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Files_are_downloaded_several_at_a_time_and_written_correctly()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var fake = new FakeRemoteStore(clock);
        fake.WebCreateFolder(null, "Ảnh");
        for (var i = 0; i < 12; i++) fake.WebUpload("Ảnh", $"ảnh {i}.jpg", Content.Of($"nội dung {i}"));
        var remote = new ConcurrentRemote(fake);
        using var db = new SyncStateDb(":memory:");
        db.UpsertRoot(FakeRemoteStore.Root, "", true);
        var fs = new FakeLocalFileSystem(clock);
        var engine = new SyncEngine(db, remote, fs, clock, new SyncEngineOptions { DeviceName = "PC", ParallelDownloads = 4, TimeZone = TimeZoneInfo.Utc });

        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();

        remote.MaxActive.Should().BeInRange(2, 4, "several downloads at once, never more than the setting");
        remote.Downloads.Should().Be(12, "each file is downloaded once (the step reuses the prefetched copy)");
        for (var i = 0; i < 12; i++) fs.Read($"Ảnh/ảnh {i}.jpg").Should().Equal(Content.Of($"nội dung {i}"));
        (await engine.SyncAsync(CancellationToken.None)).Operations.Should().Be(0);
    }
}
