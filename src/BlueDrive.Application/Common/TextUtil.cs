using System.Globalization;
using System.Text;

namespace BlueDrive.Application.Common;

public static class TextUtil
{
    /// <summary>Removes Vietnamese diacritics and lowercases ("Dân Số" → "dan so").</summary>
    public static string Unaccent(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>
    /// ASCII-only fallback for the Content-Disposition <c>filename</c> parameter ("Báo cáo Đầu năm.pdf" → "Bao cao Dau nam.pdf").
    /// Browsers that ignore <c>filename*</c> (Safari for some responses) would otherwise save the download under the URL token.
    /// </summary>
    public static string AsciiFileName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "download";
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var mapped = c switch { 'đ' => 'd', 'Đ' => 'D', _ => c };
            sb.Append(mapped is >= ' ' and <= '~' && mapped is not '"' and not '\\' ? mapped : '_');
        }
        return sb.ToString();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }
}
