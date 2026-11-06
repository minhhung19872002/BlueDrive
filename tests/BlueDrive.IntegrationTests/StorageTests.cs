using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #18–24, #28, #39–40, #44–48, #57–62: lưu trữ lõi.</summary>
[Collection(ApiCollection.Name)]
public class StorageTests(ApiFactory factory)
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static async Task<Guid> PersonalWorkspaceAsync(HttpClient c)
        => (await (await c.GetAsync("/api/v1/auth/me")).ReadJsonAsync()).GetProperty("personalWorkspaceId").GetGuid();

    public static async Task<JsonElement> UploadAsync(HttpClient c, Guid ws, Guid? folder, string name, byte[] bytes, string? relativePath = null, Guid? target = null)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        form.Add(new StringContent(ws.ToString()), "workspaceId");
        if (folder.HasValue) form.Add(new StringContent(folder.Value.ToString()), "folderId");
        if (relativePath is not null) form.Add(new StringContent(relativePath), "relativePath");
        if (target.HasValue) form.Add(new StringContent(target.Value.ToString()), "targetFileId");
        return await (await c.PostAsync("/api/v1/files/upload", form)).ReadJsonAsync();
    }

    [Fact]
    public async Task Tus_resumable_upload_then_complete_creates_encrypted_file()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await PersonalWorkspaceAsync(c);
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Dân số và phát triển. ", 50_000)));
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        var create = new HttpRequestMessage(HttpMethod.Post, "/tus");
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", data.Length.ToString());
        create.Headers.Add("Upload-Metadata", $"filename {B64("báo cáo.txt")},workspaceId {B64(ws.ToString())}");
        create.Content = new ByteArrayContent([]);
        var created = await c.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var location = created.Headers.Location!.ToString();
        var tusId = location.TrimEnd('/').Split('/').Last();

        // Two chunks: simulates pause/resume.
        long offset = 0;
        foreach (var chunk in new[] { data[..400_000], data[400_000..] })
        {
            var patch = new HttpRequestMessage(HttpMethod.Patch, location);
            patch.Headers.Add("Tus-Resumable", "1.0.0");
            patch.Headers.Add("Upload-Offset", offset.ToString());
            patch.Content = new ByteArrayContent(chunk);
            patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
            (await c.SendAsync(patch)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            offset += chunk.Length;
        }
        var head = new HttpRequestMessage(HttpMethod.Head, location);
        head.Headers.Add("Tus-Resumable", "1.0.0");
        (await c.SendAsync(head)).Headers.GetValues("Upload-Offset").Single().Should().Be(data.Length.ToString());

        var done = await (await c.PostAsJsonAsync($"/api/v1/uploads/{tusId}/complete", new { fileName = "báo cáo.txt", workspaceId = ws, tags = new[] { "dân số" } })).ReadJsonAsync();
        var fileId = done.GetProperty("fileId").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var v = await db.FileVersions.FirstAsync(x => x.FileId == fileId);
        v.IsEncrypted.Should().BeTrue();
        var raw = await File.ReadAllBytesAsync(Path.Combine(factory.StorageRoot, "storage", v.StorageKey.Replace('/', Path.DirectorySeparatorChar)));
        Encoding.UTF8.GetString(raw).Should().NotContain("Dân số", "content is encrypted at rest");

        var url = (await (await c.GetAsync($"/api/v1/files/{fileId}/download-url")).ReadJsonAsync()).GetProperty("url").GetString()!;
        var anon = factory.CreateClient();
        var full = await anon.GetByteArrayAsync(url);
        full.Should().Equal(data);
        var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Range = new RangeHeaderValue(100_000, 100_099);
        var partial = await anon.SendAsync(range);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(data[100_000..100_100]);
    }

    [Fact]
    public async Task Blocked_and_mismatched_files_are_rejected()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await PersonalWorkspaceAsync(c);
        var form = new MultipartFormDataContent { { new ByteArrayContent([0x4D, 0x5A, 0x90, 0]), "file", "setup.exe" }, { new StringContent(ws.ToString()), "workspaceId" } };
        var exe = await c.PostAsync("/api/v1/files/upload", form);
        exe.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await exe.Content.ReadAsStringAsync()).Should().Contain("thực thi");

        var fake = new MultipartFormDataContent { { new ByteArrayContent([0x4D, 0x5A, 0x90, 0, 1, 2, 3, 4]), "file", "anh.jpg" }, { new StringContent(ws.ToString()), "workspaceId" } };
        (await c.PostAsync("/api/v1/files/upload", fake)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Folder_upload_keeps_tree_and_drive_operations_work()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await PersonalWorkspaceAsync(c);
        await UploadAsync(c, ws, null, "a.png", Png, "Chiến dịch 2026/Ảnh/a.png");
        await UploadAsync(c, ws, null, "b.png", Png, "Chiến dịch 2026/Ảnh/b.png");
        var root = await (await c.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync();
        var campaign = root.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Chiến dịch 2026");
        var campaignId = campaign.GetProperty("id").GetGuid();
        var sub = await (await c.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={campaignId}")).ReadJsonAsync();
        var photos = sub.GetProperty("items")[0];
        photos.GetProperty("name").GetString().Should().Be("Ảnh");
        var photosId = photos.GetProperty("id").GetGuid();
        var files = await (await c.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={photosId}&sort=name")).ReadJsonAsync();
        files.GetProperty("total").GetInt32().Should().Be(2);
        var fileA = files.GetProperty("items")[0].GetProperty("id").GetGuid();

        var info = await (await c.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={photosId}")).ReadJsonAsync();
        info.GetProperty("breadcrumb").GetArrayLength().Should().Be(3);

        // Rename keeps extension, metadata bulk update, favorite, recent.
        (await c.PutAsJsonAsync($"/api/v1/files/{fileA}/name", new { name = "Ảnh bìa" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await (await c.PutAsJsonAsync("/api/v1/files/metadata", new { fileIds = new[] { fileA }, title = "Ảnh bìa chiến dịch", tags = new[] { "dân số", "2026" } })).ReadJsonAsync();
        var detail = await (await c.GetAsync($"/api/v1/files/{fileA}")).ReadJsonAsync();
        detail.GetProperty("name").GetString().Should().Be("Ảnh bìa.png");
        detail.GetProperty("tags").GetArrayLength().Should().Be(2);
        detail.GetProperty("location").GetArrayLength().Should().Be(3);
        (await c.PutAsJsonAsync("/api/v1/favorites", new { itemType = "file", itemId = fileA, starred = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await c.GetAsync("/api/v1/favorites")).ReadJsonAsync()).EnumerateArray().Should().Contain(i => i.GetProperty("id").GetGuid() == fileA);
        (await (await c.GetAsync("/api/v1/recent")).ReadJsonAsync()).EnumerateArray().Should().Contain(i => i.GetProperty("id").GetGuid() == fileA);

        // Copy folder, move file to root, zip download.
        await (await c.PostAsJsonAsync("/api/v1/items/copy", new { items = new[] { new { type = "folder", id = photosId } }, targetWorkspaceId = ws, targetFolderId = (Guid?)null })).ReadJsonAsync();
        await (await c.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "file", id = fileA } }, targetWorkspaceId = ws, targetFolderId = (Guid?)null })).ReadJsonAsync();
        var rootAfter = await (await c.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync();
        rootAfter.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).Should().Contain(["Ảnh", "Ảnh bìa.png", "Chiến dịch 2026"]);
        var bad = await c.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "folder", id = campaignId } }, targetWorkspaceId = ws, targetFolderId = photosId });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var zipUrl = (await (await c.PostAsJsonAsync("/api/v1/downloads/zip", new { items = new[] { new { type = "folder", id = campaignId } } })).ReadJsonAsync()).GetProperty("url").GetString()!;
        var zipBytes = await factory.CreateClient().GetByteArrayAsync(zipUrl);
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["Chiến dịch 2026/Ảnh/b.png"]);
    }

    [Fact]
    public async Task Trash_restore_purge_and_quota_release()
    {
        var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await PersonalWorkspaceAsync(c);
        var folder = await (await c.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Tạm" })).ReadJsonAsync();
        var folderId = folder.GetProperty("id").GetGuid();
        var f = await UploadAsync(c, ws, folderId, "x.png", Png);
        var fileId = f.GetProperty("fileId").GetGuid();
        async Task<long> Used() => (await (await c.GetAsync("/api/v1/auth/me")).ReadJsonAsync()).GetProperty("usedBytes").GetInt64();
        (await Used()).Should().Be(Png.Length);

        await (await c.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "folder", id = folderId } } })).ReadJsonAsync();
        (await c.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var trash = await (await c.GetAsync($"/api/v1/workspaces/{ws}/trash")).ReadJsonAsync();
        var entry = trash.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == folderId);
        entry.GetProperty("deletedByName").GetString().Should().NotBeNullOrEmpty();
        (await Used()).Should().Be(Png.Length, "space is released only on permanent deletion");

        await (await c.PostAsJsonAsync("/api/v1/trash/restore", new { items = new[] { new { type = "folder", id = folderId } } })).ReadJsonAsync();
        (await c.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        await (await c.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = fileId } } })).ReadJsonAsync();
        await (await c.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "folder", id = folderId } } })).ReadJsonAsync();
        // Trash holding both a file (media type stored as text) and a folder (no media type) lists both.
        var mixed = (await (await c.GetAsync($"/api/v1/workspaces/{ws}/trash")).ReadJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        mixed.Single(i => i.GetProperty("id").GetGuid() == fileId).GetProperty("mediaType").GetString().Should().Be("image");
        mixed.Single(i => i.GetProperty("id").GetGuid() == folderId).GetProperty("mediaType").ValueKind.Should().Be(JsonValueKind.Null);
        // File's parent is gone (spec 8.1): restoring the file makes a folder of the same name again, the deleted one stays in the trash.
        await (await c.PostAsJsonAsync("/api/v1/trash/restore", new { items = new[] { new { type = "file", id = fileId } } })).ReadJsonAsync();
        var remade = (await (await c.GetAsync($"/api/v1/files/{fileId}")).ReadJsonAsync()).GetProperty("folderId").GetGuid();
        remade.Should().NotBe(folderId);
        (await (await c.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={remade}")).ReadJsonAsync()).GetProperty("name").GetString().Should().Be("Tạm");

        await (await c.PostAsJsonAsync("/api/v1/items/delete", new { items = new object[] { new { type = "file", id = fileId }, new { type = "folder", id = remade } } })).ReadJsonAsync();
        // Emptying the trash moves it to the second stage, which only an administrator sees for a user's own drive; emptying that deletes for good.
        await (await c.PostAsync($"/api/v1/workspaces/{ws}/trash/empty", null)).ReadJsonAsync();
        (await Used()).Should().Be(Png.Length, "the second stage still holds it");
        (await c.GetAsync($"/api/v1/workspaces/{ws}/trash?stage=2")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var admin = await factory.AdminClientAsync();
        (await (await admin.GetAsync($"/api/v1/workspaces/{ws}/trash?stage=2")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("id").GetGuid() == fileId);
        await (await admin.PostAsync($"/api/v1/workspaces/{ws}/trash/empty?stage=2", null)).ReadJsonAsync();
        (await Used()).Should().Be(0);
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.AnyAsync(a => a.UserId == userId && a.Action == Domain.Enums.AuditAction.Purge)).Should().BeTrue();
    }

    [Fact]
    public async Task Retention_job_purges_expired_trash_files_and_folders()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await PersonalWorkspaceAsync(c);
        var folderId = (await (await c.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Hết hạn" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var fileId = (await UploadAsync(c, ws, null, "het-han.png", Png)).GetProperty("fileId").GetGuid();
        var keptId = (await UploadAsync(c, ws, null, "moi-xoa.png", Png)).GetProperty("fileId").GetGuid();
        await (await c.PostAsJsonAsync("/api/v1/items/delete", new
        {
            items = new object[] { new { type = "folder", id = folderId }, new { type = "file", id = fileId }, new { type = "file", id = keptId } },
        })).ReadJsonAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var old = DateTime.UtcNow.AddDays(-400);
        await db.Files.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s.SetProperty(f => f.DeletedAt, old));
        await db.Folders.Where(f => f.Id == folderId).ExecuteUpdateAsync(s => s.SetProperty(f => f.DeletedAt, old));

        var purged = await scope.ServiceProvider.GetRequiredService<MediatR.ISender>().Send(new Application.Features.Storage.PurgeExpiredTrashCommand());

        purged.Should().BeGreaterThanOrEqualTo(2);
        (await db.Files.IgnoreQueryFilters().AnyAsync(f => f.Id == fileId)).Should().BeFalse();
        (await db.Folders.IgnoreQueryFilters().AnyAsync(f => f.Id == folderId)).Should().BeFalse();
        (await db.Files.IgnoreQueryFilters().AnyAsync(f => f.Id == keptId)).Should().BeTrue("deleted today, still within retention");
    }

    [Fact]
    public async Task Quota_is_enforced_before_upload()
    {
        var admin = await factory.AdminClientAsync();
        var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        await (await admin.PutAsJsonAsync("/api/v1/quotas/personal", new { userIds = new[] { userId }, quotaGb = 0.0000001 })).ReadJsonAsync(); // ~107 bytes
        var ws = await PersonalWorkspaceAsync(c);
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[500]), "file", "big.txt" }, { new StringContent(ws.ToString()), "workspaceId" } };
        var res = await c.PostAsync("/api/v1/files/upload", form);
        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Không đủ dung lượng");
    }

    [Fact]
    public async Task Group_workspace_members_lock_and_new_version()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (outsider, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm Truyền thông", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var up = await UploadAsync(member, ws, null, "poster.png", Png);
        var fileId = up.GetProperty("fileId").GetGuid();
        (await outsider.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await member.GetAsync("/api/v1/workspaces")).ReadJsonAsync()).EnumerateArray().Should().Contain(w => w.GetProperty("id").GetGuid() == ws);

        var v2 = await UploadAsync(member, ws, null, "poster.png", Png, target: fileId);
        v2.GetProperty("versionNo").GetInt32().Should().Be(2);

        (await owner.PostAsync($"/api/v1/workspaces/{ws}/lock", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var locked = await member.PutAsJsonAsync($"/api/v1/files/{fileId}/name", new { name = "khác" });
        ((int)locked.StatusCode).Should().Be(423);
        (await member.GetAsync($"/api/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK, "a locked workspace stays readable");
    }

    [Fact]
    public async Task Macro_enabled_office_files_upload_and_open_read_only_online()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await PersonalWorkspaceAsync(c);
        // Like SharePoint: .xlsm / .docm / .pptm are accepted (scanned like any file); online they are only shown, macros never run there.
        var zipHeader = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 };
        foreach (var name in new[] { "bang-tinh.xlsm", "van-ban.docm", "trinh-chieu.pptm" })
        {
            var up = await UploadAsync(c, ws, null, name, zipHeader);
            var id = up.GetProperty("fileId").GetGuid();
            var cfg = await (await c.GetAsync($"/api/v1/office/config/{id}")).ReadJsonAsync();
            cfg.GetProperty("mode").GetString().Should().Be("view", $"{name} opens read-only");
        }
        // Executables stay refused whatever the list says.
        var form = new MultipartFormDataContent { { new ByteArrayContent([0x4D, 0x5A, 0x90, 0x00]), "file", "chay.exe" }, { new StringContent(ws.ToString()), "workspaceId" } };
        (await c.PostAsync("/api/v1/files/upload", form)).IsSuccessStatusCode.Should().BeFalse();
    }
}
