using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using nClam;
using SkiaSharp;

namespace BlueDrive.Infrastructure.Media;

/// <summary>Thumbnail/preview keys may carry the wrapped data key: "{storageKey}#{wrappedKey}".</summary>
public static class DerivedKeys
{
    public static string Compose(StoredContent c) => c.WrappedKey is null ? c.StorageKey : $"{c.StorageKey}#{c.WrappedKey}";

    public static (string Key, bool Encrypted, string? Wrapped) Parse(string composed)
    {
        var i = composed.IndexOf('#');
        return i < 0 ? (composed, false, null) : (composed[..i], true, composed[(i + 1)..]);
    }
}

/// <summary>Decrypts a version to a local temp file for external tools (ffmpeg, pdftoppm, clamd).</summary>
public sealed class PlainCopy(IFileContentService content, IOptions<StorageOptions> options)
{
    public async Task<string> ExtractAsync(FileVersion v, string extension, CancellationToken ct)
    {
        Directory.CreateDirectory(options.Value.TempDirectory);
        var path = Path.Combine(options.Value.TempDirectory, $"proc-{Guid.NewGuid():N}.{extension}");
        await using var src = await content.OpenReadAsync(v, ct);
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst, ct);
        return path;
    }
}

public sealed class ClamAvOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "clamav";
    public int Port { get; set; } = 3310;
}

/// <summary>Malware scan (ClamAV). Infected files are quarantined: downloads are refused, an alert is raised.</summary>
public sealed class MalwareScanStep(IOptions<ClamAvOptions> options, IFileContentService content, IAlertService alerts, IAuditLogger audit, ILogger<MalwareScanStep> logger)
    : IFileProcessingStep
{
    public int Order => 10;

    public async Task<bool> RunAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            file.ScanStatus = ScanStatus.Skipped;
            return true;
        }
        file.ScanStatus = ScanStatus.Scanning;
        var clam = new ClamClient(options.Value.Host, options.Value.Port) { MaxStreamSize = long.MaxValue };
        await using var stream = await content.OpenReadAsync(version, ct);
        var result = await clam.SendAndScanFileAsync(stream, ct);
        switch (result.Result)
        {
            case ClamScanResults.Clean:
                file.ScanStatus = ScanStatus.Clean;
                return true;
            case ClamScanResults.VirusDetected:
                file.ScanStatus = ScanStatus.Infected;
                var virus = string.Join(", ", result.InfectedFiles?.Select(i => i.VirusName) ?? []);
                await alerts.RaiseAsync(AlertType.MalwareDetected, AlertSeverity.Critical,
                    $"Phát hiện mã độc ({virus}) trong tệp \"{file.Name}\" do người dùng tải lên. Tệp đã bị cách ly.", file.CreatedBy, file.WorkspaceId, ct: ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { quarantine = true, virus }), ct);
                return false;
            default:
                logger.LogWarning("ClamAV không quét được tệp {FileId}: {Raw}", file.Id, result.RawResult);
                throw new IOException("ClamAV chưa sẵn sàng, sẽ thử lại.");
        }
    }
}

