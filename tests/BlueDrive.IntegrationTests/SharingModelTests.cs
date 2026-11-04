using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Domain.Permissions;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>Perm-Phase 4: sharing on the new permission model (spec 5.1; items/{id}/permissions, inheritance, general-access).</summary>
[Collection(ApiCollection.Name)]
public class SharingModelTests(ApiFactory factory)
{
    private static object User(Guid id) => new { kind = "user", id };

    [Fact]
    public async Task Share_with_levels_list_sources_change_and_take_back()
    {
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (editor, editorId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (commenter, commenterId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (stranger, strangerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space chia sẻ mới", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { editorId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Chiến dịch" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, folder, "ke-hoach.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        levels.Keys.Should().Contain(["owner", "content_manager", "editor", "contributor", "commenter", "viewer", "view_only", "approver"]);

        // The owner shares the file "Người nhận xét" with someone outside the Space, until a date; they are told.
        var until = DateTime.UtcNow.AddDays(10);
        var granted = await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions",
            new { targets = new[] { User(commenterId) }, levelId = levels["commenter"], expiresAt = until, notify = true, message = "Nhờ góp ý" })).ReadJsonAsync();
        granted.GetProperty("granted").GetInt32().Should().Be(1);
        (await commenter.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await commenter.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={folder}")).StatusCode.Should().Be(HttpStatusCode.OK, "the shared file shows in its folder");
        (await commenter.GetAsync($"/api/v1/items/{folder}/permissions")).StatusCode.Should().Be(HttpStatusCode.NotFound, "nothing on the folder above");
        var notes = await (await commenter.GetAsync("/api/v1/notifications")).ReadJsonAsync();
        notes.GetProperty("items").EnumerateArray().Should().Contain(n => n.GetProperty("title").GetString()!.Contains("ke-hoach.png") && n.GetProperty("title").GetString()!.Contains("Người nhận xét"));

        var list = await (await owner.GetAsync($"/api/v1/items/{file}/permissions")).ReadJsonAsync();
        list.GetProperty("hasUniqueAcl").GetBoolean().Should().BeTrue("sharing one item gives it its own permissions, like SharePoint");
        list.GetProperty("canManage").GetBoolean().Should().BeTrue();
        var entries = list.GetProperty("entries").EnumerateArray().ToList();
        var mine = entries.Single(e => e.GetProperty("principalId").GetGuid() == commenterId);
        mine.GetProperty("levelName").GetString().Should().Be("Người nhận xét");
        mine.GetProperty("origin").GetString().Should().Be("direct");
        mine.GetProperty("editable").GetBoolean().Should().BeTrue();
        mine.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(until, TimeSpan.FromSeconds(2));
        entries.Should().Contain(e => e.GetProperty("origin").GetString() == "space" && e.GetProperty("originLabel").GetString() == "Thành viên Space");

        // A Space member with "Biên tập" may share, but not above their own rights; someone without rights cannot share at all.
        (await editor.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { User(strangerId) }, levelId = levels["owner"] }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await (await editor.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { User(strangerId) }, levelId = levels["viewer"], notify = false })).ReadJsonAsync())
            .GetProperty("granted").GetInt32().Should().Be(1);
        (await commenter.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { User(ownerId) }, levelId = levels["viewer"] }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Người nhận xét cannot share");
        (await editor.GetAsync($"/api/v1/items/{file}/permissions")).StatusCode.Should().Be(HttpStatusCode.OK);
        var strangerEntry = (await (await owner.GetAsync($"/api/v1/items/{file}/permissions")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("principalId").GetGuid() == strangerId).GetProperty("id").GetGuid();
        (await editor.DeleteAsync($"/api/v1/items/{file}/permissions/{strangerEntry}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "removing others needs Quản lý");

        // Change the level, then take it back.
        var entryId = mine.GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/v1/items/{file}/permissions/{entryId}", new { levelId = levels["view_only"], clearExpiry = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rights = await (await commenter.GetAsync($"/api/v1/items/{file}/effective-permissions")).ReadJsonAsync();
        rights.GetProperty("rights").EnumerateArray().Select(r => r.GetString()).Should().Contain("OpenItems").And.NotContain("Download");
        (await owner.DeleteAsync($"/api/v1/items/{file}/permissions/{entryId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await commenter.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // The stranger leaves the grant given to them.
        (await stranger.DeleteAsync($"/api/v1/items/{file}/permissions/{strangerEntry}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stranger.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Break_and_restore_inheritance_and_general_access_for_the_whole_unit()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (anyone, _, _) = await factory.CreateUserClientAsync("chi_xem");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space kế thừa", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Riêng" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());

        // Start empty (only who manages): the Space member loses access; restore brings it back.
        (await owner.PostAsync($"/api/v1/items/{folder}/inheritance?copy=false", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await member.GetAsync($"/api/v1/items/{folder}/permissions")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var why = await (await owner.GetAsync($"/api/v1/items/{folder}/permissions")).ReadJsonAsync();
        why.GetProperty("hasUniqueAcl").GetBoolean().Should().BeTrue();
        (await owner.DeleteAsync($"/api/v1/items/{folder}/inheritance")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await member.GetAsync($"/api/v1/items/{folder}/permissions")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.PostAsync($"/api/v1/items/{folder}/inheritance", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "Biên tập does not manage permissions");

        // "Mọi người trong đơn vị": anyone signed in may open it (here: view), then it is restricted again.
        (await anyone.GetAsync($"/api/v1/items/{folder}/permissions")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/general-access", new { mode = "organization", levelId = levels["viewer"] })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anyone.GetAsync($"/api/v1/items/{folder}/permissions")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await owner.GetAsync($"/api/v1/items/{folder}/permissions")).ReadJsonAsync()).GetProperty("generalAccess").GetString().Should().Be("organization");
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/general-access", new { mode = "restricted" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anyone.GetAsync($"/api/v1/items/{folder}/permissions")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Request_access_then_approve_with_another_level()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (asker, askerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "bao-cao-rieng.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());

        // Opened by its address: no access, the owner's name but not the file's.
        (await asker.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var info = await (await asker.GetAsync($"/api/v1/items/{file}/access-info")).ReadJsonAsync();
        info.GetProperty("hasAccess").GetBoolean().Should().BeFalse();
        info.GetProperty("ownerName").GetString().Should().NotBeNullOrEmpty();
        info.ToString().Should().NotContain("bao-cao-rieng");

        (await asker.PostAsJsonAsync($"/api/v1/items/{file}/access-requests", new { levelId = levels["editor"], message = "Cần sửa số liệu" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await asker.GetAsync($"/api/v1/items/{file}/access-info")).ReadJsonAsync()).GetProperty("requestPending").GetBoolean().Should().BeTrue();
        var notes = await (await owner.GetAsync("/api/v1/notifications")).ReadJsonAsync();
        notes.GetProperty("items").EnumerateArray().Should().Contain(n => n.GetProperty("type").GetString() == "access_request");
        (await asker.GetAsync($"/api/v1/items/{file}/access-requests")).StatusCode.Should().Be(HttpStatusCode.NotFound, "only who manages the item sees its requests");

        var pending = (await (await owner.GetAsync("/api/v1/access-requests")).ReadJsonAsync()).EnumerateArray().Single(r => r.GetProperty("itemId").GetGuid() == file);
        pending.GetProperty("requesterId").GetGuid().Should().Be(askerId);
        pending.GetProperty("requestedLevelName").GetString().Should().Be("Biên tập");
        // Approved, but as "Người xem".
        (await owner.PostAsJsonAsync($"/api/v1/access-requests/{pending.GetProperty("id").GetGuid()}/decide", new { approve = true, levelId = levels["viewer"] }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await asker.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var rights = await (await asker.GetAsync($"/api/v1/items/{file}/effective-permissions")).ReadJsonAsync();
        rights.GetProperty("rights").EnumerateArray().Select(r => r.GetString()).Should().Contain("Download").And.NotContain("EditItems");
        (await (await asker.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().Contain(n => n.GetProperty("type").GetString() == "access_request" && n.GetProperty("title").GetString()!.Contains("đã được duyệt"));
        (await owner.PostAsJsonAsync($"/api/v1/access-requests/{pending.GetProperty("id").GetGuid()}/decide", new { approve = false }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "already decided");
    }

    [Fact]
    public async Task Old_share_api_changes_only_its_own_principal()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (newModel, newModelId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (legacy, legacyId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "chia-se-hai-cach.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());

        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { User(newModelId) }, levelId = levels["commenter"], notify = false })).ReadJsonAsync();
        // Shared through the old API (mobile / scripts): the grant made with the new dialog stays.
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = file, targets = new[] { new { type = "user", id = legacyId } }, accessLevel = 1 })).ReadJsonAsync();
        (await newModel.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await legacy.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var share = (await (await owner.GetAsync($"/api/v1/shares?resourceType=file&resourceId={file}")).ReadJsonAsync()).EnumerateArray().Single();
        (await owner.DeleteAsync($"/api/v1/shares/{share.GetProperty("id").GetGuid()}")).IsSuccessStatusCode.Should().BeTrue();
        (await legacy.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await newModel.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = (await (await owner.GetAsync($"/api/v1/items/{file}/permissions")).ReadJsonAsync()).GetProperty("entries").EnumerateArray().ToList();
        entries.Should().ContainSingle(e => e.GetProperty("principalId").GetGuid() == newModelId).Which.GetProperty("levelName").GetString().Should().Be("Người nhận xét");
    }
}
