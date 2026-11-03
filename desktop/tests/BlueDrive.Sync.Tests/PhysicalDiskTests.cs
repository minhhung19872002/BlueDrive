using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>Full sync mode on a real disk (Sync-Phase 4): <see cref="PhysicalFileSystem"/> and the engine over it.</summary>
public sealed class PhysicalDiskTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bd-sync-" + Guid.NewGuid().ToString("N")[..8]);

    public PhysicalDiskTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { }
    }

    private PhysicalFileSystem Disk(string name)
    {
        var dir = Path.Combine(_tmp, name);
        Directory.CreateDirectory(dir);
        return new PhysicalFileSystem(dir, useRecycleBin: false);
    }

    private static void Write(PhysicalFileSystem fs, string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fs.Abs(path))!);
        File.WriteAllText(fs.Abs(path), text);
    }

    private static string Read(PhysicalFileSystem fs, string path) => File.ReadAllText(fs.Abs(path));

    [Fact]
    public void Scan_reports_relative_paths_and_a_file_id_that_survives_rename_and_move()
    {
        var fs = Disk("a");
        Write(fs, "Báo cáo/quý 1.txt", "một");
        Write(fs, "Báo cáo/quý 2.txt", "hai");
        var scan = fs.Scan("");
        scan.Select(e => e.Path).Should().Equal("Báo cáo", "Báo cáo/quý 1.txt", "Báo cáo/quý 2.txt");
        var id = scan.Single(e => e.Path == "Báo cáo/quý 1.txt").FileId;
        id.Should().NotBe(scan.Single(e => e.Path == "Báo cáo/quý 2.txt").FileId);

        fs.CreateDirectory("Lưu trữ");
        fs.Move("Báo cáo/quý 1.txt", "Lưu trữ/Quý 1 - 2026.txt");
        var moved = fs.Stat("Lưu trữ/Quý 1 - 2026.txt")!;
        if (OperatingSystem.IsWindows()) moved.FileId.Should().Be(id, "NTFS keeps the file id across renames and moves");
        moved.Size.Should().Be(5); // "một" is 5 bytes in UTF-8
        fs.Stat("Báo cáo/quý 1.txt").Should().BeNull();
    }

    [Fact]
    public async Task Atomic_write_replaces_content_keeps_server_mtime_and_leaves_no_temp_file()
    {
        var fs = Disk("a");
        Write(fs, "a.txt", "cũ");
        var mtime = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc);
        await fs.WriteAtomicAsync("a.txt", async s => await s.WriteAsync("mới"u8.ToArray()), mtime, CancellationToken.None);
        Read(fs, "a.txt").Should().Be("mới");
        fs.Stat("a.txt")!.MTime.Should().Be(mtime);
        Directory.GetFiles(fs.Root).Should().HaveCount(1);
        await fs.Invoking(x => x.WriteAtomicAsync("thiếu/b.txt", _ => Task.CompletedTask, mtime, CancellationToken.None)).Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public void Case_only_rename_of_a_folder_works_and_moving_a_folder_into_itself_is_refused()
    {
        var fs = Disk("a");
        Write(fs, "hop dong/x.txt", "x");
        fs.Move("hop dong", "Hop Dong");
        fs.Scan("").Select(e => e.Path).Should().Contain("Hop Dong/x.txt");
        fs.Invoking(x => x.Move("Hop Dong", "Hop Dong/con")).Should().Throw<IOException>();
        fs.Delete("Hop Dong");
        fs.Scan("").Should().BeEmpty();
    }

    [WindowsFact]
    public void Hidden_system_files_and_links_are_not_synced()
    {
        var fs = Disk("a");
        Write(fs, "thường.txt", "x");
        Write(fs, "ẩn.dat", "x");
        File.SetAttributes(fs.Abs("ẩn.dat"), FileAttributes.Hidden | FileAttributes.System);
        var skipped = new List<string>();
        fs.Skipped += (p, _) => skipped.Add(p);
        var outside = Disk("ngoài");
        Write(outside, "bí mật.txt", "không được ra khỏi thư mục đồng bộ");
        var linked = true;
        try { Directory.CreateSymbolicLink(fs.Abs("liên kết"), outside.Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { linked = false; } // needs Developer Mode on Windows
        fs.Scan("").Select(e => e.Path).Should().Equal("thường.txt");
        if (linked) skipped.Should().Contain("liên kết");
    }

    [WindowsFact]
    public void A_file_open_for_writing_cannot_be_read_for_upload()
    {
        var fs = Disk("a");
        Write(fs, "đang mở.docx", "x");
        using (new FileStream(fs.Abs("đang mở.docx"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            fs.Invoking(x => x.OpenRead("đang mở.docx").Dispose()).Should().Throw<IOException>();
        using var s = fs.OpenRead("đang mở.docx");
        s.Length.Should().Be(1);
    }

    // ---------------------------------------------------------------- engine on real disks

    private (SyncEngine Engine, PhysicalFileSystem Fs, SyncStateDb Db) Pc(string name, FakeRemoteStore remote)
    {
        var fs = Disk(name);
        var db = new SyncStateDb(":memory:");
        db.UpsertRoot(FakeRemoteStore.Root, "", true);
        return (new SyncEngine(db, remote, fs, SystemClock.Instance, new SyncEngineOptions { DeviceName = name, UserDisplayName = "Người thử" }), fs, db);
    }

    [Fact]
    public async Task Two_computers_converge_on_real_disks_and_a_locked_file_waits_without_blocking_the_rest()
    {
        var remote = new FakeRemoteStore(new FakeClock(DateTime.UtcNow));
        var (a, fa, dba) = Pc("PC-A", remote);
        var (b, fb, dbb) = Pc("PC-B", remote);
        using (dba)
        using (dbb)
        {
            Write(fa, "Dự án/kế hoạch.txt", "v1");
            Write(fa, "Dự án/ngân sách.txt", "100");
            (await a.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
            (await b.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
            Read(fb, "Dự án/kế hoạch.txt").Should().Be("v1");

            // B renames the folder; A follows without downloading again (same file on disk).
            var idBefore = fa.Stat("Dự án/kế hoạch.txt")!.FileId;
            fb.Move("Dự án", "Dự án 2026");
            await b.SyncAsync(CancellationToken.None);
            await a.SyncAsync(CancellationToken.None);
            if (OperatingSystem.IsWindows()) fa.Stat("Dự án 2026/kế hoạch.txt")!.FileId.Should().Be(idBefore);
            else Read(fa, "Dự án 2026/kế hoạch.txt").Should().Be("v1"); // path-based ids elsewhere: delete + create, same result

            // A file held open by an editor is skipped (reported), the other edit goes through; once closed it syncs.
            File.WriteAllText(fa.Abs("Dự án 2026/kế hoạch.txt"), "v2 - đang sửa");
            File.WriteAllText(fa.Abs("Dự án 2026/ngân sách.txt"), "250");
            if (OperatingSystem.IsWindows()) // sharing modes are only enforced by Windows
            {
                using (new FileStream(fa.Abs("Dự án 2026/kế hoạch.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    var locked = await a.SyncAsync(CancellationToken.None);
                    locked.Errors.Should().ContainSingle().Which.Should().Contain("kế hoạch.txt");
                }
                await b.SyncAsync(CancellationToken.None);
                Read(fb, "Dự án 2026/ngân sách.txt").Should().Be("250");
                Read(fb, "Dự án 2026/kế hoạch.txt").Should().Be("v1");
            }
            (await a.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
            await b.SyncAsync(CancellationToken.None);
            Read(fb, "Dự án 2026/kế hoạch.txt").Should().Be("v2 - đang sửa");

            // Delete on B → gone on A; nothing left to do afterwards, no temp files anywhere.
            fb.Delete("Dự án 2026/ngân sách.txt");
            await b.SyncAsync(CancellationToken.None);
            await a.SyncAsync(CancellationToken.None);
            fa.Stat("Dự án 2026/ngân sách.txt").Should().BeNull();
            (await a.SyncAsync(CancellationToken.None)).Operations.Should().Be(0);
            (await b.SyncAsync(CancellationToken.None)).Operations.Should().Be(0);
            Directory.GetFiles(_tmp, "*" + IgnoreRules.TempSuffix, SearchOption.AllDirectories).Should().BeEmpty();
        }
    }
}

/// <summary>Runs only on Windows (NTFS sharing modes, hidden+system attributes); skipped on the Linux CI runners.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Chỉ áp dụng trên Windows.";
    }
}
