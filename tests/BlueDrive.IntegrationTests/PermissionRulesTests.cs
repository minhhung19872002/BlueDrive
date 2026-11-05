using System.Net;
using System.Net.Http.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>
/// HSMT #8/#54: the role's functional permissions (doc.edit, doc.upload, workspace.view, workspace.edit) bound what
/// workspace ownership / membership grants; explicit shares and ACL entries stay deliberate exceptions.
/// </summary>
[Collection(ApiCollection.Name)]
public class PermissionRulesTests(ApiFactory factory)
{
    private async Task<string> RoleAsync(params string[] permissions)
    {
        var admin = await factory.AdminClientAsync();
        var code = "kt_" + Guid.NewGuid().ToString("N")[..8];
        var id = (await (await admin.PostAsJsonAsync("/api/v1/roles", new { code, name = "Vai trò " + code })).ReadJsonAsync()).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/roles/{id}/permissions", permissions)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return code;
    }

    private static async Task<Guid> GroupWorkspaceAsync(HttpClient owner, string name)
        => (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name, type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();

    private static async Task AddMemberAsync(HttpClient owner, Guid ws, Guid userId, string role)
        => await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { userId }, role })).ReadJsonAsync();

    [Fact]
    public async Task Editor_member_whose_role_is_view_only_can_read_but_not_change_unless_shared_for_editing()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (viewer, viewerId, _) = await factory.CreateUserClientAsync("chi_xem");
        var ws = await GroupWorkspaceAsync(owner, "Nhóm kiểm thử quyền sửa");
        await AddMemberAsync(owner, ws, viewerId, "editor");
        var poster = (await StorageTests.UploadAsync(owner, ws, null, "poster.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var doc = (await (await owner.PostAsJsonAsync("/api/v1/office/new", new { workspaceId = ws, kind = "docx", name = "Kế hoạch" })).ReadJsonAsync())
            .GetProperty("fileId").GetGuid();

        (await viewer.GetAsync($"/api/v1/files/{poster}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/v1/files/{poster}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PutAsJsonAsync($"/api/v1/files/{poster}/name", new { name = "doi-ten.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PutAsJsonAsync("/api/v1/files/metadata", new { fileIds = new[] { poster }, title = "Không được sửa" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var info = await (await viewer.GetAsync($"/api/v1/files/{poster}")).ReadJsonAsync();
        info.GetProperty("access").GetProperty("canEdit").GetBoolean().Should().BeFalse("the UI hides edit actions");
        (await (await viewer.GetAsync($"/api/v1/office/config/{doc}")).ReadJsonAsync()).GetProperty("mode").GetString().Should().Be("view");

        // Explicit "Chỉnh sửa" share: allowed even for a view-only role (commit 3155a67).
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = doc, targets = new[] { new { type = "user", id = viewerId } }, accessLevel = 3 })).ReadJsonAsync();
        (await (await viewer.GetAsync($"/api/v1/office/config/{doc}")).ReadJsonAsync()).GetProperty("mode").GetString().Should().Be("edit");
        (await viewer.PutAsJsonAsync($"/api/v1/files/{doc}/name", new { name = "Kế hoạch đã sửa" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await viewer.PutAsJsonAsync($"/api/v1/files/{poster}/name", new { name = "van-khong.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Moving_items_requires_doc_upload_even_for_an_editor_member()
    {
        var code = await RoleAsync("doc.view", "doc.edit", "workspace.view");
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (editor, editorId, _) = await factory.CreateUserClientAsync(code);
        var ws = await GroupWorkspaceAsync(owner, "Nhóm kiểm thử di chuyển");
        await AddMemberAsync(owner, ws, editorId, "editor");
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = (Guid?)null, name = "Lưu trữ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, null, "bang-tin.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        (await editor.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "bang-tin-moi.png" })).StatusCode.Should().Be(HttpStatusCode.NoContent, "doc.edit allows renaming");
        var move = await editor.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "file", id = file } }, targetWorkspaceId = ws, targetFolderId = folder });
        move.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await move.Content.ReadAsStringAsync()).Should().Contain("đưa tư liệu vào vị trí khác");

        await (await owner.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "file", id = file } }, targetWorkspaceId = ws, targetFolderId = folder })).ReadJsonAsync();
    }

    [Fact]
    public async Task Workspace_view_and_edit_permissions_gate_listing_and_management()
    {
        var noGroups = await RoleAsync("doc.view", "doc.upload", "doc.edit");
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (editorManager, editorManagerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (approverManager, approverManagerId, _) = await factory.CreateUserClientAsync("phe_duyet");
        var (hidden, hiddenId, _) = await factory.CreateUserClientAsync(noGroups);
        var ws = await GroupWorkspaceAsync(owner, "Nhóm kiểm thử quản lý");
        await AddMemberAsync(owner, ws, editorManagerId, "manager");
        await AddMemberAsync(owner, ws, approverManagerId, "manager");
        await AddMemberAsync(owner, ws, hiddenId, "viewer");

        // workspace.view: without it group workspaces are not listed (the personal one still is).
        var personal = await StorageTests.PersonalWorkspaceAsync(hidden);
        var listed = (await (await hidden.GetAsync("/api/v1/workspaces")).ReadJsonAsync()).EnumerateArray().ToList();
        listed.Should().NotContain(w => w.GetProperty("id").GetGuid() == ws);
        listed.Should().Contain(w => w.GetProperty("id").GetGuid() == personal);
        (await hidden.GetAsync($"/api/v1/workspaces/{ws}/members")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // A manager whose role cannot change the group does not administer it: no member list either.
        (await approverManager.GetAsync($"/api/v1/workspaces/{ws}/members")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editorManager.GetAsync($"/api/v1/workspaces/{ws}/members")).StatusCode.Should().Be(HttpStatusCode.OK);

        // workspace.edit: a manager whose role lacks it cannot change the workspace.
        var rename = new { name = "Nhóm đã đổi tên", type = "group" };
        var denied = await approverManager.PutAsJsonAsync($"/api/v1/workspaces/{ws}", rename);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("không có quyền chỉnh sửa nhóm tài nguyên");
        (await approverManager.PostAsync($"/api/v1/workspaces/{ws}/lock", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await approverManager.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { hiddenId }, role = "editor" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await (await editorManager.PutAsJsonAsync($"/api/v1/workspaces/{ws}", rename)).ReadJsonAsync();
        (await editorManager.PostAsync($"/api/v1/workspaces/{ws}/lock", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        ((int)(await editorManager.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { hiddenId }, role = "editor" })).StatusCode)
            .Should().Be(423, "a locked workspace accepts no membership changes");
        (await editorManager.PostAsync($"/api/v1/workspaces/{ws}/unlock", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AddMemberAsync(editorManager, ws, hiddenId, "editor");
    }
}
