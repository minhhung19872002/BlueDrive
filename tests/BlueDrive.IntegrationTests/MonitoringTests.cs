using System.Net;
using System.Net.Http.Json;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #11–15: nhật ký, thống kê dung lượng, cảnh báo, báo cáo.</summary>
[Collection(ApiCollection.Name)]
public class MonitoringTests(ApiFactory factory)
{
    [Fact]
    public async Task Every_operation_is_logged_and_filterable()
    {
        var admin = await factory.AdminClientAsync();
        var (c, userId, username) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        var fileId = (await StorageTests.UploadAsync(c, ws, null, "nhat-ky.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await c.GetAsync($"/api/v1/files/{fileId}/download-url")).ReadJsonAsync();
        await (await c.PutAsJsonAsync($"/api/v1/files/{fileId}/name", new { name = "doi-ten" })).Content.ReadAsStringAsync();
        await (await c.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = fileId } } })).ReadJsonAsync();
        await (await c.PostAsJsonAsync("/api/v1/trash/restore", new { items = new[] { new { type = "file", id = fileId } } })).ReadJsonAsync();
        await c.PostAsync("/api/v1/auth/logout", null);

        var logs = await (await admin.PostAsJsonAsync("/api/v1/audit-logs/search?pageSize=100", new { userId })).ReadJsonAsync();
        var actions = logs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToHashSet();
        actions.Should().Contain(["login", "logout", "upload", "download", "rename", "delete", "restore", "password_change"]);

        var filtered = await (await admin.PostAsJsonAsync("/api/v1/audit-logs/search", new { actions = new[] { "rename" }, q = "doi-ten", from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd") })).ReadJsonAsync();
        filtered.GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("action").GetString() == "rename");
        filtered.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("username").GetString() == username);

        var verify = await (await admin.GetAsync("/api/v1/audit-logs/verify")).ReadJsonAsync();
        verify.GetProperty("valid").GetBoolean().Should().BeTrue();
        verify.GetProperty("checked").GetInt64().Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task Storage_statistics_by_user_unit_and_workspace()
    {
        var admin = await factory.AdminClientAsync();
        var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        await StorageTests.UploadAsync(c, ws, null, "thong-ke.png", StorageTests.Png);
        var byUser = await (await admin.GetAsync("/api/v1/storage-stats?by=user&pageSize=500")).ReadJsonAsync();
        var row = byUser.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == userId);
        row.GetProperty("usedBytes").GetInt64().Should().Be(StorageTests.Png.Length);
        row.GetProperty("files").GetInt32().Should().Be(1);
        (await (await admin.GetAsync("/api/v1/storage-stats?by=organization")).ReadJsonAsync()).GetProperty("total").GetInt32().Should().BeGreaterThan(0);
        (await admin.GetAsync("/api/v1/storage-stats?by=workspace")).StatusCode.Should().Be(HttpStatusCode.OK);
        var totals = await (await admin.GetAsync("/api/v1/storage-stats/totals")).ReadJsonAsync();
        totals.GetProperty("usedBytes").GetInt64().Should().BeGreaterThan(0);
        (await c.GetAsync("/api/v1/storage-stats")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unusual_downloads_raise_an_alert_that_can_be_acknowledged()
    {
        var admin = await factory.AdminClientAsync();
        await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["download.unusual_per_hour"] = 2 })).ReadJsonAsync();
        try
        {
            var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
            var ws = await StorageTests.PersonalWorkspaceAsync(c);
            var fileId = (await StorageTests.UploadAsync(c, ws, null, "nhieu.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
            for (var i = 0; i < 3; i++) await (await c.GetAsync($"/api/v1/files/{fileId}/download-url")).ReadJsonAsync();

            var alerts = await (await admin.PostAsJsonAsync("/api/v1/alerts/search", new { type = "unusual_download", acknowledged = false })).ReadJsonAsync();
            var alert = alerts.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("relatedUserId").GetGuid() == userId);
            alert.GetProperty("typeName").GetString().Should().Be("Tải xuống bất thường");
            await (await admin.PostAsJsonAsync("/api/v1/alerts/acknowledge", new[] { alert.GetProperty("id").GetGuid() })).ReadJsonAsync();
            var after = await (await admin.PostAsJsonAsync("/api/v1/alerts/search", new { type = "unusual_download", acknowledged = true })).ReadJsonAsync();
            after.GetProperty("items").EnumerateArray().Should().Contain(a => a.GetProperty("relatedUserId").GetGuid() == userId);
        }
        finally
        {
            await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["download.unusual_per_hour"] = 200 })).ReadJsonAsync();
        }
    }

    [Fact]
    public async Task Large_reports_run_in_background_and_notify_when_ready()
    {
        var admin = await factory.AdminClientAsync();
        var job = await (await admin.PostAsJsonAsync("/api/v1/reports/export", new { reportType = "audit_logs", format = "xlsx", parameters = new { actions = new[] { "login" } } })).ReadJsonAsync();
        job.GetProperty("status").GetString().Should().Be("queued");
        job.GetProperty("message").GetString().Should().Contain("đang xử lý");
        var id = job.GetProperty("id").GetGuid();

        // The Worker would pick this up; run the same command in-process.
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RunReportJobCommand(id));

        var done = await (await admin.GetAsync($"/api/v1/reports/{id}")).ReadJsonAsync();
        done.GetProperty("status").GetString().Should().Be("succeeded");
        var url = (await (await admin.GetAsync($"/api/v1/reports/{id}/download-url")).ReadJsonAsync()).GetProperty("url").GetString()!;
        var bytes = await factory.CreateClient().GetByteArrayAsync(url);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        wb.Worksheet(1).Cell(1, 1).GetString().Should().Contain("NHẬT KÝ");
        var notes = await (await admin.GetAsync("/api/v1/notifications")).ReadJsonAsync();
        notes.GetProperty("items").EnumerateArray().Should().Contain(n => n.GetProperty("type").GetString() == "report");

        foreach (var type in new[] { "storage_users", "storage_organizations", "alerts" })
        {
            var j = await (await admin.PostAsJsonAsync("/api/v1/reports/export", new { reportType = type, format = "pdf" })).ReadJsonAsync();
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RunReportJobCommand(j.GetProperty("id").GetGuid()));
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.ReportJobs.Where(r => r.Id == j.GetProperty("id").GetGuid()).Select(r => r.Status).FirstAsync()).Should().Be(Domain.Enums.JobStatus.Succeeded, type);
        }
    }
}
