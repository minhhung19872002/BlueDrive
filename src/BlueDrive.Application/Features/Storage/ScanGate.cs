using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

/// <summary>
/// Content gate for the malware scan: quarantined files are never served; a version still waiting for its scan is served
/// only to the person who uploaded it (everyone else gets 423 until the Worker has scanned it).
/// </summary>
public static class ScanGate
{
    public const string InfectedMessage = "Tệp đã bị cách ly do phát hiện mã độc.";
    public const string PendingMessage = "Tệp đang được kiểm tra virus. Vui lòng thử lại sau ít phút.";

    public static bool IsPending(ScanStatus status) => status is ScanStatus.Pending or ScanStatus.Scanning;

    /// <summary>Null when <paramref name="userId"/> may read <paramref name="versionId"/>; otherwise the Vietnamese reason.</summary>
    public static async Task<string?> BlockReasonAsync(IAppDbContext db, StoredFile file, Guid? versionId, Guid? userId, CancellationToken ct)
    {
        if (file.ScanStatus == ScanStatus.Infected) return InfectedMessage;
        // The status describes the current version; older versions were scanned while they were current.
        if (!IsPending(file.ScanStatus) || (versionId is { } v && v != file.CurrentVersionId)) return null;
        var current = file.CurrentVersionId;
        if (userId is { } uid && await db.FileVersions.AnyAsync(x => x.Id == current && x.CreatedBy == uid, ct)) return null;
        return PendingMessage;
    }

    public static async Task EnsureAsync(IAppDbContext db, StoredFile file, Guid? versionId, Guid? userId, CancellationToken ct)
    {
        if (await BlockReasonAsync(db, file, versionId, userId, ct) is { } reason) throw new AppException(reason, 423);
    }
}
