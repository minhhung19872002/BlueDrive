using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Domain.Permissions;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task Four_kinds_of_link_with_levels_hashed_tokens_and_revocation()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (listed, listedId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (colleague, colleagueId, _) = await factory.CreateUserClientAsync("bien_tap");
        var anon = factory.CreateClient();
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "lien-ket-bon-loai.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        async Task<JsonElement> Save(object body, Guid? id = null) => id is { } x
            ? await (await owner.PutAsJsonAsync($"/api/v1/share-links/{x}", body)).ReadJsonAsync()
            : await (await owner.PostAsJsonAsync("/api/v1/share-links", body)).ReadJsonAsync();
        string Token(JsonElement link) => link.GetProperty("token").GetString()!;

        // "Người cụ thể": only the listed person, who gets "Người nhận xét" through the link (also in their own drive views).
        var specific = await Save(new { resourceType = "file", resourceId = file, scope = "specific", levelId = levels["commenter"], allowDownload = true, people = new[] { listedId } });
        specific.GetProperty("people").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(listedId);
        Token(specific).Should().NotBeNullOrEmpty();
        (await listed.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK, "a listed person's access comes from the model");
        (await (await listed.GetAsync($"/api/v1/items/{file}/effective-permissions")).ReadJsonAsync()).GetProperty("rights").EnumerateArray()
            .Select(r => r.GetString()).Should().Contain("Comment").And.NotContain("EditItems");
        (await colleague.PostAsJsonAsync($"/api/v1/public/s/{Token(specific)}/open", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await anon.PostAsJsonAsync($"/api/v1/public/s/{Token(specific)}/open", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await listed.PostAsJsonAsync($"/api/v1/public/s/{Token(specific)}/open", new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        // "Mọi người trong tổ chức": whoever signed in opens it gets its level; not before.
        var org = await Save(new { resourceType = "file", resourceId = file, scope = "organization", levelId = levels["viewer"], allowDownload = true });
        (await colleague.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await colleague.PostAsJsonAsync($"/api/v1/public/s/{Token(org)}/open", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await colleague.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = (await (await owner.GetAsync($"/api/v1/items/{file}/permissions")).ReadJsonAsync()).GetProperty("entries").EnumerateArray().ToList();
        entries.Where(e => e.GetProperty("principalKind").GetString() == "link").Should().HaveCount(2)
            .And.OnlyContain(e => !e.GetProperty("editable").GetBoolean() && e.GetProperty("originLabel").GetString() == "Qua liên kết chia sẻ");

        // "Người đã có quyền": just the address; "Bất kỳ ai": no sign-in, the link's level (Chỉ xem: no download).
        var existing = await Save(new { resourceType = "file", resourceId = file, scope = "existing", allowDownload = true });
        existing.GetProperty("levelId").ValueKind.Should().Be(JsonValueKind.Null);
        var anyone = await Save(new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["view_only"], allowDownload = true });
        anyone.GetProperty("allowDownload").GetBoolean().Should().BeFalse("Chỉ xem never downloads");
        (await anon.PostAsJsonAsync($"/api/v1/public/s/{Token(anyone)}/open", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["owner"] }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Only hashes are stored; the address is shown again from the server key.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlueDrive.Application.Abstractions.IAppDbContext>();
            var row = await db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == anyone.GetProperty("id").GetGuid());
            row.TokenHash.Should().HaveLength(64).And.NotContain(Token(anyone));
        }
        var list = (await (await owner.GetAsync($"/api/v1/share-links?resourceType=file&resourceId={file}")).ReadJsonAsync()).EnumerateArray().ToList();
        list.Should().HaveCount(4).And.OnlyContain(l => l.GetProperty("url").GetString()!.EndsWith("/s/" + l.GetProperty("token").GetString()));

        // Revoked: the access it gave goes with it.
        (await owner.DeleteAsync($"/api/v1/share-links/{org.GetProperty("id").GetGuid()}")).IsSuccessStatusCode.Should().BeTrue();
        (await colleague.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Turning "Người cụ thể" into "Người đã có quyền" takes the listed person's access away too.
        await Save(new { resourceType = "file", resourceId = file, scope = "existing", allowDownload = true }, specific.GetProperty("id").GetGuid());
        (await listed.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sharing_policies_strictest_tier_wins_and_the_space_rules_hold()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (_, otherMemberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (outsider, outsiderId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space chính sách", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId, otherMemberId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Nội bộ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, folder, "chinh-sach.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        object Anyone(DateTime? until = null) => new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["viewer"], allowDownload = true, expiresAt = until };

        // An "anyone" link: at most 30 days by default (none given = 30 days).
        var link = await (await owner.PostAsJsonAsync("/api/v1/share-links", Anyone())).ReadJsonAsync();
        link.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(5));
        (await owner.PostAsJsonAsync("/api/v1/share-links", Anyone(DateTime.UtcNow.AddDays(60)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The folder says "Chỉ nội bộ": no anyone link below it, and the policy says why.
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/external-sharing", new { external = "off" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var p = await (await owner.GetAsync($"/api/v1/items/{file}/sharing-policy")).ReadJsonAsync();
        p.GetProperty("external").GetString().Should().Be("off");
        p.GetProperty("externalSource").GetString().Should().Be("Thư mục \"Nội bộ\"");
        p.GetProperty("anyoneLinksAllowed").GetBoolean().Should().BeFalse();
        (await owner.PostAsJsonAsync("/api/v1/share-links", Anyone())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PutAsJsonAsync($"/api/v1/items/{folder}/external-sharing", new { external = "anyone" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/external-sharing", new { external = (string?)null })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The Space: stricter outside sharing, members share only with members, only its owners decide access requests.
        (await member.PutAsJsonAsync($"/api/v1/workspaces/{ws}/sharing-policy", new { external = "off", membersShareOutside = false, allowItemSharing = true, accessRequestDeciders = "space_owners" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/sharing-policy", new { external = "existing_guests", membersShareOutside = false, allowItemSharing = true, accessRequestDeciders = "space_owners" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await owner.GetAsync($"/api/v1/items/{file}/sharing-policy")).ReadJsonAsync()).GetProperty("externalSource").GetString().Should().Be("Space \"Space chính sách\"");
        (await owner.PostAsJsonAsync("/api/v1/share-links", Anyone())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { User(outsiderId) }, levelId = levels["viewer"], notify = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "members share only inside the Space");
        (await member.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { User(otherMemberId) }, levelId = levels["viewer"], notify = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { User(outsiderId) }, levelId = levels["viewer"], notify = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "who manages the Space is not bound by it");

        // No single items shared by members: neither grants on a folder nor stopping inheritance.
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/sharing-policy", new { external = "existing_guests", membersShareOutside = true, allowItemSharing = false, accessRequestDeciders = "space_owners" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await member.PostAsJsonAsync($"/api/v1/items/{folder}/permissions", new { targets = new[] { User(otherMemberId) }, levelId = levels["viewer"], notify = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var space = await (await owner.GetAsync($"/api/v1/workspaces/{ws}/sharing-policy")).ReadJsonAsync();
        space.GetProperty("allowItemSharing").GetBoolean().Should().BeFalse();
        space.GetProperty("accessRequestDeciders").GetString().Should().Be("space_owners");

        // Access requests go to the Space's owners.
        var other = (await StorageTests.UploadAsync(owner, ws, null, "rieng-tu.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        (await outsider.PostAsJsonAsync($"/api/v1/items/{other}/access-requests", new { levelId = levels["viewer"] })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await owner.GetAsync("/api/v1/access-requests")).ReadJsonAsync()).EnumerateArray().Should().Contain(r => r.GetProperty("itemId").GetGuid() == other);
        (await (await member.GetAsync("/api/v1/access-requests")).ReadJsonAsync()).EnumerateArray().Should().NotContain(r => r.GetProperty("itemId").GetGuid() == other);
    }

    [Fact]
    public async Task Expiry_is_announced_three_days_ahead_and_can_be_extended()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (guest, guestId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "sap-het-han.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions",
            new { targets = new[] { User(guestId) }, levelId = levels["viewer"], expiresAt = DateTime.UtcNow.AddDays(2), notify = false })).ReadJsonAsync();
        var link = await (await owner.PostAsJsonAsync("/api/v1/share-links",
            new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["viewer"], allowDownload = true, expiresAt = DateTime.UtcNow.AddDays(1) })).ReadJsonAsync();
        var entry = (await (await owner.GetAsync($"/api/v1/items/{file}/permissions")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("principalId").GetGuid() == guestId).GetProperty("id").GetGuid();

        async Task Run()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BlueDrive.Application.Features.Access.ExpiryReminders>().RunAsync(CancellationToken.None);
        }
        async Task<List<JsonElement>> Notes(HttpClient c) => (await (await c.GetAsync("/api/v1/notifications?pageSize=50")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("type").GetString() == "permission_expiry").ToList();

        await Run();
        await Run(); // once only
        var toGuest = (await Notes(guest)).Where(n => n.GetProperty("title").GetString()!.Contains("sẽ hết hạn")).ToList();
        toGuest.Should().ContainSingle().Which.GetProperty("link").GetString().Should().Be($"/renew/permission/{entry}");
        var toOwner = await Notes(owner);
        toOwner.Should().Contain(n => n.GetProperty("title").GetString()!.Contains("Quyền của") && n.GetProperty("link").GetString() == $"/renew/permission/{entry}");
        toOwner.Should().Contain(n => n.GetProperty("link").GetString() == $"/renew/link/{link.GetProperty("id").GetGuid()}");

        // The guest may only ask; the owner extends (from the old end date), and the guest is told.
        var seen = await (await guest.GetAsync($"/api/v1/renewals/permission/{entry}")).ReadJsonAsync();
        seen.GetProperty("canExtend").GetBoolean().Should().BeFalse();
        seen.GetProperty("canRequest").GetBoolean().Should().BeTrue();
        (await guest.PostAsJsonAsync($"/api/v1/renewals/permission/{entry}/extend", new { days = 30 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await guest.PostAsync($"/api/v1/renewals/permission/{entry}/request", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await owner.GetAsync("/api/v1/access-requests")).ReadJsonAsync()).EnumerateArray().Should().Contain(r => r.GetProperty("itemId").GetGuid() == file);
        var extended = await (await owner.PostAsJsonAsync($"/api/v1/renewals/permission/{entry}/extend", new { days = 30 })).ReadJsonAsync();
        extended.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(32), TimeSpan.FromMinutes(5));
        (await Notes(guest)).Should().Contain(n => n.GetProperty("title").GetString()!.Contains("đã được gia hạn"));
        (await (await owner.PostAsJsonAsync($"/api/v1/renewals/link/{link.GetProperty("id").GetGuid()}/extend", new { days = 90 })).ReadJsonAsync())
            .GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(5), "an anyone link never lives longer than the policy allows");

        // Once it has ended: told again.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlueDrive.Application.Abstractions.IAppDbContext>();
            await db.AclEntries.Where(e => e.Id == entry).ExecuteUpdateAsync(u => u.SetProperty(e => e.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }
        await Run();
        (await Notes(guest)).Should().Contain(n => n.GetProperty("title").GetString()!.Contains("đã hết hạn"));
    }
}
