using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Enums;

namespace BlueDrive.Infrastructure.Storage;

/// <summary>Extension → MIME/media type mapping and magic-byte verification (spec §4.7).</summary>
public sealed class FileTypeInspector : IFileTypeInspector
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "bat", "cmd", "com", "msi", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "scr", "pif", "cpl",
        "sh", "bash", "jar", "apk", "app", "dmg", "lnk", "reg", "hta", "msc", "gadget", "inf", "elf", "bin", "run", "php", "asp", "aspx", "jsp",
    };

    private static readonly Dictionary<string, (string Mime, MediaType Type)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = ("image/jpeg", MediaType.Image), ["jpeg"] = ("image/jpeg", MediaType.Image), ["png"] = ("image/png", MediaType.Image),
        ["gif"] = ("image/gif", MediaType.Image), ["webp"] = ("image/webp", MediaType.Image), ["svg"] = ("image/svg+xml", MediaType.Image),
        ["bmp"] = ("image/bmp", MediaType.Image), ["tif"] = ("image/tiff", MediaType.Image), ["tiff"] = ("image/tiff", MediaType.Image),
        ["heic"] = ("image/heic", MediaType.Image), ["heif"] = ("image/heif", MediaType.Image),
        ["mp4"] = ("video/mp4", MediaType.Video), ["m4v"] = ("video/mp4", MediaType.Video), ["mov"] = ("video/quicktime", MediaType.Video),
        ["avi"] = ("video/x-msvideo", MediaType.Video), ["mkv"] = ("video/x-matroska", MediaType.Video), ["webm"] = ("video/webm", MediaType.Video),
        ["mp3"] = ("audio/mpeg", MediaType.Audio), ["wav"] = ("audio/wav", MediaType.Audio), ["aac"] = ("audio/aac", MediaType.Audio),
        ["m4a"] = ("audio/mp4", MediaType.Audio), ["ogg"] = ("audio/ogg", MediaType.Audio), ["flac"] = ("audio/flac", MediaType.Audio),
        ["doc"] = ("application/msword", MediaType.Document),
        ["docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", MediaType.Document),
        ["docm"] = ("application/vnd.ms-word.document.macroEnabled.12", MediaType.Document),
        ["odt"] = ("application/vnd.oasis.opendocument.text", MediaType.Document), ["rtf"] = ("application/rtf", MediaType.Document),
        ["txt"] = ("text/plain", MediaType.Document),
        ["xls"] = ("application/vnd.ms-excel", MediaType.Spreadsheet),
        ["xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", MediaType.Spreadsheet),
        ["xlsm"] = ("application/vnd.ms-excel.sheet.macroEnabled.12", MediaType.Spreadsheet),
        ["ods"] = ("application/vnd.oasis.opendocument.spreadsheet", MediaType.Spreadsheet), ["csv"] = ("text/csv", MediaType.Spreadsheet),
        ["ppt"] = ("application/vnd.ms-powerpoint", MediaType.Presentation),
        ["pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", MediaType.Presentation),
        ["pptm"] = ("application/vnd.ms-powerpoint.presentation.macroEnabled.12", MediaType.Presentation),
        ["odp"] = ("application/vnd.oasis.opendocument.presentation", MediaType.Presentation),
        ["pdf"] = ("application/pdf", MediaType.Pdf), ["epub"] = ("application/epub+zip", MediaType.Epub),
        ["psd"] = ("image/vnd.adobe.photoshop", MediaType.Design), ["ai"] = ("application/postscript", MediaType.Design),
        ["indd"] = ("application/x-indesign", MediaType.Design),
        ["zip"] = ("application/zip", MediaType.Archive), ["rar"] = ("application/vnd.rar", MediaType.Archive), ["7z"] = ("application/x-7z-compressed", MediaType.Archive),
    };

    private static readonly byte[] Zip = [0x50, 0x4B];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Accepted signatures per extension; empty = no reliable signature (text formats).</summary>
    private static readonly Dictionary<string, byte[][]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = [[0xFF, 0xD8, 0xFF]], ["jpeg"] = [[0xFF, 0xD8, 0xFF]], ["png"] = [[0x89, 0x50, 0x4E, 0x47]],
        ["gif"] = [[0x47, 0x49, 0x46, 0x38]], ["bmp"] = [[0x42, 0x4D]], ["tif"] = [[0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A]],
        ["tiff"] = [[0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A]], ["pdf"] = [[0x25, 0x50, 0x44, 0x46]],
        ["zip"] = [Zip], ["docx"] = [Zip], ["xlsx"] = [Zip], ["pptx"] = [Zip], ["docm"] = [Zip], ["xlsm"] = [Zip], ["pptm"] = [Zip], ["odt"] = [Zip], ["ods"] = [Zip], ["odp"] = [Zip], ["epub"] = [Zip],
        ["doc"] = [Ole], ["xls"] = [Ole], ["ppt"] = [Ole], ["rar"] = [[0x52, 0x61, 0x72, 0x21]], ["7z"] = [[0x37, 0x7A, 0xBC, 0xAF]],
        ["psd"] = [[0x38, 0x42, 0x50, 0x53]], ["flac"] = [[0x66, 0x4C, 0x61, 0x43]], ["ogg"] = [[0x4F, 0x67, 0x67, 0x53]],
        ["mkv"] = [[0x1A, 0x45, 0xDF, 0xA3]], ["webm"] = [[0x1A, 0x45, 0xDF, 0xA3]], ["rtf"] = [[0x7B, 0x5C, 0x72, 0x74]],
        ["mp3"] = [[0x49, 0x44, 0x33], [0xFF, 0xFB], [0xFF, 0xF3], [0xFF, 0xF2], [0xFF, 0xFA]],
        ["aac"] = [[0xFF, 0xF1], [0xFF, 0xF9]],
    };

    public FileTypeInfo FromName(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return Map.TryGetValue(ext, out var m) ? new FileTypeInfo(ext, m.Mime, m.Type) : new FileTypeInfo(ext, "application/octet-stream", MediaType.Other);
    }

    public bool IsBlockedExtension(string extension) => Blocked.Contains(extension.TrimStart('.'));

    public string? ValidateContent(string fileName, ReadOnlySpan<byte> header)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (IsBlockedExtension(ext)) return $"Không cho phép tải lên tệp thực thi/kịch bản (.{ext}).";
        // Executables disguised under any extension (PE "MZ", ELF, Mach-O, shebang scripts).
        if (header.Length >= 2 && header[0] == 0x4D && header[1] == 0x5A) return "Tệp có nội dung là chương trình thực thi, không được phép tải lên.";
        if (header.Length >= 4 && header[..4].SequenceEqual(new byte[] { 0x7F, 0x45, 0x4C, 0x46 })) return "Tệp có nội dung là chương trình thực thi (ELF), không được phép tải lên.";
        if (header.Length >= 4 && (header[..4].SequenceEqual(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }) || header[..4].SequenceEqual(new byte[] { 0xFE, 0xED, 0xFA, 0xCF })))
            return "Tệp có nội dung là chương trình thực thi, không được phép tải lên.";
        if (header.Length >= 2 && header[0] == (byte)'#' && header[1] == (byte)'!' && ext is not ("txt" or "csv"))
            return "Tệp chứa kịch bản thực thi, không được phép tải lên.";
        if (header.Length == 0) return null;

        if (Signatures.TryGetValue(ext, out var sigs))
        {
            foreach (var s in sigs) if (header.Length >= s.Length && header[..s.Length].SequenceEqual(s)) return null;
            return $"Nội dung tệp không khớp với định dạng .{ext}. Vui lòng kiểm tra lại tệp.";
        }
        return ext switch
        {
            // ISO-BMFF containers: "ftyp" at offset 4.
            "mp4" or "m4v" or "mov" or "m4a" or "heic" or "heif" => header.Length >= 8 && header.Slice(4, 4).SequenceEqual("ftyp"u8) || ext == "mov" && header.Length >= 8 && (header.Slice(4, 4).SequenceEqual("moov"u8) || header.Slice(4, 4).SequenceEqual("wide"u8) || header.Slice(4, 4).SequenceEqual("mdat"u8))
                ? null : $"Nội dung tệp không khớp với định dạng .{ext}.",
            "avi" or "wav" or "webp" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) ? null : $"Nội dung tệp không khớp với định dạng .{ext}.",
            "svg" => LooksLikeText(header) ? null : "Tệp SVG không hợp lệ.",
            "txt" or "csv" => LooksLikeText(header) ? null : $"Tệp .{ext} phải là văn bản thuần.",
            _ => null,
        };
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> b)
    {
        var limit = Math.Min(b.Length, 1024);
        for (var i = 0; i < limit; i++) if (b[i] == 0) return false;
        return true;
    }
}
