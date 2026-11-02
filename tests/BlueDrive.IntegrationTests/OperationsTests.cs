using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>Service quality items 3.5 (backup), 4.2 (data hand-over), 5.1 (support/SLA), 5.2 (change log, status).</summary>
[Collection(ApiCollection.Name)]
public class OperationsTests(ApiFactory factory)
{
    [Fact]
    public async Task Support_ticket_lifecycle_with_sla()
    {
        var admin = await factory.AdminClientAsync();
        var (user, userId, _) = await factory.CreateUserClientAsync("chi_xem");
        var t = await (await user.PostAsJsonAsync("/api/v1/tickets", new { title = "Không mở được video", content = "Video tập huấn báo lỗi", priority = "khan_cap" })).ReadJsonAsync();
        var id = t.GetProperty("id").GetGuid();
        t.GetProperty("code").GetString().Should().StartWith("HT-");
        (t.GetProperty("dueAt").GetDateTime() - t.GetProperty("receivedAt").GetDateTime()).TotalHours.Should().BeApproximately(4, 0.01);

        // A reporter only sees their own tickets; the admin sees all.
        var (other, _, _) = await factory.CreateUserClientAsync("chi_xem");
        (await other.GetAsync($"/api/v1/tickets/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var all = await (await admin.PostAsJsonAsync("/api/v1/tickets/search", new { })).ReadJsonAsync();
        all.GetProperty("items").EnumerateArray().Should().Contain(x => x.GetProperty("id").GetGuid() == id);

        var adminId = (await (await admin.GetAsync("/api/v1/auth/me")).ReadJsonAsync()).GetProperty("id").GetGuid();
        var assigned = await (await admin.PutAsJsonAsync($"/api/v1/tickets/{id}", new { assigneeId = adminId, status = "dang_xu_ly", note = "Đã tiếp nhận" })).ReadJsonAsync();
        assigned.GetProperty("assigneeName").GetString().Should().NotBeNullOrEmpty();
        var resolved = await (await admin.PutAsJsonAsync($"/api/v1/tickets/{id}", new { status = "da_xu_ly", resolution = "Đã chuyển mã video" })).ReadJsonAsync();
        resolved.GetProperty("isOverdue").GetBoolean().Should().BeFalse();
        resolved.GetProperty("events").GetArrayLength().Should().Be(3);
        (await (await user.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Should().Contain(n => n.GetProperty("type").GetString() == "ticket");

        var sla = await (await admin.GetAsync("/api/v1/tickets/sla")).ReadJsonAsync();
        sla.GetProperty("resolved").GetInt32().Should().BeGreaterThan(0);
        var export = await admin.PostAsJsonAsync("/api/v1/tickets/sla/export?format=pdf", new { });
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        userId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Change_log_and_system_status()
    {
        var admin = await factory.AdminClientAsync();
        await (await admin.PostAsJsonAsync("/api/v1/change-logs", new { version = "1.0.1", changeType = "fix", description = "Sửa lỗi hiển thị" })).ReadJsonAsync();
        var logs = await (await admin.GetAsync("/api/v1/change-logs")).ReadJsonAsync();
        logs.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("changeType").GetString()).Should().Contain(["deploy", "fix"]);

        var status = await (await admin.GetAsync("/api/v1/system/status")).ReadJsonAsync();
        var checks = status.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        checks["Cơ sở dữ liệu PostgreSQL"].Should().Be("Healthy");
        checks["Kho lưu trữ tệp"].Should().Be("Healthy");
        status.GetProperty("diskFreeBytes").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Full_data_export_package_contains_files_and_metadata()
    {
        var admin = await factory.AdminClientAsync();
        var (c, _, username) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        await StorageTests.UploadAsync(c, ws, null, "ban-giao.png", StorageTests.Png, "Tư liệu 2026/ban-giao.png");

        var job = await (await admin.PostAsJsonAsync("/api/v1/data-export", new { includeVersions = true, includeAuditLogs = true })).ReadJsonAsync();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RunReportJobCommand(job.GetProperty("id").GetGuid()));
        var url = (await (await admin.GetAsync($"/api/v1/reports/{job.GetProperty("id").GetGuid()}/download-url")).ReadJsonAsync()).GetProperty("url").GetString()!;
        var bytes = await factory.CreateClient().GetByteArrayAsync(url);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        names.Should().Contain(["metadata.json", "metadata.csv", "audit-logs.csv", "README.md"]);
        var entry = zip.GetEntry($"files/Ca nhan - {username}/Tư liệu 2026/ban-giao.png");
        entry.Should().NotBeNull();
        using (var s = entry!.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            ms.ToArray().Should().Equal(StorageTests.Png, "files are exported decrypted");
        }
        using var meta = JsonDocument.Parse(zip.GetEntry("metadata.json")!.Open());
        meta.RootElement.GetProperty("files").EnumerateArray().Should().Contain(f => f.GetProperty("exportPath").GetString()!.EndsWith("ban-giao.png"));
        (await c.PostAsJsonAsync("/api/v1/data-export", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Backup_schedule_and_manual_backup_queue()
    {
        var admin = await factory.AdminClientAsync();
        (await admin.PutAsJsonAsync("/api/v1/backups/schedule", new { cron = "30 3 * * *", retentionCount = 10, fullDayOfWeek = 0, toS3 = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var schedule = await (await admin.GetAsync("/api/v1/backups/schedule")).ReadJsonAsync();
        schedule.GetProperty("cron").GetString().Should().Be("30 3 * * *");
        schedule.GetProperty("nextRunAt").ValueKind.Should().Be(JsonValueKind.String);
        (await admin.PutAsJsonAsync("/api/v1/backups/schedule", new { cron = "sai", retentionCount = 10, fullDayOfWeek = 0, toS3 = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var started = await (await admin.PostAsync("/api/v1/backups?full=true", null)).ReadJsonAsync();
        started.GetProperty("status").GetString().Should().Be("queued");
        (await admin.PostAsync("/api/v1/backups", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "only one backup at a time");
        var list = await (await admin.GetAsync("/api/v1/backups")).ReadJsonAsync();
        list.GetProperty("items").EnumerateArray().Should().Contain(b => b.GetProperty("id").GetGuid() == started.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Demo_seed_creates_users_groups_and_sample_files_of_every_kind()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:Demo"] = "true", ["Seed:DemoPassword"] = "Demo@12345" }).Build();
        var seeder = ActivatorUtilities.CreateInstance<BlueDrive.Infrastructure.Persistence.Seed.DemoDataSeeder>(sp, config);
        await seeder.SeedAsync();
        await seeder.SeedAsync(); // idempotent

        var db = sp.GetRequiredService<BlueDrive.Infrastructure.Persistence.AppDbContext>();
        (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Users, u => u.Username.StartsWith("canbo"))).Should().Be(5);
        var kinds = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            System.Linq.Queryable.Distinct(System.Linq.Queryable.Select(db.Files, f => f.MediaType)));
        kinds.Should().Contain([Domain.Enums.MediaType.Image, Domain.Enums.MediaType.Video, Domain.Enums.MediaType.Audio, Domain.Enums.MediaType.Document,
            Domain.Enums.MediaType.Spreadsheet, Domain.Enums.MediaType.Presentation, Domain.Enums.MediaType.Pdf]);

        var anon = factory.CreateClient();
        var login = await (await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "canbo1", password = "Demo@12345" })).ReadJsonAsync();
        login.GetProperty("user").GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
    }
}