using System.Diagnostics;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Windows.Tests;

/// <summary>Sync-Phase 5: Files On-Demand through the Cloud Files API on this computer (temporary sync root under %TEMP%).</summary>
public sealed class FilesOnDemandTests : IAsyncLifetime
{
    // Under the user profile: Windows silently ignores shell sync root registrations in %TEMP% / AppData.
    private static readonly string Parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BlueDrive-tests");
    private readonly string _tmp = Path.Combine(Parent, "cf-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeRemoteStore _remote = new(new FakeClock(DateTime.UtcNow));
    private CloudFilesPlatform _platform = null!;
    private SyncStateDb _db = null!;
    private SyncEngine _engine = null!;
    private string Root => Path.Combine(_tmp, "BlueDrive");

    /// <summary>Hydration source over the fake server.</summary>
    private sealed class FakeRange(FakeRemoteStore remote) : IRemoteRangeReader
    {
        public int Reads;

        public async Task ReadRangeAsync(string itemId, long offset, long length, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            var data = remote.Data(itemId);
            for (var pos = offset; pos < offset + length; pos += 100_000)
                await sink(data.AsMemory((int)pos, (int)Math.Min(100_000, offset + length - pos)), ct);
        }
    }

    private FakeRange _range = null!;
    private readonly Guid _accountId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        var dbPath = Path.Combine(_tmp, "state.db");
        _db = new SyncStateDb(dbPath);
        _db.UpsertRoot(FakeRemoteStore.Root, "", true);
        _range = new FakeRange(_remote);
        _platform = new CloudFilesPlatform(new OnDemandContext(Root, _accountId, "Kiểm thử", new Uri("https://bluedrive.example/"), _range, dbPath));
        await _platform.StartAsync(CancellationToken.None);
        _engine = new SyncEngine(_db, _remote, _platform.FileSystem, SystemClock.Instance,
            new SyncEngineOptions { UserDisplayName = "Người thử", DeviceName = "PC-CF" }, _platform.Placeholders);
    }

    public async Task DisposeAsync()
    {
        await _platform.UnregisterAsync();
        await _platform.DisposeAsync();
        SyncRootRegistrar.IsRegistered(_accountId).Should().BeFalse("signing out removes the sync root");
        _db.Dispose();
        try
        {
            Directory.Delete(_tmp, recursive: true);
            if (!Directory.EnumerateFileSystemEntries(Parent).Any()) Directory.Delete(Parent);
        }
        catch (IOException) { }
    }

    private string Abs(string rel) => Path.Combine(Root, rel.Replace('/', '\\'));

