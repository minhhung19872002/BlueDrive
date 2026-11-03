using System.Diagnostics;
using System.Security.Cryptography;

namespace BlueDrive.Sync.Client;

/// <summary>Token bucket in bytes per second; 0 = unlimited. The limit can change while transfers run (settings "Băng thông").</summary>
public sealed class BandwidthLimiter(long bytesPerSecond = 0)
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _tokens;
    private double _last;

    public long BytesPerSecond { get; set; } = bytesPerSecond;

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var rate = BytesPerSecond;
                if (rate <= 0) return;
                var now = _clock.Elapsed.TotalSeconds;
                // Burst of at most one second worth of data.
                _tokens = Math.Min(rate, _tokens + (now - _last) * rate);
                _last = now;
                if (_tokens >= bytes || _tokens >= rate)
                {
                    _tokens -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds(Math.Min(1, (Math.Min(bytes, rate) - _tokens) / rate));
            }
            await Task.Delay(wait, ct);
        }
    }
}

/// <summary>Read-only wrapper that paces reads through a <see cref="BandwidthLimiter"/> (uploads).</summary>
public sealed class ThrottledStream(Stream inner, BandwidthLimiter limiter, bool leaveOpen = false) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], ct);
        if (n > 0) await limiter.WaitAsync(n, ct);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }
}

public static class ContentHash
{
    /// <summary>SHA-256 (lowercase hex, the server's format) computed while streaming; the stream is read to its end.</summary>
    public static async Task<string> Sha256Async(Stream s, CancellationToken ct = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0) sha.AppendData(buffer, 0, n);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
