using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BlueDrive.Infrastructure.Media;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkiaSharp;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #21, #26–27, #29, #31, #33, #37–38, #41–43 and media previews.</summary>
[Collection(ApiCollection.Name)]
public class CollaborationTests(ApiFactory factory)
{
    private static string SignForDocumentServer(object payload)
        => new JsonWebTokenHandler().CreateToken(System.Text.Json.JsonSerializer.Serialize(payload),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.OnlyOfficeSecret)), SecurityAlgorithms.HmacSha256));

    [Fact]
    public async Task Desktop_sees_who_is_co_editing_a_document_until_it_is_closed()
    {
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (outsider, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await (await owner.PostAsJsonAsync("/api/v1/office/new", new { workspaceId = ws, kind = "docx", name = "Báo cáo quý" })).ReadJsonAsync())
            .GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 3 })).ReadJsonAsync();
        var anon = factory.CreateClient();
        async Task Callback(int status, params Guid[] users) =>
            (await (await anon.PostAsJsonAsync($"/api/office/callback/{fileId}", new { token = SignForDocumentServer(new { key = "k", status, users = users.Select(u => u.ToString()) }) }))
                .ReadJsonAsync()).GetProperty("error").GetInt32().Should().Be(0);

        await Callback(1, ownerId, friendId);
        var editing = await (await friend.GetAsync("/api/v1/sync/editing")).ReadJsonAsync();
        var row = editing.EnumerateArray().Single(e => e.GetProperty("fileId").GetGuid() == fileId);
        row.GetProperty("name").GetString().Should().Be("Báo cáo quý.docx");
        row.GetProperty("editors").GetArrayLength().Should().Be(2);
        (await (await outsider.GetAsync("/api/v1/sync/editing")).ReadJsonAsync()).EnumerateArray()
            .Should().NotContain(e => e.GetProperty("fileId").GetGuid() == fileId, "only people who can see the document");

        await Callback(1, friendId); // the owner left
        (await (await friend.GetAsync("/api/v1/sync/editing")).ReadJsonAsync()).EnumerateArray()
            .Single(e => e.GetProperty("fileId").GetGuid() == fileId).GetProperty("editors").GetArrayLength().Should().Be(1);
        await Callback(4); // closed without changes
        (await (await friend.GetAsync("/api/v1/sync/editing")).ReadJsonAsync()).EnumerateArray()
            .Should().NotContain(e => e.GetProperty("fileId").GetGuid() == fileId);
    }

    [Fact]
    public async Task New_document_opens_in_editor_and_callback_saves_a_new_version_then_restore()
    {
        var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        var created = await (await c.PostAsJsonAsync("/api/v1/office/new", new { workspaceId = ws, kind = "docx", name = "Kế hoạch truyền thông" })).ReadJsonAsync();
        var fileId = created.GetProperty("fileId").GetGuid();
        created.GetProperty("name").GetString().Should().Be("Kế hoạch truyền thông.docx");

        var cfg = await (await c.GetAsync($"/api/v1/office/config/{fileId}")).ReadJsonAsync();
        cfg.GetProperty("mode").GetString().Should().Be("edit");
        cfg.GetProperty("apiScriptUrl").GetString().Should().Be("http://public.test/office/web-apps/apps/api/documents/api.js");
        var config = cfg.GetProperty("config");
        var token = config.GetProperty("token").GetString()!;
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = false, RequireExpirationTime = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.OnlyOfficeSecret)),
        });
        validated.IsValid.Should().BeTrue("Document Server verifies the config signature");
        var docUrl = config.GetProperty("document").GetProperty("url").GetString()!;
        config.GetProperty("editorConfig").GetProperty("callbackUrl").GetString().Should().Be($"http://localhost/api/office/callback/{fileId}");

        // Simulate the Document Server: the "edited" document is served from a public URL that gets internalized.
        var other = await StorageTests.UploadAsync(c, ws, null, "ban-sua.docx", EditedPackage());
        var editedUrl = (await (await c.GetAsync($"/api/v1/files/{other.GetProperty("fileId").GetGuid()}/download-url")).ReadJsonAsync()).GetProperty("url").GetString()!;
        var payload = new { key = "k", status = 2, url = $"http://public.test/office{editedUrl}", users = new[] { userId.ToString() } };
        var anon = factory.CreateClient();
        var bad = await (await anon.PostAsJsonAsync($"/api/office/callback/{fileId}", new { status = 2, url = "http://x", token = "invalid" })).ReadJsonAsync();
        bad.GetProperty("error").GetInt32().Should().Be(1, "unsigned callbacks are rejected");
        var ok = await (await anon.PostAsJsonAsync($"/api/office/callback/{fileId}", new { token = SignForDocumentServer(payload) })).ReadJsonAsync();
        ok.GetProperty("error").GetInt32().Should().Be(0);

        var versions = await (await c.GetAsync($"/api/v1/files/{fileId}/versions")).ReadJsonAsync();
        versions.GetArrayLength().Should().Be(2);
        versions[0].GetProperty("changeNote").GetString().Should().Be("Chỉnh sửa trực tuyến");
        var v1 = versions[1].GetProperty("id").GetGuid();
        var restored = await (await c.PostAsync($"/api/v1/files/{fileId}/versions/{v1}/restore", null)).ReadJsonAsync();
        restored.GetProperty("versionNo").GetInt32().Should().Be(3);
        restored.GetProperty("changeNote").GetString().Should().Contain("phiên bản 1");
        docUrl.Should().StartWith("http://localhost/api/v1/content/");

        // A "Chỉ xem" user edits a document shared with them for editing, and only views one shared for viewing.
        var (viewer, viewerId, _) = await factory.CreateUserClientAsync("chi_xem");
        await (await c.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = viewerId } }, accessLevel = 1 })).ReadJsonAsync();
        (await (await viewer.GetAsync($"/api/v1/office/config/{fileId}")).ReadJsonAsync()).GetProperty("mode").GetString().Should().Be("view");
        await (await c.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = viewerId } }, accessLevel = 3 })).ReadJsonAsync();
        (await (await viewer.GetAsync($"/api/v1/office/config/{fileId}")).ReadJsonAsync()).GetProperty("mode").GetString().Should().Be("edit", "the edit share decides, whatever the role");
    }

    [Fact]
    public async Task Anyone_with_an_editing_link_co_edits_without_signing_in_and_the_version_is_the_link_owners()
    {
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await (await owner.PostAsJsonAsync("/api/v1/office/new", new { workspaceId = ws, kind = "docx", name = "Biên bản họp" })).ReadJsonAsync())
            .GetProperty("fileId").GetGuid();
        var ownerKey = (await (await owner.GetAsync($"/api/v1/office/config/{fileId}")).ReadJsonAsync()).GetProperty("config").GetProperty("document").GetProperty("key").GetString();
        var link = await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = fileId, allowDownload = true, allowEdit = true })).ReadJsonAsync();
        link.GetProperty("allowEdit").GetBoolean().Should().BeTrue();
        var token = link.GetProperty("token").GetString()!;

        var anon = factory.CreateClient();
        (await (await anon.GetAsync($"/api/v1/public/s/{token}")).ReadJsonAsync()).GetProperty("allowEdit").GetBoolean().Should().BeTrue();
        var access = Uri.EscapeDataString((await (await anon.PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { })).ReadJsonAsync()).GetProperty("accessToken").GetString()!);
        async Task<System.Text.Json.JsonElement> PublicConfig() =>
            await (await anon.GetAsync($"/api/v1/office/public-config/{token}?access={access}&guest={Uri.EscapeDataString("  Nguyễn   Lan ")}")).ReadJsonAsync();
        var cfg = await PublicConfig();
        cfg.GetProperty("mode").GetString().Should().Be("edit");
        var editor = cfg.GetProperty("config").GetProperty("editorConfig");
        editor.GetProperty("user").GetProperty("name").GetString().Should().Be("Nguyễn Lan (khách)");
        var guestId = editor.GetProperty("user").GetProperty("id").GetString()!;
        guestId.Should().StartWith($"link:{link.GetProperty("id").GetGuid():N}:");
        editor.GetProperty("callbackUrl").GetString().Should().Be($"http://localhost/api/office/callback/{fileId}");
        cfg.GetProperty("config").GetProperty("document").GetProperty("key").GetString().Should().Be(ownerKey, "same co-editing session as signed-in users");

        // The guest saved last: a new version in the owner's name, the audit names the link.
        var other = await StorageTests.UploadAsync(owner, ws, null, "ban-khach-sua.docx", EditedPackage());
        var editedUrl = (await (await owner.GetAsync($"/api/v1/files/{other.GetProperty("fileId").GetGuid()}/download-url")).ReadJsonAsync()).GetProperty("url").GetString()!;
        var saved = await (await anon.PostAsJsonAsync($"/api/office/callback/{fileId}",
            new { token = SignForDocumentServer(new { key = ownerKey, status = 2, url = $"http://public.test/office{editedUrl}", users = new[] { guestId } }) })).ReadJsonAsync();
        saved.GetProperty("error").GetInt32().Should().Be(0);
        var versions = await (await owner.GetAsync($"/api/v1/files/{fileId}/versions")).ReadJsonAsync();
        versions.GetArrayLength().Should().Be(2);
        versions[0].GetProperty("createdBy").GetGuid().Should().Be(ownerId);

        // Only someone who may edit can make an editing link.
        var (friend, friendId, _) = await factory.CreateUserClientAsync("bien_tap");
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = friendId } }, accessLevel = 1 })).ReadJsonAsync();
        (await friend.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = fileId, allowEdit = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Turned off by the administrator: links open read-only, new editing links are refused.
        var admin = await factory.AdminClientAsync();
        await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["share.link_allow_edit"] = false })).ReadJsonAsync();
        try
        {
            (await PublicConfig()).GetProperty("mode").GetString().Should().Be("view");
            (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = fileId, allowEdit = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["share.link_allow_edit"] = true })).ReadJsonAsync(); }

        // A link without editing stays read-only.
        await (await owner.PutAsJsonAsync($"/api/v1/share-links/{link.GetProperty("id").GetGuid()}", new { resourceType = "file", resourceId = fileId, allowDownload = true, allowEdit = false })).ReadJsonAsync();
        (await PublicConfig()).GetProperty("mode").GetString().Should().Be("view");
    }

    /// <summary>Any different OOXML (zip) package stands in for the edited document.</summary>
    private static byte[] EditedPackage()
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        wb.AddWorksheet("Sửa").Cell(1, 1).Value = "Nội dung đã chỉnh sửa";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Comments_with_mentions_attachments_edit_and_delete()
    {
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (colleague, colleagueId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (stranger, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var fileId = (await StorageTests.UploadAsync(owner, ws, null, "anh.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = fileId, targets = new[] { new { type = "user", id = colleagueId } }, accessLevel = 1 })).ReadJsonAsync();

        var colleagueWs = await StorageTests.PersonalWorkspaceAsync(colleague);
        var attachment = (await StorageTests.UploadAsync(colleague, colleagueWs, null, "ghi-chu.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var comment = await (await colleague.PostAsJsonAsync($"/api/v1/files/{fileId}/comments", new { content = "Ảnh rất đẹp @owner", mentions = new[] { ownerId }, attachmentFileIds = new[] { attachment } })).ReadJsonAsync();
        var commentId = comment.GetProperty("id").GetGuid();
        comment.GetProperty("attachments").GetArrayLength().Should().Be(1);

        (await stranger.GetAsync($"/api/v1/files/{fileId}/comments")).StatusCode.Should().Be(HttpStatusCode.NotFound, "only users who can view the file see the discussion");
        var reply = await (await owner.PostAsJsonAsync($"/api/v1/files/{fileId}/comments", new { parentId = commentId, content = "Cảm ơn" })).ReadJsonAsync();
        reply.GetProperty("parentId").GetGuid().Should().Be(commentId);

        // The owner can open the colleague's attachment through the comment.
        var att = await (await owner.GetAsync($"/api/v1/comments/{commentId}/attachments/{attachment}/url")).ReadJsonAsync();
        (await factory.CreateClient().GetByteArrayAsync(att.GetProperty("url").GetString()!)).Should().Equal(StorageTests.Png);
        (await (await owner.GetAsync("/api/v1/comments/attachments")).ReadJsonAsync()).EnumerateArray().Should().Contain(a => a.GetProperty("commentId").GetGuid() == commentId);

        var notes = await (await owner.GetAsync("/api/v1/notifications")).ReadJsonAsync();
        notes.GetProperty("items").EnumerateArray().Should().Contain(n => n.GetProperty("type").GetString() == "mention");

        (await owner.PutAsJsonAsync($"/api/v1/comments/{commentId}", new { content = "sửa hộ" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var edited = await (await colleague.PutAsJsonAsync($"/api/v1/comments/{commentId}", new { content = "Ảnh rất đẹp!" })).ReadJsonAsync();
        edited.GetProperty("isEdited").GetBoolean().Should().BeTrue();
        (await colleague.DeleteAsync($"/api/v1/comments/{commentId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await (await owner.GetAsync($"/api/v1/files/{fileId}/comments")).ReadJsonAsync();
        list.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == commentId).GetProperty("isDeleted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Image_thumbnail_is_generated_and_served_through_signed_url()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        using var bmp = new SKBitmap(800, 600);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.SteelBlue);
        var jpg = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        var up = await StorageTests.UploadAsync(c, ws, null, "banner.jpg", jpg);
        var fileId = up.GetProperty("fileId").GetGuid();

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IFileProcessor>().ProcessAsync(fileId, up.GetProperty("versionId").GetGuid(), CancellationToken.None);

        var detail = await (await c.GetAsync($"/api/v1/files/{fileId}")).ReadJsonAsync();
        detail.GetProperty("width").GetInt32().Should().Be(800);
        detail.GetProperty("scanStatus").GetString().Should().Be("skipped");
        var thumb = await factory.CreateClient().GetByteArrayAsync(detail.GetProperty("thumbnailUrl").GetString()!);
        using var decoded = SKBitmap.Decode(thumb);
        decoded.Width.Should().Be(320);
        var listing = await (await c.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync();
        listing.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == fileId).GetProperty("thumbnailUrl").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Transparent_png_thumbnail_has_white_background()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        // A logo-like PNG: transparent canvas with a green square in the middle.
        using var bmp = new SKBitmap(new SKImageInfo(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Green };
            canvas.DrawRect(150, 150, 100, 100, paint);
        }
        var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var up = await StorageTests.UploadAsync(c, ws, null, "logo.png", png);
        var fileId = up.GetProperty("fileId").GetGuid();

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IFileProcessor>().ProcessAsync(fileId, up.GetProperty("versionId").GetGuid(), CancellationToken.None);

        var detail = await (await c.GetAsync($"/api/v1/files/{fileId}")).ReadJsonAsync();
        using var thumb = SKBitmap.Decode(await factory.CreateClient().GetByteArrayAsync(detail.GetProperty("thumbnailUrl").GetString()!));
        var corner = thumb.GetPixel(5, 5);
        corner.Red.Should().BeGreaterThan(240, "transparent areas must not turn black");
        corner.Green.Should().BeGreaterThan(240);
        corner.Blue.Should().BeGreaterThan(240);
        var center = thumb.GetPixel(thumb.Width / 2, thumb.Height / 2);
        center.Green.Should().BeGreaterThan(center.Red);
    }

    [Fact]
    public async Task Group_summary_and_member_activity()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm tổng hợp", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        await StorageTests.UploadAsync(owner, ws, null, "a.png", StorageTests.Png);
        var mine = await StorageTests.UploadAsync(member, ws, null, "b.png", StorageTests.Png);
        await (await member.PutAsJsonAsync($"/api/v1/files/{mine.GetProperty("fileId").GetGuid()}/name", new { name = "b-moi" })).Content.ReadAsStringAsync();

        var summary = await (await owner.GetAsync($"/api/v1/workspaces/{ws}/summary?memberId={memberId}")).ReadJsonAsync();
        summary.GetProperty("totalFiles").GetInt32().Should().Be(2);
        summary.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == memberId).GetProperty("fileCount").GetInt32().Should().Be(1);
        summary.GetProperty("files").GetProperty("total").GetInt32().Should().Be(1);

        var activity = await (await owner.GetAsync($"/api/v1/workspaces/{ws}/activity?userId={memberId}")).ReadJsonAsync();
        activity.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()).Should().Contain(["upload", "rename"]);
    }
}
