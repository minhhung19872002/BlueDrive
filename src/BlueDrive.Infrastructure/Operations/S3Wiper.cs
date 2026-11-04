using BlueDrive.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueDrive.Infrastructure.Operations;

public sealed record S3WipeReport(string Bucket, long DataObjects, long DataBytes, long BackupObjects, long BackupBytes, long Remaining);

/// <summary>
/// End-of-contract wipe of the S3 bucket (HSMT 3.6): stored blobs and the S3 backup copies. Run by scripts/secure-wipe.* through
/// "worker wipe-s3" before the volumes are removed; the counts go into the deletion record.
/// </summary>
public sealed class S3Wiper(IOptions<StorageOptions> storage, ILogger<S3Wiper> logger)
{
    /// <summary>Null when no bucket is configured.</summary>
    public async Task<S3WipeReport?> WipeAsync(CancellationToken ct)
    {
        using var store = BackupS3Store.TryCreate(storage.Value.S3);
        if (store is null) return null;
        var backups = await store.DeleteAllAsync(BackupS3Store.Prefix, ct);
        var data = await store.DeleteAllAsync("", ct);
        var remaining = await store.CountAsync("", ct);
        logger.LogWarning("Đã xóa bucket S3 {Bucket}: {Data} đối tượng dữ liệu, {Backups} đối tượng sao lưu, còn lại {Remaining}",
            store.Bucket, data.Count, backups.Count, remaining);
        return new S3WipeReport(store.Bucket, data.Count, data.Bytes, backups.Count, backups.Bytes, remaining);
    }
}
