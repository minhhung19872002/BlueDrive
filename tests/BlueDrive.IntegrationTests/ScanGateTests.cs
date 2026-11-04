using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>
/// HSMT #48 (quét mã độc): a file still waiting for its scan is readable only by its uploader, quarantined files by nobody
/// (download URL, signed content ticket, zip, public link), and files too large to scan stay downloadable.
/// </summary>
[Collection(ApiCollection.Name)]
public class ScanGateTests(ApiFactory factory)
{
    private async Task SetScanStatusAsync(Guid fileId, ScanStatus status)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Files.Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ScanStatus, status));
    }

    [Fact]
    public async Task Pending_file_is_served_only_to_its_uploader_and_infected_to_nobody()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (viewer, viewerId, _) = await factory.CreateUserClientAsync("chi_xem");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm kiểm thử quét virus", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { viewerId }, role = "editor" })).ReadJsonAsync();
        var clean = (await StorageTests.UploadAsync(owner, ws, null, "sach.png", StorageTests.Png, "Tài liệu/sach.png")).GetProperty("fileId").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, null, "moi-tai.png", StorageTests.Png, "Tài liệu/moi-tai.png")).GetProperty("fileId").GetGuid();
        var folder = (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "Tài liệu").GetProperty("id").GetGuid();
        // A ticket handed out before the file went back to "pending" must be re-checked when used.
        var earlyUrl = (await (await viewer.GetAsync($"/api/v1/files/{file}/download-url")).ReadJsonAsync()).GetProperty("url").GetString()!;
        await SetScanStatusAsync(file, ScanStatus.Pending);

        (await (await owner.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync()).GetProperty("contentBlocked").GetBoolean().Should().BeFalse();
        (await owner.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK, "the uploader may check their own upload");

        (await (await viewer.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync()).GetProperty("contentBlocked").GetBoolean().Should().BeTrue();
        var blocked = await viewer.GetAsync($"/api/v1/files/{file}/download-url");
        blocked.StatusCode.Should().Be((HttpStatusCode)423);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("đang được kiểm tra virus");
        (await factory.CreateClient().GetAsync(earlyUrl)).StatusCode.Should().Be((HttpStatusCode)423);

        var zipUrl = (await (await viewer.PostAsJsonAsync("/api/v1/downloads/zip", new { items = new[] { new { type = "folder", id = folder } } })).ReadJsonAsync()).GetProperty("url").GetString()!;
        using (var zip = new ZipArchive(new MemoryStream(await factory.CreateClient().GetByteArrayAsync(zipUrl))))
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["Tài liệu/sach.png"]);

        var token = (await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "folder", resourceId = folder, allowDownload = true })).ReadJsonAsync())
            .GetProperty("token").GetString()!;
        var anon = factory.CreateClient();
        var access = Uri.EscapeDataString((await (await anon.PostAsJsonAsync($"/api/v1/public/s/{token}/open", new { })).ReadJsonAsync()).GetProperty("accessToken").GetString()!);
        (await anon.GetAsync($"/api/v1/public/s/{token}/content?access={access}&fileId={file}")).StatusCode.Should().Be((HttpStatusCode)423);
        (await anon.GetAsync($"/api/v1/public/s/{token}/content?access={access}&fileId={clean}")).StatusCode.Should().Be(HttpStatusCode.OK);

        await SetScanStatusAsync(file, ScanStatus.Infected);
        var quarantined = await owner.GetAsync($"/api/v1/files/{file}/download-url");
        quarantined.StatusCode.Should().Be((HttpStatusCode)423, "not even the uploader gets a quarantined file");
        (await quarantined.Content.ReadAsStringAsync()).Should().Contain("cách ly");
        (await anon.GetAsync($"/api/v1/public/s/{token}/content?access={access}&fileId={file}")).StatusCode.Should().Be((HttpStatusCode)423);

        await SetScanStatusAsync(file, ScanStatus.TooLarge);
        var detail = await (await viewer.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync();
        detail.GetProperty("scanStatus").GetString().Should().Be("too_large");
        detail.GetProperty("contentBlocked").GetBoolean().Should().BeFalse("files above the scan limit download with a warning");
        (await viewer.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
