using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;

namespace BlueDrive.Application.Features.Operations;

/// <summary>Builds the hand-over package (decrypted files + metadata.json/csv + README); see docs/data-export-format.md.</summary>
public interface IDataExporter
{
    Task<(string Key, string FileName, long Size)> ExportAsync(ReportJob job, Func<int, Task> progress, CancellationToken ct);
    Stream Open(string key);
}

/// <summary>"Xuất toàn bộ dữ liệu" (service-quality item 4.2) — runs in the Worker; the admin is notified when ready.</summary>
public sealed record StartDataExportCommand(bool IncludeVersions, bool IncludeAuditLogs) : IRequest<ReportJobDto>;

public sealed class StartDataExportHandler(IAppDbContext db, ICurrentUser me, IReportQueue queue, IAuditLogger audit, TimeProvider clock) : IRequestHandler<StartDataExportCommand, ReportJobDto>
{
    public async Task<ReportJobDto> Handle(StartDataExportCommand cmd, CancellationToken ct)
    {
        var job = new ReportJob
        {
            RequestedBy = me.RequireUserId(), ReportType = DataExport.Type, Format = "zip", Status = JobStatus.Queued, CreatedAt = clock.GetUtcNow().UtcDateTime,
            ParametersJson = System.Text.Json.JsonSerializer.Serialize(new { cmd.IncludeVersions, cmd.IncludeAuditLogs }),
        };
        db.ReportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(job.Id);
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: "Xuất toàn bộ dữ liệu", Detail: new { job = job.Id, cmd.IncludeVersions, cmd.IncludeAuditLogs }), ct);
        return ReportMapping.ToDto(job);
    }
}

public static class DataExport
{
    public const string Type = "data_export";
    public const string KeyPrefix = "export:";
}

public sealed record ServiceCheck(string Name, string Status, string? Detail, double? DurationMs);

public sealed record SystemStatusDto(string Version, string GitSha, DateTime ServerTimeUtc, IReadOnlyList<ServiceCheck> Checks, long DiskFreeBytes, long DiskTotalBytes,
    BackupJobDto? LastBackup, int QueuedJobs, int FailedJobs, int WorkerServers);

public interface ISystemStatusService
{
    Task<SystemStatusDto> GetAsync(CancellationToken ct);
}

public sealed record GetSystemStatusQuery : IRequest<SystemStatusDto>;

public sealed class GetSystemStatusHandler(ISystemStatusService status) : IRequestHandler<GetSystemStatusQuery, SystemStatusDto>
{
    public Task<SystemStatusDto> Handle(GetSystemStatusQuery q, CancellationToken ct) => status.GetAsync(ct);
}
