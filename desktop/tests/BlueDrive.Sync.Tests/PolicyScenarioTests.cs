using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>Server sync policy applied by the engine (spec 7.2, 8.2): "Mở trên web" links, files refused before upload, blocked downloads.</summary>
public class PolicyScenarioTests : IDisposable
{
    private const string Web = "https://bluedrive.example.vn";
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 4, 7, 0, 0, DateTimeKind.Utc));
    private readonly FakeRemoteStore _remote;
    private readonly Device _pc;

    public PolicyScenarioTests()
    {
        _remote = new FakeRemoteStore(_clock);
        _pc = new Device("PC-A", _remote, _clock);
        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web };
    }

    public void Dispose() => _pc.Dispose();

    private static string Link(string path) => path + SyncClientPolicy.LinkSuffix;

    private string LinkText(string path) => System.Text.Encoding.UTF8.GetString(_pc.Fs.Read(Link(path)));

    [Fact]
    public async Task View_only_file_is_a_web_link_and_comes_back_as_a_file_when_downloadable_again()
    {
        _remote.WebCreateFolder(null, "Báo cáo");
        var id = _remote.WebUpload("Báo cáo", "Quý 3.docx", Content.Of("số liệu"));
        await _pc.SyncAsync();
        _pc.Fs.Exists("Báo cáo/Quý 3.docx").Should().BeTrue();

        _remote.SetCanDownload("Báo cáo/Quý 3.docx", false);
        await _pc.SyncAsync();
        _pc.Fs.Exists("Báo cáo/Quý 3.docx").Should().BeFalse("a view-only file is never kept on the computer");
        LinkText("Báo cáo/Quý 3.docx").Should().Be($"[InternetShortcut]\r\nURL={Web}/files/{id}\r\n");
        _remote.Tree().Should().ContainKey("Báo cáo/Quý 3.docx", "removing the local copy is not a deletion");
        _remote.Tree().Keys.Should().NotContain(k => k.EndsWith(".url"), "links are never uploaded");

        var ops = _pc.Engine.TotalOperations;
        await _pc.SyncAsync();
        _pc.Engine.TotalOperations.Should().Be(ops, "a converged state stays quiet");

        _remote.SetCanDownload("Báo cáo/Quý 3.docx", true);
        await _pc.SyncAsync();
        _pc.Fs.Read("Báo cáo/Quý 3.docx").Should().Equal(Content.Of("số liệu"));
        _pc.Fs.Exists(Link("Báo cáo/Quý 3.docx")).Should().BeFalse();
    }

    [Fact]
    public async Task View_only_files_listed_by_a_full_snapshot_get_links_and_deleted_ones_lose_them()
    {
        _remote.WebUpload(null, "Chỉ xem.pdf", Content.Of("pdf"));
        _remote.SetCanDownload("Chỉ xem.pdf", false);
        await _pc.SyncAsync();
        _pc.Fs.Exists("Chỉ xem.pdf").Should().BeFalse();
        _pc.Fs.Exists(Link("Chỉ xem.pdf")).Should().BeTrue();

        _remote.WebDelete("Chỉ xem.pdf");
        await _pc.SyncAsync();
        _pc.Fs.Exists(Link("Chỉ xem.pdf")).Should().BeFalse();
    }

    [Fact]
    public async Task Links_follow_the_policy_switch_and_need_a_web_address()
    {
        _remote.WebUpload(null, "Chỉ xem.pdf", Content.Of("pdf"));
        _remote.SetCanDownload("Chỉ xem.pdf", false);
        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web, ViewOnlyAsLink = false };
        await _pc.SyncAsync();
        _pc.Fs.Files.Should().BeEmpty("\"Hiển thị tệp chỉ xem dạng liên kết\" is off");

        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web };
        await _pc.SyncAsync();
        _pc.Fs.Exists(Link("Chỉ xem.pdf")).Should().BeTrue();

        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web, ViewOnlyAsLink = false };
        await _pc.SyncAsync();
        _pc.Fs.Files.Should().BeEmpty("links no longer wanted are removed");
    }

    [Fact]
    public async Task A_type_the_server_refuses_is_not_uploaded_and_shows_in_problems()
    {
        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web, AllowedUploadExtensions = new HashSet<string> { "docx", "txt" } };
        _pc.Fs.UserWrite("Công cụ.exe", Content.Of("MZ"));
        _pc.Fs.UserWrite("Ghi chú.txt", Content.Of("ghi chú"));
        var requests = _remote.Requests;

        var result = await _pc.SyncAsync();

        result.Errors.Should().ContainSingle(e => e.StartsWith("Công cụ.exe:") && e.Contains("Định dạng .exe chưa được phép"));
        _remote.Tree().Keys.Should().BeEquivalentTo(["Ghi chú.txt"]);
        _pc.Fs.Exists("Công cụ.exe").Should().BeTrue("the file stays on the computer");
        _remote.Requests.Should().BeLessThan(requests + 10, "the refused file is never sent");
    }

    [Fact]
    public async Task A_new_version_over_the_size_limit_is_not_uploaded()
    {
        _remote.WebUpload(null, "Dữ liệu.txt", Content.Of("nhỏ"));
        await _pc.SyncAsync();
        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web, MaxFileSizeBytes = 100 };
        _clock.Tick();
        _pc.Fs.UserWrite("Dữ liệu.txt", new byte[500]);

        var result = await _pc.SyncAsync();

        result.Errors.Should().ContainSingle(e => e.StartsWith("Dữ liệu.txt:") && e.Contains("lớn hơn giới hạn đồng bộ"));
        _remote.Data(_remote.FindPath("Dữ liệu.txt")!).Should().Equal(Content.Of("nhỏ"));
    }

    [Fact]
    public async Task A_blocked_type_is_not_downloaded_but_linked_and_never_deleted_on_the_server()
    {
        var id = _remote.WebUpload(null, "Bộ cài.exe", Content.Of("MZ"));
        _remote.WebUpload(null, "Đọc tôi.txt", Content.Of("hướng dẫn"));
        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web, BlockedDownloadExtensions = new HashSet<string> { "exe" } };

        await _pc.SyncAsync();
        var ops = _pc.Engine.TotalOperations;
        await _pc.SyncAsync();

        _pc.Fs.Exists("Bộ cài.exe").Should().BeFalse();
        _pc.Fs.Exists("Đọc tôi.txt").Should().BeTrue();
        LinkText("Bộ cài.exe").Should().Contain($"{Web}/files/{id}");
        _remote.Tree().Should().ContainKey("Bộ cài.exe");
        _pc.Engine.TotalOperations.Should().Be(ops);

        // The administrator lifts the block: the file comes down, the link goes.
        _pc.Engine.Policy = new SyncClientPolicy { WebBaseUrl = Web };
        await _pc.SyncAsync();
        _pc.Fs.Read("Bộ cài.exe").Should().Equal(Content.Of("MZ"));
        _pc.Fs.Exists(Link("Bộ cài.exe")).Should().BeFalse();
    }

    [Fact]
    public void Link_names_are_ignored_by_the_scan()
    {
        IgnoreRules.IsIgnored("Báo cáo.docx - Mở trên web.url").Should().BeTrue();
        IgnoreRules.IsIgnored("Lối tắt.url").Should().BeFalse();
        new SyncClientPolicy { UploadMaxSizeBytes = 1L << 30 }.UploadRefusal("a.mp4", 2L << 30).Should().Contain("1 GB");
    }
}
