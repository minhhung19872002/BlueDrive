using Amazon.S3;
using Amazon.S3.Model;
using BlueDrive.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BlueDrive.Infrastructure.Storage;

public sealed class StorageOptions
{
    /// <summary>"local" (docker volume) or "s3".</summary>
    public string Provider { get; set; } = "local";
    public string LocalRoot { get; set; } = "storage-data";
    public bool EncryptAtRest { get; set; } = true;
    /// <summary>Base64 of a 32-byte AES-256 key (env STORAGE_MASTER_KEY) or a path to a mounted key file.</summary>
    public string? MasterKey { get; set; }
    public string? MasterKeyFile { get; set; }
    public S3Options S3 { get; set; } = new();
    /// <summary>Scratch directory for in-flight uploads/encryption (always on local disk).</summary>
    public string TempDirectory => Path.Combine(LocalRoot, ".tmp");
}

public sealed class S3Options
{
    public string? ServiceUrl { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Region { get; set; } = "us-east-1";
}

/// <summary>Storage that can adopt a finished local temp file without copying (rename on the same volume).</summary>
public interface IFileImportingStorage
{
    Task ImportFileAsync(string key, string localPath, CancellationToken ct = default);
}

/// <summary>Blobs on the local file system (docker volume file_storage). Keys never leave the server.</summary>
public sealed class LocalFileStorage(IOptions<StorageOptions> options) : IFileStorage, IFileImportingStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.LocalRoot);

    private string PathOf(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("..") || Path.IsPathRooted(key) || key.Contains('\\'))
            throw new ArgumentException("Khóa lưu trữ không hợp lệ.", nameof(key));
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root, StringComparison.Ordinal)) throw new ArgumentException("Khóa lưu trữ không hợp lệ.", nameof(key));
        return full;
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".part-" + Guid.NewGuid().ToString("N");
        await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
            await content.CopyToAsync(fs, 1 << 16, ct);
        File.Move(tmp, path, true);
    }

    public Task ImportFileAsync(string key, string localPath, CancellationToken ct = default)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { File.Move(localPath, path, true); }
        catch (IOException)
        {
            // Different volume: fall back to copy.
            File.Copy(localPath, path, true);
            File.Delete(localPath);
        }
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => OpenReadAtAsync(key, 0, ct);

    public Task<Stream> OpenReadAtAsync(string key, long offset, CancellationToken ct = default)
    {
        var path = PathOf(key);
        if (!File.Exists(path)) throw new FileNotFoundException("Không tìm thấy dữ liệu tệp trên kho lưu trữ.", key);
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(fs);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathOf(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<long> GetSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(new FileInfo(PathOf(key)).Length);

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var dir = prefix.Trim('/') is { Length: > 0 } p ? PathOf(p) : _root;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Contains(".part-")) continue;
            var info = new FileInfo(f);
            yield return new StoredObject(Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'), info.Length, info.LastWriteTimeUtc);
            await Task.Yield();
        }
    }
}

/// <summary>S3-compatible object storage (MinIO, Viettel/FPT/CMC Cloud S3...).</summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _tempDir;

    public S3FileStorage(IOptions<StorageOptions> options)
    {
        var o = options.Value.S3;
        _bucket = o.Bucket ?? throw new InvalidOperationException("Thiếu cấu hình Storage:S3:Bucket.");
        // Checksums only when the operation requires them: the SDK's default aws-chunked trailing checksums are stored
        // verbatim (corrupting the object) by several S3-compatible servers, e.g. SeaweedFS.
        var cfg = new AmazonS3Config
        {
            ForcePathStyle = true, AuthenticationRegion = o.Region,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl)) cfg.ServiceURL = o.ServiceUrl; else cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region);
        _client = new AmazonS3Client(o.AccessKey, o.SecretKey, cfg);
        _tempDir = options.Value.TempDirectory;
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct = default)
    {
        if (content.CanSeek)
        {
            await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = content, AutoCloseStream = false }, ct);
            return;
        }
        Directory.CreateDirectory(_tempDir);
        var tmp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = File.Create(tmp)) await content.CopyToAsync(fs, ct);
            await using var read = File.OpenRead(tmp);
            await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = read, AutoCloseStream = false }, ct);
        }
        finally { File.Delete(tmp); }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => OpenReadAtAsync(key, 0, ct);

    public async Task<Stream> OpenReadAtAsync(string key, long offset, CancellationToken ct = default)
    {
        var req = new GetObjectRequest { BucketName = _bucket, Key = key };
        if (offset > 0)
        {
            var size = await GetSizeAsync(key, ct);
            req.ByteRange = new ByteRange(offset, Math.Max(offset, size - 1));
        }
        var res = await _client.GetObjectAsync(req, ct);
        return res.ResponseStream;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try { await _client.GetObjectMetadataAsync(_bucket, key, ct); return true; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) => _client.DeleteObjectAsync(_bucket, key, ct);

    public async Task<long> GetSizeAsync(string key, CancellationToken ct = default)
        => (await _client.GetObjectMetadataAsync(_bucket, key, ct)).ContentLength;

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? token = null;
        do
        {
            var res = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, ct);
            foreach (var o in res.S3Objects ?? [])
                yield return new StoredObject(o.Key, o.Size ?? 0, o.LastModified?.ToUniversalTime() ?? DateTime.UtcNow);
            token = res.IsTruncated == true ? res.NextContinuationToken : null;
        } while (token is not null);
    }

    public void Dispose() => _client.Dispose();
}