    private static async Task Until(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(what);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Only_closing_a_changed_file_asks_for_a_sync()
    {
        // Windows Search / the antivirus read every file of a renamed folder: those closes must not delay the next pass.
        _remote.WebUpload(null, "đọc.txt", Content.Of("nội dung"));
        (await _engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        var file = Abs("đọc.txt");
        File.ReadAllText(file).Should().Be("nội dung"); // hydrated by this process
        var closed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _platform.FileClosed += closed.Enqueue;

        static void Other(string command)
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c {command}") { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit();
        }
        Other($"type \"{file}\" > nul");
        await Task.Delay(1500);
        closed.Should().BeEmpty("another program only read the file");

        Other($"echo thêm>> \"{file}\"");
        await Until(() => closed.Contains("đọc.txt"), "close after a write");
        CloudFilesPlatform.UnchangedPlaceholder(file).Should().BeFalse();
    }

    [Fact]
    public async Task Renamed_folders_show_synced_again_on_both_sides()
    {
        _remote.WebCreateFolder(null, "Trên web");
        _remote.WebUpload("Trên web", "a.txt", Content.Of("a"));
        _remote.WebCreateFolder(null, "Trên máy");
        (await _engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();

        _remote.WebRename("Trên web", "Trên web (đổi tên)");       // renamed elsewhere: moved here by BlueDrive
        Directory.Move(Abs("Trên máy"), Abs("Trên máy (đổi tên)")); // renamed here: moved on the server
        (await _engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();

        CloudPlaceholders.IsInSync(Abs("Trên web (đổi tên)")).Should().BeTrue("Explorer must not keep showing \"waiting to sync\" after a rename");
        CloudPlaceholders.IsInSync(Abs("Trên máy (đổi tên)")).Should().BeTrue();
    }

    [Fact]
    public async Task Root_folders_and_their_folders_show_synced_in_explorer()
    {
        // As in the app: the root ("Tài liệu của tôi") is a folder of the sync root, not an item of the server.
        using var db = new SyncStateDb(Path.Combine(_tmp, "roots.db"));
        db.UpsertRoot(FakeRemoteStore.Root, "Tài liệu của tôi", true);
        var engine = new SyncEngine(db, _remote, _platform.FileSystem, SystemClock.Instance,
            new SyncEngineOptions { UserDisplayName = "Người thử", DeviceName = "PC-CF" }, _platform.Placeholders);
        _remote.WebCreateFolder(null, "Báo cáo");
        _remote.WebUpload("Báo cáo", "quý 1.docx", Content.Of("q1"));
        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        CloudPlaceholders.IsInSync(Abs("Tài liệu của tôi")).Should().BeTrue("Explorer must not show the root as waiting to sync");
        CloudPlaceholders.IsInSync(Abs("Tài liệu của tôi/Báo cáo")).Should().BeTrue();

        _remote.WebUpload("Báo cáo", "quý 2.docx", Content.Of("q2"));
        (await engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        CloudPlaceholders.IsInSync(Abs("Tài liệu của tôi/Báo cáo/quý 2.docx")).Should().BeTrue();
        CloudPlaceholders.IsInSync(Abs("Tài liệu của tôi/Báo cáo")).Should().BeTrue();
        CloudPlaceholders.IsInSync(Abs("Tài liệu của tôi")).Should().BeTrue();
    }

    [Fact]
    public async Task Signing_out_leaves_downloaded_files_as_ordinary_files()
    {
        _remote.WebCreateFolder(null, "Hồ sơ");
        _remote.WebUpload("Hồ sơ", "đã tải.docx", Content.Of("nội dung đã tải"));
        (await _engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        CloudPlaceholders.Hydrate(Abs("Hồ sơ/đã tải.docx"));
        CloudPlaceholders.IsDehydrated(Abs("Hồ sơ/đã tải.docx")).Should().BeFalse();
        CloudPlaceholders.IsPlaceholder(Abs("Hồ sơ/đã tải.docx")).Should().BeTrue();

        // What signing out does first (spec 7.1: CfRevertPlaceholder), checked while the folder is still a sync root.
        CloudPlaceholders.RevertAll(Root).Should().BeGreaterThanOrEqualTo(2, "the downloaded file and its folder");
        CloudPlaceholders.IsPlaceholder(Abs("Hồ sơ/đã tải.docx")).Should().BeFalse();
        CloudPlaceholders.IsPlaceholder(Abs("Hồ sơ")).Should().BeFalse();

        await _platform.UnregisterAsync();
        foreach (var path in new[] { Abs("Hồ sơ/đã tải.docx"), Abs("Hồ sơ") })
            CloudPlaceholders.IsPlaceholder(path).Should().BeFalse($"{path} is a plain file system item again");
        (await File.ReadAllTextAsync(Abs("Hồ sơ/đã tải.docx"))).Should().Be("nội dung đã tải");
    }

    [Fact]
    public async Task A_registration_dropped_by_windows_is_noticed_on_the_next_start()
    {
        if (!_platform.ShellRegistered) return; // the shell refused the folder: nothing can be checked
        var ctx = new OnDemandContext(Root, _accountId, "Kiểm thử", new Uri("https://bluedrive.example/"), _range, Path.Combine(_tmp, "state.db"));

        // An ordinary restart keeps the registration: missing files are the user's deletions.
        await _platform.DisposeAsync();
        _platform = new CloudFilesPlatform(ctx);
        await _platform.StartAsync(CancellationToken.None);
        _platform.RegistrationLost.Should().BeFalse();

        // Windows dropped it (MSIX package removed, profile repaired): the agent then puts missing items back instead of deleting them.
        await _platform.DisposeAsync();
        SyncRootRegistrar.Unregister(new SyncRootInfo(Root, _accountId, "", "1.0.0", "", null));
        _platform = new CloudFilesPlatform(ctx);
        await _platform.StartAsync(CancellationToken.None);
        _platform.RegistrationLost.Should().BeTrue();
    }

    [Fact]
    public async Task Explorer_shows_bluedrive_states_on_items()
    {
        _remote.WebUpload(null, "đang cùng soạn.docx", Content.Of("x"));
        (await _engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        if (!_platform.ShellRegistered) return; // the shell refused the folder (see Server_files_arrive…): nothing to show
        var path = Abs("đang cùng soạn.docx");
        var icon = Path.Combine(Environment.SystemDirectory, "imageres.dll") + ",-1015";
        await ItemStatus.SetAsync(path, "Đang được Nguyễn Văn A chỉnh sửa", icon);
        await ItemStatus.SetAsync(path, null, icon);
        CloudPlaceholders.IsDehydrated(path).Should().BeTrue("setting the state does not download the file");
    }

    [Fact]
    public async Task Server_files_arrive_as_placeholders_and_download_only_when_opened()
    {
        var big = new byte[3_000_000];
        Random.Shared.NextBytes(big);
        _remote.WebCreateFolder(null, "Ấn phẩm");
        _remote.WebUpload("Ấn phẩm", "tờ rơi.pdf", big);
        _remote.WebUpload(null, "ghi chú.txt", Content.Of("xin chào"));

        (await _engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        // Explorer integration (navigation pane, status column) when the shell accepts the registration.
        _platform.ShellRegistered.Should().BeTrue("the folder appears in Explorer's navigation pane");
        (await SyncRootRegistrar.RootIdForFolderAsync(Root)).Should().Be(SyncRootRegistrar.Id(_accountId));
        var pdf = Abs("Ấn phẩm/tờ rơi.pdf");
        new FileInfo(pdf).Length.Should().Be(big.Length, "the placeholder shows the real size");
        CloudPlaceholders.IsDehydrated(pdf).Should().BeTrue("nothing is downloaded before the file is opened");
        _range.Reads.Should().Be(0);

        // Opening the file downloads it through the provider.
        (await File.ReadAllBytesAsync(pdf)).Should().Equal(big);
        CloudPlaceholders.IsDehydrated(pdf).Should().BeFalse();
        _range.Reads.Should().BeGreaterThan(0);

        // "Giải phóng dung lượng" then "Luôn giữ trên thiết bị".
        CloudPlaceholders.Dehydrate(pdf);
        CloudPlaceholders.IsDehydrated(pdf).Should().BeTrue();
        CloudPlaceholders.Pin(pdf, true);
        await Until(() => !CloudPlaceholders.IsDehydrated(pdf), "giữ trên thiết bị phải tải tệp về");
        File.GetAttributes(pdf).HasFlag(CloudNative.Pinned).Should().BeTrue();

        // The engine sees nothing to do: placeholders and hydration do not look like local edits.
        (await _engine.SyncAsync(CancellationToken.None)).Operations.Should().Be(0);
    }

    [Fact]
    public async Task Local_edits_upload_and_server_versions_replace_the_placeholder()
    {
        var id = _remote.WebUpload(null, "kế hoạch.txt", Content.Of("v1"));
        await _engine.SyncAsync(CancellationToken.None);
        var file = Abs("kế hoạch.txt");

        // Edit on the computer (the file hydrates on open) → new version on the server.
        await File.WriteAllTextAsync(file, "v2 sửa trên máy");
        (await _engine.SyncAsync(CancellationToken.None)).Errors.Should().BeEmpty();
        _remote.Data(id).Should().Equal(Content.Of("v2 sửa trên máy"));

        // New version on the web → the placeholder takes the new size and drops the old content.
        _remote.WebUpload(null, "kế hoạch.txt", Content.Of("v3 từ web, dài hơn"));
        await _engine.SyncAsync(CancellationToken.None);
        CloudPlaceholders.IsDehydrated(file).Should().BeTrue();
        (await File.ReadAllTextAsync(file)).Should().Be("v3 từ web, dài hơn");

        // A new local file becomes a placeholder too once uploaded (so it can be freed later).
        await File.WriteAllTextAsync(Abs("mới.txt"), "tạo trên máy");
        await _engine.SyncAsync(CancellationToken.None);
        CloudPlaceholders.Dehydrate(Abs("mới.txt"));
        (await File.ReadAllTextAsync(Abs("mới.txt"))).Should().Be("tạo trên máy");

        // Deleted on the web → gone locally; afterwards nothing left to do.
        _remote.WebDelete("kế hoạch.txt");
        await _engine.SyncAsync(CancellationToken.None);
        File.Exists(file).Should().BeFalse();
        (await _engine.SyncAsync(CancellationToken.None)).Operations.Should().Be(0);
    }

    [Fact]
    public async Task Explorer_keep_on_device_and_free_up_space_commands_are_honoured()
    {
        var data = new byte[600_000];
        Random.Shared.NextBytes(data);
        _remote.WebUpload(null, "video.mp4", data);
        await _engine.SyncAsync(CancellationToken.None);
        var file = Abs("video.mp4");
        CloudPlaceholders.IsDehydrated(file).Should().BeTrue();

        // Explorer's commands only set the pin attribute (same as attrib +P / +U); the provider does the rest.
        Cmd($"attrib +P \"{file}\"");
        await Until(() => !CloudPlaceholders.IsDehydrated(file), "Luôn giữ trên thiết bị: tệp phải được tải về");
        (await File.ReadAllBytesAsync(file)).Should().Equal(data);

        Cmd($"attrib -P +U \"{file}\"");
        await Until(() => CloudPlaceholders.IsDehydrated(file), "Giải phóng dung lượng: nội dung phải được bỏ khỏi máy");
        new FileInfo(file).Length.Should().Be(data.Length);
    }

    [Fact]
    public async Task Another_program_cannot_delete_a_view_only_file()
    {
        _remote.WebUpload(null, "chỉ xem.docx", Content.Of("nội dung"));
        _remote.WebUpload(null, "được sửa.docx", Content.Of("nội dung"));
        _remote.SetCanEdit("chỉ xem.docx", false);
        await _engine.SyncAsync(CancellationToken.None);

        // Deletions by other processes go through NOTIFY_DELETE; BlueDrive's own operations are always allowed.
        Cmd($"del /q \"{Abs("chỉ xem.docx")}\"");
        Cmd($"del /q \"{Abs("được sửa.docx")}\"");
        File.Exists(Abs("chỉ xem.docx")).Should().BeTrue("the filter refuses deleting an item the user may only view");
        File.Exists(Abs("được sửa.docx")).Should().BeFalse();
    }

    private static void Cmd(string command)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c chcp 65001 >nul & {command}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        })!;
        p.WaitForExit(15_000);
    }
}
