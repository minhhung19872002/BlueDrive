using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BlueDrive.IntegrationTests.Infrastructure;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>Sync-Phase 12: LAN sync between two BlueDrive Desktops of the same server (tickets checked by the real API).</summary>
[Collection(ApiCollection.Name)]
public sealed class LanSyncTests(ApiFactory factory) : IDisposable
{
    private const string Redirect = "http://127.0.0.1:53682/callback";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bd-lan-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            // View-only files are made read-only by the sync engine: clear that so the folder can go.
            if (Directory.Exists(_tmp))
                foreach (var f in Directory.EnumerateFiles(_tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_tmp, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private async Task<MemoryTokenStore> SignInAsync(HttpClient user, string device)
    {
        var auth = new DesktopAuthenticator(factory.Server.BaseAddress, factory.Server.CreateHandler());
        var pkce = DesktopAuthenticator.PkcePair.Create();
        var authorize = await (await user.PostAsJsonAsync("/api/v1/connect/authorize", new
        {
            clientId = DesktopAuthenticator.ClientId, redirectUri = Redirect, codeChallenge = pkce.Challenge, codeChallengeMethod = "S256", state = "s", deviceName = device,
        })).ReadJsonAsync();
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(authorize.GetProperty("redirectUrl").GetString()!).Query)["code"]!;
        var store = new MemoryTokenStore();
        store.Save(await auth.ExchangeCodeAsync(code, pkce.Verifier, Redirect, new DeviceInfo(device, "Windows 11", "1.0.0", Guid.NewGuid().ToString())));
        return store;
    }

    private SyncAgent Agent(MemoryTokenStore tokens, string name) => new(new AgentOptions
    {
        Server = factory.Server.BaseAddress,
        SyncFolder = Path.Combine(_tmp, name, "BlueDrive"),
        StateDbPath = Path.Combine(_tmp, name, "state", "state.db"),
        Tokens = tokens,
        UserDisplayName = "Người thử",
        DeviceName = name,
        LocalQuiet = TimeSpan.FromMilliseconds(500),
        UseRecycleBin = false,
        HandlerFactory = factory.Server.CreateHandler,
        LanSync = true,
        LanDiscovery = false,
    });

    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException($"Hết thời gian chờ: {what}");
            await Task.Delay(200);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string what, int seconds = 60, Func<string>? status = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException($"Hết thời gian chờ: {what} ({status?.Invoke()})");
            await Task.Delay(200);
        }
    }

    private static async Task<List<string>> NamesAsync(HttpClient user, Guid ws) =>
        (await (await user.GetAsync($"/api/v1/workspaces/{ws}/items?pageSize=200")).ReadJsonAsync())
        .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task A_second_computer_gets_the_file_from_the_first_one_on_the_lan()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        await using var a = Agent(await SignInAsync(user, "MAY-A"), "a");
        await a.StartAsync();
        var mineA = Path.Combine(_tmp, "a", "BlueDrive", "Tài liệu của tôi");
        await Until(() => Directory.Exists(mineA) && a.Status.State == AgentState.Idle, "máy A sẵn sàng");
        var data = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(1536 * 1024 + 61)));
        await File.WriteAllBytesAsync(Path.Combine(mineA, "số liệu khảo sát.csv"), data);
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        await UntilAsync(async () => (await NamesAsync(user, ws)).Contains("số liệu khảo sát.csv") && a.Status.State == AgentState.Idle,
            "máy A tải tệp lên", status: () => $"{a.Status.State} {a.Status.Message} {string.Join("; ", a.Status.Problems)}");

        await using var b = Agent(await SignInAsync(user, "MAY-B"), "b");
        b.Lan!.AddPeer(new IPEndPoint(IPAddress.Loopback, a.Lan!.Port));
        await b.StartAsync();
        var fileB = Path.Combine(_tmp, "b", "BlueDrive", "Tài liệu của tôi", "số liệu khảo sát.csv");
        await Until(() => File.Exists(fileB) && b.Status.State == AgentState.Idle, "máy B có tệp", 60);
        (await File.ReadAllBytesAsync(fileB)).Should().Equal(data);
        b.Lan.Received.Should().Be(1, "the file came from computer A");
        a.Lan.Served.Should().Be(1);
    }

    [Fact]
    public async Task Tickets_follow_the_download_rights_of_both_computers()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (stranger, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await StorageTests.UploadAsync(owner, ws, null, "bí mật.txt", "nội dung riêng"u8.ToArray())).GetProperty("fileId").GetGuid();

        await using var mine = Agent(await SignInAsync(owner, "MAY-CHU"), "chu");
        await using var other = Agent(await SignInAsync(owner, "MAY-CHU-2"), "chu2");
        await using var outsider = Agent(await SignInAsync(stranger, "MAY-LA"), "la");

        // Someone who cannot download the file gets no ticket.
        (await outsider.Invoking(x => x.Remote.LanTicketAsync(fileId.ToString(), CancellationToken.None)).Should().ThrowAsync<Exception>())
            .Which.Should().Match(e => e is RemoteNotFoundException || e is RemotePermanentException);

        var ticket = await mine.Remote.LanTicketAsync(fileId.ToString(), CancellationToken.None);
        ticket.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData("nội dung riêng"u8.ToArray())));
        ticket.Size.Should().Be("nội dung riêng"u8.Length);

        // A peer that cannot see the file learns nothing; the device that asked cannot confirm its own ticket.
        await outsider.Invoking(x => x.Remote.LanConfirmAsync(ticket.Ticket, CancellationToken.None)).Should().ThrowAsync<RemoteNotFoundException>();
        await mine.Invoking(x => x.Remote.LanConfirmAsync(ticket.Ticket, CancellationToken.None)).Should().ThrowAsync<RemoteNotFoundException>();
        var info = await other.Remote.LanConfirmAsync(ticket.Ticket, CancellationToken.None);
        (info.FileId, info.Key, info.Sha256).Should().Be((fileId, ticket.Key, ticket.Sha256));
        await other.Invoking(x => x.Remote.LanConfirmAsync("vé giả", CancellationToken.None)).Should().ThrowAsync<RemoteNotFoundException>();

        // A web session (not a BlueDrive Desktop) cannot ask for tickets.
        (await owner.PostAsJsonAsync("/api/v1/sync/lan/tickets", new { fileId })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
