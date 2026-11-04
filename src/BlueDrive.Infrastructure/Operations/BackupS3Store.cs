using Amazon.S3;
using Amazon.S3.Model;
using BlueDrive.Infrastructure.Storage;

namespace BlueDrive.Infrastructure.Operations;

/// <summary>
/// Off-server copy of the backup folders in the S3 bucket (<c>backups/&lt;folder&gt;/&lt;part&gt;</c>) plus bucket-wide deletion
/// for the end-of-contract wipe. Uses the same client settings as <see cref="S3FileStorage"/>.
/// </summary>
public sealed class BackupS3Store : IDisposable
{
    public const string Prefix = "backups/";
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    private BackupS3Store(S3Options s3)
    {
        _bucket = s3.Bucket!;
        // No default aws-chunked checksums, which some S3-compatible servers store verbatim.
        var cfg = new AmazonS3Config
        {
            ForcePathStyle = true, AuthenticationRegion = s3.Region,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(s3.ServiceUrl)) cfg.ServiceURL = s3.ServiceUrl; else cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s3.Region);
        _client = new AmazonS3Client(s3.AccessKey, s3.SecretKey, cfg);
    }

    public string Bucket => _bucket;

    /// <summary>Null when no bucket is configured.</summary>
    public static BackupS3Store? TryCreate(S3Options s3) => string.IsNullOrWhiteSpace(s3.Bucket) ? null : new BackupS3Store(s3);

    public async Task UploadFolderAsync(string dir, CancellationToken ct)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
            await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = $"{Prefix}{Path.GetFileName(dir)}/{Path.GetFileName(f)}", FilePath = f }, ct);
    }

    /// <summary>Downloads the folder whose name ends with <paramref name="nameSuffix"/> (backup id without dashes) or equals it.</summary>
    public async Task<string?> DownloadFolderAsync(string nameSuffix, string localRoot, CancellationToken ct)
    {
        string? folder = null;
        var keys = new List<string>();
        await foreach (var o in ListAsync(Prefix, ct))
        {
            var parts = o.Key[Prefix.Length..].Split('/', 2);
            if (parts.Length != 2 || !(parts[0] == nameSuffix || parts[0].EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase))) continue;
            if (folder is not null && folder != parts[0]) continue;
            folder = parts[0];
            keys.Add(o.Key);
        }
        if (folder is null) return null;
        var target = Path.Combine(localRoot, folder);
        Directory.CreateDirectory(target);
        foreach (var key in keys)
        {
            using var res = await _client.GetObjectAsync(_bucket, key, ct);
            await using var fs = File.Create(Path.Combine(target, Path.GetFileName(key)));
            await res.ResponseStream.CopyToAsync(fs, ct);
        }
        return target;
    }

    public async Task DeleteFolderAsync(string folder, CancellationToken ct) => await DeleteAllAsync($"{Prefix}{folder}/", ct);

    /// <summary>Deletes every object under <paramref name="prefix"/> ("" = whole bucket). Returns the count and bytes deleted.</summary>
    public async Task<(long Count, long Bytes)> DeleteAllAsync(string prefix, CancellationToken ct)
    {
        long count = 0, bytes = 0;
        var batch = new List<KeyVersion>(1000);
        await foreach (var o in ListAsync(prefix, ct))
        {
            batch.Add(new KeyVersion { Key = o.Key });
            count++;
            bytes += o.Size ?? 0;
            if (batch.Count == 1000) await FlushAsync();
        }
        await FlushAsync();
        return (count, bytes);

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            var res = await _client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = _bucket, Objects = [.. batch], Quiet = true }, ct);
            if (res.DeleteErrors is { Count: > 0 } errors)
                throw new IOException($"Không xóa được {errors.Count} đối tượng trên S3 (ví dụ {errors[0].Key}: {errors[0].Message}).");
            batch.Clear();
        }
    }

    public async Task<long> CountAsync(string prefix, CancellationToken ct)
    {
        long n = 0;
        await foreach (var _ in ListAsync(prefix, ct)) n++;
        return n;
    }

    private async IAsyncEnumerable<S3Object> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        do
        {
            var res = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, ct);
            foreach (var o in res.S3Objects ?? []) yield return o;
            token = res.IsTruncated == true ? res.NextContinuationToken : null;
        } while (token is not null);
    }

    public void Dispose() => _client.Dispose();
}
