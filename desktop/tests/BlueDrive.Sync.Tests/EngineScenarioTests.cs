using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>One BlueDrive Desktop installation in tests: its own disk, state database and engine, sharing the fake server.</summary>
public sealed class Device : IDisposable
{
    public readonly FakeLocalFileSystem Fs;
    public readonly SyncStateDb Db;
    public readonly string Name;
    private readonly FakeRemoteStore _remote;
    private readonly FakeClock _clock;
    public SyncEngine Engine { get; private set; }

    public Device(string name, FakeRemoteStore remote, FakeClock clock)
    {
        Name = name;
        _remote = remote;
        _clock = clock;
        Fs = new FakeLocalFileSystem(clock);
        Db = new SyncStateDb(":memory:");
        Db.UpsertRoot(FakeRemoteStore.Root, "", true);
        Engine = NewEngine();
    }

    /// <summary>Simulates a crash + restart: a fresh engine on the same disk and database.</summary>
    public SyncEngine NewEngine() => Engine = new SyncEngine(Db, _remote, Fs, _clock,
        new SyncEngineOptions { UserDisplayName = "Nguyễn Văn A", DeviceName = Name, TimeZone = TimeZoneInfo.Utc, MaxRounds = 20 }) { Diagnostics = Verbose };

    public bool Verbose;

    public async Task<SyncPassResult> SyncAsync()
    {
        _remote.CurrentDevice = Name;
        try { return await Engine.SyncAsync(CancellationToken.None); }
        finally { _remote.CurrentDevice = null; }
    }

    public void Dispose() => Db.Dispose();
}

