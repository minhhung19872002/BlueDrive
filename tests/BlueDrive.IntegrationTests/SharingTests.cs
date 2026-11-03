using System.Net;
using System.Net.Http.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #19, #30, #49, #53–56 and the approval workflow.</summary>
[Collection(ApiCollection.Name)]
public class SharingTests(ApiFactory factory)
{
    [Fact]
    public async Task Internal_share_appears_in_shared_with_me_and_can_be_revoked()
    {
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("chi_xem");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await StorageTests.UploadAsync(owner, ws, null, "anh.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        (await friend.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 1, message = "Mời xem" })).ReadJsonAsync();
        (await friend.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await friend.GetAsync($"/api/v1/files/{fileId}/download-url")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "view-only share");
        (await friend.GetAsync($"/api/v1/files/{fileId}/download-url?inline=true")).StatusCode.Should().Be(HttpStatusCode.OK);

        var shared = await (await friend.GetAsync($"/api/v1/shares/with-me?sharedBy={ownerId}")).ReadJsonAsync();
        var row = shared.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("item").GetProperty("id").GetGuid() == fileId);
        row.GetProperty("message").GetString().Should().Be("Mời xem");
        var notes = await (await friend.GetAsync("/api/v1/notifications")).ReadJsonAsync();
        notes.GetProperty("items").EnumerateArray().Should().Contain(n => n.GetProperty("type").GetString() == "share");

        var access = await (await owner.GetAsync($"/api/v1/access/file/{fileId}")).ReadJsonAsync();
        access.GetProperty("entries").EnumerateArray().Should().Contain(e => e.GetProperty("source").GetString() == "share");

        (await owner.DeleteAsync($"/api/v1/shares/{row.GetProperty("shareId").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await friend.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Access_dialog_changes_and_removes_shares_of_the_item_but_not_inherited_ones()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Thư mục mới 2" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var fileId = (await StorageTests.UploadAsync(owner, ws, folder, "anh.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "folder", resourceId = folder, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 1 })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 3 })).ReadJsonAsync();

        var entries = (await (await owner.GetAsync($"/api/v1/access/file/{fileId}")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("source").GetString() == "share").ToList();
        entries.Should().HaveCount(2);
        var own = entries.Single(e => e.GetProperty("editable").GetBoolean());
        own.GetProperty("sourceName").GetString().Should().Be("Chia sẻ trực tiếp mục này");
        entries.Single(e => !e.GetProperty("editable").GetBoolean()).GetProperty("sourceName").GetString().Should().Contain("Thư mục mới 2");

        // Lower the file share to "view" …
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{fileId}", new { entries = Array.Empty<object>(), shares = new[] { new { id = own.GetProperty("id").GetGuid(), accessLevel = "view" } } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await friend.PutAsJsonAsync($"/api/v1/files/{fileId}/name", new { name = "doi-ten.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // … then remove it: the share inherited from the folder stays (managed on the folder).
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{fileId}", new { entries = Array.Empty<object>(), shares = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = (await (await owner.GetAsync($"/api/v1/access/file/{fileId}")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("source").GetString() == "share").ToList();
        after.Should().ContainSingle().Which.GetProperty("editable").GetBoolean().Should().BeFalse();
        (await friend.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        // A share level above "Chỉnh sửa" is refused.
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{fileId}", new { entries = Array.Empty<object>(), shares = new[] { new { id = Guid.NewGuid(), accessLevel = "manage" } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task File_can_stop_inheriting_permissions_like_sharepoint_and_restore_them()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("chi_xem");
        var (other, otherId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Chung" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var fileId = (await StorageTests.UploadAsync(owner, ws, folder, "rieng.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "folder", resourceId = folder, targets = new[] { new { type = "user", id = friendId }, new { type = "user", id = otherId } }, accessLevel = 3 })).ReadJsonAsync();
        async Task<List<Guid>> Listed(HttpClient c) => (await (await c.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={folder}")).ReadJsonAsync())
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        // Stop inheriting, copying today's access: both keep it, now as direct permissions of the file.
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{fileId}", new { entries = Array.Empty<object>(), breakInheritance = true, copyInheritedOnBreak = true }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var acl = await (await owner.GetAsync($"/api/v1/access/file/{fileId}")).ReadJsonAsync();
        acl.GetProperty("breakInheritance").GetBoolean().Should().BeTrue();
        acl.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("source").GetString() == "direct").Select(e => e.GetProperty("principalId").GetGuid())
            .Should().BeEquivalentTo([friendId, otherId]);
        acl.GetProperty("entries").EnumerateArray().Should().NotContain(e => e.GetProperty("fromParent").GetBoolean());

        // Remove one person: only the file is affected, the folder share stays.
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{fileId}", new
        {
            entries = new[] { new { principalType = "user", principalId = otherId, accessLevel = "edit", inherit = true } }, breakInheritance = true,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await friend.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Listed(friend)).Should().NotContain(fileId, "an item with unique permissions is hidden from those without access");
        (await other.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Listed(other)).Should().Contain(fileId);
        (await friend.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={folder}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Restore inheritance (unique permissions removed): the folder share applies again.
        (await owner.PutAsJsonAsync($"/api/v1/access/file/{fileId}", new { entries = Array.Empty<object>(), breakInheritance = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await friend.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Listed(friend)).Should().Contain(fileId);
        (await (await owner.GetAsync($"/api/v1/access/file/{fileId}")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .Should().Contain(e => e.GetProperty("fromParent").GetBoolean() && e.GetProperty("principalId").GetGuid() == friendId);
    }

    [Fact]
    public async Task Folder_share_is_inherited_by_subfolders_and_files_until_inheritance_is_broken()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (outsider, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var parent = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Dự án" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var child = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = parent, name = "Hồ sơ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var grandChild = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = child, name = "Ảnh" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var deepFile = (await StorageTests.UploadAsync(owner, ws, grandChild, "sau.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        // Share only the top folder with edit rights.
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "folder", resourceId = parent, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 3 })).ReadJsonAsync();

        // Sub-folders and files inherit: browse, open, edit, upload, create folders.
        var inParent = await (await friend.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={parent}")).ReadJsonAsync();
        inParent.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("id").GetGuid() == child);
        var inGrandChild = await (await friend.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={grandChild}")).ReadJsonAsync();
        inGrandChild.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("id").GetGuid() == deepFile);
        (await friend.GetAsync($"/api/v1/files/{deepFile}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await friend.PutAsJsonAsync($"/api/v1/files/{deepFile}/name", new { name = "sau-da-sua.png" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StorageTests.UploadAsync(friend, ws, grandChild, "ban-gui.png", StorageTests.Png)).GetProperty("fileId").GetGuid().Should().NotBeEmpty();
        (await friend.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = child, name = "Mới" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var folderAccess = await (await friend.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={grandChild}")).ReadJsonAsync();
        folderAccess.GetProperty("access").GetProperty("canEdit").GetBoolean().Should().BeTrue();
        (await outsider.GetAsync($"/api/v1/files/{deepFile}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The share dialog of a sub-folder / file lists the inherited share with its origin (read-only).
        var listed = await (await owner.GetAsync($"/api/v1/shares?resourceType=file&resourceId={deepFile}")).ReadJsonAsync();
        var viaParent = listed.EnumerateArray().Single(x => x.GetProperty("sharedWithId").GetGuid() == friendId);
        viaParent.GetProperty("inheritedFromId").GetGuid().Should().Be(parent);
        viaParent.GetProperty("inheritedFromName").GetString().Should().Be("Dự án");
        var acl = await (await owner.GetAsync($"/api/v1/access/folder/{grandChild}")).ReadJsonAsync();
        acl.GetProperty("entries").EnumerateArray().Should().Contain(e => e.GetProperty("source").GetString() == "share" && e.GetProperty("principalId").GetGuid() == friendId);

        // Breaking inheritance on "Hồ sơ" (without copying grants) stops the share below it.
        (await owner.PutAsJsonAsync($"/api/v1/access/folder/{child}", new { entries = Array.Empty<object>(), breakInheritance = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await friend.GetAsync($"/api/v1/files/{deepFile}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await friend.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={parent}")).StatusCode.Should().Be(HttpStatusCode.OK, "the shared folder itself stays visible");
        (await (await owner.GetAsync($"/api/v1/shares?resourceType=folder&resourceId={grandChild}")).ReadJsonAsync()).GetArrayLength().Should().Be(0);
        (await (await owner.GetAsync($"/api/v1/access/folder/{grandChild}")).ReadJsonAsync()).GetProperty("entries").EnumerateArray()
            .Should().NotContain(e => e.GetProperty("source").GetString() == "share");
    }

    [Fact]
    public async Task Link_cannot_be_switched_to_download_without_download_right()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await StorageTests.UploadAsync(owner, ws, null, "chi-xem.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 1 })).ReadJsonAsync();

        // The view-only friend may share a view-only link and edit its settings...
        var link = await (await friend.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = fileId, allowDownload = false, requireLogin = true })).ReadJsonAsync();
        var id = link.GetProperty("id").GetGuid();
        var token = link.GetProperty("token").GetString();
        var edited = await (await friend.PutAsJsonAsync($"/api/v1/share-links/{id}", new { resourceType = "file", resourceId = fileId, allowDownload = false, requireLogin = false })).ReadJsonAsync();
        edited.GetProperty("token").GetString().Should().Be(token, "editing keeps the same URL");
        edited.GetProperty("requireLogin").GetBoolean().Should().BeFalse();

        // ...but cannot turn downloads on afterwards.
        var res = await friend.PutAsJsonAsync($"/api/v1/share-links/{id}", new { resourceType = "file", resourceId = fileId, allowDownload = true, requireLogin = false });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Folder_acl_inherits_and_break_inheritance_stops_it()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (reader, readerId, _) = await factory.CreateUserClientAsync("chi_xem");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var parent = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Cha" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var child = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = parent, name = "Con" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var fileId = (await StorageTests.UploadAsync(owner, ws, child, "a.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        (await owner.PutAsJsonAsync($"/api/v1/access/folder/{parent}", new { entries = new[] { new { principalType = "user", principalId = readerId, accessLevel = 2, inherit = true } } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await reader.GetAsync($"/api/v1/files/{fileId}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK, "download inherited from the parent folder");

        // Break inheritance on the child and remove the copied grant: the reader loses access.
        (await owner.PutAsJsonAsync($"/api/v1/access/folder/{child}", new { entries = Array.Empty<object>(), breakInheritance = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var childAcl = await (await owner.GetAsync($"/api/v1/access/folder/{child}")).ReadJsonAsync();
        childAcl.GetProperty("breakInheritance").GetBoolean().Should().BeTrue();
        (await reader.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Public_link_with_password_limit_and_revoke()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await StorageTests.UploadAsync(owner, ws, null, "poster.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var link = await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = fileId, allowDownload = false, password = "1234", maxAccessCount = 2 })).ReadJsonAsync();
        var token = link.GetProperty("token").GetString()!;
        link.GetProperty("url").GetString().Should().EndWith($"/s/{token}");
        (await owner.GetAsync($"/api/v1/share-links/{link.GetProperty("id").GetGuid()}/qr")).Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        var anon = factory.CreateClient();
        var info = await (await anon.GetAsync($"/api/v1/public/s/{token}")).ReadJsonAsync();
        info.GetProperty("requiresPassword").GetBoolean().Should().BeTrue();
        (await anon.PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { password = "sai" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var grant = await (await anon.PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { password = "1234" })).ReadJsonAsync();
        var access = Uri.EscapeDataString(grant.GetProperty("accessToken").GetString()!);
        var view = await anon.GetAsync($"/api/v1/public/s/{token}/content?access={access}");
        view.StatusCode.Should().Be(HttpStatusCode.OK);
        (await view.Content.ReadAsByteArrayAsync()).Should().Equal(StorageTests.Png);
        (await anon.GetAsync($"/api/v1/public/s/{token}/content?access={access}&download=true")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await (await anon.PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { password = "1234" })).ReadJsonAsync();
        (await anon.PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { password = "1234" })).StatusCode.Should().Be(HttpStatusCode.Gone, "max 2 accesses");

        var link2 = await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = fileId, allowDownload = true })).ReadJsonAsync();
        (await owner.DeleteAsync($"/api/v1/share-links/{link2.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anon.GetAsync($"/api/v1/public/s/{link2.GetProperty("token").GetString()}")).StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Public_folder_link_lists_and_streams_children_and_require_login()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        await StorageTests.UploadAsync(owner, ws, null, "a.png", StorageTests.Png, "Album/Con/a.png");
        var album = (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Album").GetProperty("id").GetGuid();
        var link = await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "folder", resourceId = album, allowDownload = true, requireLogin = true })).ReadJsonAsync();
        var token = link.GetProperty("token").GetString()!;
        (await factory.CreateClient().PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var (other, _, _) = await factory.CreateUserClientAsync("chi_xem");
        var grant = await (await other.PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { })).ReadJsonAsync();
        var access = Uri.EscapeDataString(grant.GetProperty("accessToken").GetString()!);
        var root = await (await other.GetAsync($"/api/v1/public/s/{token}/items?access={access}")).ReadJsonAsync();
        var sub = root.GetProperty("items")[0].GetProperty("id").GetGuid();
        var inner = await (await other.GetAsync($"/api/v1/public/s/{token}/items?access={access}&folderId={sub}")).ReadJsonAsync();
        var fid = inner.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await other.GetAsync($"/api/v1/public/s/{token}/content?access={access}&fileId={fid}&download=true")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.GetAsync($"/api/v1/public/s/{token}/content?access={access}&fileId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approval_flow_hides_pending_files_from_viewers()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (uploader, uploaderId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (viewer, viewerId, _) = await factory.CreateUserClientAsync("chi_xem");
        var (approver, approverId, _) = await factory.CreateUserClientAsync("phe_duyet");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm duyệt", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { uploaderId }, role = "uploader" })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { viewerId }, role = "viewer" })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { approverId }, role = "approver" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Cần duyệt" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/folders/{folder}", new { requiresApproval = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var up = await StorageTests.UploadAsync(uploader, ws, folder, "tin.png", StorageTests.Png);
        up.GetProperty("approvalStatus").GetString().Should().Be("pending");
        var fileId = up.GetProperty("fileId").GetGuid();
        (await viewer.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var listing = await (await viewer.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={folder}")).ReadJsonAsync();
        listing.GetProperty("total").GetInt32().Should().Be(0);

        var pending = await (await approver.GetAsync("/api/v1/approvals")).ReadJsonAsync();
        pending.GetProperty("items").EnumerateArray().Should().Contain(p => p.GetProperty("item").GetProperty("id").GetGuid() == fileId);
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{fileId}/reject", new { reason = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await approver.PostAsync($"/api/v1/approvals/{fileId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await viewer.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var notes = await (await uploader.GetAsync("/api/v1/notifications")).ReadJsonAsync();
        notes.GetProperty("items").EnumerateArray().Should().Contain(n => n.GetProperty("type").GetString() == "approval");
    }
}
