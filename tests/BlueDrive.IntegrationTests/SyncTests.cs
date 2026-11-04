using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Sync;
using BlueDrive.Application.Settings;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>Mở rộng "Đồng bộ desktop" — Sync-Phase 1: PKCE, thiết bị, roots/snapshot/changes, If-Match, batch, chính sách, xóa từ xa.</summary>
[Collection(ApiCollection.Name)]
public class SyncTests(ApiFactory factory)
{
    private const string Redirect = "http://127.0.0.1:53682/callback";

    private sealed record DeviceSession(HttpClient Client, Guid DeviceId, string RefreshToken, string Secret, string AccessToken);

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = SyncClients.Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, SyncClients.S256(verifier));
    }

    private static async Task<string> AuthorizeAsync(HttpClient user, string challenge, string redirect = Redirect)
    {
        var res = await (await user.PostAsJsonAsync("/api/v1/connect/authorize", new
        {
            clientId = SyncClients.DesktopClientId, redirectUri = redirect, codeChallenge = challenge, codeChallengeMethod = "S256", state = "st-1", deviceName = "Máy thử",
        })).ReadJsonAsync();
        var url = new Uri(res.GetProperty("redirectUrl").GetString()!);
        url.GetLeftPart(UriPartial.Path).Should().Be(redirect);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        query["state"].Should().Be("st-1");
        return query["code"]!;
    }

    private async Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form) =>
        await factory.CreateClient().PostAsync("/api/v1/connect/token", new FormUrlEncodedContent(form));

    private async Task<DeviceSession> DeviceAsync(HttpClient user, string machine = "")
    {
        var (verifier, challenge) = Pkce();
        var code = await AuthorizeAsync(user, challenge);
        var json = await (await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Redirect,
            ["client_id"] = SyncClients.DesktopClientId, ["device_name"] = "PC-KE-TOAN", ["device_os"] = "Windows 11 23H2", ["app_version"] = "1.0.0",
            ["machine_id"] = machine.Length > 0 ? machine : Guid.NewGuid().ToString(),
        })).ReadJsonAsync();
        json.GetProperty("token_type").GetString().Should().Be("Bearer");
        return new DeviceSession(factory.CreateClient().WithToken(json.GetProperty("access_token").GetString()!), json.GetProperty("device_id").GetGuid(),
            json.GetProperty("refresh_token").GetString()!, json.GetProperty("device_secret").GetString()!, json.GetProperty("access_token").GetString()!);
    }

    private static async Task<List<JsonElement>> SnapshotAsync(HttpClient device, string rootId, Action<long>? cursor = null)
    {
        var items = new List<JsonElement>();
        string? token = null;
        do
        {
            var page = await (await device.GetAsync($"/api/v1/sync/snapshot?root_id={Uri.EscapeDataString(rootId)}" + (token is null ? "" : $"&page_token={Uri.EscapeDataString(token)}"))).ReadJsonAsync();
            items.AddRange(page.GetProperty("items").EnumerateArray());
            cursor?.Invoke(page.GetProperty("cursor").GetInt64());
            token = page.GetProperty("nextPageToken").ValueKind == JsonValueKind.String ? page.GetProperty("nextPageToken").GetString() : null;
        } while (token is not null);
        return items;
    }

    private static async Task<List<JsonElement>> ChangesAsync(HttpClient device, long cursor) =>
        (await (await device.GetAsync($"/api/v1/sync/changes?cursor={cursor}")).ReadJsonAsync()).GetProperty("changes").EnumerateArray().ToList();

    [Fact]
    public async Task Pkce_login_registers_a_device_session_that_refreshes_and_is_revocable()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");

        // Non-loopback redirect and plain (non S256) PKCE are refused.
        (await user.PostAsJsonAsync("/api/v1/connect/authorize", new
        {
            clientId = SyncClients.DesktopClientId, redirectUri = "https://evil.example/cb", codeChallenge = Pkce().Challenge, codeChallengeMethod = "S256",
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Wrong verifier: invalid_grant, and the code is burnt.
        var (verifier, challenge) = Pkce();
        var code = await AuthorizeAsync(user, challenge);
        var bad = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = Pkce().Verifier, ["redirect_uri"] = Redirect });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_grant");
        (await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Redirect }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a code is single use");

        var device = await DeviceAsync(user, "machine-A");
        var status = await (await device.Client.GetAsync("/api/v1/sync/devices/current?appVersion=1.0.0")).ReadJsonAsync();
        status.GetProperty("status").GetString().Should().Be("active");
        status.GetProperty("wipe").ValueKind.Should().Be(JsonValueKind.Null);
        var mine = await (await user.GetAsync("/api/v1/devices/mine")).ReadJsonAsync();
        mine.EnumerateArray().Should().ContainSingle(d => d.GetProperty("id").GetGuid() == device.DeviceId && d.GetProperty("name").GetString() == "PC-KE-TOAN");

        // Same machine logs in again: same device registration.
        (await DeviceAsync(user, "machine-A")).DeviceId.Should().Be(device.DeviceId);

        // Refresh rotates the device refresh token (long-lived, no idle timeout).
        var refreshed = await (await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = device.RefreshToken })).ReadJsonAsync();
        refreshed.GetProperty("device_id").GetGuid().Should().Be(device.DeviceId);
        refreshed.GetProperty("refresh_token_expires_at").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddDays(80));

        // Revoke from the web: the app is signed out and cannot refresh any more.
        (await user.PostAsync($"/api/v1/devices/{device.DeviceId}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await device.Client.GetAsync("/api/v1/sync/roots")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var denied = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshed.GetProperty("refresh_token").GetString()! });
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Snapshot_and_change_feed_follow_drive_operations_with_etags()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        var folder = (await (await user.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Đồng bộ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var target = (await (await user.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Lưu trữ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var fileId = (await StorageTests.UploadAsync(user, ws, folder, "ke-hoach.txt", Encoding.UTF8.GetBytes("v1"))).GetProperty("fileId").GetGuid();
        var device = await DeviceAsync(user);

        var roots = await (await device.Client.GetAsync("/api/v1/sync/roots")).ReadJsonAsync();
        roots.EnumerateArray().Should().Contain(r => r.GetProperty("rootId").GetString() == $"ws:{ws}" && r.GetProperty("kind").GetString() == "personal");
        roots.EnumerateArray().Should().Contain(r => r.GetProperty("rootId").GetString() == "shared");

        long cursor = 0;
        var items = await SnapshotAsync(device.Client, $"ws:{ws}", c => cursor = c);
        var file = items.Single(i => i.GetProperty("id").GetGuid() == fileId);
        file.GetProperty("parentId").GetGuid().Should().Be(folder);
        file.GetProperty("hash").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData("v1"u8.ToArray())));
        file.GetProperty("canDownload").GetBoolean().Should().BeTrue();
        var etag = file.GetProperty("eTag").GetString()!;
        items.FindIndex(i => i.GetProperty("id").GetGuid() == folder).Should().BeLessThan(items.FindIndex(i => i.GetProperty("id").GetGuid() == fileId), "parents first");

        // Stale If-Match: 412 with the current metadata; matching If-Match succeeds and returns the new ETag.
        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/files/{fileId}/name") { Content = JsonContent.Create(new { name = "x.txt" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"999\"");
        var staleRes = await device.Client.SendAsync(stale);
        staleRes.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await staleRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("current").GetProperty("name").GetString().Should().Be("ke-hoach.txt");
        var rename = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/files/{fileId}/name") { Content = JsonContent.Create(new { name = "ke-hoach-2026.txt" }) };
        rename.Headers.TryAddWithoutValidation("If-Match", etag);
        var renamed = await device.Client.SendAsync(rename);
        renamed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        renamed.Headers.ETag!.Tag.Should().NotBe(etag);

        // Web changes: move, new version (content hash verified), then delete the folder.
        await (await user.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "file", id = fileId } }, targetWorkspaceId = ws, targetFolderId = target })).ReadJsonAsync();
        var afterMove = (await (await device.Client.GetAsync($"/api/v1/sync/items/{fileId}")).ReadJsonAsync()).GetProperty("revision").GetInt64();
        var v2 = Encoding.UTF8.GetBytes("v2 noi dung moi");
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(v2), "file", "ke-hoach-2026.txt" }, { new StringContent(ws.ToString()), "workspaceId" }, { new StringContent(fileId.ToString()), "targetFileId" },
            { new StringContent(Convert.ToHexStringLower(SHA256.HashData(v2))), "content_hash" }, { new StringContent(afterMove.ToString()), "base_etag" },
        };
        await (await user.PostAsync("/api/v1/files/upload", form)).ReadJsonAsync();
        await (await user.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "folder", id = folder } } })).ReadJsonAsync();

        var changes = await ChangesAsync(device.Client, cursor);
        var mine = changes.Where(c => c.GetProperty("itemId").GetGuid() == fileId || c.GetProperty("itemId").GetGuid() == folder).ToList();
        mine.Select(c => c.GetProperty("op").GetString()).Should().ContainInOrder("rename", "move", "update_content", "delete");
        var rn = mine.First(c => c.GetProperty("op").GetString() == "rename");
        rn.GetProperty("deviceId").GetGuid().Should().Be(device.DeviceId, "the change came from this device (no ping-pong)");
        var latest = mine.Last(c => c.GetProperty("itemId").GetGuid() == fileId).GetProperty("item");
        latest.GetProperty("parentId").GetGuid().Should().Be(target);
        latest.GetProperty("hash").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(v2)));

        // The content endpoint serves Range requests.
        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sync/items/{fileId}/content");
        range.Headers.Range = new RangeHeaderValue(0, 1);
        var part = await device.Client.SendAsync(range);
        part.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await part.Content.ReadAsByteArrayAsync()).Should().Equal(v2[..2]);

        // Audit carries the device.
        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.AsNoTracking()
            .Where(a => a.ResourceId == fileId && a.Action == Domain.Enums.AuditAction.Rename).ToListAsync();
        audit.Should().ContainSingle().Which.Detail.Should().Contain(device.DeviceId.ToString()).And.Contain("desktop_sync");

        // Base ETag mismatch on a version upload: 412; wrong content hash: 422; conflict=fail on an existing name: 409.
        var staleVersion = new MultipartFormDataContent
        {
            { new ByteArrayContent(v2), "file", "ke-hoach-2026.txt" }, { new StringContent(ws.ToString()), "workspaceId" }, { new StringContent(fileId.ToString()), "targetFileId" },
            { new StringContent(etag), "base_etag" },
        };
        (await device.Client.PostAsync("/api/v1/files/upload", staleVersion)).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        var badHash = new MultipartFormDataContent
        {
            { new ByteArrayContent(v2), "file", "khac.txt" }, { new StringContent(ws.ToString()), "workspaceId" }, { new StringContent(new string('0', 64)), "content_hash" },
        };
        (await device.Client.PostAsync("/api/v1/files/upload", badHash)).StatusCode.Should().Be((HttpStatusCode)422);
        var dup = new MultipartFormDataContent
        {
            { new ByteArrayContent(v2), "file", "ke-hoach-2026.txt" }, { new StringContent(ws.ToString()), "workspaceId" }, { new StringContent(target.ToString()), "folderId" },
            { new StringContent("fail"), "conflict" },
        };
        (await device.Client.PostAsync("/api/v1/files/upload", dup)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Shared_root_lists_shares_flags_view_only_and_reports_access_revoked()
    {
        var (owner, _, ownerName) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Chia sẻ đồng bộ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var inside = (await StorageTests.UploadAsync(owner, ws, folder, "trong.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var viewOnly = (await StorageTests.UploadAsync(owner, ws, null, "chi-xem.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "folder", resourceId = folder, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 3 })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = viewOnly, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 1 })).ReadJsonAsync();

        var device = await DeviceAsync(friend);
        long cursor = 0;
        var shared = await SnapshotAsync(device.Client, "shared", c => cursor = c);
        // Spec 7.2 "Được chia sẻ với tôi\<Tên người chia sẻ>": a read-only virtual folder per person, listed before what it holds.
        var sharer = shared.Single(i => i.GetProperty("virtual").GetBoolean());
        sharer.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null, "top level of the shared root");
        sharer.GetProperty("name").GetString().Should().Be("Người dùng " + ownerName);
        sharer.GetProperty("canEdit").GetBoolean().Should().BeFalse();
        shared.IndexOf(sharer).Should().Be(0);
        var top = shared.Single(i => i.GetProperty("id").GetGuid() == folder);
        top.GetProperty("parentId").GetGuid().Should().Be(sharer.GetProperty("id").GetGuid(), "inside the folder of the person who shares it");
        top.GetProperty("rootId").GetString().Should().Be("shared");
        (await (await device.Client.GetAsync($"/api/v1/sync/items/{sharer.GetProperty("id").GetGuid()}")).ReadJsonAsync()).GetProperty("name").GetString()
            .Should().Be("Người dùng " + ownerName, "the virtual folder can be asked for like any item");
        shared.Single(i => i.GetProperty("id").GetGuid() == inside).GetProperty("canEdit").GetBoolean().Should().BeTrue("the folder share is inherited");
        shared.Single(i => i.GetProperty("id").GetGuid() == viewOnly).GetProperty("canDownload").GetBoolean().Should().BeFalse();
        (await device.Client.GetAsync($"/api/v1/sync/items/{viewOnly}/content")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "view-only files are never synced down");
        (await device.Client.GetAsync($"/api/v1/sync/snapshot?root_id=ws:{ws}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "not one of the friend's roots");

        // A file of the folder stops inheriting permissions (without the friend): it leaves the friend's sync tree.
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{inside}", new { entries = Array.Empty<object>(), breakInheritance = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SnapshotAsync(device.Client, "shared")).Should().NotContain(i => i.GetProperty("id").GetGuid() == inside);
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{inside}", new { entries = Array.Empty<object>(), breakInheritance = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Owner stops sharing the folder: the friend's feed says access_revoked for it.
        var shares = await (await owner.GetAsync($"/api/v1/shares?resourceType=folder&resourceId={folder}")).ReadJsonAsync();
        foreach (var s in shares.EnumerateArray()) await owner.DeleteAsync($"/api/v1/shares/{s.GetProperty("id").GetGuid()}");
        var changes = await ChangesAsync(device.Client, cursor);
        changes.Should().Contain(c => c.GetProperty("itemId").GetGuid() == folder && c.GetProperty("op").GetString() == "access_revoked");
        changes.Should().NotContain(c => c.GetProperty("itemId").GetGuid() == sharer.GetProperty("id").GetGuid(), "the view-only file is still shared");

        // Nothing of the owner is shared any more: the person's folder goes too.
        var fileShares = await (await owner.GetAsync($"/api/v1/shares?resourceType=file&resourceId={viewOnly}")).ReadJsonAsync();
        foreach (var s in fileShares.EnumerateArray()) await owner.DeleteAsync($"/api/v1/shares/{s.GetProperty("id").GetGuid()}");
        changes = await ChangesAsync(device.Client, cursor);
        changes.Should().Contain(c => c.GetProperty("itemId").GetGuid() == sharer.GetProperty("id").GetGuid() && c.GetProperty("op").GetString() == "access_revoked");
        (await SnapshotAsync(device.Client, "shared")).Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_creates_trees_and_reports_conflicts_per_operation()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        var device = await DeviceAsync(user);
        var fileId = (await StorageTests.UploadAsync(user, ws, null, "di-chuyen.txt", "x"u8.ToArray())).GetProperty("fileId").GetGuid();
        var results = await (await device.Client.PostAsJsonAsync("/api/v1/sync/batch", new
        {
            operations = new object[]
            {
                new { op = "create_folder", clientRef = "a", workspaceId = ws, name = "Cây" },
                new { op = "create_folder", clientRef = "b", workspaceId = ws, parentRef = "a", name = "Nhánh" },
                new { op = "move", clientRef = "m1", itemId = fileId, targetWorkspaceId = ws, targetRef = "b", ifMatch = "\"77\"" },
                new { op = "move", clientRef = "m2", itemId = fileId, targetWorkspaceId = ws, targetRef = "b", ifMatch = "*" },
                new { op = "explode", clientRef = "x" },
            },
        })).ReadJsonAsync();
        var r = results.EnumerateArray().ToList();
        r[0].GetProperty("status").GetInt32().Should().Be(200);
        r[1].GetProperty("item").GetProperty("parentId").GetGuid().Should().Be(r[0].GetProperty("item").GetProperty("id").GetGuid());
        r[2].GetProperty("status").GetInt32().Should().Be(412);
        r[2].GetProperty("item").GetProperty("id").GetGuid().Should().Be(fileId);
        r[3].GetProperty("item").GetProperty("parentId").GetGuid().Should().Be(r[1].GetProperty("item").GetProperty("id").GetGuid());
        r[4].GetProperty("status").GetInt32().Should().Be(400);
    }

    [Fact]
    public async Task Sync_hub_pushes_changes_available_within_seconds()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(user);
        var device = await DeviceAsync(user);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/sync"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(device.AccessToken);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            }).Build();
        hub.On<JsonElement>("changes_available", e =>
        {
            if (e.GetProperty("workspaceId").GetGuid() == ws) received.TrySetResult(e);
        });
        await hub.StartAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await (await user.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Đẩy thời gian thực" })).ReadJsonAsync();
        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        e.GetProperty("latestSeq").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_device_hears_at_once_when_its_user_is_added_to_a_workspace()
    {
        // Found on two real computers: a new group or share only showed up at the next periodic pass (up to 15 min).
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm mới " + Guid.NewGuid().ToString("N")[..6], type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var device = await DeviceAsync(member);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/sync"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(device.AccessToken);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            }).Build();
        hub.On<JsonElement>("changes_available", e =>
        {
            if (e.GetProperty("workspaceId").GetGuid() == ws) received.TrySetResult(e);
        });
        await hub.StartAsync(); // connected before being a member: not in that workspace's group

        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).EnsureSuccessStatusCode();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Remote_wipe_is_signed_with_the_device_secret_and_ends_the_device()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var device = await DeviceAsync(user);
        var admin = await factory.AdminClientAsync();
        var all = await (await admin.GetAsync("/api/v1/devices?pageSize=200")).ReadJsonAsync();
        all.GetProperty("items").EnumerateArray().Should().Contain(d => d.GetProperty("id").GetGuid() == device.DeviceId);

        (await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/wipe", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await (await device.Client.GetAsync("/api/v1/sync/devices/current")).ReadJsonAsync();
        status.GetProperty("status").GetString().Should().Be("wipe_pending");
        var wipe = status.GetProperty("wipe");
        var expected = DeviceCommands.Sign(device.Secret, device.DeviceId, wipe.GetProperty("requestedAt").GetDateTime().ToUniversalTime());
        wipe.GetProperty("signature").GetString().Should().Be(expected, "the client verifies the command with its own secret");

        (await device.Client.PostAsync("/api/v1/sync/devices/current/wiped", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await device.Client.GetAsync("/api/v1/sync/roots")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var mine = await (await user.GetAsync("/api/v1/devices/mine")).ReadJsonAsync();
        mine.EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == device.DeviceId).GetProperty("status").GetString().Should().Be("wiped");
    }

    [Fact]
    public async Task Policy_can_disable_sync_and_old_cursors_need_a_full_resync()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var device = await DeviceAsync(user);
        var policy = await (await device.Client.GetAsync("/api/v1/sync/policy")).ReadJsonAsync();
        policy.GetProperty("enabled").GetBoolean().Should().BeTrue();
        policy.GetProperty("viewOnlyAsLink").GetBoolean().Should().BeTrue();
        // Upload rules sent too: the client refuses a file before sending it.
        policy.GetProperty("uploadAllowedExtensions").EnumerateArray().Select(e => e.GetString()).Should().Contain("docx");
        policy.GetProperty("uploadMaxSizeBytes").GetInt64().Should().BePositive();

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(SettingKeys.SyncEnabled, false);
        try
        {
            (await device.Client.GetAsync("/api/v1/sync/roots")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var (_, challenge) = Pkce();
            (await user.PostAsJsonAsync("/api/v1/connect/authorize", new
            {
                clientId = SyncClients.DesktopClientId, redirectUri = Redirect, codeChallenge = challenge, codeChallengeMethod = "S256",
            })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(SettingKeys.SyncEnabled, true);
        }

        long min;
        using (var scope = factory.Services.CreateScope())
            min = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().ChangeJournal.MinAsync(c => (long?)c.Seq) ?? 1;
        (await device.Client.GetAsync($"/api/v1/sync/changes?cursor={min - 2}")).StatusCode.Should().Be(HttpStatusCode.Gone);
    }
}
