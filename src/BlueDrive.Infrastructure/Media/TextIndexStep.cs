using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Infrastructure.Media;

/// <summary>
/// Search inside documents: after the malware scan, the text of the version (<see cref="DocumentText"/>) is stored in file_texts,
/// whose accent-insensitive tsvector the search matches besides the name, title, description and tags.
/// </summary>
public sealed class TextIndexStep(IAppDbContext db, PlainCopy plain, TimeProvider clock, ILogger<TextIndexStep> logger) : IFileProcessingStep
{
    /// <summary>Larger files are not read for their text (a 1 GB PDF is a scan of images, not something to type a word from).</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    public FileProcessingStage Stage => FileProcessingStage.Derivatives;
    public int Order => 30;

    public async Task<bool> RunAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        await IndexAsync(file, version, ct);
        return true;
    }

    /// <summary>Stores (or clears) the text of <paramref name="version"/>; best effort, never blocks the file.</summary>
    public async Task IndexAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        var row = await db.FileTexts.FirstOrDefaultAsync(t => t.FileId == file.Id, ct);
        string? text = null;
        if (DocumentText.Supports(file.Extension) && version.SizeBytes <= MaxBytes && file.ScanStatus != ScanStatus.Infected)
        {
            string? tmp = null;
            try
            {
                tmp = await plain.ExtractAsync(version, file.Extension, ct);
                text = await DocumentText.ExtractAsync(tmp, file.Extension, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Không đọc được nội dung tệp {FileId} ({Ext}) để tìm kiếm", file.Id, file.Extension);
            }
            finally { if (tmp is not null) File.Delete(tmp); }
        }
        // An empty row also marks the version as done, so the backfill does not read it again.
        if (row is null) db.FileTexts.Add(row = new FileText { FileId = file.Id });
        row.VersionId = version.Id;
        row.Text = text ?? "";
        row.ExtractedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
}
