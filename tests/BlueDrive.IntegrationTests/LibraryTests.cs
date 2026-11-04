using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>Perm-Phase 6 (spec 1.4): document libraries — major / minor versions, drafts, limits, check-out, a library of its own.</summary>
[Collection(ApiCollection.Name)]
public class LibraryTests(ApiFactory factory)
{
    private static async Task<HttpResponseMessage> RawUploadAsync(HttpClient c, Guid ws, Guid? folder, string name, byte[] bytes, Guid? target = null)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        form.Add(new StringContent(ws.ToString()), "workspaceId");
        if (folder.HasValue) form.Add(new StringContent(folder.Value.ToString()), "folderId");
        if (target.HasValue) form.Add(new StringContent(target.Value.ToString()), "targetFileId");
        return await c.PostAsync("/api/v1/files/upload", form);
    }

    private static byte[] Png(int extra) => [.. StorageTests.Png, .. new byte[extra]];

    private static async Task<List<JsonElement>> VersionsAsync(HttpClient c, Guid file) =>
        (await (await c.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync()).GetProperty("versions").EnumerateArray().ToList();

    private static string Label(JsonElement v) => $"{v.GetProperty("major").GetInt32()}.{v.GetProperty("minor").GetInt32()}";

    [Fact]
    public async Task Minor_versions_are_drafts_until_published_and_limits_hold()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (reader, readerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space phiên bản", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var library = (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/libraries")).ReadJsonAsync()).EnumerateArray().Single();
        library.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        var libId = library.GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/libraries/{libId}", new
        {
            name = "Tài liệu", versioning = "major_minor", majorLimit = 100, minorLimit = 2, requireCheckout = false, contentApproval = false,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var file = (await StorageTests.UploadAsync(owner, ws, null, "du-thao.png", Png(10))).GetProperty("fileId").GetGuid();
        Label((await VersionsAsync(owner, file)).Single()).Should().Be("0.1");
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = readerId } }, levelId = levels["viewer"], notify = false })).ReadJsonAsync();
        // Only a draft so far: a reader does not see it, in a list or by its address.
        (await reader.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await reader.GetAsync("/api/v1/shares/with-me")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().NotContain(i => i.GetProperty("item").GetProperty("id").GetGuid() == file);

        (await (await owner.PostAsync($"/api/v1/files/{file}/publish", null)).ReadJsonAsync()).GetProperty("version").GetString().Should().Be("1.0");
        (await reader.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // A new draft: the reader keeps getting 1.0 and does not see the draft in the versions.
        await StorageTests.UploadAsync(owner, ws, null, "du-thao.png", Png(20), target: file);
        Label((await VersionsAsync(owner, file)).First()).Should().Be("1.1");
        (await VersionsAsync(reader, file)).Select(Label).Should().Equal("1.0");
        var dl = await (await reader.GetAsync($"/api/v1/files/{file}/download-url")).ReadJsonAsync();
        dl.GetProperty("sizeBytes").GetInt64().Should().Be(StorageTests.Png.Length + 10, "the published version's content");
        var draftId = (await VersionsAsync(owner, file)).First().GetProperty("id").GetGuid();
        (await reader.GetAsync($"/api/v1/files/{file}/download-url?versionId={draftId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // At most 2 minor versions after a major one: 1.1, 1.2, 1.3 → 1.2 and 1.3 stay.
        await StorageTests.UploadAsync(owner, ws, null, "du-thao.png", Png(30), target: file);
        await StorageTests.UploadAsync(owner, ws, null, "du-thao.png", Png(40), target: file);
        (await VersionsAsync(owner, file)).Select(Label).Should().Equal("1.3", "1.2", "1.0");
        (await owner.PostAsync($"/api/v1/files/{file}/publish", null)).IsSuccessStatusCode.Should().BeTrue();
        Label((await VersionsAsync(reader, file)).First()).Should().Be("2.0");
    }

    [Fact]
    public async Task Check_out_keeps_others_from_changing_and_a_library_of_its_own_has_its_rules()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (a, aId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (b, bId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space check-out", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { aId, bId }, role = "editor" })).ReadJsonAsync();

        // A library of its own: only images, and a check-out is required before changing content.
        (await a.PostAsJsonAsync($"/api/v1/workspaces/{ws}/libraries", new { name = "Ảnh chiến dịch" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var lib = (await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/libraries", new { name = "Ảnh chiến dịch" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/libraries/{lib}", new
        {
            name = "Ảnh chiến dịch", versioning = "major", majorLimit = 100, minorLimit = 0, requireCheckout = true, contentApproval = false, allowedExtensions = new[] { "png" },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync($"/api/v1/workspaces/{ws}/libraries/{lib}", new
        {
            name = "Ảnh chiến dịch", versioning = "major", majorLimit = 100, minorLimit = 0, requireCheckout = true, contentApproval = false, allowedExtensions = new[] { "exe" },
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "never wider than the system");
        (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/libraries")).ReadJsonAsync()).EnumerateArray().Should().HaveCount(2);
        (await RawUploadAsync(a, ws, lib, "ghi-chu.txt", "ghi chu"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        var file = (await StorageTests.UploadAsync(a, ws, lib, "poster.png", Png(1))).GetProperty("fileId").GetGuid();

        (await RawUploadAsync(a, ws, null, "poster.png", Png(2), file)).StatusCode.Should().Be(HttpStatusCode.Locked, "the library requires a check-out");
        (await a.PostAsync($"/api/v1/files/{file}/checkout/check_out", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var detail = await (await b.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync();
        detail.GetProperty("checkedOutBy").GetGuid().Should().Be(aId);
        // Someone else: neither content nor name, and no check-out of their own.
        (await RawUploadAsync(b, ws, null, "poster.png", Png(3), file)).IsSuccessStatusCode.Should().BeFalse();
        (await b.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi-ten.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await b.PostAsync($"/api/v1/files/{file}/checkout/check_out", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await b.PostAsync($"/api/v1/files/{file}/checkout/force_check_in", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await RawUploadAsync(a, ws, null, "poster.png", Png(4), file)).IsSuccessStatusCode.Should().BeTrue();
        (await a.PostAsJsonAsync($"/api/v1/files/{file}/checkout/check_in", new { note = "Đổi màu nền" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await VersionsAsync(a, file)).First().GetProperty("changeNote").GetString().Should().Be("Đổi màu nền");
        // The Space's manager takes a check-out back.
        (await b.PostAsync($"/api/v1/files/{file}/checkout/check_out", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsync($"/api/v1/files/{file}/checkout/force_check_in", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await b.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().Contain(n => n.GetProperty("type").GetString() == "checkout");
    }
}
