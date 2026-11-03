using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>Spec 6.3 mass delete with a pass in the middle of a one-by-one deletion (pushed by the server, file system events late).</summary>
public sealed class DeleteSettleTests
{
    [Fact]
    public async Task Deletions_wait_one_pass_so_a_half_done_mass_delete_is_still_asked()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var remote = new FakeRemoteStore(clock);
        remote.WebCreateFolder(null, "Lưu trữ");
        for (var i = 0; i < 400; i++) remote.WebUpload("Lưu trữ", $"{i:000}.txt", Content.Of($"tệp {i}"));
        using var db = new SyncStateDb(":memory:");
        db.UpsertRoot(FakeRemoteStore.Root, "", true);
        var fs = new FakeLocalFileSystem(clock);
        var engine = new SyncEngine(db, remote, fs, clock,
            new SyncEngineOptions { DeviceName = "PC", TimeZone = TimeZoneInfo.Utc, DeleteSettleTime = TimeSpan.FromSeconds(3) });
        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();

        // A pass while only 50 files are deleted: nothing goes to the server yet.
        for (var i = 0; i < 50; i++) fs.UserDelete($"Lưu trữ/{i:000}.txt");
        var early = await engine.SyncAsync(CancellationToken.None);
        early.DeletesDeferred.Should().BeTrue();
        early.MassDeletePending.Should().Be(0);
        remote.Tree().Keys.Count(k => k.StartsWith("Lưu trữ/")).Should().Be(400);

        // The user finishes (120 deleted), the next pass after the settle time sees all of them and asks.
        for (var i = 50; i < 120; i++) fs.UserDelete($"Lưu trữ/{i:000}.txt");
        clock.Now = clock.Now.AddSeconds(4);
        var later = await engine.SyncAsync(CancellationToken.None);
        later.MassDeletePending.Should().Be(120);
        remote.Tree().Keys.Count(k => k.StartsWith("Lưu trữ/")).Should().Be(400, "nothing deleted before the user answers");

        // A few deletions (not "mass") go up once settled.
        engine.ResolveMassDelete(FakeRemoteStore.Root, deleteOnServer: false);
        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        fs.UserDelete("Lưu trữ/000.txt");
        (await engine.SyncAsync(CancellationToken.None)).DeletesDeferred.Should().BeTrue();
        remote.Tree().Should().ContainKey("Lưu trữ/000.txt");
        clock.Now = clock.Now.AddSeconds(4);
        await engine.SyncAsync(CancellationToken.None);
        remote.Tree().Should().NotContainKey("Lưu trữ/000.txt");
    }
}
