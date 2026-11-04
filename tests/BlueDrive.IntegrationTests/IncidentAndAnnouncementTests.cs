using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Domain.Enums;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>Service quality items 3.4 (security incident records) and 5.2 (system announcements).</summary>
[Collection(ApiCollection.Name)]
public class IncidentAndAnnouncementTests(ApiFactory factory)
{
    private async Task<JsonElement> AuditAsync(HttpClient admin, string q) =>
        (await (await admin.PostAsJsonAsync("/api/v1/audit-logs/search?pageSize=50", new { q })).ReadJsonAsync()).GetProperty("items");

    [Fact]
    public async Task Incident_opened_from_alert_is_handled_closed_and_exported()
    {
        var admin = await factory.AdminClientAsync();
        Guid alertId;
        using (var scope = factory.Services.CreateScope())
            alertId = (await scope.ServiceProvider.GetRequiredService<IAlertService>()
                .RaiseAsync(AlertType.MalwareDetected, AlertSeverity.Critical, $"Phát hiện mã độc EICAR {Guid.NewGuid():N}", dedupKey: "test")).Id;

        var created = await (await admin.PostAsJsonAsync("/api/v1/security-incidents", new
        {
            title = "Tệp nhiễm mã độc tải lên kho", description = "ClamAV phát hiện EICAR trong tệp tải lên", category = "malware", severity = "high",
            affectedSystems = "Kho lưu trữ tệp", alertId,
        })).ReadJsonAsync();
        var id = created.GetProperty("id").GetGuid();
        var code = created.GetProperty("code").GetString()!;
        code.Should().StartWith("SC-");
        created.GetProperty("status").GetString().Should().Be("reported");
        created.GetProperty("alertId").GetGuid().Should().Be(alertId);

        // The alert is acknowledged, shows its incident, and cannot be opened twice.
        var alerts = await (await admin.PostAsJsonAsync("/api/v1/alerts/search?pageSize=200", new { type = "malware_detected" })).ReadJsonAsync();
        var alert = alerts.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == alertId);
        alert.GetProperty("isAcknowledged").GetBoolean().Should().BeTrue();
        alert.GetProperty("incidentCode").GetString().Should().Be(code);
        (await admin.PostAsJsonAsync("/api/v1/security-incidents", new { title = "Lặp", description = "Lặp", category = "malware", severity = "low", alertId }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Closing needs cause, remediation and conclusion.
        var adminId = (await (await admin.GetAsync("/api/v1/auth/me")).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await admin.PutAsJsonAsync($"/api/v1/security-incidents/{id}", new { status = "investigating", handlerId = adminId, note = "Cô lập tệp" })).ReadJsonAsync();
        (await admin.PutAsJsonAsync($"/api/v1/security-incidents/{id}", new { status = "closed" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await (await admin.PutAsJsonAsync($"/api/v1/security-incidents/{id}", new
        {
            status = "resolved", cause = "Người dùng tải tệp từ nguồn không tin cậy", coordinatingParties = "Cục An toàn thông tin",
            remediation = "Xoá vĩnh viễn tệp, quét lại toàn bộ kho", conclusion = "Không lây lan, không lộ dữ liệu",
        })).ReadJsonAsync();
        var closed = await (await admin.PutAsJsonAsync($"/api/v1/security-incidents/{id}", new { status = "closed" })).ReadJsonAsync();
        closed.GetProperty("closedAt").ValueKind.Should().Be(JsonValueKind.String);
        closed.GetProperty("resolvedAt").ValueKind.Should().Be(JsonValueKind.String);
        closed.GetProperty("handlerName").GetString().Should().NotBeNullOrEmpty();
        closed.GetProperty("events").GetArrayLength().Should().Be(4);
        (await admin.PutAsJsonAsync($"/api/v1/security-incidents/{id}", new { note = "Sau khi đóng" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var search = await (await admin.PostAsJsonAsync($"/api/v1/security-incidents/search?q={code}", new { open = false })).ReadJsonAsync();
        search.GetProperty("items").EnumerateArray().Should().ContainSingle(i => i.GetProperty("id").GetGuid() == id);
        var pdf = await admin.GetAsync($"/api/v1/security-incidents/{id}/export?format=pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await admin.PostAsJsonAsync("/api/v1/security-incidents/export?format=xlsx", new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await AuditAsync(admin, code);
        audit.EnumerateArray().Select(a => a.GetProperty("action").GetString()).Should().Contain(["create", "edit", "export"]);
        (await (await admin.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().Contain(n => n.GetProperty("type").GetString() == "incident");
    }

    [Fact]
    public async Task Incident_endpoints_require_incident_permissions()
    {
        var (user, _, _) = await factory.CreateUserClientAsync("bien_tap");
        (await user.PostAsJsonAsync("/api/v1/security-incidents/search", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PostAsJsonAsync("/api/v1/security-incidents", new { title = "X", description = "Y", category = "other", severity = "low" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var admin = await factory.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/v1/security-incidents", new { title = "", description = "", category = "other", severity = "low" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Announcement_banner_is_visible_to_every_user_within_its_window()
    {
        var admin = await factory.AdminClientAsync();
        var (user, _, _) = await factory.CreateUserClientAsync("chi_xem");
        var title = $"Bảo trì hệ thống {Guid.NewGuid():N}"[..30];
        var a = await (await admin.PostAsJsonAsync("/api/v1/announcements", new
        {
            title, content = "Hệ thống tạm dừng 22:00–23:00 để nâng cấp.", level = "maintenance", endsAt = DateTime.UtcNow.AddHours(2),
        })).ReadJsonAsync();
        var id = a.GetProperty("id").GetGuid();
        a.GetProperty("isCurrent").GetBoolean().Should().BeTrue();

        async Task<IEnumerable<string?>> ActiveTitles() =>
            (await (await user.GetAsync("/api/v1/announcements/active")).ReadJsonAsync()).EnumerateArray().Select(x => x.GetProperty("title").GetString());
        (await ActiveTitles()).Should().Contain(title);

        // Scheduled for later → not shown yet; deactivated → hidden.
        await (await admin.PutAsJsonAsync($"/api/v1/announcements/{id}", new { title, content = "Lùi lịch", level = "maintenance", startsAt = DateTime.UtcNow.AddDays(1) })).ReadJsonAsync();
        (await ActiveTitles()).Should().NotContain(title);
        (await admin.PostAsJsonAsync("/api/v1/announcements", new { title = "Sai", content = "Sai", level = "info", startsAt = DateTime.UtcNow, endsAt = DateTime.UtcNow.AddHours(-1) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await user.PostAsJsonAsync("/api/v1/announcements", new { title = "X", content = "Y", level = "info" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync("/api/v1/announcements")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/v1/announcements/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AuditAsync(admin, title)).EnumerateArray().Select(x => x.GetProperty("action").GetString()).Should().Contain(["create", "edit", "delete"]);
    }

    [Fact]
    public async Task Change_log_entry_can_be_announced_to_users()
    {
        var admin = await factory.AdminClientAsync();
        var (user, _, _) = await factory.CreateUserClientAsync("chi_xem");
        var version = $"9.{Random.Shared.Next(1000, 9999)}.0";
        (await admin.PostAsJsonAsync("/api/v1/change-logs", new { version, changeType = "feature", description = "Thêm hồ sơ sự cố ATTT", announceDays = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var active = await (await user.GetAsync("/api/v1/announcements/active")).ReadJsonAsync();
        var item = active.EnumerateArray().Single(x => x.GetProperty("title").GetString()!.Contains(version));
        (item.GetProperty("endsAt").GetDateTime() - item.GetProperty("startsAt").GetDateTime()).TotalDays.Should().BeApproximately(3, 0.01);
        (await admin.PostAsJsonAsync("/api/v1/change-logs", new { version, changeType = "fix", description = "x", announceDays = 365 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
