using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Infrastructure.Media;

/// <summary>
/// The post-upload pipeline has two stages: the malware scan, which gates downloads by other users and must not wait, and the
/// derivatives (thumbnails, previews), which can take seconds per file and run on their own queue afterwards.
/// </summary>
public enum FileProcessingStage
{
    Scan,
    Derivatives,
}

public interface IFileProcessor
{
    /// <summary>Both stages in a row (used where no queue is involved).</summary>
    async Task ProcessAsync(Guid fileId, Guid versionId, CancellationToken ct)
    {
        if (await ScanAsync(fileId, versionId, false, ct)) await DeriveAsync(fileId, versionId, ct);
    }

    /// <param name="lastAttempt">The job will not be retried: a scanner outage is recorded as <see cref="ScanStatus.Failed"/> instead of thrown.</param>
    /// <returns>True when the derivatives stage should follow (the version is still current and was not quarantined).</returns>
    Task<bool> ScanAsync(Guid fileId, Guid versionId, bool lastAttempt, CancellationToken ct);

    Task DeriveAsync(Guid fileId, Guid versionId, CancellationToken ct);
}

/// <summary>Runs the configured steps of each stage for a stored version. Steps are pluggable.</summary>
public sealed class FileProcessor(IAppDbContext db, IEnumerable<IFileProcessingStep> steps, ILogger<FileProcessor> logger) : IFileProcessor
{
    public async Task<bool> ScanAsync(Guid fileId, Guid versionId, bool lastAttempt, CancellationToken ct)
    {
        var target = await LoadAsync(fileId, versionId, ct);
        if (target is not var (file, version)) return false;
        var proceed = await RunStageAsync(FileProcessingStage.Scan, file, version, lastAttempt, ct);
        if (file.ScanStatus is ScanStatus.Pending or ScanStatus.Scanning) file.ScanStatus = ScanStatus.Skipped;
        await db.SaveChangesAsync(ct);
        return proceed;
    }

    public async Task DeriveAsync(Guid fileId, Guid versionId, CancellationToken ct)
    {
        var target = await LoadAsync(fileId, versionId, ct);
        if (target is not var (file, version)) return;
        if (file.ScanStatus == ScanStatus.Infected) return;
        await RunStageAsync(FileProcessingStage.Derivatives, file, version, false, ct);
    }

    private async Task<(StoredFile File, FileVersion Version)?> LoadAsync(Guid fileId, Guid versionId, CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        var version = await db.FileVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (file is null || version is null || file.CurrentVersionId != versionId)
        {
            logger.LogInformation("Bỏ qua xử lý {FileId}: tệp đã bị xóa hoặc có phiên bản mới hơn", fileId);
            return null;
        }
        return (file, version);
    }

    /// <returns>False when a step stopped the pipeline (e.g. infected file).</returns>
    private async Task<bool> RunStageAsync(FileProcessingStage stage, StoredFile file, FileVersion version, bool lastAttempt, CancellationToken ct)
    {
        foreach (var step in steps.Where(s => s.Stage == stage).OrderBy(s => s.Order))
        {
            bool proceed;
            try { proceed = await step.RunAsync(file, version, ct); }
            catch (MalwareScanUnavailableException ex) when (lastAttempt && step is MalwareScanStep scan)
            {
                logger.LogError(ex, "Hết số lần thử quét virus tệp {FileId}", file.Id);
                await scan.MarkFailedAsync(file, ex.Message, ct);
                proceed = true;
            }
            await db.SaveChangesAsync(ct);
            if (!proceed) return false;
        }
        return true;
    }
}

public interface IFileProcessingStep
{
    FileProcessingStage Stage { get; }
    int Order { get; }
    /// <summary>Returns false to stop the pipeline (e.g. infected file).</summary>
    Task<bool> RunAsync(StoredFile file, FileVersion version, CancellationToken ct);
}
