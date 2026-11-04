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

    [Fact]
    public async Task Moving_between_a_space_and_a_drive_keeps_direct_grants_only()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (direct, directId, _) = await factory.CreateUserClientAsync("bien_tap");
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        var space = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space di chuyển", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{space}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = space, name = "Hồ sơ đi" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, space, folder, "ho-so.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        // Given its own permissions in the Space: a copy of the Space's members, plus someone directly.
        await (await owner.PostAsJsonAsync($"/api/v1/items/{folder}/permissions", new { targets = new[] { new { kind = "user", id = directId } }, levelId = levels["viewer"], notify = false })).ReadJsonAsync();
        (await member.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var drive = await StorageTests.PersonalWorkspaceAsync(owner);
        var items = new[] { new { type = "folder", id = folder } };

        // A member who does not manage the Space may not take things out of it.
        (await member.PostAsJsonAsync("/api/v1/items/move", new { items, targetWorkspaceId = await StorageTests.PersonalWorkspaceAsync(member), targetFolderId = (Guid?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // The Space may forbid it even to who manages it.
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{space}/settings", new { allowDownload = true, allowSync = true, activityForMembers = true, allowMoveOut = false }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await owner.PostAsJsonAsync("/api/v1/items/move-preview", new { items, targetWorkspaceId = drive, targetFolderId = (Guid?)null })).ReadJsonAsync())
            .GetProperty("blocked").GetBoolean().Should().BeTrue();
        (await owner.PostAsJsonAsync("/api/v1/items/move", new { items, targetWorkspaceId = drive, targetFolderId = (Guid?)null })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{space}/settings", new { allowDownload = true, allowSync = true, activityForMembers = true, allowMoveOut = true }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The warning says the Space's members lose access; the person given access directly is not listed.
        var preview = await (await owner.PostAsJsonAsync("/api/v1/items/move-preview", new { items, targetWorkspaceId = drive, targetFolderId = (Guid?)null })).ReadJsonAsync();
        preview.GetProperty("lose").EnumerateArray().Select(x => x.GetString()).Should().Contain(n => n!.Contains("Space di chuyển"));
        preview.GetProperty("lose").EnumerateArray().Select(x => x.GetString()).Should().NotContain(n => n!.Contains("bien") || n == "");
        (await owner.PostAsJsonAsync("/api/v1/items/move", new { items, targetWorkspaceId = drive, targetFolderId = (Guid?)null })).IsSuccessStatusCode.Should().BeTrue();
        (await member.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "what came from the Space stays there");
        (await direct.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK, "a direct grant goes with the item");

        // Out of someone's drive only by its owner: an editor of it may not move it into a Space.
        var mine = (await StorageTests.UploadAsync(owner, drive, null, "cua-toi.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/items/{mine}/permissions", new { targets = new[] { new { kind = "user", id = memberId } }, levelId = levels["editor"], notify = false })).ReadJsonAsync();
        (await member.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "file", id = mine } }, targetWorkspaceId = space, targetFolderId = (Guid?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var toSpace = await (await owner.PostAsJsonAsync("/api/v1/items/move-preview", new { items = new[] { new { type = "file", id = mine } }, targetWorkspaceId = space, targetFolderId = (Guid?)null })).ReadJsonAsync();
        toSpace.GetProperty("ownershipToSpace").GetBoolean().Should().BeTrue();
        (await owner.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "file", id = mine } }, targetWorkspaceId = space, targetFolderId = (Guid?)null }))
            .IsSuccessStatusCode.Should().BeTrue();
        (await member.GetAsync($"/api/v1/files/{mine}")).StatusCode.Should().Be(HttpStatusCode.OK, "now a Space member's");
    }

    [Fact]
    public async Task Ownership_moves_only_when_the_recipient_accepts_and_the_former_owner_keeps_editing()
    {
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (heir, heirId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (other, otherId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ownerWs = await StorageTests.PersonalWorkspaceAsync(owner);
        var heirWs = await StorageTests.PersonalWorkspaceAsync(heir);
        var file = (await StorageTests.UploadAsync(owner, ownerWs, null, "ban-giao.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = otherId } }, levelId = levels["viewer"], notify = false })).ReadJsonAsync();

        (await heir.PostAsJsonAsync("/api/v1/ownership-transfers", new { itemId = file, toUserId = otherId })).StatusCode.Should().Be(HttpStatusCode.NotFound, "not theirs (they cannot even see it)");
        var offer = (await (await owner.PostAsJsonAsync("/api/v1/ownership-transfers", new { itemId = file, toUserId = heirId })).ReadJsonAsync()).GetProperty("id").GetGuid();
        (await (await heir.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().Contain(n => n.GetProperty("link").GetString() == $"/ownership/{offer}");
        // Nothing changes before the recipient accepts; someone else cannot accept for them.
        (await heir.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/v1/ownership-transfers/{offer}/accept", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PostAsync($"/api/v1/ownership-transfers/{offer}/accept", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await heir.PostAsync($"/api/v1/ownership-transfers/{offer}/accept", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var detail = await (await heir.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync();
        detail.GetProperty("workspaceId").GetGuid().Should().Be(heirWs, "it is in the new owner's drive");
        (await other.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK, "direct grants go with it");
        var rights = await (await owner.GetAsync($"/api/v1/items/{file}/effective-permissions")).ReadJsonAsync();
        rights.GetProperty("rights").EnumerateArray().Select(r => r.GetString()).Should().Contain("EditItems").And.NotContain("ManagePermissions");
        (await heir.PostAsync($"/api/v1/ownership-transfers/{offer}/decline", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await (await owner.GetAsync("/api/v1/ownership-transfers?incoming=false")).ReadJsonAsync()).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == offer).GetProperty("status").GetString().Should().Be("accepted");
        _ = ownerId;
    }

    [Fact]
    public async Task Shortcuts_carry_no_rights_and_outlive_nothing_but_themselves()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ownerWs = await StorageTests.PersonalWorkspaceAsync(owner);
        var friendWs = await StorageTests.PersonalWorkspaceAsync(friend);
        var file = (await StorageTests.UploadAsync(owner, ownerWs, null, "ke-hoach-chung.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());

        // Nothing to make a shortcut to before it is shared.
        (await friend.PostAsJsonAsync("/api/v1/shortcuts", new { targetId = file })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var grant = await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = friendId } }, levelId = levels["viewer"], notify = false })).ReadJsonAsync();
        grant.GetProperty("granted").GetInt32().Should().Be(1);
        var shortcut = (await (await friend.PostAsJsonAsync("/api/v1/shortcuts", new { targetId = file })).ReadJsonAsync()).GetProperty("id").GetGuid();
        (await friend.PostAsJsonAsync("/api/v1/shortcuts", new { targetId = file })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Nobody adds a shortcut into someone else's drive.
        (await owner.PostAsJsonAsync("/api/v1/shortcuts", new { targetId = file, workspaceId = friendWs })).IsSuccessStatusCode.Should().BeFalse();

        async Task<JsonElement> Row() => (await (await friend.GetAsync($"/api/v1/workspaces/{friendWs}/items")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Single(i => i.TryGetProperty("shortcutId", out var s) && s.ValueKind == JsonValueKind.String && s.GetGuid() == shortcut);
        var row = await Row();
        row.GetProperty("id").GetGuid().Should().Be(file);
        row.GetProperty("shortcutState").GetString().Should().Be("ok");

        // Access taken back: the shortcut stays, but opens nothing.
        var entry = (await (await owner.GetAsync($"/api/v1/items/{file}/permissions")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("principalId").GetGuid() == friendId).GetProperty("id").GetGuid();
        (await owner.DeleteAsync($"/api/v1/items/{file}/permissions/{entry}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Row()).GetProperty("shortcutState").GetString().Should().Be("no_access");
        (await friend.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The target deleted: shown as deleted; removing the shortcut leaves the target alone.
        (await owner.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeTrue();
        (await Row()).GetProperty("shortcutState").GetString().Should().Be("deleted");
        (await friend.DeleteAsync($"/api/v1/shortcuts/{shortcut}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await friend.GetAsync($"/api/v1/workspaces/{friendWs}/items")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Where(i => i.TryGetProperty("shortcutId", out var s) && s.ValueKind == JsonValueKind.String).Should().BeEmpty();
        (await owner.PostAsJsonAsync("/api/v1/trash/restore", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeTrue();
        (await owner.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
