using System.Security.Cryptography;
using BlueDrive.Application.Common;
using BlueDrive.Infrastructure.Storage;
using FluentAssertions;

namespace BlueDrive.UnitTests.Storage;

public class StorageTests
{
    private static async Task<(byte[] Plain, byte[] Cipher, byte[] Key)> EncryptRandomAsync(int size, int chunk)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var key = RandomNumberGenerator.GetBytes(32);
        using var output = new MemoryStream();
        var (len, sha) = await ChunkedAesGcm.EncryptAsync(new MemoryStream(plain), output, key, CancellationToken.None, chunk);
        len.Should().Be(size);
        sha.Should().Be(Convert.ToHexStringLower(SHA256.HashData(plain)));
        return (plain, output.ToArray(), key);
    }

    private static Task<DecryptingStream> OpenAsync(byte[] cipher, byte[] key) => DecryptingStream.OpenAsync((offset, _) =>
    {
        var ms = new MemoryStream(cipher) { Position = offset };
        return Task.FromResult<Stream>(ms);
    }, key, CancellationToken.None);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(10_000)]
    [InlineData(1_000_003)]
    public async Task Chunked_encryption_round_trips(int size)
    {
        var (plain, cipher, key) = await EncryptRandomAsync(size, 4096);
        if (size >= 64) cipher.AsSpan(ChunkedAesGcm.HeaderSize, 64).ToArray().Should().NotEqual(plain.Take(64));
        await using var s = await OpenAsync(cipher, key);
        s.Length.Should().Be(size);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        ms.ToArray().Should().Equal(plain);
    }

    [Fact]
    public async Task Random_access_reads_match_plaintext_for_http_range()
    {
        var (plain, cipher, key) = await EncryptRandomAsync(250_000, 4096);
        await using var s = await OpenAsync(cipher, key);
        foreach (var (offset, count) in new[] { (0L, 10), (4095L, 3), (123_456L, 9000), (249_990L, 10), (8192L, 4096) })
        {
            s.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[count];
            var read = 0;
            while (read < count) read += await s.ReadAsync(buf.AsMemory(read));
            buf.Should().Equal(plain.AsSpan((int)offset, count).ToArray(), $"range {offset}+{count}");
        }
    }

    [Fact]
    public async Task Tampered_ciphertext_is_detected()
    {
        var (_, cipher, key) = await EncryptRandomAsync(20_000, 4096);
        cipher[ChunkedAesGcm.HeaderSize + 5000] ^= 0xFF;
        await using var s = await OpenAsync(cipher, key);
        var act = async () => { using var ms = new MemoryStream(); await s.CopyToAsync(ms); };
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public void Data_keys_are_wrapped_with_the_master_key()
    {
        var master = RandomNumberGenerator.GetBytes(32);
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrapped = ChunkedAesGcm.WrapKey(master, dek);
        ChunkedAesGcm.UnwrapKey(master, wrapped).Should().Equal(dek);
        var wrongMaster = RandomNumberGenerator.GetBytes(32);
        var act = () => ChunkedAesGcm.UnwrapKey(wrongMaster, wrapped);
        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData("photo.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData("photo.jpg", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, false)]
    [InlineData("report.docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true)]
    [InlineData("report.pdf", new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, false)]
    [InlineData("virus.exe", new byte[] { 0x4D, 0x5A }, false)]
    [InlineData("script.ps1", new byte[] { 0x23, 0x20 }, false)]
    [InlineData("note.txt", new byte[] { 0x44, 0xC3, 0xA2, 0x6E }, true)]
    [InlineData("clip.mp4", new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, true)]
    public void Magic_bytes_are_verified(string name, byte[] header, bool ok)
        => (new FileTypeInspector().ValidateContent(name, header) is null).Should().Be(ok);

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\a<b>.txt", "ab.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("  Báo cáo dân số.docx  ", "Báo cáo dân số.docx")]
    [InlineData("...", "Không tên")]
    public void File_names_are_sanitized(string input, string expected) => FileNameSanitizer.Sanitize(input).Should().Be(expected);
}
