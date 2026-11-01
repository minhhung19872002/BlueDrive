namespace BlueDrive.Application.Common;

/// <summary>Removes path separators/control chars and reserved names from user-supplied file names.</summary>
public static class FileNameSanitizer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };

    public static string Sanitize(string? name)
    {
        var n = (name ?? "").Normalize(System.Text.NormalizationForm.FormC);
        n = Path.GetFileName(n.Replace('\\', '/'));
        var chars = n.Where(c => !char.IsControl(c) && c is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray();
        n = new string(chars).Trim().TrimEnd('.', ' ');
        if (n.Length == 0 || n is "." or "..") n = "Không tên";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(n))) n = "_" + n;
        if (n.Length > 200)
        {
            var ext = Path.GetExtension(n);
            n = n[..(200 - ext.Length)] + ext;
        }
        return n;
    }
}
