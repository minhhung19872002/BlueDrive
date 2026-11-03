using System.IO.Compression;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>Agent features used by the desktop UI (Sync-Phase 6): conflict resolution, pause, diagnostics package.</summary>
public sealed class AgentUiSupportTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bd-agentui-" + Guid.NewGuid().ToString("N")[..8]);

    public AgentUiSupportTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { }
    }

    private SyncAgent Agent(string name) => new(new AgentOptions
    {
        Server = new Uri("https://bluedrive.example/"), SyncFolder = Path.Combine(_tmp, name), StateDbPath = Path.Combine(_tmp, name + ".db"),
        Tokens = new MemoryTokenStore(), UseRecycleBin = false,
    });

    /// <summary>A content conflict as the engine leaves it: server copy at the original name, local edits in the conflict copy.</summary>
    private (SyncAgent Agent, ConflictEntry Conflict, string Original, string Copy) Conflict(string name)
    {
        var agent = Agent(name);
        var root = Path.Combine(_tmp, name, "Tài liệu của tôi");
        Directory.CreateDirectory(root);
        agent.State.UpsertRoot("ws:1", "Tài liệu của tôi", true);
        File.WriteAllText(Path.Combine(root, "kế hoạch.txt"), "bản máy chủ");
        const string copy = "kế hoạch (Bản xung đột - Người thử - PC-A - 2026-11-03 0915).txt";
        File.WriteAllText(Path.Combine(root, copy), "bản trên máy");
        agent.State.AddConflict("ws:1", "kế hoạch.txt", copy, "content");
        return (agent, agent.State.Conflicts().Single(), Path.Combine(root, "kế hoạch.txt"), Path.Combine(root, copy));
    }

    [Fact]
    public async Task Conflict_choices_keep_both_keep_local_or_keep_server()
    {
        var (a, c, original, copy) = Conflict("both");
        await using (a)
        {
            await a.ResolveConflictAsync(c, SyncAgent.ConflictChoice.KeepBoth);
            File.Exists(copy).Should().BeTrue();
            a.State.Conflicts().Should().BeEmpty("resolved conflicts leave the list");
        }

        var (b, c2, original2, copy2) = Conflict("local");
        await using (b)
        {
            await b.ResolveConflictAsync(c2, SyncAgent.ConflictChoice.KeepLocal);
            File.ReadAllText(original2).Should().Be("bản trên máy", "the computer's copy becomes the new version of the original");
            File.Exists(copy2).Should().BeFalse();
        }

        var (d, c3, original3, copy3) = Conflict("server");
        await using (d)
        {
            await d.ResolveConflictAsync(c3, SyncAgent.ConflictChoice.KeepServer);
            File.ReadAllText(original3).Should().Be("bản máy chủ");
            File.Exists(copy3).Should().BeFalse();
            d.State.Activity().Select(x => x.Detail).Should().Contain("Giữ bản trên BlueDrive");
        }
    }

    [Fact]
    public async Task Pause_for_a_while_and_resume()
    {
        await using var a = Agent("pause");
        a.PauseFor(TimeSpan.FromHours(2));
        a.IsPaused.Should().BeTrue();
        a.Status.State.Should().Be(AgentState.Paused);
        a.Status.Message.Should().StartWith("Đã tạm dừng đồng bộ đến ");
        a.PauseFor(null);
        a.IsPaused.Should().BeFalse();
        a.PauseFor(Timeout.InfiniteTimeSpan);
        a.Status.Message.Should().Be("Đã tạm dừng đồng bộ.");
        a.PauseFor(null);
        a.SetAutoPause("Đã tạm dừng: đang dùng mạng tính phí.");
        a.Status.State.Should().Be(AgentState.Paused);
        a.AutoPauseReason.Should().NotBeNull();
        a.SetAutoPause(null);
        a.AutoPauseReason.Should().BeNull();
    }

    [Fact]
    public void Diagnostics_package_never_contains_tokens()
    {
        var store = new AccountStore(Path.Combine(_tmp, "home"));
        var id = Guid.NewGuid();
        store.Save(new AccountConfig(id, "https://bluedrive.example/", Path.Combine(_tmp, "f"), "nguoithu", "Người thử", Guid.NewGuid()));
        var logs = Path.Combine(_tmp, "logs");
        Directory.CreateDirectory(logs);
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJlLXNlY3JldA";
        File.WriteAllText(Path.Combine(logs, "bluedrive-20261103.log"),
            $"GET /api/v1/sync/changes Authorization: Bearer {jwt}\nrefresh_token=Abc123secret&grant_type=refresh_token\nĐã tải lên: báo cáo.docx\n");

        var zipPath = Diagnostics.Export(store, logs, Path.Combine(_tmp, "chan-doan.zip"), "1.0.0");
        using var zip = ZipFile.OpenRead(zipPath);
        zip.Entries.Select(e => e.FullName).Should().Contain(["he-thong.txt", "tai-khoan-va-trang-thai.txt", "logs/bluedrive-20261103.log"]);
        var all = string.Concat(zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        all.Should().NotContain(jwt).And.NotContain("Abc123secret");
        all.Should().Contain("Đã tải lên: báo cáo.docx").And.Contain("nguoithu");
        Diagnostics.Redact("password=Mk@123456 xyz").Should().Be("password=*** xyz");
    }
}
