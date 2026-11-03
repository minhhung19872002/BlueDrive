using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>Spec 8.2 "Đang đồng bộ 12 tệp (35 MB còn lại, ~2 phút)": the engine counts what is left in the running pass.</summary>
public sealed class SyncProgressTests
{
    [Fact]
    public async Task The_engine_counts_the_files_and_bytes_left_while_it_transfers()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var remote = new FakeRemoteStore(clock);
        remote.WebCreateFolder(null, "Ảnh");
        for (var i = 0; i < 3; i++) remote.WebUpload("Ảnh", $"{i}.jpg", Content.Of(new string('x', 1000 * (i + 1))));
        using var db = new SyncStateDb(":memory:");
        db.UpsertRoot(FakeRemoteStore.Root, "", true);
        var engine = new SyncEngine(db, remote, new FakeLocalFileSystem(clock), clock, new SyncEngineOptions { DeviceName = "PC", TimeZone = TimeZoneInfo.Utc });
        var seen = new List<(int Files, long Bytes)>();
        remote.Downloading = _ => seen.Add(engine.Remaining);

        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        seen.Should().HaveCount(3);
        seen[0].Should().Be((3, 6000L), "folders are not counted, files with their size");
        seen.Select(s => s.Files).Should().BeInDescendingOrder();
        seen[^1].Files.Should().Be(1);
        engine.Remaining.Should().Be((0, 0L), "nothing left once the pass is over");
    }
}
