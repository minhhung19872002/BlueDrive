using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #20, #32, #50–52: tìm kiếm có dấu/không dấu, lọc theo loại, danh mục, đơn vị đăng tải, thời gian; chỉ trả về tư liệu có quyền.</summary>
[Collection(ApiCollection.Name)]
public class SearchTests(ApiFactory factory)
{
    private static async Task<JsonElement> Search(HttpClient c, object criteria) => await (await c.PostAsJsonAsync("/api/v1/search", criteria)).ReadJsonAsync();

    [Fact]
    public async Task Unaccented_query_finds_vietnamese_names_and_respects_access()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (other, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var marker = "Zq" + Random.Shared.Next(100000, 999999);
        var up = await StorageTests.UploadAsync(owner, ws, null, $"Báo cáo Dân số {marker}.png", StorageTests.Png);
        var fileId = up.GetProperty("fileId").GetGuid();
        await (await owner.PutAsJsonAsync("/api/v1/files/metadata", new { fileIds = new[] { fileId }, description = "Tài liệu về kế hoạch hóa gia đình", tags = new[] { "truyền thông" } })).ReadJsonAsync();

        var res = await Search(owner, new { q = $"dan so {marker}" });
        var hit = res.GetProperty("items").EnumerateArray().Single();
        hit.GetProperty("item").GetProperty("id").GetGuid().Should().Be(fileId);
        hit.GetProperty("highlightedName").GetString().Should().Contain("<mark>Dân</mark> <mark>số</mark>");
        hit.GetProperty("location").GetString().Should().Be("Tài liệu của tôi");

        (await Search(owner, new { q = $"ke hoach hoa {marker[..4]}" })).GetProperty("items").EnumerateArray()
            .Should().Contain(h => h.GetProperty("item").GetProperty("id").GetGuid() == fileId, "description words are indexed and prefixes match");
        (await Search(owner, new { q = $"Dân Số {marker}" })).GetProperty("total").GetInt32().Should().Be(1, "accented input works too");
        (await Search(other, new { q = marker })).GetProperty("total").GetInt32().Should().Be(0, "other users cannot find private files");

        var quick = await (await owner.GetAsync($"/api/v1/search?q={marker}")).ReadJsonAsync();
        quick.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Filters_by_type_catalog_unit_date_and_saved_searches()
    {
        var admin = await factory.AdminClientAsync();
        var orgTree = await (await admin.GetAsync("/api/v1/organizations/tree")).ReadJsonAsync();
        var dept = orgTree[0].GetProperty("children")[1].GetProperty("id").GetGuid();
        var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        await (await admin.PostAsJsonAsync("/api/v1/users/bulk-assign", new { userIds = new[] { userId }, organizationId = dept })).ReadJsonAsync();
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        var marker = "Fx" + Random.Shared.Next(100000, 999999);
        var img = (await StorageTests.UploadAsync(c, ws, null, $"anh {marker}.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var txtForm = new MultipartFormDataContent { { new ByteArrayContent("văn bản"u8.ToArray()), "file", $"vb {marker}.txt" }, { new StringContent(ws.ToString()), "workspaceId" } };
        var txt = (await (await c.PostAsync("/api/v1/files/upload", txtForm)).ReadJsonAsync()).GetProperty("fileId").GetGuid();

        var catalogs = await (await c.GetAsync("/api/v1/catalogs/options")).ReadJsonAsync();
        var topic = catalogs.EnumerateArray().Single(x => x.GetProperty("code").GetString() == "chu_de").GetProperty("items")[0].GetProperty("id").GetGuid();
        await (await c.PutAsJsonAsync("/api/v1/files/metadata", new { fileIds = new[] { img }, catalogItemIds = new[] { topic } })).ReadJsonAsync();

        (await Search(c, new { q = marker, mediaTypes = new[] { "image" } })).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("id").GetGuid()).Should().Equal(img);
        (await Search(c, new { q = marker, catalogItemIds = new[] { topic } })).GetProperty("total").GetInt32().Should().Be(1);
        (await Search(c, new { q = marker, organizationId = dept })).GetProperty("total").GetInt32().Should().Be(2, "#51 filter by uploading unit");
        (await Search(c, new { q = marker, organizationId = orgTree[0].GetProperty("children")[0].GetProperty("id").GetGuid() })).GetProperty("total").GetInt32().Should().Be(0);
        // Dates are Vietnam dates (UTC+7): "tomorrow" in UTC is still today in Vietnam from 17:00 UTC.
        var today = DateTime.UtcNow.AddHours(7).Date;
        (await Search(c, new { q = marker, from = today.AddDays(1).ToString("yyyy-MM-dd") })).GetProperty("total").GetInt32().Should().Be(0);
        (await Search(c, new { q = marker, from = today.AddDays(-1).ToString("yyyy-MM-dd"), to = today.AddDays(1).ToString("yyyy-MM-dd"), extensions = new[] { "txt" } }))
            .GetProperty("items")[0].GetProperty("item").GetProperty("id").GetGuid().Should().Be(txt);

        await (await c.PostAsJsonAsync("/api/v1/search/saved", new { name = "Ảnh chủ đề", criteria = new { q = marker, mediaTypes = new[] { "image" } } })).ReadJsonAsync();
        var saved = await (await c.GetAsync("/api/v1/search/saved")).ReadJsonAsync();
        saved[0].GetProperty("criteria").GetProperty("mediaTypes")[0].GetString().Should().Be("image");
    }

    [Fact]
    public async Task Broad_terms_are_counted_up_to_the_limit_and_reported_as_more()
    {
        var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        var marker = "Cap" + Random.Shared.Next(100000, 999999);
        const int count = 1050;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < count; i++)
                db.Files.Add(new StoredFile
                {
                    WorkspaceId = ws, Name = $"Tờ rơi {marker} {i:0000}.txt", Extension = "txt", MimeType = "text/plain", MediaType = MediaType.Document,
                    SizeBytes = 10, ApprovalStatus = ApprovalStatus.Approved, ScanStatus = ScanStatus.Clean, CreatedBy = userId, CreatedAt = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        var first = await Search(c, new { q = marker, page = 1, pageSize = 20 });
        first.GetProperty("total").GetInt32().Should().Be(1000, "matches are counted up to the limit");
        first.GetProperty("totalCapped").GetBoolean().Should().BeTrue();
        first.GetProperty("items").GetArrayLength().Should().Be(20);

        var deep = await Search(c, new { q = marker, page = 51, pageSize = 20 });
        deep.GetProperty("total").GetInt32().Should().Be(1040, "past the limit the count reaches one page beyond the current one");
        deep.GetProperty("totalCapped").GetBoolean().Should().BeTrue();
        var next = await Search(c, new { q = marker, page = 52, pageSize = 20 });
        next.GetProperty("total").GetInt32().Should().Be(count, "the next page is the last one, so the count is exact");
        next.GetProperty("totalCapped").GetBoolean().Should().BeFalse();

        var last = await Search(c, new { q = marker, page = 53, pageSize = 20 });
        last.GetProperty("items").GetArrayLength().Should().Be(10);
        last.GetProperty("total").GetInt32().Should().Be(count, "the last, partly filled page gives the exact total");
        last.GetProperty("totalCapped").GetBoolean().Should().BeFalse();
    }
}
