using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Application.Features.Monitoring;

public interface IAlertService
{
    /// <summary>Creates an alert, pushes it in realtime to users holding alert.view and optionally e-mails them.</summary>
    Task<Alert> RaiseAsync(AlertType type, AlertSeverity severity, string message, Guid? userId = null, Guid? workspaceId = null,
        string? dedupKey = null, CancellationToken ct = default);
}

public sealed class AlertService(
    IAppDbContext db, INotificationPublisher notifier, IEmailSender email, ISettingsService settings,
    ISensitiveDataProtector pii, TimeProvider clock, ILogger<AlertService> logger) : IAlertService
{
    public static readonly IReadOnlyDictionary<AlertType, string> TypeNames = new Dictionary<AlertType, string>
    {
        [AlertType.BruteForce] = "Đăng nhập sai nhiều lần",
        [AlertType.QuotaWarning] = "Dung lượng sắp vượt ngưỡng",
        [AlertType.QuotaExceeded] = "Dung lượng vượt hạn mức",
        [AlertType.UnusualDownload] = "Tải xuống bất thường",
        [AlertType.BackupFailed] = "Sao lưu thất bại",
        [AlertType.MalwareDetected] = "Phát hiện mã độc",
    };

    public async Task<Alert> RaiseAsync(AlertType type, AlertSeverity severity, string message, Guid? userId = null, Guid? workspaceId = null,
        string? dedupKey = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Avoid alert storms: same type + subject unacknowledged within the last hour is not duplicated.
        var recent = await db.Alerts.Where(a => a.Type == type && !a.IsAcknowledged && a.RelatedUserId == userId
                                                && a.RelatedWorkspaceId == workspaceId && a.CreatedAt > now.AddHours(-1))
            .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
        if (recent is not null && (dedupKey is null || recent.Message == message)) return recent;

        var alert = new Alert { Type = type, Severity = severity, Message = message, RelatedUserId = userId, RelatedWorkspaceId = workspaceId, CreatedAt = now };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);

        var title = $"Cảnh báo: {TypeNames.GetValueOrDefault(type, type.ToString())}";
        await notifier.NotifyPermissionAsync(Perm.AlertView, "alert", title, message, "/admin/alerts", ct);

        if (await settings.GetAsync(SettingKeys.AlertEmailAdmins, true, ct))
        {
            var recipients = await (from ur in db.UserRoles
                                    join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                                    join u in db.Users on ur.UserId equals u.Id
                                    where rp.PermissionCode == Perm.AlertView && u.IsActive && u.Email != null
                                    select u.Email).Distinct().ToListAsync(ct);
            foreach (var enc in recipients)
            {
                var to = pii.Unprotect(enc);
                if (string.IsNullOrWhiteSpace(to)) continue;
                try
                {
                    await email.SendAsync(to, $"[BlueDrive] {title}",
                        $"<p><b>{System.Net.WebUtility.HtmlEncode(title)}</b></p><p>{System.Net.WebUtility.HtmlEncode(message)}</p><p>Thời điểm: {now.AddHours(7):dd/MM/yyyy HH:mm}</p>", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Không gửi được email cảnh báo");
                }
            }
        }
        return alert;
    }
}
