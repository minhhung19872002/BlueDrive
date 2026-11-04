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
        => client.Create<FileProcessingJob>(j => j.RunAsync(fileId, versionId, null, CancellationToken.None), new Hangfire.States.EnqueuedState(HangfireSetup.QueueMedia));
}

/// <summary>Post-upload pipeline executed by the Worker: malware scan, metadata, thumbnails/previews.</summary>
public sealed class FileProcessingJob(IFileProcessor processor, ILogger<FileProcessingJob> logger)
{
    public const int MaxRetries = 3;

    /// <param name="context">Supplied by Hangfire: tells the last retry apart, so an unreachable scanner ends as "Không quét được" instead of a stuck file.</param>
    [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = [30, 120, 600])]
    [Queue(HangfireSetup.QueueMedia)]
    public async Task RunAsync(Guid fileId, Guid versionId, PerformContext? context, CancellationToken ct)
    {
        var retry = context?.GetJobParameter<int>("RetryCount") ?? 0;
        logger.LogInformation("Xử lý tệp {FileId} phiên bản {VersionId} (lần thử lại {Retry})", fileId, versionId, retry);
        await processor.ProcessAsync(fileId, versionId, retry >= MaxRetries, ct);
    }

    /// <summary>Signature of jobs enqueued before the retry-aware overload existed.</summary>
    [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = [30, 120, 600])]
    [Queue(HangfireSetup.QueueMedia)]
    public Task RunAsync(Guid fileId, Guid versionId, CancellationToken ct) => RunAsync(fileId, versionId, null, ct);
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