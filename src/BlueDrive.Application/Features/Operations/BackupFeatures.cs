using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Operations;

public sealed record BackupJobDto(Guid Id, BackupType Type, string Mode, JobStatus Status, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt,
    long SizeBytes, string? Checksum, string? Error, string? RequestedByName, bool HasDbDump, bool HasFiles);

public sealed record BackupScheduleDto(string Cron, int RetentionCount, int FullDayOfWeek, bool ToS3, DateTime? NextRunAt, BackupJobDto? LastBackup, long FreeDiskBytes);

/// <summary>Executes backups/restores on the Worker host (pg_dump + incremental tar of the storage volume).</summary>
public interface IBackupRunner
{
    Task RunAsync(Guid backupJobId, CancellationToken ct);
    Task<Stream> OpenArtifactAsync(BackupJob job, string part, CancellationToken ct);
}

public interface IBackupScheduler
{
    void Enqueue(Guid backupJobId);
    void Reschedule(string cron);
    DateTime? NextRun(string cron);
}

public static class BackupMapping
{
    public static IQueryable<BackupJobDto> Project(IAppDbContext db, IQueryable<BackupJob> q) => q.Select(b => new BackupJobDto(b.Id, b.Type, b.Mode, b.Status, b.CreatedAt,
        b.StartedAt, b.FinishedAt, b.SizeBytes, b.Checksum, b.Error, db.Users.Where(u => u.Id == b.RequestedBy).Select(u => u.FullName).FirstOrDefault(),
        b.DbDumpKey != null, b.FilesArchiveKey != null));
}

public sealed record ListBackupsQuery : PageQuery, IRequest<PagedResult<BackupJobDto>>;

public sealed class ListBackupsHandler(IAppDbContext db) : IRequestHandler<ListBackupsQuery, PagedResult<BackupJobDto>>
{
    public Task<PagedResult<BackupJobDto>> Handle(ListBackupsQuery q, CancellationToken ct)
        => BackupMapping.Project(db, db.BackupJobs.AsNoTracking().OrderByDescending(b => b.CreatedAt)).ToPagedAsync(q, ct);
}

public sealed record GetBackupScheduleQuery : IRequest<BackupScheduleDto>;

public sealed class GetBackupScheduleHandler(IAppDbContext db, ISettingsService settings, IBackupScheduler scheduler, Monitoring.IDiskInfo disk) : IRequestHandler<GetBackupScheduleQuery, BackupScheduleDto>
{
    public async Task<BackupScheduleDto> Handle(GetBackupScheduleQuery q, CancellationToken ct)
    {
        var cron = await settings.GetAsync(SettingKeys.BackupCron, "0 2 * * *", ct);
        var last = await BackupMapping.Project(db, db.BackupJobs.AsNoTracking().OrderByDescending(b => b.CreatedAt)).FirstOrDefaultAsync(ct);
        return new BackupScheduleDto(cron, await settings.GetAsync(SettingKeys.BackupRetentionCount, 7, ct), await settings.GetAsync(SettingKeys.BackupFullDayOfWeek, 0, ct),
            await settings.GetAsync(SettingKeys.BackupToS3, false, ct), scheduler.NextRun(cron), last, disk.FreeBytes());
    }
}

/// <summary>Updates the schedule (daily/weekly/custom cron), retention and targets; applied to the Worker immediately.</summary>
public sealed record UpdateBackupScheduleCommand(string Cron, int RetentionCount, int FullDayOfWeek, bool ToS3) : IRequest<Unit>;

