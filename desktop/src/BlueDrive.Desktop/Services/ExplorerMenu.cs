using Microsoft.Win32;

namespace BlueDrive.Desktop.Services;

/// <summary>
/// "BlueDrive" submenu in Explorer's context menu for items inside a BlueDrive folder (spec 7.2): Chia sẻ…, Sao chép liên kết BlueDrive, Xem trên
/// web, Lịch sử phiên bản…, Giải quyết xung đột… (conflict copies only). Registered per user (HKCU\Software\Classes) and limited to the sync
/// folders with AppliesTo, so it needs no administrator rights; on Windows 11 it appears under "Hiện thêm tùy chọn" until the MSIX package
/// (Sync-Phase 9) adds the modern IExplorerCommand menu.
/// </summary>
public static class ExplorerMenu
{
    private const string Key = "BlueDrive";

    public sealed record Command(string Id, string Text, string Arg, bool FilesOnly = false, string? Condition = null);

    public static readonly Command[] Commands =
    [
        new("01share", "Chia sẻ…", "--share"),
        new("02link", "Sao chép liên kết BlueDrive", "--link"),
        new("03web", "Xem trên web", "--web"),
        new("04versions", "Lịch sử phiên bản…", "--versions", FilesOnly: true),
        new("05resolve", "Giải quyết xung đột…", "--resolve", FilesOnly: true, Condition: "System.FileName:~~\"(Bản xung đột -\""),
    ];

    /// <summary>AQS condition: the item is inside one of the folders.</summary>
    public static string AppliesTo(IEnumerable<string> folders) =>
        string.Join(" OR ", folders.Select(f => $"System.ItemPathDisplay:~<\"{Path.GetFullPath(f).TrimEnd('\\')}\\\""));

    public static void Register(IReadOnlyCollection<string> syncFolders, string exe)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (syncFolders.Count == 0)
        {
            Unregister();
            return;
        }
        var applies = AppliesTo(syncFolders);
        foreach (var cls in new[] { "*", "Directory" })
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell\{Key}");
            root.SetValue("MUIVerb", "BlueDrive");
            root.SetValue("Icon", $"\"{exe}\",0");
            root.SetValue("SubCommands", "");
            root.SetValue("AppliesTo", applies);
            root.DeleteSubKeyTree("shell", throwOnMissingSubKey: false);
            foreach (var c in Commands.Where(c => cls == "*" || !c.FilesOnly))
            {
                using var verb = root.CreateSubKey($@"shell\{c.Id}");
                verb.SetValue("MUIVerb", c.Text);
                if (c.Condition is not null) verb.SetValue("AppliesTo", c.Condition);
                using var cmd = verb.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" {c.Arg} \"%1\"");
            }
        }
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var cls in new[] { "*", "Directory" })
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{cls}\shell\{Key}", throwOnMissingSubKey: false);
    }

    /// <summary>Parses "--share &lt;path&gt;" style arguments (context menu, second app start).</summary>
    public static (string Action, string Path)? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (Commands.FirstOrDefault(c => c.Arg == args[i]) is { } c) return (c.Arg[2..], args[i + 1]);
        return null;
    }
}
