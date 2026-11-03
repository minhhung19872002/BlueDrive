using System.Net;
using System.Net.Http.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>Sync-Phase 4: the background agent in full sync mode on a real folder against the real API (watcher, push, revoke, wipe).</summary>
[Collection(ApiCollection.Name)]
public sealed class SyncAgentTests(ApiFactory factory) : IDisposable
{
    private const string Redirect = "http://127.0.0.1:53682/callback";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bd-agent-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { }
    }

    private async Task<MemoryTokenStore> SignInAsync(HttpClient user)
    {
        var auth = new DesktopAuthenticator(factory.Server.BaseAddress, factory.Server.CreateHandler());
        var pkce = DesktopAuthenticator.PkcePair.Create();
        var authorize = await (await user.PostAsJsonAsync("/api/v1/connect/authorize", new
        {
            clientId = DesktopAuthenticator.ClientId, redirectUri = Redirect, codeChallenge = pkce.Challenge, codeChallengeMethod = "S256", state = "s", deviceName = "PC-AGENT",
        })).ReadJsonAsync();
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(authorize.GetProperty("redirectUrl").GetString()!).Query)["code"]!;
        var store = new MemoryTokenStore();
        store.Save(await auth.ExchangeCodeAsync(code, pkce.Verifier, Redirect, new DeviceInfo("PC-AGENT", "Windows 11", "1.0.0", Guid.NewGuid().ToString())));
        return store;
    }

    private SyncAgent Agent(MemoryTokenStore tokens) => new(new AgentOptions
    {
        Server = factory.Server.BaseAddress,
        SyncFolder = Path.Combine(_tmp, "BlueDrive"),
        StateDbPath = Path.Combine(_tmp, "state", "state.db"),
        Tokens = tokens,
        UserDisplayName = "Người thử",
        LocalQuiet = TimeSpan.FromMilliseconds(500),
        UseRecycleBin = false,
        HandlerFactory = factory.Server.CreateHandler,
    });

    private static async Task Until(Func<Task<bool>> condition, string what, int seconds = 15)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Hết thời gian chờ: {what}");
    }

    private static async Task<List<string>> NamesAsync(HttpClient user, Guid ws, string? folderId = null) =>
        (await (await user.GetAsync($"/api/v1/workspaces/{ws}/items?pageSize=200" + (folderId is null ? "" : $"&folderId={folderId}"))).ReadJsonAsync())
        .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task A_sync_folder_copied_to_another_drive_carries_on_without_deleting_or_duplicating()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        var tokens = await SignInAsync(user);
        var mine = Path.Combine(_tmp, "BlueDrive", "Tài liệu của tôi");
        await using (var agent = Agent(tokens))
        {
            await agent.StartAsync();
            await Until(() => Task.FromResult(Directory.Exists(mine) && agent.Status.State == AgentState.Idle), "thư mục Tài liệu của tôi");
            for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(mine, $"tệp {i}.txt"), $"nội dung {i}");
            await Until(async () => (await NamesAsync(user, ws)).Count(n => n.StartsWith("tệp ")) == 5 && agent.Status.State == AgentState.Idle, "5 tệp lên máy chủ", 30);
        }

        // "Đổi vị trí thư mục" to another drive: a copy (new file ids), the old folder removed.
        var moved = Path.Combine(_tmp, "ổ D", "BlueDrive");
        CopyTree(Path.Combine(_tmp, "BlueDrive"), moved);
        Directory.Delete(Path.Combine(_tmp, "BlueDrive"), recursive: true);
        await using (var agent = new SyncAgent(new AgentOptions
        {
            Server = factory.Server.BaseAddress, SyncFolder = moved, StateDbPath = Path.Combine(_tmp, "state", "state.db"), Tokens = tokens,
            UserDisplayName = "Người thử", LocalQuiet = TimeSpan.FromMilliseconds(500), UseRecycleBin = false, HandlerFactory = factory.Server.CreateHandler,
        }))
        {
            await agent.StartAsync();
            await Until(() => Task.FromResult(agent.Status.State == AgentState.Idle && agent.Status.LastSyncAt is not null), "đồng bộ ở vị trí mới", 30);
            agent.Status.PendingMassDelete.Should().Be(0);
            (await NamesAsync(user, ws)).Where(n => n.StartsWith("tệp ")).Should().HaveCount(5, "nothing deleted or duplicated on the server");
            Directory.EnumerateFiles(Path.Combine(moved, "Tài liệu của tôi")).Select(Path.GetFileName).Should().BeEquivalentTo(
                Enumerable.Range(0, 5).Select(i => $"tệp {i}.txt"), "no conflict copies");
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    [Fact]
    public async Task Shared_files_are_grouped_by_the_person_who_shares_them()
    {
        var (owner, _, ownerName) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await StorageTests.UploadAsync(owner, ws, null, "kế hoạch chung.txt", "kế hoạch"u8.ToArray())).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 3 })).ReadJsonAsync();

        await using var agent = Agent(await SignInAsync(friend));
        await agent.StartAsync();
        var person = Path.Combine(_tmp, "BlueDrive", "Được chia sẻ với tôi", "Người dùng " + ownerName);
        var shared = Path.Combine(person, "kế hoạch chung.txt");
        await Until(() => Task.FromResult(File.Exists(shared) && agent.Status.State == AgentState.Idle), "Được chia sẻ với tôi\\<người chia sẻ>\\tệp", 30);
        (await File.ReadAllTextAsync(shared)).Should().Be("kế hoạch");

        // The person's folder is not a folder on the server: nothing can be created straight in it.
        await File.WriteAllTextAsync(Path.Combine(person, "của tôi.txt"), "x");
        agent.SyncNow();
        bool Aside() => Directory.EnumerateFiles(Path.Combine(_tmp, "BlueDrive"), "của tôi.txt", SearchOption.AllDirectories)
            .Any(f => f.Contains("_Không đồng bộ được", StringComparison.Ordinal));
        await Until(() => Task.FromResult(Aside()), "tệp đặt vào thư mục người chia sẻ được chuyển sang _Không đồng bộ được", 30);
        File.Exists(Path.Combine(person, "của tôi.txt")).Should().BeFalse();

        // The owner takes the share back: the file and the person's folder leave this computer.
        var shares = await (await owner.GetAsync($"/api/v1/shares?resourceType=file&resourceId={fileId}")).ReadJsonAsync();
        foreach (var s in shares.EnumerateArray()) await owner.DeleteAsync($"/api/v1/shares/{s.GetProperty("id").GetGuid()}");
        agent.SyncNow();
        await Until(() => Task.FromResult(!Directory.Exists(person)), "thư mục người chia sẻ bị gỡ", 30);
    }

    [Fact]
    public async Task Group_workspaces_are_under_nhom_and_nothing_else_can_live_there()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await user.PostAsJsonAsync("/api/v1/workspaces", new { name = "Truyền thông 2026", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await StorageTests.UploadAsync(user, ws, null, "kế hoạch.txt", "kế hoạch"u8.ToArray());
        await using var agent = Agent(await SignInAsync(user));
        await agent.StartAsync();
        var groups = Path.Combine(_tmp, "BlueDrive", "Nhóm");
        var plan = Path.Combine(groups, "Truyền thông 2026", "kế hoạch.txt");
        await Until(() => Task.FromResult(File.Exists(plan) && agent.Status.State == AgentState.Idle), "tệp của nhóm trong Nhóm\\Truyền thông 2026", 30);
        (await File.ReadAllTextAsync(plan)).Should().Be("kế hoạch");

        // "Nhóm" holds the groups only: a file put there goes to "_Không đồng bộ được" and is reported.
        await File.WriteAllTextAsync(Path.Combine(groups, "lạc chỗ.txt"), "x");
        agent.SyncNow();
        var aside = Path.Combine(_tmp, "BlueDrive", "_Không đồng bộ được", "lạc chỗ.txt");
        await Until(() => Task.FromResult(File.Exists(aside)), "tệp lạc chỗ được chuyển sang _Không đồng bộ được", 30);
        File.Exists(Path.Combine(groups, "lạc chỗ.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_one_file_reaches_the_server_after_it_settles()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        await using var agent = Agent(await SignInAsync(user));
        await agent.StartAsync();
        var mine = Path.Combine(_tmp, "BlueDrive", "Tài liệu của tôi");
        await Until(() => Task.FromResult(Directory.Exists(mine) && agent.Status.State == AgentState.Idle), "thư mục Tài liệu của tôi");
        await File.WriteAllTextAsync(Path.Combine(mine, "nháp.txt"), "nháp");
        await Until(async () => (await NamesAsync(user, ws)).Contains("nháp.txt") && agent.Status.State == AgentState.Idle, "tệp lên máy chủ", 30);
        File.Delete(Path.Combine(mine, "nháp.txt"));
        await Until(async () => !(await NamesAsync(user, ws)).Contains("nháp.txt"), "tệp bị xóa trên máy chủ", 15);
    }

    [Fact]
    public async Task Deleting_many_files_asks_first_even_when_the_server_pushes_a_change_meanwhile()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        await using var agent = Agent(await SignInAsync(user));
        await agent.StartAsync();
        var mine = Path.Combine(_tmp, "BlueDrive", "Tài liệu của tôi");
        await Until(() => Task.FromResult(Directory.Exists(mine) && agent.Status.State == AgentState.Idle), "thư mục Tài liệu của tôi");
        var folder = Path.Combine(mine, "Lưu trữ");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 120; i++) await File.WriteAllTextAsync(Path.Combine(folder, $"{i:000}.txt"), $"tệp {i}");
        string? folderId = null;
        await Until(async () =>
        {
            var items = await (await user.GetAsync($"/api/v1/workspaces/{ws}/items?pageSize=200")).ReadJsonAsync();
            folderId = items.GetProperty("items").EnumerateArray().FirstOrDefault(i => i.GetProperty("name").GetString() == "Lưu trữ") is { ValueKind: System.Text.Json.JsonValueKind.Object } f
                ? f.GetProperty("id").GetString() : null;
            return folderId is not null && (await NamesAsync(user, ws, folderId)).Count == 120 && agent.Status.State == AgentState.Idle;
        }, "120 tệp lên máy chủ", 60);

        // The user deletes them one by one (like Explorer), and halfway the web changes something: the pushed pass must wait.
        var files = Directory.GetFiles(folder);
        for (var i = 0; i < files.Length; i++)
        {
            File.Delete(files[i]);
            if (i == 40) await (await user.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Từ trình duyệt" })).ReadJsonAsync();
            await Task.Delay(5);
        }
        await Until(() => Task.FromResult(agent.Status.PendingMassDelete > 0), "câu hỏi xóa hàng loạt", 30);
        agent.Status.PendingMassDelete.Should().Be(120);
        (await NamesAsync(user, ws, folderId)).Should().HaveCount(120, "nothing is deleted on BlueDrive before the user answers");
    }

    [Fact]
    public async Task Agent_mirrors_local_edits_to_the_server_and_web_changes_to_disk()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        await using var agent = Agent(await SignInAsync(user));
        await agent.StartAsync();
        var mine = Path.Combine(_tmp, "BlueDrive", "Tài liệu của tôi");
        await Until(() => Task.FromResult(Directory.Exists(mine) && agent.Status.State == AgentState.Idle), "thư mục Tài liệu của tôi");
        Directory.Exists(Path.Combine(_tmp, "BlueDrive", "Được chia sẻ với tôi")).Should().BeTrue();

        // Local → server: the watcher notices the new file, the agent uploads it after the quiet period.
        Directory.CreateDirectory(Path.Combine(mine, "Báo cáo"));
        await File.WriteAllTextAsync(Path.Combine(mine, "Báo cáo", "tuần 45.txt"), "nội dung tuần 45");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? folderId = null;
        await Until(async () =>
        {
            var root = await (await user.GetAsync($"/api/v1/workspaces/{ws}/items?pageSize=200")).ReadJsonAsync();
            folderId = root.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("name").GetString() == "Báo cáo")
                .Select(i => i.GetProperty("id").GetString()).FirstOrDefault();
            return folderId is not null && (await NamesAsync(user, ws, folderId)).Contains("tuần 45.txt");
        }, "tệp lên máy chủ");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "spec: a local save reaches the server in under 10 seconds");

        // Server → local: a folder made on the web appears on disk (SignalR push).
        watch.Restart();
        await (await user.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Từ trình duyệt" })).ReadJsonAsync();
        await Until(() => Task.FromResult(Directory.Exists(Path.Combine(mine, "Từ trình duyệt"))), "thư mục xuống máy");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "spec: a web change reaches the computer in under 5 seconds");

        // The top of the BlueDrive folder only holds the root folders: a file put there is moved aside (spec 7.2).
        var stray = Path.Combine(_tmp, "BlueDrive", "lạc chỗ.txt");
        await File.WriteAllTextAsync(stray, "x");
        await Until(() => Task.FromResult(!File.Exists(stray)), "tệp ở cấp ngoài cùng được chuyển đi");
        File.Exists(Path.Combine(_tmp, "BlueDrive", "_Không đồng bộ được", "lạc chỗ.txt")).Should().BeTrue();

        // Deleting the whole sync folder never deletes anything on the server: the agent stops with an explanation.
        await Until(() => Task.FromResult(agent.Status.State == AgentState.Idle), "đồng bộ xong");
        Directory.Delete(Path.Combine(_tmp, "BlueDrive"), recursive: true);
        agent.SyncNow();
        await Until(() => Task.FromResult(agent.Status.State == AgentState.Error), "báo lỗi thiếu thư mục");
        agent.Status.Message.Should().Contain("không có gì bị xóa trên BlueDrive");
        (await NamesAsync(user, ws, folderId)).Should().Contain("tuần 45.txt");
    }

    [Fact]
    public async Task Selective_sync_removes_a_folder_from_this_computer_and_brings_it_back()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        var folder = await (await user.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Lưu trữ 2020" })).ReadJsonAsync();
        var folderId = folder.GetProperty("id").GetGuid();
        await StorageTests.UploadAsync(user, ws, folderId, "cũ.txt", "cũ"u8.ToArray());
        await using var agent = Agent(await SignInAsync(user));
        await agent.StartAsync();
        var local = Path.Combine(_tmp, "BlueDrive", "Tài liệu của tôi", "Lưu trữ 2020");
        await Until(() => Task.FromResult(File.Exists(Path.Combine(local, "cũ.txt"))), "thư mục xuống máy");
        var rootId = $"ws:{ws}";

        (await agent.SetFolderIncludedAsync(rootId, folderId.ToString(), include: false)).Should().BeTrue();
        Directory.Exists(local).Should().BeFalse("the folder leaves this computer");
        await StorageTests.UploadAsync(user, ws, folderId, "mới.txt", "mới"u8.ToArray());
        agent.SyncNow();
        await Task.Delay(1500);
        Directory.Exists(local).Should().BeFalse("an excluded folder is not downloaded again");
        (await NamesAsync(user, ws, folderId.ToString())).Should().BeEquivalentTo(["cũ.txt", "mới.txt"], "nothing is deleted on BlueDrive");

        (await agent.SetFolderIncludedAsync(rootId, folderId.ToString(), include: true)).Should().BeTrue();
        await Until(() => Task.FromResult(File.Exists(Path.Combine(local, "mới.txt")) && File.Exists(Path.Combine(local, "cũ.txt"))), "thư mục quay lại máy");
    }

    [Fact]
    public async Task Killing_the_agent_in_the_middle_loses_nothing_and_duplicates_nothing()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        var tokens = await SignInAsync(user);
        var mine = Path.Combine(_tmp, "BlueDrive", "Tài liệu của tôi");
        for (var round = 0; round < 4; round++)
        {
            // New files appear, the agent starts and is stopped abruptly after a moment (no graceful end of the pass).
            await using (var agent = Agent(tokens))
            {
                await agent.StartAsync();
                await Until(() => Task.FromResult(Directory.Exists(mine)), "thư mục gốc");
                for (var i = 0; i < 8; i++) await File.WriteAllTextAsync(Path.Combine(mine, $"vòng {round} tệp {i}.txt"), $"nội dung {round}-{i}");
                await Task.Delay(Random.Shared.Next(300, 1500));
            }
        }
        await using (var final = Agent(tokens))
        {
            await final.StartAsync();
            await Until(async () => (await NamesAsync(user, ws)).Count(n => n.StartsWith("vòng ")) == 32, "đủ 32 tệp", 30);
            await Until(() => Task.FromResult(final.Status.State == AgentState.Idle), "đồng bộ xong", 30);
        }
        var names = await NamesAsync(user, ws);
        names.Where(n => n.StartsWith("vòng ")).Should().HaveCount(32).And.OnlyHaveUniqueItems();
        names.Should().NotContain(n => n.Contains("(1)") || n.Contains("Bản xung đột"), "an interrupted upload never creates a duplicate");
        Directory.GetFiles(mine).Should().HaveCount(32);
    }

    [Fact]
    public async Task Revoked_device_stops_and_a_signed_remote_wipe_deletes_the_local_copy()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        await StorageTests.UploadAsync(user, ws, null, "mật.txt", "dữ liệu nhạy cảm"u8.ToArray());
        var tokens = await SignInAsync(user);
        var deviceId = tokens.Load()!.DeviceId;
        await using (var agent = Agent(tokens))
        {
            await agent.StartAsync();
            var file = Path.Combine(_tmp, "BlueDrive", "Tài liệu của tôi", "mật.txt");
            await Until(() => Task.FromResult(File.Exists(file)), "tệp xuống máy");

            // A forged wipe command (wrong signature) is ignored.
            agent.VerifyWipe(deviceId, new HttpRemoteStore.WipeCommand(DateTime.UtcNow, new string('0', 64))).Should().BeFalse();

            var admin = await factory.AdminClientAsync();
            (await admin.PostAsync($"/api/v1/devices/{deviceId}/wipe", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            agent.SyncNow();
            await Until(() => Task.FromResult(agent.Status.State == AgentState.Wiped), "xóa dữ liệu từ xa");
            File.Exists(file).Should().BeFalse();
            tokens.Load().Should().BeNull();
        }
        var devices = await (await user.GetAsync("/api/v1/devices/mine")).ReadJsonAsync();
        devices.EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == deviceId).GetProperty("status").GetString().Should().Be("wiped");
        (await NamesAsync(user, ws)).Should().Contain("mật.txt", "a remote wipe removes the computer's copy, never the server's");

        // Revocation: the next device (signed in again) is revoked by the admin → the agent stops, files stay on disk.
        Directory.CreateDirectory(_tmp);
        var tokens2 = await SignInAsync(user);
        await using var agent2 = new SyncAgent(new AgentOptions
        {
            Server = factory.Server.BaseAddress, SyncFolder = Path.Combine(_tmp, "BlueDrive2"), StateDbPath = Path.Combine(_tmp, "state2", "state.db"),
            Tokens = tokens2, UseRecycleBin = false, HandlerFactory = factory.Server.CreateHandler,
        });
        await agent2.StartAsync();
        var file2 = Path.Combine(_tmp, "BlueDrive2", "Tài liệu của tôi", "mật.txt");
        await Until(() => Task.FromResult(File.Exists(file2)), "tệp xuống máy 2");
        var admin2 = await factory.AdminClientAsync();
        (await admin2.PostAsync($"/api/v1/devices/{tokens2.Load()!.DeviceId}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        agent2.SyncNow();
        await Until(() => Task.FromResult(agent2.Status.State is AgentState.NeedsSignIn or AgentState.Revoked), "dừng khi bị thu hồi");
        File.Exists(file2).Should().BeTrue("revocation stops syncing but keeps the user's files");
    }
}
