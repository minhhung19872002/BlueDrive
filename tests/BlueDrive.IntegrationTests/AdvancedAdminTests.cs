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
}
