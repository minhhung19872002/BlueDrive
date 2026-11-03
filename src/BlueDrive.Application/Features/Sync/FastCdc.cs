using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace BlueDrive.Sync.Core;

/// <summary>One content-defined chunk: position in the file, length, SHA-256 (hex).</summary>
public sealed record ContentChunk(long Offset, int Length, string Hash);

/// <summary>
/// Content-defined chunking (FastCDC, Xia et al. 2016, normalized chunking level 2) for delta uploads (Sync-Phase 12): an edit in the middle
/// of a large file only changes the chunks around it, so only those are sent. Shared by the desktop client and the BlueDrive server
/// (BlueDrive.Sync.Core links this file): both sides must cut the same boundaries — the gear table is fixed (SplitMix64 from a constant seed).
/// </summary>
public static class FastCdc
{
    public const int MinSize = 32 * 1024, AvgSize = 128 * 1024, MaxSize = 512 * 1024;

    // log2(AvgSize) = 17: "small" mask with 19 bits before the average size, "large" mask with 15 bits after it (normalization level 2).
    private const ulong MaskS = ((1UL << 19) - 1) << (64 - 19);
    private const ulong MaskL = ((1UL << 15) - 1) << (64 - 15);
    private static readonly ulong[] Gear = BuildGear();

    private static ulong[] BuildGear()
    {
        var table = new ulong[256];
        var x = 0x426C75654472697EUL; // "BlueDri~"
        for (var i = 0; i < 256; i++)
        {
            // SplitMix64
            x += 0x9E3779B97F4A7C15UL;
            var z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }
        return table;
    }

    /// <summary>Length of the first chunk of <paramref name="data"/> (all of it when shorter than the minimum or at the end of the file).</summary>
    public static int Cut(ReadOnlySpan<byte> data)
    {
        var n = data.Length;
        if (n <= MinSize) return n;
        if (n > MaxSize) n = MaxSize;
        var normal = Math.Min(AvgSize, n);
        ulong fp = 0;
        var i = MinSize;
        for (; i < normal; i++)
        {
            fp = (fp << 1) + Gear[data[i]];
            if ((fp & MaskS) == 0) return i + 1;
        }
        for (; i < n; i++)
        {
            fp = (fp << 1) + Gear[data[i]];
            if ((fp & MaskL) == 0) return i + 1;
        }
        return n;
    }

    /// <summary>Chunks of a stream, read sequentially from its current position.</summary>
    public static async IAsyncEnumerable<ContentChunk> SplitAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buf = new byte[MaxSize];
        var filled = 0;
        long offset = 0;
        var end = false;
        while (true)
        {
            while (!end && filled < MaxSize)
            {
                var n = await stream.ReadAsync(buf.AsMemory(filled, MaxSize - filled), ct);
                if (n == 0) end = true;
                filled += n;
            }
            if (filled == 0) yield break;
            var cut = end && filled <= MinSize ? filled : Cut(buf.AsSpan(0, filled));
            yield return new ContentChunk(offset, cut, Convert.ToHexStringLower(SHA256.HashData(buf.AsSpan(0, cut))));
            offset += cut;
            filled -= cut;
            Buffer.BlockCopy(buf, cut, buf, 0, filled);
        }
    }

    public static async Task<List<ContentChunk>> SplitAllAsync(Stream stream, CancellationToken ct = default)
    {
        var list = new List<ContentChunk>();
        await foreach (var c in SplitAsync(stream, ct)) list.Add(c);
        return list;
    }
}
