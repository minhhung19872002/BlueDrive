using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Infrastructure.Media;
using Hangfire;
using Hangfire.Server;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Infrastructure.Jobs;

public sealed class HangfireBackgroundJobs(IBackgroundJobClient client) : IBackgroundJobs
{
    public void EnqueueFileProcessing(Guid fileId, Guid versionId)
        => client.Create<FileProcessingJob>(j => j.RunAsync(fileId, versionId, null, CancellationToken.None), new Hangfire.States.EnqueuedState(HangfireSetup.QueueScan));
}

/// <summary>
/// First stage after an upload: the malware scan, on its own queue and workers so that a backlog of thumbnails never keeps a
/// new file "đang quét" (other users cannot download it until then). Thumbnails/previews follow as <see cref="FileDerivativesJob"/>.
/// </summary>
public sealed class FileProcessingJob(IFileProcessor processor, IBackgroundJobClient client, ILogger<FileProcessingJob> logger)
{
    public const int MaxRetries = 3;

    /// <param name="context">Supplied by Hangfire: tells the last retry apart, so an unreachable scanner ends as "Không quét được" instead of a stuck file.</param>
    [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = [30, 120, 600])]
    [Queue(HangfireSetup.QueueScan)]
    public async Task RunAsync(Guid fileId, Guid versionId, PerformContext? context, CancellationToken ct)
    {
        var retry = context?.GetJobParameter<int>("RetryCount") ?? 0;
        logger.LogInformation("Quét tệp {FileId} phiên bản {VersionId} (lần thử lại {Retry})", fileId, versionId, retry);
        if (await processor.ScanAsync(fileId, versionId, retry >= MaxRetries, ct))
            client.Create<FileDerivativesJob>(j => j.RunAsync(fileId, versionId, CancellationToken.None), new Hangfire.States.EnqueuedState(HangfireSetup.QueueMedia));
    }

    /// <summary>Signature of jobs enqueued before the retry-aware overload existed.</summary>
    [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = [30, 120, 600])]
    [Queue(HangfireSetup.QueueScan)]
    public Task RunAsync(Guid fileId, Guid versionId, CancellationToken ct) => RunAsync(fileId, versionId, null, ct);
}

/// <summary>Second stage: metadata, thumbnails and previews (images, video, PDF, Office through ONLYOFFICE).</summary>
public sealed class FileDerivativesJob(IFileProcessor processor, ILogger<FileDerivativesJob> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [60, 600])]
    [Queue(HangfireSetup.QueueMedia)]
    public async Task RunAsync(Guid fileId, Guid versionId, CancellationToken ct)
    {
        logger.LogInformation("Tạo ảnh xem trước tệp {FileId} phiên bản {VersionId}", fileId, versionId);
        await processor.DeriveAsync(fileId, versionId, ct);
    }
}

public sealed class TrashRetentionJob(ISender mediator, ILogger<TrashRetentionJob> logger)
{
    [Queue(HangfireSetup.QueueMaintenance)]
    public async Task RunAsync(CancellationToken ct)
    {
        var n = await mediator.Send(new PurgeExpiredTrashCommand(), ct);
        if (n > 0) logger.LogInformation("Đã xóa vĩnh viễn {Count} mục hết hạn lưu trong thùng rác", n);
    }
}

