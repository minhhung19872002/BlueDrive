using System.Text;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;
using BlueDrive.Sync.Testing;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>Sync-Phase 10/11: the full sync mode on macOS / Linux disks (run by the macOS CI job and the Linux backend job).</summary>
public sealed class UnixDiskTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bd-unix-" + Guid.NewGuid().ToString("N")[..8]);

    public UnixDiskTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { }
    }

    [UnixFact]
    public void Inode_identifies_a_file_across_renames_and_moves()
    {
        var fs = new PhysicalFileSystem(_tmp, useRecycleBin: false);
        fs.CreateDirectory("Ấn phẩm");
        File.WriteAllText(Path.Combine(_tmp, "tờ rơi.txt"), "nội dung");
        var before = fs.Stat("tờ rơi.txt")!;
        before.FileId.Should().StartWith("u:");
        fs.Move("tờ rơi.txt", "Ấn phẩm/tờ rơi 2026.txt");
        fs.Stat("Ấn phẩm/tờ rơi 2026.txt")!.FileId.Should().Be(before.FileId);
        fs.Scan("").Single(e => e.Path == "Ấn phẩm/tờ rơi 2026.txt").FileId.Should().Be(before.FileId);
        fs.Stat("Ấn phẩm")!.FileId.Should().NotBe(before.FileId);
    }

    [MacFact]
    public void Decomposed_names_from_the_disk_are_reported_in_nfc()
    {
        var nfd = "Kế hoạch truyền thông dân số.docx".Normalize(NormalizationForm.FormD);
        File.WriteAllText(Path.Combine(_tmp, nfd), "x");
        var fs = new PhysicalFileSystem(_tmp, useRecycleBin: false);
        var entry = fs.Scan("").Single();
        entry.Path.Should().Be("Kế hoạch truyền thông dân số.docx".Normalize(NormalizationForm.FormC));
        entry.Path.IsNormalized(NormalizationForm.FormC).Should().BeTrue();
        fs.Stat(entry.Path).Should().NotBeNull("the file system finds the NFD file under its NFC name");
    }

    [UnixFact]
    public async Task Engine_syncs_both_ways_on_this_disk()
    {
        var remote = new FakeRemoteStore(new FakeClock(DateTime.UtcNow));
        remote.WebCreateFolder(null, "Ảnh sự kiện");
        remote.WebUpload("Ảnh sự kiện", "khai mạc.txt", Content.Of("ảnh"));
        using var db = new SyncStateDb(Path.Combine(_tmp, "state.db"));
        db.UpsertRoot(FakeRemoteStore.Root, "", true);
        var root = Path.Combine(_tmp, "BlueDrive");
        var fs = new PhysicalFileSystem(root, useRecycleBin: false);
        var engine = new SyncEngine(db, remote, fs, SystemClock.Instance, new SyncEngineOptions { DeviceName = "MAC" });
        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        File.ReadAllText(Path.Combine(root, "Ảnh sự kiện", "khai mạc.txt")).Should().Be("ảnh");

        File.Move(Path.Combine(root, "Ảnh sự kiện", "khai mạc.txt"), Path.Combine(root, "Ảnh sự kiện", "khai mạc 2026.txt"));
        File.WriteAllText(Path.Combine(root, "bế mạc.txt"), "mới");
        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        remote.Tree().Keys.Should().BeEquivalentTo(["Ảnh sự kiện", "Ảnh sự kiện/khai mạc 2026.txt", "bế mạc.txt"], "a rename stays a rename (same inode)");
    }

    [UnixFact]
    public void Deleted_items_go_to_the_trash()
    {
        var home = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(Path.Combine(home, ".Trash"));
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        var oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
        try
        {
            var fs = new PhysicalFileSystem(Path.Combine(_tmp, "sync"), useRecycleBin: true);
            Directory.CreateDirectory(fs.Root);
            File.WriteAllText(Path.Combine(fs.Root, "biên bản.txt"), "x");
            fs.Delete("biên bản.txt");
            File.Exists(Path.Combine(fs.Root, "biên bản.txt")).Should().BeFalse();
            var trashed = OperatingSystem.IsMacOS() ? Path.Combine(home, ".Trash", "biên bản.txt") : Path.Combine(home, ".local", "share", "Trash", "files", "biên bản.txt");
            File.Exists(trashed).Should().BeTrue();
            if (OperatingSystem.IsLinux())
                File.ReadAllText(Path.Combine(home, ".local", "share", "Trash", "info", "biên bản.txt.trashinfo")).Should().Contain("Path=");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldData);
        }
    }

    [MacFact]
    public void Tokens_are_kept_in_the_keychain()
    {
        var store = new KeychainTokenStore("bluedrive-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            store.Load().Should().BeNull();
            store.Save(new DeviceTokens("access", "refresh", 3600, Guid.NewGuid(), "secret"));
            store.Load()!.RefreshToken.Should().Be("refresh");
            store.Save(new DeviceTokens("access2", "refresh2", 3600, Guid.NewGuid(), "secret"));
            store.Load()!.RefreshToken.Should().Be("refresh2");
        }
        finally { store.Clear(); }
        store.Load().Should().BeNull();
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Linux_tokens_file_is_readable_only_by_the_user()
    {
        var path = Path.Combine(_tmp, "tokens.json");
        var store = new UserOnlyFileTokenStore(path);
        store.Save(new DeviceTokens("access", "refresh", 3600, Guid.NewGuid(), "secret"));
        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        store.Load()!.RefreshToken.Should().Be("refresh");
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Chỉ áp dụng trên macOS / Linux.";
    }
}

public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Chỉ áp dụng trên macOS.";
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Chỉ áp dụng trên Linux.";
    }
}
