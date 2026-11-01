using BlueDrive.Application.Features.Office;
using Microsoft.Extensions.Options;

namespace BlueDrive.Infrastructure.Storage;

public sealed class FileStorageTemp(IOptions<StorageOptions> options) : IFileStorageTemp
{
    public string NewTempPath()
    {
        Directory.CreateDirectory(options.Value.TempDirectory);
        return Path.Combine(options.Value.TempDirectory, "t-" + Guid.NewGuid().ToString("N"));
    }

    public async Task<string> WriteTempAsync(byte[] content, CancellationToken ct)
    {
        var path = NewTempPath();
        await File.WriteAllBytesAsync(path, content, ct);
        return path;
    }
}