/// <summary>
/// Reads the text of documents stored before searching inside documents existed, or whose reading failed: a batch every 10 minutes
/// until every current version has its row in file_texts (new uploads get theirs right after the scan).
/// </summary>
public sealed class TextIndexBackfillJob(IAppDbContext db, Media.TextIndexStep step, ILogger<TextIndexBackfillJob> logger)
{
    public const int BatchSize = 200;
    private static readonly string[] Extensions = [.. Media.DocumentText.Extensions];

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(600)]
    [Queue(HangfireSetup.QueueMaintenance)]
    public async Task<int> RunAsync(CancellationToken ct)
    {
        Domain.Enums.ScanStatus[] ready = [Domain.Enums.ScanStatus.Clean, Domain.Enums.ScanStatus.Skipped, Domain.Enums.ScanStatus.TooLarge, Domain.Enums.ScanStatus.Failed];
        var todo = await db.Files.Where(f => !f.IsDeleted && f.CurrentVersionId != null && ready.Contains(f.ScanStatus) && Extensions.Contains(f.Extension)
                                             && !db.FileTexts.Any(t => t.FileId == f.Id && t.VersionId == f.CurrentVersionId))
            .OrderBy(f => f.CreatedAt).Take(BatchSize).ToListAsync(ct);
        foreach (var f in todo)
        {
            var v = await db.FileVersions.FirstOrDefaultAsync(x => x.Id == f.CurrentVersionId, ct);
            if (v is not null) await step.IndexAsync(f, v, ct);
        }
        if (todo.Count > 0) logger.LogInformation("Đã đọc nội dung {Count} tệp để tìm kiếm", todo.Count);
        return todo.Count;
    }
}

/// <summary>Perm-Phase 1: keeps the new permission model in step with the current one (the source of writes until Perm-Phase 2).</summary>
public sealed class PermissionProjectionJob(Application.Features.Access.PermissionProjection projection, ILogger<PermissionProjectionJob> logger)
{
    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(1800)]
    [Queue(HangfireSetup.QueueMaintenance)]
    public async Task RunAsync(CancellationToken ct)
    {
        var r = await projection.RebuildAsync(ct);
        logger.LogInformation("Đã dựng lại mô hình phân quyền mới: {Securables} mục, {Scopes} phạm vi quyền, {Entries} quyền, {Grants} dòng lọc", r.Securables, r.Scopes, r.Entries, r.Grants);
    }
}

public sealed class SyncJournalRetentionJob(ISender mediator, ILogger<SyncJournalRetentionJob> logger)
{
    [Queue(HangfireSetup.QueueMaintenance)]
    public async Task RunAsync(CancellationToken ct)
    {
        var n = await mediator.Send(new Application.Features.Sync.PurgeSyncJournalCommand(), ct);
        if (n > 0) logger.LogInformation("Đã dọn {Count} dòng nhật ký thay đổi đồng bộ đã hết hạn lưu", n);
    }
}

public sealed class StorageRecurringJobs(IRecurringJobManager jobs) : IRecurringJobRegistration
{
    public Task RegisterAsync(CancellationToken ct)
    {
        jobs.AddOrUpdate<TrashRetentionJob>("trash-retention", j => j.RunAsync(CancellationToken.None), "15 1 * * *",
            new RecurringJobOptions { TimeZone = Reporting.VietnamTime.Zone });
        jobs.AddOrUpdate<SyncJournalRetentionJob>("sync-journal-retention", j => j.RunAsync(CancellationToken.None), "45 1 * * *",
            new RecurringJobOptions { TimeZone = Reporting.VietnamTime.Zone });
        jobs.AddOrUpdate<TextIndexBackfillJob>("text-index-backfill", j => j.RunAsync(CancellationToken.None), "*/10 * * * *");
        jobs.AddOrUpdate<PermissionProjectionJob>("permission-projection", j => j.RunAsync(CancellationToken.None), "*/15 * * * *");
        return Task.CompletedTask;
    }
}

public sealed class HangfireReportQueue(IBackgroundJobClient client) : Application.Features.Monitoring.IReportQueue
{
    public void Enqueue(Guid reportJobId)
        => client.Create<ReportJobRunner>(j => j.RunAsync(reportJobId, CancellationToken.None), new Hangfire.States.EnqueuedState(HangfireSetup.QueueReports));
}

public sealed class ReportJobRunner(ISender mediator)
{
    [AutomaticRetry(Attempts = 1)]
    [Queue(HangfireSetup.QueueReports)]
    public Task RunAsync(Guid id, CancellationToken ct) => mediator.Send(new Application.Features.Monitoring.RunReportJobCommand(id), ct);
}