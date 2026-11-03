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

        // Deleting the whole sync folder never deletes anything on the server: the agent stops with an explanation.
        await Until(() => Task.FromResult(agent.Status.State == AgentState.Idle), "đồng bộ xong");
        Directory.Delete(Path.Combine(_tmp, "BlueDrive"), recursive: true);
        agent.SyncNow();
        await Until(() => Task.FromResult(agent.Status.State == AgentState.Error), "báo lỗi thiếu thư mục");
        agent.Status.Message.Should().Contain("không có gì bị xóa trên BlueDrive");
        (await NamesAsync(user, ws, folderId)).Should().Contain("tuần 45.txt");
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
