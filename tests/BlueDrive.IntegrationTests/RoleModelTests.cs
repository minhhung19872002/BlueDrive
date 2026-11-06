using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>
/// What the system role decides, like Google Drive: in "Tài liệu của tôi" the owner may do everything whatever the role; access shared on
/// an item is what it says; only what Space membership gives is capped by the role ("Chỉ xem" only views a group's documents).
/// </summary>
[Collection(ApiCollection.Name)]
public class RoleModelTests(ApiFactory factory)
{
    private static async Task<HttpResponseMessage> UploadRawAsync(HttpClient c, Guid ws, Guid? folder, string name, Guid? target = null)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(StorageTests.Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        form.Add(new StringContent(ws.ToString()), "workspaceId");
        if (folder.HasValue) form.Add(new StringContent(folder.Value.ToString()), "folderId");
        if (target.HasValue) form.Add(new StringContent(target.Value.ToString()), "targetFileId");
        return await c.PostAsync("/api/v1/files/upload", form);
    }

    private async Task<Dictionary<string, Guid>> LevelsAsync(HttpClient c) =>
        (await (await c.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray()
        .ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());

    [Fact]
    public async Task A_view_only_account_does_everything_in_its_own_drive()
    {
        var (viewer, _, _) = await factory.CreateUserClientAsync("chi_xem");
        var (other, otherId, _) = await factory.CreateUserClientAsync("chi_xem");
        var ws = await StorageTests.PersonalWorkspaceAsync(viewer);

        var folder = (await (await viewer.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Của tôi" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await (await UploadRawAsync(viewer, ws, folder, "anh.png")).ReadJsonAsync()).GetProperty("fileId").GetGuid();
        (await viewer.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "anh-moi.png" })).IsSuccessStatusCode.Should().BeTrue();
        (await (await UploadRawAsync(viewer, ws, folder, "anh-moi.png", file)).ReadJsonAsync()).GetProperty("fileId").GetGuid().Should().Be(file, "a new version");
        (await viewer.PostAsJsonAsync("/api/v1/items/copy", new { items = new[] { new { type = "file", id = file } }, targetWorkspaceId = ws, targetFolderId = (Guid?)null }))
            .IsSuccessStatusCode.Should().BeTrue();

        // Shares its own file (with someone who then sees it).
        var levels = await LevelsAsync(viewer);
        (await viewer.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = otherId } }, levelId = levels["viewer"], notify = false }))
            .IsSuccessStatusCode.Should().BeTrue();
        (await other.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Trash of its own drive: delete, list, restore, purge.
        var item = new[] { new { type = "file", id = file } };
        (await viewer.PostAsJsonAsync("/api/v1/items/delete", new { items = item })).IsSuccessStatusCode.Should().BeTrue();
        (await (await viewer.GetAsync($"/api/v1/workspaces/{ws}/trash")).ReadJsonAsync()).GetProperty("total").GetInt32().Should().Be(1);
        (await viewer.PostAsJsonAsync("/api/v1/trash/restore", new { items = item })).IsSuccessStatusCode.Should().BeTrue();
        (await viewer.PostAsJsonAsync("/api/v1/items/delete", new { items = item })).IsSuccessStatusCode.Should().BeTrue();
        (await viewer.PostAsJsonAsync("/api/v1/trash/purge", item)).IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Access_shared_on_an_item_is_what_it_says_whatever_the_role()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (viewer, viewerId, _) = await factory.CreateUserClientAsync("chi_xem");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Cùng làm" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, folder, "ban-thao.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = await LevelsAsync(owner);

        // "Người xem": reads, cannot change nor pass it on.
        (await owner.PostAsJsonAsync($"/api/v1/items/{folder}/permissions", new { targets = new[] { new { kind = "user", id = viewerId } }, levelId = levels["viewer"], notify = false }))
            .IsSuccessStatusCode.Should().BeTrue();
        (await viewer.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = file, allowDownload = true, requireLogin = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // "Chỉnh sửa" on the folder: a "Chỉ xem" account edits there (renames, adds, deletes) — the role does not cap what was shared.
        var entry = (await (await owner.GetAsync($"/api/v1/items/{folder}/permissions")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("principalId").GetGuid() == viewerId).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/v1/items/{folder}/permissions/{entry}", new { levelId = levels["editor"] })).EnsureSuccessStatusCode();
        (await viewer.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi.png" })).IsSuccessStatusCode.Should().BeTrue();
        (await UploadRawAsync(viewer, ws, folder, "them.png")).IsSuccessStatusCode.Should().BeTrue();
        (await viewer.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeTrue();
        var info = await (await viewer.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={folder}")).ReadJsonAsync();
        info.GetProperty("access").GetProperty("canDelete").GetBoolean().Should().BeTrue();
        info.GetProperty("access").GetProperty("canShare").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Group_membership_is_capped_by_the_role()
    {
        var (lead, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (viewer, viewerId, _) = await factory.CreateUserClientAsync("chi_xem");
        var (editor, editorId, _) = await factory.CreateUserClientAsync("bien_tap");
        var space = (await (await lead.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm vai trò", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        // Both are "Chỉnh sửa" in the group; only the system role differs.
        await (await lead.PostAsJsonAsync($"/api/v1/workspaces/{space}/members", new { userIds = new[] { viewerId, editorId }, role = "editor" })).ReadJsonAsync();
        var file = (await StorageTests.UploadAsync(lead, space, null, "chung.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        // "Chỉ xem": views and downloads the group's documents, nothing more.
        (await viewer.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "khong.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await UploadRawAsync(viewer, space, null, "khong.png")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = file } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var info = await (await viewer.GetAsync($"/api/v1/workspaces/{space}/folder-info")).ReadJsonAsync();
        info.GetProperty("access").GetProperty("canUpload").GetBoolean().Should().BeFalse();
        info.GetProperty("access").GetProperty("canDelete").GetBoolean().Should().BeFalse();
        info.GetProperty("access").GetProperty("canShare").GetBoolean().Should().BeFalse();

        // "Chỉnh sửa": does what the group role allows.
        (await editor.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi-ten.png" })).IsSuccessStatusCode.Should().BeTrue();
        (await UploadRawAsync(editor, space, null, "moi.png")).IsSuccessStatusCode.Should().BeTrue();
    }
}
