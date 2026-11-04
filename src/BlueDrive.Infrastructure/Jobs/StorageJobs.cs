using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Infrastructure.Media;
using Hangfire;
using Hangfire.Server;
using MediatR;
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