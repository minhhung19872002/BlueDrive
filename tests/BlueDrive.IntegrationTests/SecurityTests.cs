using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.RateLimiting;

namespace BlueDrive.IntegrationTests;

/// <summary>Spec 13.7: IDOR, probing link tokens, raising one's own rights, guests listing people.</summary>
[Collection(ApiCollection.Name)]
public class SecurityTests(ApiFactory factory)
{
    [Fact]
    public async Task Ids_of_someone_elses_items_reveal_nothing_on_any_endpoint()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (other, otherId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Hồ sơ mật danh K7" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, folder, "bao-cao-bi-mat-k7.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        string[] reads =
        [
            $"/api/v1/files/{file}", $"/api/v1/files/{file}/download-url", $"/api/v1/files/{file}/versions", $"/api/v1/items/{file}/permissions",
            $"/api/v1/items/{file}/label", $"/api/v1/items/{file}/sharing-policy", $"/api/v1/workspaces/{ws}/items",
            $"/api/v1/workspaces/{ws}/folder-info?folderId={folder}", $"/api/v1/items/{folder}/permissions", $"/api/v1/workspaces/{ws}/trash",
        ];
        foreach (var url in reads)
        {
            (await owner.GetAsync(url)).IsSuccessStatusCode.Should().BeTrue($"{url} exists for the owner");
            var res = await other.GetAsync(url);
            res.IsSuccessStatusCode.Should().BeFalse($"{url} must not answer someone without access");
            (await res.Content.ReadAsStringAsync()).Should().NotContain("K7", $"{url} must not leak names");
        }
        // Writes with the ids: refused, nothing changes.
        (await other.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "da-doi.png" })).IsSuccessStatusCode.Should().BeFalse();
        (await other.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeFalse();
        (await other.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = otherId } }, levelId = Guid.NewGuid(), notify = false }))
            .IsSuccessStatusCode.Should().BeFalse();
        (await other.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "file", id = file } }, targetWorkspaceId = await StorageTests.PersonalWorkspaceAsync(other), targetFolderId = (Guid?)null }))
            .IsSuccessStatusCode.Should().BeFalse();
        (await (await owner.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync()).GetProperty("name").GetString().Should().Be("bao-cao-bi-mat-k7.png");
        // Search and "recent" of the other user do not count it.
        (await (await other.GetAsync("/api/v1/search?q=k7")).ReadJsonAsync()).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Link_tokens_answer_the_same_whatever_is_wrong_and_are_rate_limited()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "lien-ket.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        var link = await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["viewer"], allowDownload = true }))
            .ReadJsonAsync();
        var token = link.GetProperty("token").GetString()!;
        var anon = factory.CreateClient();
        (await anon.GetAsync($"/api/v1/public/s/{token}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.DeleteAsync($"/api/v1/share-links/{link.GetProperty("id").GetGuid()}")).IsSuccessStatusCode.Should().BeTrue();

        // A guessed token and one altered by a character look alike (no hint that a link is near); only who holds the exact token
        // learns it was revoked — without the item's name.
        var guessed = await anon.GetAsync($"/api/v1/public/s/{Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')}");
        var altered = await anon.GetAsync($"/api/v1/public/s/{token[..^1]}{(token[^1] == 'A' ? 'B' : 'A')}");
        guessed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        altered.StatusCode.Should().Be(HttpStatusCode.NotFound);
        static string Detail(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("detail").GetString()!;
        Detail(await altered.Content.ReadAsStringAsync()).Should().Be(Detail(await guessed.Content.ReadAsStringAsync()));
        var revoked = await anon.GetAsync($"/api/v1/public/s/{token}");
        revoked.StatusCode.Should().BeOneOf(HttpStatusCode.Gone, HttpStatusCode.NotFound);
        (await revoked.Content.ReadAsStringAsync()).Should().NotContain("lien-ket");

        // Every anonymous entry point is behind a rate limiter (tokens are looked up by their SHA-256 hash: one indexed read, same time).
        var api = typeof(Program).Assembly;
        api.GetType("BlueDrive.Api.Controllers.PublicShareController")!.GetCustomAttribute<EnableRateLimitingAttribute>().Should().NotBeNull();
        var auth = api.GetType("BlueDrive.Api.Controllers.AuthController")!;
        foreach (var name in new[] { "Login", "GuestCode", "GuestVerify", "ForgotPassword", "ResetPassword" })
            auth.GetMethod(name)!.GetCustomAttribute<EnableRateLimitingAttribute>().Should().NotBeNull($"{name} is rate limited");
    }

    [Fact]
    public async Task An_editor_cannot_raise_their_own_rights_nor_share_when_the_Space_forbids_it()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (editor, editorId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space chống leo quyền", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { editorId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Chung" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());

        // Making oneself owner / content manager: needs "Quản lý quyền", which Chỉnh sửa does not have.
        foreach (var level in new[] { "owner", "content_manager" })
            (await editor.PostAsJsonAsync($"/api/v1/items/{folder}/permissions", new { targets = new[] { new { kind = "user", id = editorId } }, levelId = levels[level], notify = false }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { editorId }, role = "manager" })).IsSuccessStatusCode.Should().BeFalse();
        (await editor.PostAsync($"/api/v1/items/{folder}/inheritance", null)).IsSuccessStatusCode.Should().BeFalse();

        // Sharing inside: allowed by default for Chỉnh sửa, until the Space turns item sharing off.
        (await editor.PostAsJsonAsync($"/api/v1/items/{folder}/permissions", new { targets = new[] { new { kind = "user", id = friendId } }, levelId = levels["viewer"], notify = false }))
            .IsSuccessStatusCode.Should().BeTrue();
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/sharing-policy", new { external = (string?)null, membersShareOutside = false, allowItemSharing = false, accessRequestDeciders = "item_managers" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var (third, thirdId, _) = await factory.CreateUserClientAsync("bien_tap");
        (await editor.PostAsJsonAsync($"/api/v1/items/{folder}/permissions", new { targets = new[] { new { kind = "user", id = thirdId } }, levelId = levels["viewer"], notify = false }))
            .IsSuccessStatusCode.Should().BeFalse("the Space allows no item sharing by members");
        (await third.GetAsync($"/api/v1/items/{folder}/permissions")).IsSuccessStatusCode.Should().BeFalse();
        (await friend.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={folder}")).IsSuccessStatusCode.Should().BeTrue("what was shared before stays");
    }
}
