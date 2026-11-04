using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>Perm-Phase 7: advanced administration (custom permission levels, labels, retention and holds, trash, quotas, offboarding, emergency access).</summary>
[Collection(ApiCollection.Name)]
public class AdvancedAdminTests(ApiFactory factory)
{
    [Fact]
    public async Task Custom_levels_and_changed_system_levels_apply_at_once_and_stay()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (user, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        var rights = (await (await admin.GetAsync("/api/v1/admin/permission-levels/rights")).ReadJsonAsync()).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        rights.Should().Contain(["ViewItems", "EditItems", "DeleteItems", "ManagePermissions"]);
        (await owner.PostAsJsonAsync("/api/v1/admin/permission-levels", new { name = "X", rights = new[] { "ViewItems" } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // "Biên tập không xóa": sees, opens, downloads, edits, renames — never deletes.
        var level = (await (await admin.PostAsJsonAsync("/api/v1/admin/permission-levels", new
        {
            name = "Biên tập không xóa", description = "Sửa nhưng không xóa", rights = new[] { "ViewItems", "OpenItems", "Download", "EditItems", "RenameMove" },
        })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "cap-tuy-bien.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = userId } }, levelId = level, notify = false })).ReadJsonAsync();
        (await user.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi-ten.png" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeFalse();

        // Changing the level changes it for everyone who holds it, now.
        (await admin.PutAsJsonAsync($"/api/v1/admin/permission-levels/{level}", new { name = "Biên tập không xóa", rights = new[] { "ViewItems", "OpenItems" } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "lai-doi.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/v1/admin/permission-levels/{level}")).StatusCode.Should().Be(HttpStatusCode.Conflict, "still in use");

        // A system level changed by the administrator stays so after the model is rebuilt; "Chủ sở hữu" cannot be changed; reset brings it back.
        var levels = (await (await admin.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l);
        var commenter = levels["commenter"].GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/admin/permission-levels/{levels["owner"].GetProperty("id").GetGuid()}", new { name = "X", rights = new[] { "ViewItems" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/v1/admin/permission-levels/{commenter}", new { name = "Người góp ý", rights = new[] { "ViewItems", "OpenItems", "Comment" } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).IsSuccessStatusCode.Should().BeTrue();
        var after = (await (await admin.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == commenter);
        after.GetProperty("name").GetString().Should().Be("Người góp ý");
        after.GetProperty("isCustomized").GetBoolean().Should().BeTrue();
        after.GetProperty("rightNames").EnumerateArray().Select(r => r.GetString()).Should().NotContain("Download");
        (await admin.PostAsync($"/api/v1/admin/permission-levels/{commenter}/reset", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await admin.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == commenter)
            .GetProperty("name").GetString().Should().Be("Người nhận xét");
    }

    [Fact]
    public async Task A_label_restricts_sharing_outsiders_and_lowering_it_needs_a_reason()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (outsider, outsiderId, _) = await factory.CreateUserClientAsync("bien_tap");
        var labels = (await (await owner.GetAsync("/api/v1/labels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("id").GetGuid());
        labels.Keys.Should().Contain(["Công khai", "Nội bộ", "Hạn chế"]);
        (await admin.PostAsJsonAsync("/api/v1/admin/labels", new { name = "Tối mật", color = "#ff0000", priority = 99 })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "not for state secrets");

        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space nhãn", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Hồ sơ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, folder, "nhay-cam.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = outsiderId } }, levelId = levels["editor"], notify = false })).ReadJsonAsync();
        (await outsider.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);

        // "Hạn chế" on the folder: the outsider only views (no download, a watermark), members keep working; no outside sharing.
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Hạn chế"] })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var label = await (await member.GetAsync($"/api/v1/items/{file}/label")).ReadJsonAsync();
        label.GetProperty("label").GetProperty("name").GetString().Should().Be("Hạn chế");
        label.GetProperty("source").GetString().Should().Be("Thư mục \"Hồ sơ\"");
        (await outsider.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var detail = await (await outsider.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync();
        detail.GetProperty("watermark").GetBoolean().Should().BeTrue();
        detail.GetProperty("labelName").GetString().Should().Be("Hạn chế");
        (await member.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        var policy = await (await owner.GetAsync($"/api/v1/items/{file}/sharing-policy")).ReadJsonAsync();
        policy.GetProperty("external").GetString().Should().Be("off");
        policy.GetProperty("externalSource").GetString().Should().Be("Nhãn \"Hạn chế\"");
        (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["viewer"], allowDownload = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Lowering: who manages the item, with a reason; raising needs neither.
        (await member.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Công khai"], reason = "Đã công bố" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Công khai"] })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Công khai"], reason = "Đã công bố" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await outsider.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.PutAsJsonAsync($"/api/v1/items/{file}/label", new { labelId = labels["Nội bộ"] })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var audit = await (await admin.PostAsJsonAsync("/api/v1/audit-logs/search?pageSize=50", new { actions = new[] { "label_changed" }, workspaceId = ws })).ReadJsonAsync();
        audit.GetProperty("items").EnumerateArray().Should().Contain(a => a.GetProperty("resourceId").GetGuid() == folder);
    }
}
