using System.Globalization;
using System.Text;

namespace BlueDrive.Sync.Core;

/// <summary>
/// Server names → names valid on Windows (spec 6.4). Characters Windows forbids become look-alike Unicode characters (as OneDrive
/// does: ":" → "꞉" U+A789…), trailing dots / spaces and reserved device names are made valid, everything is NFC. The original
/// server name is kept in the sync state, so renames are pushed back exactly.
/// </summary>
public static class NameMapper
{
    private static readonly Dictionary<char, char> LookAlikes = new()
    {
        ['\\'] = '＼', ['/'] = '／', [':'] = '꞉', ['*'] = '＊', ['?'] = '？', ['"'] = '＂', ['<'] = '＜', ['>'] = '＞', ['|'] = '｜',
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Nfc(string s) => s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC);

    /// <summary>Local (Windows-safe, NFC) name for a server name.</summary>
    public static string ToLocal(string remoteName)
    {
        var sb = new StringBuilder(remoteName.Length);
        foreach (var c in Nfc(remoteName))
        {
            if (LookAlikes.TryGetValue(c, out var alike)) sb.Append(alike);
            else if (c < 32) sb.Append('�');
            else sb.Append(c);
        }
        // Windows silently strips trailing dots and spaces.
        var end = sb.Length;
        while (end > 0 && sb[end - 1] is '.' or ' ') end--;
        if (end < sb.Length)
        {
            var tail = sb.ToString(end, sb.Length - end).Replace('.', '．').Replace(' ', ' ');
            sb.Length = end;
            sb.Append(tail);
        }
        var name = sb.Length == 0 ? "_" : sb.ToString();
        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem)) name = stem + " " + name[stem.Length..]; // "CON.txt" → "CON .txt" (thin space)
        return name;
    }

    /// <summary>True when the name can be created as is on Windows.</summary>
    public static bool IsValidLocal(string name) => name.Length > 0 && ToLocal(name) == Nfc(name);

    /// <summary>"Báo cáo (Bản xung đột - Nguyễn Văn A - PC-KE-TOAN - 2026-11-03 1405).docx" (spec 6.3).</summary>
    public static string ConflictName(string name, string userName, string deviceName, DateTime localTime)
    {
        var (stem, ext) = Split(name);
        return $"{stem} (Bản xung đột - {userName} - {deviceName} - {localTime.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}){ext}";
    }

    /// <summary>"Tên (1).ext", "Tên (2).ext"… — the first name not taken (case-insensitive, like Windows and the server).</summary>
    public static string NextFree(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        var (stem, ext) = Split(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>Second sibling differing only in case: "a (trùng tên chữ hoa-thường).docx".</summary>
    public static string CaseCollisionName(string name)
    {
        var (stem, ext) = Split(name);
        return $"{stem} (trùng tên chữ hoa-thường){ext}";
    }

    public static (string Stem, string Ext) Split(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, "") : (name[..dot], name[dot..]);
    }

    public static string Combine(string? parent, string name) => string.IsNullOrEmpty(parent) ? name : parent + "/" + name;

    public static string? ParentOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? null : path[..i];
    }

    public static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static bool IsUnder(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] == '/';
}

/// <summary>Default ignore list (spec 6.2): temporary / system files never synced.</summary>
public static class IgnoreRules
{
    public const string TempSuffix = ".bluedrive-tmp";
    public const string NotSyncedFolder = "_Không đồng bộ được";

    public static bool IsIgnored(string name)
    {
        if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".~lock.", StringComparison.Ordinal)) return true;
        if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) return true;
        var lower = name.ToLowerInvariant();
        if (lower is "thumbs.db" or "desktop.ini" or ".ds_store" or "$recycle.bin" or "system volume information") return true;
        if (lower.EndsWith(".tmp") || lower.EndsWith(".partial") || lower.EndsWith(".crdownload") || lower.EndsWith(".part")) return true;
        // "<tên> - Mở trên web.url": written by the engine for view-only files, never uploaded.
        return SyncClientPolicy.IsLinkName(name);
    }
}
