using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>Perm-Phase 5 (spec 6): Space templates, member groups with user groups and units, status (archive, system trash), settings.</summary>
[Collection(ApiCollection.Name)]
public class SpaceTests(ApiFactory factory)
{
    [Fact]
    public async Task Template_groups_status_and_settings_of_a_space()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (inGroup, inGroupId, _) = await factory.CreateUserClientAsync("bien_tap");

        // Created from the "Chiến dịch truyền thông" template: its folders are there.
        (await (await owner.GetAsync("/api/v1/workspaces/templates")).ReadJsonAsync()).EnumerateArray().Select(t => t.GetProperty("code").GetString())
            .Should().Contain(["blank", "department", "project", "campaign"]);
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Chiến dịch 26/12", type = "group", template = "campaign" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var items = (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString());
        items.Should().Contain(["Kịch bản", "Hình ảnh", "Video", "Ấn phẩm", "Báo cáo kết quả"]);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "anh-bia.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        // A user group as "Thành viên": its people work in the Space.
        var group = (await (await admin.PostAsJsonAsync("/api/v1/user-groups", new { name = "Nhóm xem Space " + Guid.NewGuid().ToString("N")[..6], memberIds = new[] { inGroupId } })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        (await inGroup.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await inGroup.PostAsJsonAsync($"/api/v1/workspaces/{ws}/group-members", new { principals = new[] { new { kind = "group", id = group } }, role = "editor" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/group-members", new { principals = new[] { new { kind = "group", id = group } }, role = "editor" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await inGroup.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await inGroup.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/group-members")).ReadJsonAsync()).EnumerateArray().Single().GetProperty("memberRole").GetString().Should().Be("editor");

        // Settings: no downloads for people who do not manage the Space; a cover image of the Space.
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/settings", new { allowDownload = false, allowSync = false, trashRetentionDays = 60, activityForMembers = false, coverFileId = file }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await inGroup.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await (await inGroup.GetAsync($"/api/v1/workspaces/{ws}/settings")).ReadJsonAsync();
        settings.GetProperty("canEdit").GetBoolean().Should().BeFalse();
        settings.GetProperty("trashRetentionDays").GetInt32().Should().Be(60);
        (await (await owner.GetAsync($"/api/v1/workspaces/{ws}")).ReadJsonAsync()).GetProperty("coverFileId").GetGuid().Should().Be(file);

        // Archived: read only, off the default list, still listed when asked.
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/status", new { status = "archived" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Mới" })).IsSuccessStatusCode.Should().BeFalse("an archived Space is read only");
        (await (await owner.GetAsync("/api/v1/workspaces")).ReadJsonAsync()).EnumerateArray().Should().NotContain(w => w.GetProperty("id").GetGuid() == ws);
        (await (await owner.GetAsync("/api/v1/workspaces?includeArchived=true")).ReadJsonAsync()).EnumerateArray()
            .Should().Contain(w => w.GetProperty("id").GetGuid() == ws && w.GetProperty("status").GetString() == "archived");
        (await owner.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/status", new { status = "active" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Deleted: to the system trash, nobody opens it; the administrator restores it.
        (await owner.DeleteAsync($"/api/v1/workspaces/{ws}")).IsSuccessStatusCode.Should().BeTrue();
        (await owner.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await inGroup.GetAsync($"/api/v1/workspaces/{ws}/items")).IsSuccessStatusCode.Should().BeFalse();
        var trash = (await (await admin.GetAsync("/api/v1/workspaces/trash")).ReadJsonAsync()).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == ws);
        trash.GetProperty("purgeAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(93), TimeSpan.FromMinutes(5));
        (await owner.GetAsync("/api/v1/workspaces/trash")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsync($"/api/v1/workspaces/trash/{ws}/restore", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Purged for good: deleted again, then removed with its content.
        (await owner.DeleteAsync($"/api/v1/workspaces/{ws}")).IsSuccessStatusCode.Should().BeTrue();
        (await admin.DeleteAsync($"/api/v1/workspaces/trash/{ws}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await admin.GetAsync("/api/v1/workspaces/trash")).ReadJsonAsync()).EnumerateArray().Should().NotContain(t => t.GetProperty("id").GetGuid() == ws);
        (await owner.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Views_shared_by_me_storage_and_a_breadcrumb_that_hides_what_is_above()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var secret = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Bí mật cấp trên" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var open = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = secret, name = "Chia sẻ cho bạn" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, open, "ban-ve.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var big = (await StorageTests.UploadAsync(owner, ws, null, "lon-hon.png", [.. StorageTests.Png, .. new byte[4096]])).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{open}/permissions", new { targets = new[] { new { kind = "user", id = friendId } }, levelId = levels["viewer"], notify = false })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = big, scope = "existing", allowDownload = true })).ReadJsonAsync();

        // The friend's path starts at what was shared: no name of the drive or of the folder above.
        var info = await (await friend.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={open}")).ReadJsonAsync();
        var crumbs = info.GetProperty("breadcrumb").EnumerateArray().ToList();
        crumbs.First().GetProperty("shared").GetBoolean().Should().BeTrue();
        crumbs.Select(c => c.GetProperty("name").GetString()).Should().Equal("Được chia sẻ với tôi", "Chia sẻ cho bạn");
        var detail = (await (await friend.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync()).ToString();
        detail.Should().NotContain("Bí mật cấp trên").And.NotContain("Tài liệu của");
        // The owner sees the whole path.
        (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={open}")).ReadJsonAsync()).GetProperty("breadcrumb").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).Should().Equal("Tài liệu của tôi", "Bí mật cấp trên", "Chia sẻ cho bạn");

        // "Tôi đã chia sẻ": the folder (one person) and the file (one link).
        var byMe = (await (await owner.GetAsync("/api/v1/shares/by-me")).ReadJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        byMe.Should().Contain(r => r.GetProperty("item").GetProperty("id").GetGuid() == open && r.GetProperty("people").GetInt32() == 1);
        byMe.Should().Contain(r => r.GetProperty("item").GetProperty("id").GetGuid() == big && r.GetProperty("links").GetInt32() == 1);
        (await (await friend.GetAsync("/api/v1/shares/by-me")).ReadJsonAsync()).GetProperty("total").GetInt32().Should().Be(0);

        // "Bộ nhớ": largest first.
        var usage = (await (await owner.GetAsync("/api/v1/storage/usage")).ReadJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        usage.First().GetProperty("item").GetProperty("id").GetGuid().Should().Be(big);
        usage.Select(u => u.GetProperty("totalBytes").GetInt64()).Should().BeInDescendingOrder();
    }
}
