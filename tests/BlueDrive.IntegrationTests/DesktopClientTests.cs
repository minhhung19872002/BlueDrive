using System.Net;
using System.Net.Http.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;
using BlueDrive.Sync.Testing;
using FluentAssertions;
using SyncContent = BlueDrive.Sync.Testing.Content;

namespace BlueDrive.IntegrationTests;

/// <summary>Sync-Phase 3: BlueDrive.Sync.Client (PKCE, HttpRemoteStore, tus, Range, refresh) + engine chạy trên API thật.</summary>
[Collection(ApiCollection.Name)]
public class DesktopClientTests(ApiFactory factory)
{
    private const string Redirect = "http://127.0.0.1:53682/callback";

    private sealed class Pc : IDisposable
    {
        public required string Name;
        public required MemoryTokenStore Tokens;
        public required HttpRemoteStore Remote;
        public required HttpClient Http;
        public required FakeLocalFileSystem Fs;
        public required SyncStateDb Db;
        public required SyncEngine Engine;
        public required FlakyHandler Flaky;

        public async Task<SyncPassResult> SyncAsync()
        {
            var r = await Engine.SyncAsync(CancellationToken.None);
            r.Errors.Should().BeEmpty();
            return r;
        }

        public void Dispose()
        {
            Db.Dispose();
            Http.Dispose();
        }
    }

