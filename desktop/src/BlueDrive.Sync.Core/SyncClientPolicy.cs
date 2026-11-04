namespace BlueDrive.Sync.Core;

/// <summary>
/// The server's sync policy as the engine applies it (GET /api/v1/sync/policy, refreshed by the agent): files too large or of a type the
/// server refuses are not uploaded (reported in "Vấn đề" instead of being sent and rejected), files of a blocked type or too large are not
/// downloaded, and view-only files appear as "Mở trên web" links (or not at all).
/// </summary>
public sealed record SyncClientPolicy
{
    public static readonly SyncClientPolicy Default = new();

    /// <summary>"Kích thước tệp tối đa được đồng bộ" (both directions); 0 = no limit.</summary>
    public long MaxFileSizeBytes { get; init; }
    /// <summary>Server upload limit ("Kích thước tải lên tối đa"); 0 = no limit.</summary>
    public long UploadMaxSizeBytes { get; init; }
    /// <summary>"Đuôi tệp không đồng bộ xuống máy" (lower case, no dot).</summary>
    public IReadOnlySet<string> BlockedDownloadExtensions { get; init; } = new HashSet<string>();
    /// <summary>"Định dạng tệp cho phép" for uploads (lower case, no dot); empty = any.</summary>
    public IReadOnlySet<string> AllowedUploadExtensions { get; init; } = new HashSet<string>();
    /// <summary>View-only files are shown as "&lt;tên&gt; - Mở trên web.url" links; false = hidden.</summary>
    public bool ViewOnlyAsLink { get; init; } = true;
    /// <summary>Web address of BlueDrive ("https://…", no trailing slash); links point to {WebBaseUrl}/files/{id}.</summary>
    public string? WebBaseUrl { get; init; }

    public static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Why a local file cannot be uploaded (Vietnamese, shown in "Vấn đề"), or null when it can.</summary>
    public string? UploadRefusal(string name, long size)
    {
        var ext = ExtensionOf(name);
        if (AllowedUploadExtensions.Count > 0 && !AllowedUploadExtensions.Contains(ext))
            return ext.Length == 0
                ? "Tệp không có phần mở rộng, máy chủ không cho phép tải lên. Tệp vẫn giữ trên máy."
                : $"Định dạng .{ext} chưa được phép tải lên BlueDrive. Tệp vẫn giữ trên máy.";
        var limit = new[] { MaxFileSizeBytes, UploadMaxSizeBytes }.Where(l => l > 0).DefaultIfEmpty(0).Min();
        if (limit > 0 && size > limit)
            return $"Tệp lớn hơn giới hạn đồng bộ ({FormatSize(limit)}), không tải lên. Tệp vẫn giữ trên máy.";
        return null;
    }

    /// <summary>Why a server file is not downloaded to this computer, or null when it can be.</summary>
    public string? DownloadRefusal(string name, long size)
    {
        var ext = ExtensionOf(name);
        if (ext.Length > 0 && BlockedDownloadExtensions.Contains(ext)) return $"Tệp .{ext} không được đồng bộ xuống máy theo chính sách. Mở trên web để xem.";
        if (MaxFileSizeBytes > 0 && size > MaxFileSizeBytes)
            return $"Tệp lớn hơn giới hạn đồng bộ ({FormatSize(MaxFileSizeBytes)}), không tải xuống máy. Mở trên web để xem.";
        return null;
    }

    public static string FormatSize(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.#} GB"
        : $"{bytes / (double)(1L << 20):0.#} MB";

    // ---------------------------------------------------------------- "Mở trên web" links

    /// <summary>Suffix of the link files written for view-only files; such names are never synced (<see cref="IgnoreRules"/>).</summary>
    public const string LinkSuffix = " - Mở trên web.url";

    public static bool IsLinkName(string name) =>
        NameMapper.Nfc(name).EndsWith(LinkSuffix, StringComparison.OrdinalIgnoreCase);

    public static string LinkName(string localName) => localName + LinkSuffix;

    /// <summary>Windows Internet Shortcut content (also opened by double click on macOS / most Linux desktops).</summary>
    public static string LinkContent(string url) => $"[InternetShortcut]\r\nURL={url}\r\n";
}
