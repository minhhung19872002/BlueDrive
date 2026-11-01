using System.Net;
using System.Net.Http.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #63–70 + admin dashboard.</summary>
[Collection(ApiCollection.Name)]
public class DashboardTests(ApiFactory factory)
{
    [Fact]
    public async Task Personal_dashboard_shows_quota_types_top_folders_and_recent()
    {
        var (c, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        await StorageTests.UploadAsync(c, ws, null, "a.png", StorageTests.Png, "Ảnh lớn/a.png");
        await StorageTests.UploadAsync(c, ws, null, "b.png", StorageTests.Png, "Ảnh lớn/Con/b.png");
        var form = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(new string('a', 500))), "file", "ghi-chu.txt" }, { new StringContent(ws.ToString()), "workspaceId" } };
        await (await c.PostAsync("/api/v1/files/upload", form)).ReadJsonAsync();

        var d = await (await c.GetAsync("/api/v1/dashboards/personal")).ReadJsonAsync();
        d.GetProperty("usedBytes").GetInt64().Should().Be(StorageTests.Png.Length * 2 + 500);
        d.GetProperty("quotaBytes").GetInt64().Should().BeGreaterThan(0);
        d.GetProperty("byType").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Should().Contain(["Hình ảnh", "Tài liệu"]);
        var top = d.GetProperty("topFolders");
        top[0].GetProperty("name").GetString().Should().Be("Ảnh lớn");
        top[0].GetProperty("bytes").GetInt64().Should().Be(StorageTests.Png.Length * 2, "folder size includes sub-folders");
        d.GetProperty("recent").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Group_dashboard_counts_membership_leadership_usage_and_recent()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var g1 = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm A", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var g2 = (await (await member.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm B của tôi", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{g1}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        await StorageTests.UploadAsync(owner, g1, null, "video-gia.png", StorageTests.Png);

        var d = await (await member.GetAsync("/api/v1/dashboards/group")).ReadJsonAsync();
        d.GetProperty("memberOf").GetInt32().Should().Be(2);
        d.GetProperty("leaderOf").GetInt32().Should().Be(1);
        d.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()).Should().BeEquivalentTo([g1, g2]);
        d.GetProperty("typesByGroup").EnumerateArray().Single(t => t.GetProperty("workspaceId").GetGuid() == g1).GetProperty("slices")[0].GetProperty("name").GetString().Should().Be("Hình ảnh");
        d.GetProperty("recent")[0].GetProperty("workspaceName").GetString().Should().Be("Nhóm A");
    }

    [Fact]
    public async Task Admin_dashboard_requires_permission()
    {
        var admin = await factory.AdminClientAsync();
        var d = await (await admin.GetAsync("/api/v1/dashboards/admin")).ReadJsonAsync();
        d.GetProperty("uploadsLast30Days").GetArrayLength().Should().Be(30);
        d.GetProperty("totals").GetProperty("users").GetInt32().Should().BeGreaterThan(0);
        var (viewer, _, _) = await factory.CreateUserClientAsync("chi_xem");
        (await viewer.GetAsync("/api/v1/dashboards/admin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
