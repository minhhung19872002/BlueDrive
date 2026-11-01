using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlueDrive.Infrastructure.Auditing;

/// <summary>#14 "tải xuống bất thường": more than N downloads by one user within an hour raises an alert.</summary>
public sealed class UnusualDownloadObserver(IServiceScopeFactory scopes) : IAuditObserver
{
    public async Task OnLoggedAsync(AuditLog log, CancellationToken ct)
    {
        if (log.Action != AuditAction.Download || log.UserId is not { } uid) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<Application.Abstractions.ISettingsService>();
        var threshold = await settings.GetAsync(SettingKeys.DownloadUnusualPerHour, 200, ct);
        if (threshold <= 0) return;
        var since = log.OccurredAt.AddHours(-1);
        var count = await db.AuditLogs.CountAsync(a => a.UserId == uid && a.Action == AuditAction.Download && a.OccurredAt > since, ct);
        // Raise once when the threshold is crossed (AlertService also de-duplicates within an hour).
        if (count != threshold + 1) return;
        await scope.ServiceProvider.GetRequiredService<IAlertService>().RaiseAsync(AlertType.UnusualDownload, AlertSeverity.Warning,
            $"Tài khoản '{log.Username}' đã tải xuống {count} tệp trong 1 giờ qua (ngưỡng {threshold}) từ IP {log.Ip ?? "không xác định"}.", uid, ct: ct);
    }
}

public sealed class AuditChainVerifier(AppDbContext db) : IAuditChainVerifier
{
    public async Task<AuditChainReport> VerifyAsync(long? fromId, int max, CancellationToken ct)
    {
        var start = fromId ?? 0;
        string? prev = start > 0 ? await db.AuditLogs.Where(a => a.Id < start).OrderByDescending(a => a.Id).Select(a => a.Hash).FirstOrDefaultAsync(ct) : null;
        long checkedRows = 0;
        const int batch = 5000;
        var lastId = start - 1;
        while (checkedRows < max)
        {
            var rows = await db.AuditLogs.AsNoTracking().Where(a => a.Id > lastId).OrderBy(a => a.Id).Take(batch).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var r in rows)
            {
                if (r.PrevHash != prev || AuditLogger.ComputeHash(r) != r.Hash)
                    return new AuditChainReport(false, checkedRows, r.Id, $"Phát hiện bản ghi nhật ký #{r.Id} bị thay đổi hoặc chuỗi bị đứt.");
                prev = r.Hash;
                lastId = r.Id;
                checkedRows++;
            }
        }
        return new AuditChainReport(true, checkedRows, null, $"Đã kiểm tra {checkedRows:N0} bản ghi, chuỗi toàn vẹn.");
    }
}

public sealed class DiskInfo(IOptions<Storage.StorageOptions> options) : IDiskInfo
{
    private DriveInfo Drive() => new(Path.GetPathRoot(Path.GetFullPath(options.Value.LocalRoot)) ?? "/");
    public long FreeBytes() { try { return Drive().AvailableFreeSpace; } catch (IOException) { return -1; } }
    public long TotalBytes() { try { return Drive().TotalSize; } catch (IOException) { return -1; } }
}