public class EngineScenarioTests : IDisposable
{
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 3, 7, 0, 0, DateTimeKind.Utc));
    private readonly FakeRemoteStore _remote;
    private readonly Device _a;
    private readonly Device _b;

    public EngineScenarioTests()
    {
        _remote = new FakeRemoteStore(_clock);
        _a = new Device("PC-A", _remote, _clock);
        _b = new Device("PC-B", _remote, _clock);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private static Dictionary<string, string> Tree(FakeLocalFileSystem fs) =>
        fs.Folders.Where(p => !p.StartsWith(IgnoreRules.NotSyncedFolder)).Select(p => (p, "<dir>"))
            .Concat(fs.Files.Where(p => !p.StartsWith(IgnoreRules.NotSyncedFolder)).Select(p => (p, Content.Hash(fs.Read(p)))))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    private void AssertConverged()
    {
        var remote = _remote.Tree();
        Tree(_a.Fs).Should().BeEquivalentTo(remote);
        Tree(_b.Fs).Should().BeEquivalentTo(remote);
    }

    [Fact]
    public async Task First_sync_downloads_the_tree_and_a_second_pass_is_quiet()
    {
        _remote.WebCreateFolder(null, "Kế hoạch");
        _remote.WebUpload("Kế hoạch", "2026.docx", Content.Of("kế hoạch năm"));
        _remote.WebUpload(null, "Ghi chú.txt", Content.Of("ghi chú"));
        await _a.SyncAsync();
        _a.Fs.Read("Kế hoạch/2026.docx").Should().Equal(Content.Of("kế hoạch năm"));
        var requests = _remote.Requests;
        (await _a.SyncAsync()).Operations.Should().Be(0);
        _remote.Requests.Should().BeLessThan(requests + 3, "a quiet pass only polls the change feed");
    }

    [Fact]
    public async Task Local_creates_and_renames_reach_the_other_computer_without_reuploading()
    {
        _a.Fs.UserMkdir("Báo cáo");
        _a.Fs.UserWrite("Báo cáo/quy-1.xlsx", Content.Of("q1"));
        await _a.SyncAsync();
        var id = _remote.FindPath("Báo cáo/quy-1.xlsx")!;
        _a.Fs.UserMove("Báo cáo/quy-1.xlsx", "Báo cáo/Quý 1.xlsx").Should().BeTrue();
        _a.Fs.UserMove("Báo cáo", "Báo cáo 2026").Should().BeTrue();
        await _a.SyncAsync();
        await _b.SyncAsync();
        _remote.FindPath("Báo cáo 2026/Quý 1.xlsx").Should().Be(id, string.Join(" | ", _remote.Tree().Keys) + " || A: " + string.Join(" | ", _a.Fs.Files) + " || log: " + string.Join(" | ", _a.Db.Activity(20).Select(x => x.Action + " " + x.Path + " " + x.Detail)));
        _remote.VersionCount(id).Should().Be(1, "a rename never uploads the content again");
        AssertConverged();
    }

    [Fact]
    public async Task Remote_rename_and_move_are_applied_locally()
    {
        _remote.WebCreateFolder(null, "A");
        _remote.WebCreateFolder(null, "B");
        _remote.WebUpload("A", "x.txt", Content.Of("x"));
        await _a.SyncAsync();
        _remote.WebRename("A/x.txt", "y.txt");
        _remote.WebMove("A/y.txt", "B");
        await _a.SyncAsync();
        _a.Fs.Exists("B/y.txt").Should().BeTrue();
        _a.Fs.Exists("A/x.txt").Should().BeFalse();
        _a.Fs.EngineWrites.Should().Be(1, "only the first download wrote content");
    }

    [Fact]
    public async Task Server_moves_that_block_each_other_locally_are_untangled()
    {
        // Found by SimulationTests: folder "F1" is renamed to "a.txt" while the file "a.txt" moves into a new sub-folder of it.
        _remote.WebCreateFolder(null, "F1");
        _remote.WebUpload(null, "a.txt", Content.Of("tệp a"));
        await _a.SyncAsync();
        _remote.WebRename("a.txt", "z.txt");
        _remote.WebMove("z.txt", "F1");
        _remote.WebRename("F1", "a.txt");
        _remote.WebCreateFolder("a.txt", "G");
        _remote.WebMove("a.txt/z.txt", "a.txt/G");
        for (var i = 0; i < 6 && (await _a.SyncAsync()).Operations > 0; i++) { }
        Tree(_a.Fs).Should().BeEquivalentTo(_remote.Tree());
        _a.Fs.Read("a.txt/G/z.txt").Should().Equal(Content.Of("tệp a"));
        _a.Fs.EngineWrites.Should().Be(1, "the file is moved, never downloaded again");
        (await _a.SyncAsync()).Operations.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_edits_keep_both_versions_with_a_conflict_copy()
    {
        _remote.WebUpload(null, "Hợp đồng.docx", Content.Of("v1"));
        await _a.SyncAsync();
        await _b.SyncAsync();
        _a.Fs.UserWrite("Hợp đồng.docx", Content.Of("sửa trên máy A"));
        _b.Fs.UserWrite("Hợp đồng.docx", Content.Of("sửa trên máy B"));
        await _a.SyncAsync();
        await _b.SyncAsync();
        await _a.SyncAsync();
        var tree = _remote.Tree();
        tree["Hợp đồng.docx"].Should().Be(Content.Hash(Content.Of("sửa trên máy A")));
        var conflict = tree.Keys.Should().ContainSingle(k => k.Contains("Bản xung đột")).Which;
        conflict.Should().MatchRegex(@"^Hợp đồng \(Bản xung đột - Nguyễn Văn A - PC-B - \d{4}-\d{2}-\d{2} \d{4}\)\.docx$");
        tree[conflict].Should().Be(Content.Hash(Content.Of("sửa trên máy B")));
        AssertConverged();
    }

    [Fact]
    public async Task Deletes_go_to_the_server_trash_but_never_win_over_an_edit()
    {
        _remote.WebUpload(null, "a.txt", Content.Of("a"));
        _remote.WebUpload(null, "b.txt", Content.Of("b"));
        await _a.SyncAsync();
        await _b.SyncAsync();

        // A deletes a.txt while the web edits it: a.txt comes back on A.
        _a.Fs.UserDelete("a.txt");
        _remote.WebUpload(null, "a.txt", Content.Of("a2"));
        // B edits b.txt while the web deletes it: b.txt is uploaded again.
        _b.Fs.UserWrite("b.txt", Content.Of("b2"));
        _remote.WebDelete("b.txt");
        await _a.SyncAsync();
        await _b.SyncAsync();
        await _a.SyncAsync();
        _a.Fs.Read("a.txt").Should().Equal(Content.Of("a2"));
        _remote.Tree()["b.txt"].Should().Be(Content.Hash(Content.Of("b2")));
        AssertConverged();

        // A plain delete goes to the trash on the server.
        _a.Fs.UserDelete("a.txt");
        await _a.SyncAsync();
        await _b.SyncAsync();
        _remote.FindPath("a.txt").Should().BeNull();
        _remote.EverStored.Should().Contain(Content.Hash(Content.Of("a2")));
        AssertConverged();
    }

    [Fact]
    public async Task Deleting_a_folder_keeps_files_added_on_the_server_meanwhile()
    {
        _remote.WebCreateFolder(null, "Dự án");
        _remote.WebUpload("Dự án", "cũ.txt", Content.Of("cũ"));
        await _a.SyncAsync();
        _a.Fs.UserDelete("Dự án");
        _remote.WebUpload("Dự án", "mới.txt", Content.Of("mới"));
        await _a.SyncAsync();
        _remote.FindPath("Dự án/mới.txt").Should().NotBeNull();
        _remote.FindPath("Dự án/cũ.txt").Should().BeNull();
        _a.Fs.Exists("Dự án/mới.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Mass_delete_waits_for_the_user()
    {
        for (var i = 0; i < 20; i++) _remote.WebUpload(null, $"tep-{i:00}.txt", Content.Of($"n{i}"));
        await _a.SyncAsync();
        for (var i = 0; i < 12; i++) _a.Fs.UserDelete($"tep-{i:00}.txt");
        var r = await _a.SyncAsync();
        r.MassDeletePending.Should().Be(12);
        _remote.Tree().Should().HaveCount(20, "nothing is deleted on the server before the user confirms");

        _a.Engine.ResolveMassDelete(FakeRemoteStore.Root, deleteOnServer: false);
        await _a.SyncAsync();
        _a.Fs.Files.Should().HaveCount(20, "the files are restored");

        for (var i = 0; i < 12; i++) _a.Fs.UserDelete($"tep-{i:00}.txt");
        await _a.SyncAsync();
        _a.Engine.ResolveMassDelete(FakeRemoteStore.Root, deleteOnServer: true);
        await _a.SyncAsync();
        _remote.Tree().Should().HaveCount(8);
    }

    [Fact]
    public async Task Office_style_save_through_a_new_file_is_an_edit_not_a_new_file()
    {
        _remote.WebUpload(null, "Báo cáo.docx", Content.Of("v1"));
        await _a.SyncAsync();
        var id = _remote.FindPath("Báo cáo.docx")!;
        _a.Fs.UserSaveAsNewFile("Báo cáo.docx", Content.Of("v2"));
        await _a.SyncAsync();
        _remote.FindPath("Báo cáo.docx").Should().Be(id);
        _remote.VersionCount(id).Should().Be(2);
    }

    [Fact]
    public async Task Expired_cursor_resyncs_without_losing_local_edits()
    {
        _remote.WebUpload(null, "a.txt", Content.Of("a"));
        await _a.SyncAsync();
        _a.Fs.UserWrite("a.txt", Content.Of("sửa offline"));
        _remote.WebUpload(null, "b.txt", Content.Of("b"));
        _remote.ExpireJournal(_remote.Seq);
        await _a.SyncAsync();
        _remote.Tree()["a.txt"].Should().Be(Content.Hash(Content.Of("sửa offline")));
        _a.Fs.Exists("b.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Lost_responses_and_crashes_converge_without_duplicates()
    {
        _a.Fs.UserMkdir("Ảnh");
        for (var i = 0; i < 6; i++) _a.Fs.UserWrite($"Ảnh/{i}.jpg", Content.Of($"anh {i}"));
        _remote.Chaos = new Random(7);
        _remote.FailureRate = 0.6;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try { await _a.SyncAsync(); }
            catch (RemoteTransientException) { _a.NewEngine(); } // crash + restart
        }
        _remote.FailureRate = 0;
        await _a.SyncAsync();
        await _b.SyncAsync();
        _remote.Tree().Keys.Count(k => k.StartsWith("Ảnh/")).Should().Be(6, "no duplicate uploads after lost responses");
        AssertConverged();
    }

    [Fact]
    public async Task Edits_without_edit_right_are_kept_aside_and_the_server_copy_restored()
    {
        _remote.WebUpload(null, "Quy định.pdf", Content.Of("bản gốc"));
        _remote.SetCanEdit("Quy định.pdf", false);
        await _a.SyncAsync();
        _a.Fs.UserWrite("Quy định.pdf", Content.Of("sửa trái phép"));
        await _a.SyncAsync();
        _a.Fs.Read("Quy định.pdf").Should().Equal(Content.Of("bản gốc"));
        _a.Fs.Read($"{IgnoreRules.NotSyncedFolder}/Quy định.pdf").Should().Equal(Content.Of("sửa trái phép"));
        _a.Db.Conflicts().Should().ContainSingle(x => x.Kind == "read_only");
    }

    [Fact]
    public async Task Names_invalid_on_windows_and_case_twins_are_mapped()
    {
        _remote.WebUpload(null, "Báo cáo: quý 1?.txt", Content.Of("1"));
        _remote.WebUpload(null, "CON.txt", Content.Of("2"));
        await _a.SyncAsync();
        _a.Fs.Exists("Báo cáo꞉ quý 1？.txt").Should().BeTrue();
        _a.Fs.Exists("CON .txt").Should().BeTrue();

        // Editing the mapped file updates the original server name; a rename keeps the server's characters.
        _a.Fs.UserWrite("Báo cáo꞉ quý 1？.txt", Content.Of("1b"));
        await _a.SyncAsync();
        _remote.Tree()["Báo cáo: quý 1?.txt"].Should().Be(Content.Hash(Content.Of("1b")));
    }

    [Fact]
    public async Task Selective_sync_leaves_an_excluded_folder_out_until_it_is_included_again()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var remote = new FakeRemoteStore(clock);
        remote.WebCreateFolder(null, "Lưu trữ");
        remote.WebUpload("Lưu trữ", "cũ.pdf", Content.Of("cũ"));
        remote.WebUpload(null, "mới.docx", Content.Of("mới"));
        var folderId = remote.FindPath("Lưu trữ")!;
        using var b = new Device("PC-B", remote, clock);
        b.Db.SetAccount(SyncEngine.ExcludedFoldersKey(FakeRemoteStore.Root), $"[\"{folderId}\"]");

        await b.SyncAsync();
        b.Fs.Exists("mới.docx").Should().BeTrue();
        b.Fs.Exists("Lưu trữ").Should().BeFalse("excluded folders are not downloaded");

        // Changes inside the excluded folder on the web, and a local folder of the same name, do not interfere.
        remote.WebUpload("Lưu trữ", "thêm.pdf", Content.Of("thêm"));
        (await b.SyncAsync()).Operations.Should().Be(0);
        remote.Tree().Keys.Should().Contain("Lưu trữ/thêm.pdf");

        // Included again: it comes down.
        b.Db.SetAccount(SyncEngine.ExcludedFoldersKey(FakeRemoteStore.Root), "[]");
        await b.SyncAsync();
        b.Fs.Read("Lưu trữ/thêm.pdf").Should().Equal(Content.Of("thêm"));
        b.Fs.Read("Lưu trữ/cũ.pdf").Should().Equal(Content.Of("cũ"));
    }
}
