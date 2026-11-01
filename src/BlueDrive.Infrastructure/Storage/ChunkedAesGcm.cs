using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BlueDrive.Infrastructure.Storage;

/// <summary>
/// Random-access authenticated encryption format "BDE1":
/// header(32) = magic "BDE1" | version(1) | reserved(3) | chunkSize(int32) | noncePrefix(8) | plaintextLength(int64) | reserved(4),
/// then N chunks of AES-256-GCM(ciphertext(chunkSize, last may be shorter) + tag(16)).
/// Nonce = noncePrefix || chunkIndex(uint32 BE); AAD = header(24) || chunkIndex(int64) so chunks cannot be reordered,
/// swapped between files, or truncated without detection.
/// </summary>
public static class ChunkedAesGcm
{
    public const int HeaderSize = 32;
    public const int TagSize = 16;
    public const int DefaultChunkSize = 64 * 1024;
    private static ReadOnlySpan<byte> Magic => "BDE1"u8;

    public sealed record Header(int ChunkSize, byte[] NoncePrefix, long PlaintextLength)
    {
        public long ChunkCount => PlaintextLength == 0 ? 0 : (PlaintextLength + ChunkSize - 1) / ChunkSize;
        public long CipherLength => HeaderSize + PlaintextLength + ChunkCount * TagSize;
    }