public sealed class UpdateBackupScheduleHandler(ISender mediator, IBackupScheduler scheduler) : IRequestHandler<UpdateBackupScheduleCommand, Unit>
{
    public async Task<Unit> Handle(UpdateBackupScheduleCommand cmd, CancellationToken ct)
    {
        if (cmd.RetentionCount is < 1 or > 365) throw new RequestValidationException(new Dictionary<string, string[]> { ["retentionCount"] = ["Số bản giữ lại từ 1 đến 365."] });
        if (cmd.FullDayOfWeek is < 0 or > 6) throw new RequestValidationException(new Dictionary<string, string[]> { ["fullDayOfWeek"] = ["Ngày sao lưu đầy đủ không hợp lệ."] });
        await mediator.Send(new Settings.UpdateSettingsCommand(new Dictionary<string, System.Text.Json.JsonElement>
        {
            [SettingKeys.BackupCron] = System.Text.Json.JsonSerializer.SerializeToElement(cmd.Cron.Trim()),
            [SettingKeys.BackupRetentionCount] = System.Text.Json.JsonSerializer.SerializeToElement(cmd.RetentionCount),
            [SettingKeys.BackupFullDayOfWeek] = System.Text.Json.JsonSerializer.SerializeToElement(cmd.FullDayOfWeek),
            [SettingKeys.BackupToS3] = System.Text.Json.JsonSerializer.SerializeToElement(cmd.ToS3),
        }), ct);
        scheduler.Reschedule(cmd.Cron.Trim());
        return Unit.Value;
    }
}

/// <summary>"Sao lưu ngay" (manual backup). mode: auto | full.</summary>
public sealed record StartBackupCommand(bool Full) : IRequest<BackupJobDto>;

public sealed class StartBackupHandler(IAppDbContext db, ICurrentUser me, IBackupScheduler scheduler, IAuditLogger audit, TimeProvider clock) : IRequestHandler<StartBackupCommand, BackupJobDto>
{
    public async Task<BackupJobDto> Handle(StartBackupCommand cmd, CancellationToken ct)
    {
        if (await db.BackupJobs.AnyAsync(b => b.Status == JobStatus.Queued || b.Status == JobStatus.Running, ct))
            throw new ConflictException("Đang có một bản sao lưu được thực hiện. Vui lòng chờ hoàn tất.");
        var job = new BackupJob { Type = BackupType.Manual, Status = JobStatus.Queued, Mode = cmd.Full ? "full" : "auto", CreatedAt = clock.GetUtcNow().UtcDateTime, RequestedBy = me.UserId };
        db.BackupJobs.Add(job);
        await db.SaveChangesAsync(ct);
        scheduler.Enqueue(job.Id);
        await audit.LogAsync(new AuditEntry(AuditAction.Backup, ResourceName: "Sao lưu đột xuất", Detail: new { backupId = job.Id, full = cmd.Full }), ct);
        return await BackupMapping.Project(db, db.BackupJobs.AsNoTracking().Where(b => b.Id == job.Id)).FirstAsync(ct);
    }
}

/// <summary>Signed download of a backup part ("db" or "files").</summary>
public sealed record GetBackupDownloadUrlQuery(Guid Id, string Part) : IRequest<DownloadUrlDto>;

public sealed class GetBackupDownloadUrlHandler(IAppDbContext db, ICurrentUser me, IContentTicketService tickets, IAuditLogger audit, TimeProvider clock) : IRequestHandler<GetBackupDownloadUrlQuery, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> Handle(GetBackupDownloadUrlQuery q, CancellationToken ct)
    {
        var b = await db.BackupJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct) ?? throw new NotFoundException("Không tìm thấy bản sao lưu.");
        if (b.Status != JobStatus.Succeeded) throw new AppException("Bản sao lưu chưa hoàn tất.", 409);
        var part = q.Part == "files" ? "files" : q.Part == "manifest" ? "manifest" : "db";
        var name = $"bluedrive-backup-{b.CreatedAt.AddHours(7):yyyyMMdd-HHmm}-{part}{(part == "db" ? ".dump" : part == "files" ? ".tar" : ".json")}";
        var token = tickets.Issue(new ContentTicket(me.RequireUserId(), "backup", b.Id, null, false, [new ItemRef(part, b.Id)], name), TimeSpan.FromHours(2));
        await audit.LogAsync(new AuditEntry(AuditAction.Download, ResourceName: name, Detail: new { backupId = b.Id, part }), ct);
        return new DownloadUrlDto($"/api/v1/content/{Uri.EscapeDataString(token)}", clock.GetUtcNow().UtcDateTime.AddHours(2), name, "application/octet-stream", 0);
    }
}
