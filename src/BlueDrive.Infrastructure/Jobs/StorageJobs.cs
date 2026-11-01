using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Infrastructure.Media;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Infrastructure.Jobs;

public sealed class HangfireBackgroundJobs(IBackgroundJobClient client) : IBackgroundJobs
{
    public void EnqueueFileProcessing(Guid fileId, Guid versionId)
        => client.Create<FileProcessingJob>(j => j.RunAsync(fileId, versionId, CancellationToken.None), new Hangfire.States.EnqueuedState(HangfireSetup.QueueMedia));
}

/// <summary>Post-upload pipeline executed by the Worker: malware scan, metadata, thumbnails/previews.</summary>
public sealed class FileProcessingJob(IFileProcessor processor, ILogger<FileProcessingJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 600])]
    [Queue(HangfireSetup.QueueMedia)]
    public async Task RunAsync(Guid fileId, Guid versionId, CancellationToken ct)
    {
        logger.LogInformation("Xử lý tệp {FileId} phiên bản {VersionId}", fileId, versionId);
        await processor.ProcessAsync(fileId, versionId, ct);
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

public sealed class StorageRecurringJobs(IRecurringJobManager jobs) : IRecurringJobRegistration
{
    public Task RegisterAsync(CancellationToken ct)
    {
        jobs.AddOrUpdate<TrashRetentionJob>("trash-retention", j => j.RunAsync(CancellationToken.None), "15 1 * * *",
            new RecurringJobOptions { TimeZone = Reporting.VietnamTime.Zone });
        return Task.CompletedTask;
    }
}