    /// <summary>Cuts the first signed-content download after <see cref="CutAfter"/> bytes (simulated network drop).</summary>
    private sealed class FlakyHandler : DelegatingHandler
    {
        public int CutAfter = -1;
        public int Cuts;
        public readonly List<string> Ranges = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/v1/content/") && request.Headers.Range is { } r) Ranges.Add(r.ToString());
            var res = await base.SendAsync(request, ct);
            if (CutAfter >= 0 && request.RequestUri!.AbsolutePath.StartsWith("/api/v1/content/") && res.IsSuccessStatusCode)
            {
                var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                var cut = new BrokenStream(bytes[..Math.Min(CutAfter, bytes.Length)]);
                var content = new StreamContent(cut);
                foreach (var h in res.Content.Headers) content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                res.Content = content;
                CutAfter = -1;
                Cuts++;
            }
            return res;
        }
    }

    private sealed class BrokenStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => Position >= Length ? throw new IOException("connection reset") : base.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position >= Length ? throw new IOException("connection reset") : base.ReadAsync(buffer, ct);
    }

    private static byte[] TextBytes(int size)
    {
        var sb = new System.Text.StringBuilder(size);
        while (sb.Length < size) sb.Append(Random.Shared.Next()).Append(',').Append(Guid.NewGuid()).AppendLine();
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    private async Task<(DesktopAuthenticator Auth, DeviceTokens Tokens)> SignInAsync(HttpClient user, string machine)
    {
        var auth = new DesktopAuthenticator(factory.Server.BaseAddress, factory.Server.CreateHandler());
        var pkce = DesktopAuthenticator.PkcePair.Create();
        var authorize = await (await user.PostAsJsonAsync("/api/v1/connect/authorize", new
        {
            clientId = DesktopAuthenticator.ClientId, redirectUri = Redirect, codeChallenge = pkce.Challenge, codeChallengeMethod = "S256", state = "s", deviceName = machine,
        })).ReadJsonAsync();
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(authorize.GetProperty("redirectUrl").GetString()!).Query)["code"]!;
        var tokens = await auth.ExchangeCodeAsync(code, pkce.Verifier, Redirect, new DeviceInfo(machine, "Windows 11", "1.0.0", Guid.NewGuid().ToString()));
        return (auth, tokens);
    }

    private async Task<Pc> PcAsync(HttpClient user, string name, string? rootId = null)
    {
        var (auth, tokens) = await SignInAsync(user, name);
        var store = new MemoryTokenStore();
        store.Save(tokens);
        var flaky = new FlakyHandler { InnerHandler = factory.Server.CreateHandler() };
        var http = BlueDriveHttp.Create(factory.Server.BaseAddress, store, auth, flaky);
        // Small thresholds so that tus chunking and resume are exercised with small files.
        var remote = new HttpRemoteStore(http, new RemoteStoreOptions { SmallUploadBytes = 64 * 1024, ChunkBytes = 100 * 1024, RetryBaseDelay = TimeSpan.FromMilliseconds(50) });
        rootId ??= (await remote.RootsAsync(CancellationToken.None)).First(r => r.Kind == "personal").RootId;
        var db = new SyncStateDb(":memory:");
        db.UpsertRoot(rootId, "", true);
        var fs = new FakeLocalFileSystem(new FakeClock(DateTime.UtcNow));
        var engine = new SyncEngine(db, remote, fs, SystemClock.Instance,
            new SyncEngineOptions { UserDisplayName = "Người dùng thử", DeviceName = name, TimeZone = TimeZoneInfo.Utc, MaxRounds = 20 });
        return new Pc { Name = name, Tokens = store, Remote = remote, Http = http, Fs = fs, Db = db, Engine = engine, Flaky = flaky };
    }

    [Fact]
    public async Task Two_computers_sync_create_edit_rename_conflict_and_delete_through_the_real_api()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        using var a = await PcAsync(user, "PC-A");
        using var b = await PcAsync(user, "PC-B");

        var big = TextBytes(350 * 1024);
        a.Fs.UserMkdir("Báo cáo");
        a.Fs.UserWrite("Báo cáo/ghi chú.txt", SyncContent.Of("bản 1"));
        a.Fs.UserWrite("Báo cáo/du lieu.csv", big); // > SmallUploadBytes: tus, 4 chunks
        await a.SyncAsync();
        await b.SyncAsync();
        b.Fs.Read("Báo cáo/ghi chú.txt").Should().Equal(SyncContent.Of("bản 1"));
        b.Fs.Read("Báo cáo/du lieu.csv").Should().Equal(big);

        // Edit on B → new version on the server → A downloads it.
        b.Fs.UserWrite("Báo cáo/ghi chú.txt", SyncContent.Of("bản 2 từ PC-B"));
        await b.SyncAsync();
        await a.SyncAsync();
        a.Fs.Read("Báo cáo/ghi chú.txt").Should().Equal(SyncContent.Of("bản 2 từ PC-B"));

        // Rename a folder on A: B renames locally, no re-download.
        a.Fs.UserMove("Báo cáo", "Báo cáo 2026").Should().BeTrue();
        await a.SyncAsync();
        b.Flaky.Ranges.Clear();
        await b.SyncAsync();
        b.Fs.Exists("Báo cáo 2026/du lieu.csv").Should().BeTrue();
        b.Fs.Exists("Báo cáo").Should().BeFalse();

        // Both edit the same file: the server keeps the first, the second becomes a conflict copy.
        a.Fs.UserWrite("Báo cáo 2026/ghi chú.txt", SyncContent.Of("A sửa"));
        b.Fs.UserWrite("Báo cáo 2026/ghi chú.txt", SyncContent.Of("B sửa"));
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        b.Fs.Read("Báo cáo 2026/ghi chú.txt").Should().Equal(SyncContent.Of("A sửa"));
        var conflict = b.Fs.Files.Single(f => f.Contains("Bản xung đột"));
        b.Fs.Read(conflict).Should().Equal(SyncContent.Of("B sửa"));
        a.Fs.Exists(conflict).Should().BeTrue("the conflict copy is uploaded next to the original");

        // Delete on B → server trash → A removes it.
        b.Fs.UserDelete("Báo cáo 2026/du lieu.csv");
        await b.SyncAsync();
        await a.SyncAsync();
        a.Fs.Exists("Báo cáo 2026/du lieu.csv").Should().BeFalse();

        // Converged: another pass does nothing on either side.
        (await a.SyncAsync()).Operations.Should().Be(0);
        (await b.SyncAsync()).Operations.Should().Be(0);
    }

    [Fact]
    public async Task Download_resumes_with_range_after_a_dropped_connection()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        using var a = await PcAsync(user, "PC-A");
        var data = TextBytes(200 * 1024);
        a.Fs.UserWrite("lớn.csv", data);
        await a.SyncAsync();
        var id = a.Db.Items(a.Db.Roots().Single().RootId).Single(i => i.Name == "lớn.csv").RemoteId;

        a.Flaky.CutAfter = 70_000;
        using var ms = new MemoryStream();
        await a.Remote.DownloadAsync(id, ms, CancellationToken.None);
        a.Flaky.Cuts.Should().Be(1);
        a.Flaky.Ranges.Should().Contain("bytes=70000-");
        ms.ToArray().Should().Equal(data);

        // Files On-Demand hydration reads exactly the requested range, and resumes it after a drop too.
        a.Flaky.CutAfter = 10_000;
        var part = new MemoryStream();
        await a.Remote.ReadRangeAsync(id, 50_000, 64_000, async (chunk, ct) => await part.WriteAsync(chunk, ct), CancellationToken.None);
        part.ToArray().Should().Equal(data[50_000..114_000]);
        a.Flaky.Ranges.Should().Contain("bytes=60000-113999");
    }

    private sealed class MemoryResume : IUploadResumeStore
    {
        public readonly Dictionary<string, string> Urls = [];
        public string? Get(string key) => Urls.GetValueOrDefault(key);

        public void Set(string key, string? url)
        {
            if (url is null) Urls.Remove(key);
            else Urls[key] = url;
        }
    }

    /// <summary>Lets <see cref="AllowPatches"/> tus chunks through, then the network "goes down" for PATCH; counts chunks sent.</summary>
    private sealed class PatchGate : DelegatingHandler
    {
        public int AllowPatches = int.MaxValue;
        public int Patches;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Patch)
            {
                if (Patches >= AllowPatches) throw new HttpRequestException("mất mạng");
                Patches++;
            }
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task An_interrupted_upload_continues_after_a_restart_without_sending_the_start_again()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (auth, tokens) = await SignInAsync(user, "PC-RESUME");
        var store = new MemoryTokenStore();
        store.Save(tokens);
        var root = $"ws:{await StorageTests.PersonalWorkspaceAsync(user)}";
        var data = TextBytes(450 * 1024); // 5 chunks of 100 KB
        var hash = SyncContent.Hash(data);
        var resume = new MemoryResume();
        HttpRemoteStore Store(PatchGate gate) => new(BlueDriveHttp.Create(factory.Server.BaseAddress, store, auth, gate),
            new RemoteStoreOptions { SmallUploadBytes = 64 * 1024, ChunkBytes = 100 * 1024, MaxRetries = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(20), Resume = resume });

        var first = new PatchGate { AllowPatches = 2, InnerHandler = factory.Server.CreateHandler() };
        await FluentActions.Awaiting(() => Store(first).UploadNewAsync(root, null, "dữ liệu lớn.csv", new MemoryStream(data), hash, CancellationToken.None))
            .Should().ThrowAsync<RemoteTransientException>();
        resume.Urls.Should().ContainSingle("the unfinished tus session is remembered");

        // "Restart": a new client, same remembered session.
        var second = new PatchGate { InnerHandler = factory.Server.CreateHandler() };
        var item = await Store(second).UploadNewAsync(root, null, "dữ liệu lớn.csv", new MemoryStream(data), hash, CancellationToken.None);
        second.Patches.Should().Be(3, "only the 3 missing chunks are sent");
        item.Hash.Should().Be(hash);
        resume.Urls.Should().BeEmpty();
    }

    [Fact]
    public async Task Server_errors_map_to_engine_exceptions()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        using var a = await PcAsync(user, "PC-A");
        var root = a.Db.Roots().Single().RootId;
        var file = await a.Remote.UploadNewAsync(root, null, "báo giá.txt", new MemoryStream(SyncContent.Of("v1")), SyncContent.Hash(SyncContent.Of("v1")), CancellationToken.None);
        file.Revision.Should().BeGreaterThan(0);

        // Same name again: 409 instead of "báo giá (1).docx".
        await a.Invoking(x => x.Remote.UploadNewAsync(root, null, "báo giá.txt", new MemoryStream(SyncContent.Of("v2")), SyncContent.Hash(SyncContent.Of("v2")), CancellationToken.None))
            .Should().ThrowAsync<RemoteNameConflictException>();

        var v2 = await a.Remote.UploadVersionAsync(file.Id, file.Revision, new MemoryStream(SyncContent.Of("v2")), SyncContent.Hash(SyncContent.Of("v2")), CancellationToken.None);
        v2.Hash.Should().Be(SyncContent.Hash(SyncContent.Of("v2")));

        // Stale revision: 412 with the current item (multipart and tus paths).
        var stale = await a.Invoking(x => x.Remote.UploadVersionAsync(file.Id, file.Revision, new MemoryStream(SyncContent.Of("v3")), SyncContent.Hash(SyncContent.Of("v3")), CancellationToken.None))
            .Should().ThrowAsync<RemotePreconditionFailedException>();
        stale.Which.Current!.Revision.Should().Be(v2.Revision);
        var bigStale = TextBytes(100 * 1024);
        await a.Invoking(x => x.Remote.UploadVersionAsync(file.Id, file.Revision, new MemoryStream(bigStale), SyncContent.Hash(bigStale), CancellationToken.None))
            .Should().ThrowAsync<RemotePreconditionFailedException>();
        await a.Invoking(x => x.Remote.MoveAsync(file.Id, file.Revision, root, null, "đổi.txt", CancellationToken.None))
            .Should().ThrowAsync<RemotePreconditionFailedException>();
        await a.Invoking(x => x.Remote.DeleteAsync(file.Id, file.Revision, CancellationToken.None))
            .Should().ThrowAsync<RemotePreconditionFailedException>();

        // Wrong hash: the server refuses the upload.
        await a.Invoking(x => x.Remote.UploadNewAsync(root, null, "hỏng.txt", new MemoryStream(SyncContent.Of("x")), new string('0', 64), CancellationToken.None))
            .Should().ThrowAsync<Exception>().Where(e => e is RemotePermanentException);

        var moved = await a.Remote.MoveAsync(file.Id, v2.Revision, root, null, "đổi tên.txt", CancellationToken.None);
        moved.Name.Should().Be("đổi tên.txt");
        await a.Remote.DeleteAsync(file.Id, moved.Revision, CancellationToken.None);
        await a.Invoking(x => x.Remote.GetItemAsync(file.Id, CancellationToken.None)).Should().ThrowAsync<RemoteNotFoundException>();
        var changes = await a.Remote.ChangesAsync(0, CancellationToken.None);
        changes.Changes.Should().Contain(c => c.ItemId == file.Id && c.Kind == RemoteChangeKind.Delete);
    }

    [Fact]
    public async Task Notifications_push_a_sync_request_when_the_web_changes_a_synced_workspace()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        using var a = await PcAsync(user, "PC-A");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        await using var notes = new SyncNotifications(factory.Server.BaseAddress, () => Task.FromResult(a.Tokens.Load()?.AccessToken), factory.Server.CreateHandler);
        var pushed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        notes.SyncRequested += reason => { if (reason == "push") pushed.TrySetResult(reason); };
        await notes.StartAsync();
        notes.State.Should().Be(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected);

        await (await user.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Từ trình duyệt" })).ReadJsonAsync();
        (await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("push");
        await a.SyncAsync();
        a.Fs.IsFolder("Từ trình duyệt").Should().BeTrue();
    }

    [Fact]
    public async Task Explorer_context_menu_commands_work_against_the_api()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (other, _, otherName) = await factory.CreateUserClientAsync("bien_tap");
        using var a = await PcAsync(user, "PC-A");
        a.Fs.UserMkdir("Hồ sơ");
        a.Fs.UserWrite("Hồ sơ/tờ trình.txt", SyncContent.Of("bản 1"));
        await a.SyncAsync();
        a.Fs.UserWrite("Hồ sơ/tờ trình.txt", SyncContent.Of("bản 2"));
        await a.SyncAsync();
        var root = a.Db.Roots().Single();
        var item = a.Db.Items(root.RootId).Single(i => i.Name == "tờ trình.txt");
        var api = new BlueDriveApi(a.Http);

        // Lookup by local path (what the context menu receives).
        var syncFolder = Path.Combine(Path.GetTempPath(), "BlueDrive");
        var hit = BlueDrive.Sync.Agent.LocalIndex.Find(a.Db, syncFolder, Path.Combine(syncFolder, "Hồ sơ", "tờ trình.txt"));
        hit!.Item.RemoteId.Should().Be(item.RemoteId);
        BlueDrive.Sync.Agent.LocalIndex.Find(a.Db, syncFolder, Path.Combine(Path.GetTempPath(), "Khác", "tờ trình.txt")).Should().BeNull();

        (await api.WebUrlAsync(item.RemoteId)).Url.Should().EndWith($"/files/{item.RemoteId}");

        // Chia sẻ… with a user, then listed and removable.
        var found = await api.FindUsersAsync(otherName);
        var target = found.Single(u => u.Username == otherName);
        await api.ShareAsync("file", item.RemoteId, "user", target.Id, "download", "Nhờ anh xem giúp");
        var shares = await api.SharesAsync("file", item.RemoteId);
        shares.Should().ContainSingle(s => s.SharedWithId == target.Id && s.AccessLevel == "download");
        (await other.GetAsync($"/api/v1/files/{item.RemoteId}")).StatusCode.Should().Be(HttpStatusCode.OK, "the colleague can open it now");
        await api.RemoveShareAsync(shares.Single().Id);
        (await api.SharesAsync("file", item.RemoteId)).Should().BeEmpty();

        // Link with password and expiry.
        var link = await api.CreateLinkAsync(item.RemoteId, allowDownload: false, password: "Mk@123456", DateTime.UtcNow.AddDays(7), requireLogin: false);
        link.HasPassword.Should().BeTrue();
        link.Url.Should().Contain("/s/");

        // Lịch sử phiên bản…: save version 1, restore it as current.
        var versions = await api.VersionsAsync(item.RemoteId);
        versions.Should().HaveCount(2);
        var v1 = versions.Single(v => v.VersionNo == 1);
        var saved = new MemoryStream();
        await api.DownloadVersionAsync(item.RemoteId, v1.Id, saved);
        saved.ToArray().Should().Equal(SyncContent.Of("bản 1"));
        await api.RestoreVersionAsync(item.RemoteId, v1.Id);
        await a.SyncAsync();
        a.Fs.Read("Hồ sơ/tờ trình.txt").Should().Equal(SyncContent.Of("bản 1"), "the restored version comes down to the computer");
    }

    [Fact]
    public async Task OnlyOffice_desktop_ticket_opens_the_editor_without_signing_in_again_once()
    {
        var anon = factory.CreateClient();
        var status = await (await anon.GetAsync("/api/v1/onlyoffice-desktop/status")).ReadJsonAsync();
        status.GetProperty("provider").GetString().Should().Be("bluedrive");

        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (stranger, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        var file = await StorageTests.UploadAsync(user, ws, null, "tờ trình.txt", "x"u8.ToArray());
        var fileId = file.GetProperty("fileId").GetGuid();
        using var a = await PcAsync(user, "PC-A");

        var link = await (await a.Http.PostAsJsonAsync("api/v1/onlyoffice-desktop/tickets", new { fileId })).ReadJsonAsync();
        var url = new Uri(factory.Server.BaseAddress, link.GetProperty("url").GetString()!); // absolute when App:PublicBaseUrl is set
        url.AbsolutePath.Should().Be("/oo-desktop/open");
        link.GetProperty("canEdit").GetBoolean().Should().BeTrue();
        var ticket = System.Web.HttpUtility.ParseQueryString(url.Query)["ticket"]!;

        var redeemed = await (await anon.PostAsJsonAsync("/api/v1/onlyoffice-desktop/tickets/redeem", new { ticket })).ReadJsonAsync();
        redeemed.GetProperty("next").GetString().Should().Be($"/files/{fileId}/edit");
        var web = factory.CreateClient().WithToken(redeemed.GetProperty("auth").GetProperty("accessToken").GetString()!);
        (await web.GetAsync($"/api/v1/office/config/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK, "the redeemed session opens the editor");

        (await anon.PostAsJsonAsync("/api/v1/onlyoffice-desktop/tickets/redeem", new { ticket })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a ticket works once");
        (await stranger.PostAsJsonAsync("/api/v1/onlyoffice-desktop/tickets", new { fileId })).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "no ticket for a document the user cannot see");
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_and_rotated_transparently()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        using var a = await PcAsync(user, "PC-A");
        var before = a.Tokens.Load()!;
        a.Tokens.Save(before with { AccessExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
        (await a.Remote.RootsAsync(CancellationToken.None)).Should().NotBeEmpty();
        var after = a.Tokens.Load()!;
        after.RefreshToken.Should().NotBe(before.RefreshToken);
        after.DeviceId.Should().Be(before.DeviceId);

        // A garbage access token: 401 → refresh once → request succeeds.
        a.Tokens.Save(after with { AccessToken = "x" });
        (await a.Remote.RootsAsync(CancellationToken.None)).Should().NotBeEmpty();

        // Revoked device: refresh fails → sign in again, tokens wiped.
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsync($"/api/v1/devices/{after.DeviceId}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        a.Tokens.Save(a.Tokens.Load()! with { AccessExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
        await a.Invoking(x => x.Remote.RootsAsync(CancellationToken.None)).Should().ThrowAsync<SignInRequiredException>();
        a.Tokens.Load().Should().BeNull();
    }
}