/// <summary>Metadata + thumbnail (320px) and preview (1600px) generation for images, video, audio, PDF and Office documents.</summary>
public sealed class ThumbnailStep(IFileContentService content, PlainCopy plain, IOfficeEditorProvider office, IContentTicketService tickets, ILogger<ThumbnailStep> logger)
    : IFileProcessingStep
{
    public int Order => 20;
    private const int ThumbSize = 320, PreviewSize = 1600;

    public async Task<bool> RunAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        try
        {
            switch (file.MediaType)
            {
                case MediaType.Image: await ImageAsync(file, version, ct); break;
                case MediaType.Video: await VideoAsync(file, version, ct); break;
                case MediaType.Audio: await AudioAsync(file, version, ct); break;
                case MediaType.Pdf: await PdfAsync(file, version, ct); break;
                case MediaType.Document or MediaType.Spreadsheet or MediaType.Presentation when office.CanView(file.Extension): await OfficeAsync(file, version, ct); break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Previews are best-effort: a broken thumbnail must never block the file.
            logger.LogWarning(ex, "Không tạo được ảnh thu nhỏ cho {FileId} ({Ext})", file.Id, file.Extension);
        }
        return true;
    }

    private async Task<string> SaveImageAsync(Guid fileId, SKBitmap source, int max, CancellationToken ct)
    {
        var scale = Math.Min(1.0, (double)max / Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)(source.Width * scale));
        var h = Math.Max(1, (int)(source.Height * scale));
        using var resized = scale < 1 ? source.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell)) : source.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        await using var ms = new MemoryStream(data.ToArray());
        return DerivedKeys.Compose(await content.SaveAsync(ms, $"thumbs/{fileId:N}", ct));
    }

    private async Task StoreBitmapAsync(StoredFile file, SKBitmap bmp, bool withPreview, CancellationToken ct)
    {
        file.ThumbnailKey = await SaveImageAsync(file.Id, bmp, ThumbSize, ct);
        if (withPreview) file.PreviewKey = await SaveImageAsync(file.Id, bmp, PreviewSize, ct);
    }

    private async Task ImageAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        await using var stream = await content.OpenReadAsync(version, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        ms.Position = 0;
        using var codec = SKCodec.Create(ms);
        SKBitmap? bmp = codec is null ? null : SKBitmap.Decode(codec);
        if (bmp is null)
        {
            // HEIC/TIFF/SVG: let ffmpeg rasterize the first frame.
            var tmp = await plain.ExtractAsync(version, file.Extension, ct);
            try { bmp = await FfmpegFrameAsync(tmp, null, ct); }
            finally { File.Delete(tmp); }
        }
        if (bmp is null) return;
        using (bmp)
        {
            file.Width = bmp.Width;
            file.Height = bmp.Height;
            await StoreBitmapAsync(file, bmp, Math.Max(bmp.Width, bmp.Height) > PreviewSize || file.Extension is "heic" or "heif" or "tif" or "tiff" or "bmp", ct);
        }
    }

    private async Task VideoAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        var tmp = await plain.ExtractAsync(version, file.Extension, ct);
        try
        {
            await ProbeAsync(file, tmp, ct);
            var at = file.DurationSeconds is > 4 ? 3 : 0;
            using var bmp = await FfmpegFrameAsync(tmp, at, ct);
            if (bmp is not null) await StoreBitmapAsync(file, bmp, false, ct);
        }
        finally { File.Delete(tmp); }
    }

    private async Task AudioAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        var tmp = await plain.ExtractAsync(version, file.Extension, ct);
        try
        {
            await ProbeAsync(file, tmp, ct);
            using var cover = await FfmpegFrameAsync(tmp, null, ct); // embedded cover art, if any
            if (cover is not null) await StoreBitmapAsync(file, cover, false, ct);
        }
        finally { File.Delete(tmp); }
    }

    private async Task PdfAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        var tmp = await plain.ExtractAsync(version, "pdf", ct);
        var outPrefix = tmp + "-p";
        try
        {
            await RunAsync("pdftoppm", ["-f", "1", "-l", "1", "-singlefile", "-jpeg", "-scale-to", PreviewSize.ToString(), tmp, outPrefix], ct);
            var jpg = outPrefix + ".jpg";
            if (!File.Exists(jpg)) return;
            using var bmp = SKBitmap.Decode(jpg);
            if (bmp is not null) await StoreBitmapAsync(file, bmp, true, ct);
            File.Delete(jpg);
        }
        finally { File.Delete(tmp); }
    }

    private async Task OfficeAsync(StoredFile file, FileVersion version, CancellationToken ct)
    {
        if (file.CreatedBy is not { } owner) return;
        var ticket = tickets.Issue(new ContentTicket(owner, "file", file.Id, version.Id, true, null, null), TimeSpan.FromMinutes(30));
        var png = await office.RenderThumbnailAsync($"{office.CallbackBaseUrl}/api/v1/content/{Uri.EscapeDataString(ticket)}", file.Extension, $"thumb-{version.Id:N}", ct);
        if (png is null) return;
        using var bmp = SKBitmap.Decode(png);
        if (bmp is not null) await StoreBitmapAsync(file, bmp, true, ct);
    }

    private static async Task ProbeAsync(StoredFile file, string path, CancellationToken ct)
    {
        var json = await RunAsync("ffprobe", ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var seconds)) file.DurationSeconds = Math.Round(seconds, 2);
        if (doc.RootElement.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
                if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "video" && s.TryGetProperty("width", out var w) && s.TryGetProperty("height", out var h))
                {
                    file.Width = w.GetInt32();
                    file.Height = h.GetInt32();
                    break;
                }
    }

    private static async Task<SKBitmap?> FfmpegFrameAsync(string path, double? atSeconds, CancellationToken ct)
    {
        var output = path + "-frame.jpg";
        var args = new List<string> { "-y", "-v", "error" };
        if (atSeconds is { } s) args.AddRange(["-ss", s.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", path, "-frames:v", "1", "-vf", $"scale='min({PreviewSize},iw)':-2", output]);
        try
        {
            await RunAsync("ffmpeg", args, ct);
            return File.Exists(output) ? SKBitmap.Decode(output) : null;
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Không chạy được {exe}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        await p.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        if (p.ExitCode != 0) throw new IOException($"{exe} lỗi (mã {p.ExitCode}): {(await stderr).Trim()}");
        return output;
    }
}
