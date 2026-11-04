using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Infrastructure.Media;

public interface IFileProcessor
{
    Task ProcessAsync(Guid fileId, Guid versionId, CancellationToken ct) => ProcessAsync(fileId, versionId, false, ct);

    /// <param name="lastAttempt">The job will not be retried: a scanner outage is recorded as <see cref="ScanStatus.Failed"/> instead of thrown.</param>
    Task ProcessAsync(Guid fileId, Guid versionId, bool lastAttempt, CancellationToken ct);
}

/// <summary>Runs the configured steps (scan → metadata/thumbnails) for a stored version. Steps are pluggable.</summary>
public sealed class FileProcessor(IAppDbContext db, IEnumerable<IFileProcessingStep> steps, ILogger<FileProcessor> logger) : IFileProcessor
{
    public async Task ProcessAsync(Guid fileId, Guid versionId, bool lastAttempt, CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        var version = await db.FileVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (file is null || version is null || file.CurrentVersionId != versionId)
        {
            logger.LogInformation("Bỏ qua xử lý {FileId}: tệp đã bị xóa hoặc có phiên bản mới hơn", fileId);
            return;
        }
        foreach (var step in steps.OrderBy(s => s.Order))
        {
            bool proceed;
            try { proceed = await step.RunAsync(file, version, ct); }
            catch (MalwareScanUnavailableException ex) when (lastAttempt && step is MalwareScanStep scan)
            {
                logger.LogError(ex, "Hết số lần thử quét virus tệp {FileId}", fileId);
                await scan.MarkFailedAsync(file, ex.Message, ct);
                proceed = true;
            }
            await db.SaveChangesAsync(ct);
            if (!proceed) break;
        }
        if (file.ScanStatus is ScanStatus.Pending or ScanStatus.Scanning) file.ScanStatus = ScanStatus.Skipped;
        await db.SaveChangesAsync(ct);
    }
}

public interface IFileProcessingStep
{
    int Order { get; }
    /// <summary>Returns false to stop the pipeline (e.g. infected file).</summary>
    Task<bool> RunAsync(Domain.Entities.StoredFile file, Domain.Entities.FileVersion version, CancellationToken ct);
}
