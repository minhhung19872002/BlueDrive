using System.Security.Cryptography;
using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using Microsoft.Extensions.Options;

namespace BlueDrive.Infrastructure.Storage;

/// <summary>Envelope encryption: a random data key per blob, wrapped with the master key (AES-256-GCM).</summary>
public sealed class FileContentService : IFileContentService
{
    private readonly IFileStorage _storage;
    private readonly StorageOptions _opt;
    private readonly byte[]? _masterKey;

    public FileContentService(IFileStorage storage, IOptions<StorageOptions> options)
    {
        _storage = storage;
        _opt = options.Value;
        _masterKey = LoadMasterKey(_opt);
        if (_opt.EncryptAtRest && _masterKey is null)
            throw new InvalidOperationException("Đã bật mã hóa khi lưu (Storage:EncryptAtRest) nhưng thiếu khóa chủ STORAGE_MASTER_KEY (base64 32 byte).");
    }

    public bool EncryptionEnabled => _opt.EncryptAtRest;

    internal static byte[]? LoadMasterKey(StorageOptions o)
    {
        var b64 = o.MasterKey;
        if (string.IsNullOrWhiteSpace(b64) && !string.IsNullOrWhiteSpace(o.MasterKeyFile) && File.Exists(o.MasterKeyFile))
            b64 = File.ReadAllText(o.MasterKeyFile).Trim();
        if (string.IsNullOrWhiteSpace(b64)) return null;
        var key = Convert.FromBase64String(b64.Trim());
        if (key.Length != 32) throw new InvalidOperationException("Khóa chủ STORAGE_MASTER_KEY phải là 32 byte (AES-256) mã hóa base64.");
        return key;
    }

    public async Task<StoredContent> SaveAsync(Stream plaintext, string keyPrefix, CancellationToken ct = default)
    {
        var key = $"{keyPrefix.TrimEnd('/')}/{Guid.CreateVersion7():N}";
        Directory.CreateDirectory(_opt.TempDirectory);
        var tmp = Path.Combine(_opt.TempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            long length;
            string sha;
            string? wrapped = null;
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, true))
            {
                if (_opt.EncryptAtRest)
                {
                    var dataKey = RandomNumberGenerator.GetBytes(32);
                    (length, sha) = await ChunkedAesGcm.EncryptAsync(plaintext, output, dataKey, ct);
                    wrapped = ChunkedAesGcm.WrapKey(_masterKey!, dataKey);
                    CryptographicOperations.ZeroMemory(dataKey);
                }
                else
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buf = new byte[1 << 16];
                    length = 0;
                    int n;
                    while ((n = await plaintext.ReadAsync(buf, ct)) > 0)
                    {
                        hash.AppendData(buf, 0, n);
                        await output.WriteAsync(buf.AsMemory(0, n), ct);
                        length += n;
                    }
                    sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                }
            }
            if (_storage is IFileImportingStorage importer) await importer.ImportFileAsync(key, tmp, ct);
            else
            {
                await using var read = File.OpenRead(tmp);
                await _storage.WriteAsync(key, read, ct);
            }
            return new StoredContent(key, length, sha, _opt.EncryptAtRest, wrapped);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public Task<Stream> OpenReadAsync(FileVersion version, CancellationToken ct = default)
        => OpenReadAsync(version.StorageKey, version.IsEncrypted, version.WrappedKey, ct);

    public async Task<Stream> OpenReadAsync(string storageKey, bool isEncrypted, string? wrappedKey, CancellationToken ct = default)
    {
        if (!isEncrypted)
        {
            var s = await _storage.OpenReadAsync(storageKey, ct);
            return s.CanSeek ? s : new LengthKnownStream(s, await _storage.GetSizeAsync(storageKey, ct));
        }
        if (_masterKey is null) throw new InvalidOperationException("Thiếu khóa chủ để giải mã dữ liệu (STORAGE_MASTER_KEY).");
        var dataKey = ChunkedAesGcm.UnwrapKey(_masterKey, wrappedKey ?? throw new CryptographicException("Thiếu khóa dữ liệu của tệp."));
        return await DecryptingStream.OpenAsync((offset, c) => _storage.OpenReadAtAsync(storageKey, offset, c), dataKey, ct);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default) => _storage.DeleteAsync(storageKey, ct);
}

/// <summary>Wraps a forward-only stream exposing a known Length (needed for Content-Length on S3 plaintext reads).</summary>
internal sealed class LengthKnownStream(Stream inner, long length) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