    public static byte[] WriteHeader(Header h)
    {
        var buf = new byte[HeaderSize];
        Magic.CopyTo(buf);
        buf[4] = 1;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(8), h.ChunkSize);
        h.NoncePrefix.CopyTo(buf.AsSpan(12, 8));
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(20), h.PlaintextLength);
        return buf;
    }

    public static Header ReadHeader(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < HeaderSize || !buf[..4].SequenceEqual(Magic) || buf[4] != 1)
            throw new CryptographicException("Dữ liệu mã hóa không hợp lệ (sai định dạng).");
        return new Header(BinaryPrimitives.ReadInt32BigEndian(buf[8..]), buf.Slice(12, 8).ToArray(), BinaryPrimitives.ReadInt64BigEndian(buf[20..]));
    }

    private static void Nonce(Span<byte> nonce, byte[] prefix, long index)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], checked((uint)index));
    }

    private static byte[] Aad(byte[] header, long index)
    {
        var aad = new byte[24 + 8];
        header.AsSpan(0, 24).CopyTo(aad);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(24), index);
        return aad;
    }

    /// <summary>Encrypts <paramref name="input"/> into <paramref name="output"/>, returning plaintext length and SHA-256.</summary>
    public static async Task<(long Length, string Sha256)> EncryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct, int chunkSize = DefaultChunkSize)
    {
        var prefix = RandomNumberGenerator.GetBytes(8);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var gcm = new AesGcm(key, TagSize);
        var plain = new byte[chunkSize];
        var cipher = new byte[chunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[12];
        long total = 0, index = 0;
        // The header (part of every chunk's AAD) carries the plaintext length, so non-seekable input is spooled first.
        Stream source = input;
        string? spool = null;
        if (!input.CanSeek)
        {
            spool = Path.GetTempFileName();
            await using (var fs = new FileStream(spool, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true)) await input.CopyToAsync(fs, ct);
            source = new FileStream(spool, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        }
        try
        {
            var length = source.Length - source.Position;
            var header = WriteHeader(new Header(chunkSize, prefix, length));
            await output.WriteAsync(header, ct);
            while (true)
            {
                var read = await ReadFullAsync(source, plain, ct);
                if (read == 0) break;
                sha.AppendData(plain, 0, read);
                Nonce(nonce, prefix, index);
                gcm.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, Aad(header, index));
                await output.WriteAsync(cipher.AsMemory(0, read), ct);
                await output.WriteAsync(tag, ct);
                total += read;
                index++;
                if (read < chunkSize) break;
            }
            if (total != length) throw new IOException("Độ dài dữ liệu thay đổi trong khi mã hóa.");
            return (total, Convert.ToHexStringLower(sha.GetHashAndReset()));
        }
        finally
        {
            if (spool is not null) await source.DisposeAsync();
        }
    }

    internal static async Task<int> ReadFullAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf[total..], ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    internal static int ReadFull(Stream s, Span<byte> buf)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = s.Read(buf[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Decrypts a single chunk (cipher includes the trailing tag).</summary>
    internal static void DecryptChunk(AesGcm gcm, byte[] header, byte[] prefix, long index, ReadOnlySpan<byte> cipherWithTag, Span<byte> plain)
    {
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, prefix, index);
        var len = cipherWithTag.Length - TagSize;
        gcm.Decrypt(nonce, cipherWithTag[..len], cipherWithTag[len..], plain[..len], Aad(header, index));
    }

    public static string WrapKey(byte[] masterKey, byte[] dataKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dataKey.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(masterKey, TagSize);
        gcm.Encrypt(nonce, dataKey, cipher, tag, "bluedrive-dek"u8);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public static byte[] UnwrapKey(byte[] masterKey, string wrapped)
    {
        var raw = Convert.FromBase64String(wrapped);
        var nonce = raw.AsSpan(0, 12);
        var tag = raw.AsSpan(raw.Length - TagSize);
        var cipher = raw.AsSpan(12, raw.Length - 12 - TagSize);
        var key = new byte[cipher.Length];
        using var gcm = new AesGcm(masterKey, TagSize);
        gcm.Decrypt(nonce, cipher, tag, key, "bluedrive-dek"u8);
        return key;
    }
}

/// <summary>Seekable read-only plaintext view over a BDE1 blob; reopens the underlying blob at chunk boundaries on seek.</summary>
public sealed class DecryptingStream : Stream
{
    private readonly Func<long, CancellationToken, Task<Stream>> _openAt;
    private readonly AesGcm _gcm;
    private readonly byte[] _headerBytes;
    private readonly ChunkedAesGcm.Header _header;
    private readonly byte[] _cipherBuf;
    private readonly byte[] _plainBuf;
    private Stream? _inner;
    private long _innerPos = -1;
    private long _cachedChunk = -1;
    private int _cachedLen;
    private long _position;

    private DecryptingStream(Func<long, CancellationToken, Task<Stream>> openAt, byte[] key, byte[] headerBytes, Stream inner)
    {
        _openAt = openAt;
        _gcm = new AesGcm(key, ChunkedAesGcm.TagSize);
        _headerBytes = headerBytes;
        _header = ChunkedAesGcm.ReadHeader(headerBytes);
        _cipherBuf = new byte[_header.ChunkSize + ChunkedAesGcm.TagSize];
        _plainBuf = new byte[_header.ChunkSize];
        _inner = inner;
        _innerPos = ChunkedAesGcm.HeaderSize;
    }

    public static async Task<DecryptingStream> OpenAsync(Func<long, CancellationToken, Task<Stream>> openAt, byte[] key, CancellationToken ct)
    {
        var inner = await openAt(0, ct);
        var header = new byte[ChunkedAesGcm.HeaderSize];
        if (await ChunkedAesGcm.ReadFullAsync(inner, header, ct) != header.Length) throw new CryptographicException("Dữ liệu mã hóa bị hỏng.");
        return new DecryptingStream(openAt, key, header, inner);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _header.PlaintextLength;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => Length + offset };
        if (target < 0) throw new IOException("Vị trí không hợp lệ.");
        _position = target;
        return _position;
    }

    private long ChunkCipherOffset(long index) => ChunkedAesGcm.HeaderSize + index * (_header.ChunkSize + ChunkedAesGcm.TagSize);

    private int ChunkPlainLength(long index) => (int)Math.Min(_header.ChunkSize, _header.PlaintextLength - index * _header.ChunkSize);

    private async ValueTask LoadChunkAsync(long index, bool async, CancellationToken ct)
    {
        if (_cachedChunk == index) return;
        var offset = ChunkCipherOffset(index);
        if (_inner is null || _innerPos != offset)
        {
            if (_inner is not null) await _inner.DisposeAsync();
            _inner = async ? await _openAt(offset, ct) : _openAt(offset, ct).GetAwaiter().GetResult();
            _innerPos = offset;
        }
        var plainLen = ChunkPlainLength(index);
        var cipherLen = plainLen + ChunkedAesGcm.TagSize;
        var read = async ? await ChunkedAesGcm.ReadFullAsync(_inner, _cipherBuf.AsMemory(0, cipherLen), ct) : ChunkedAesGcm.ReadFull(_inner, _cipherBuf.AsSpan(0, cipherLen));
        if (read != cipherLen) throw new CryptographicException("Dữ liệu mã hóa bị cắt cụt.");
        _innerPos += cipherLen;
        ChunkedAesGcm.DecryptChunk(_gcm, _headerBytes, _header.NoncePrefix, index, _cipherBuf.AsSpan(0, cipherLen), _plainBuf);
        _cachedChunk = index;
        _cachedLen = plainLen;
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, bool async, CancellationToken ct)
    {
        if (_position >= Length || buffer.Length == 0) return 0;
        var index = _position / _header.ChunkSize;
        await LoadChunkAsync(index, async, ct);
        var inChunk = (int)(_position - index * _header.ChunkSize);
        var n = Math.Min(buffer.Length, _cachedLen - inChunk);
        _plainBuf.AsSpan(inChunk, n).CopyTo(buffer.Span);
        _position += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadCoreAsync(buffer.AsMemory(offset, count), false, default).GetAwaiter().GetResult();
    public override int Read(Span<byte> buffer)
    {
        var tmp = new byte[buffer.Length];
        var n = Read(tmp, 0, tmp.Length);
        tmp.AsSpan(0, n).CopyTo(buffer);
        return n;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ReadCoreAsync(buffer, true, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadCoreAsync(buffer.AsMemory(offset, count), true, ct).AsTask();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _inner?.Dispose(); _gcm.Dispose(); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_inner is not null) await _inner.DisposeAsync();
        _gcm.Dispose();
        await base.DisposeAsync();
    }
}
