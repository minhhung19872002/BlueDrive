using System.Diagnostics;
using System.Text.Json;
using BlueDrive.Sync.Agent;
using Microsoft.Win32;

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Spec 13A "Khi nhấp đúp file Office trong BlueDrive: Luôn mở chế độ cùng soạn (khi có mạng)". Windows has no per-folder file association,
/// so BlueDrive adds a verb limited to the sync folders (<c>AppliesTo</c>) to the program the document type opens with (its ProgID, per user
/// in HKCU\Software\Classes) and puts it first in that program's verb order: inside a BlueDrive folder a double click (Enter, "Mở") runs
/// <c>BlueDrive.exe --open "file"</c>; everywhere else the verb does not apply and the program's own "Mở" stays the default. BlueDrive then
/// opens the co-editing session, or the local copy with the usual program ("open" verb) when offline. What was changed is recorded in
/// office-open.json so that turning the option off (or uninstalling) puts every key back.
/// </summary>
public static class OfficeDoubleClick
{
    public const string Verb = "BlueDriveCoEdit";
    private const string StateFile = "office-open.json";

    /// <summary>Office documents (not PDF / CSV: those are rarely co-edited and usually opened elsewhere).</summary>
    public static readonly string[] Extensions = [".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".doc", ".xls", ".ppt", ".rtf"];

    /// <summary>One program we changed: its HKCU verb order before (null = none).</summary>
    public sealed record Change(string ProgId, string? PreviousOrder);

    /// <summary>The ProgIDs a document type opens with on this computer (the user's choice first, then the registered default).</summary>
    public static IReadOnlyList<string> ProgIds(string extension)
    {
        var ids = new List<string>();
        if (!OperatingSystem.IsWindows()) return ids;
        using (var choice = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice"))
            if (choice?.GetValue("ProgId") is string { Length: > 0 } chosen) ids.Add(chosen);
        using (var ext = Registry.ClassesRoot.OpenSubKey(extension))
            if (ext?.GetValue("") is string { Length: > 0 } def && !ids.Contains(def, StringComparer.OrdinalIgnoreCase)) ids.Add(def);
        var withVerbs = new List<string>();
        foreach (var id in ids)
        {
            using var shell = Registry.ClassesRoot.OpenSubKey($@"{id}\shell");
            if (shell is not null) withVerbs.Add(id);
        }
        return withVerbs;
    }

    /// <summary>The verb order with BlueDrive first: "BlueDriveCoEdit,&lt;the program's order or open&gt;".</summary>
    public static string Order(string? current)
    {
        var verbs = (current ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !v.Equals(Verb, StringComparison.OrdinalIgnoreCase)).ToList();
        if (verbs.Count == 0) verbs.Add("open");
        return string.Join(',', [Verb, .. verbs]);
    }

    /// <summary>Turns the option on for the sync folders (again: after the folders or the user's default programs changed).</summary>
    public static void Register(string baseDir, IReadOnlyCollection<string> syncFolders, string exe)
    {
        if (!OperatingSystem.IsWindows()) return;
        Register(baseDir, syncFolders, exe, Extensions.SelectMany(ProgIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    internal static void Register(string baseDir, IReadOnlyCollection<string> syncFolders, string exe, IReadOnlyList<string> progIds)
    {
        if (!OperatingSystem.IsWindows()) return;
        Unregister(baseDir);
        if (syncFolders.Count == 0) return;
        var changes = new List<Change>();
        var applies = ExplorerMenu.AppliesTo(syncFolders);
        foreach (var progId in progIds)
        {
            string? effective, previous;
            using (var merged = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell")) effective = merged?.GetValue("") as string;
            using var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell");
            previous = shell.GetValue("") as string;
            changes.Add(new Change(progId, previous));
            Save(baseDir, changes); // recorded before each change: a crash halfway still lets Unregister undo it
            shell.SetValue("", Order(effective));
            using var verb = shell.CreateSubKey(Verb);
            verb.SetValue("MUIVerb", "Mở bằng BlueDrive (cùng soạn khi có mạng)");
            verb.SetValue("AppliesTo", applies);
            verb.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" --open \"%1\"");
        }
    }

    /// <summary>Turns the option off: every changed program gets its verb order back.</summary>
    public static void Unregister(string baseDir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(baseDir, StateFile);
        List<Change> changes;
        try { changes = File.Exists(path) ? JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(path)) ?? [] : []; }
        catch (JsonException) { changes = []; }
        foreach (var c in changes)
        {
            var keyPath = $@"Software\Classes\{c.ProgId}\shell";
            using (var shell = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            {
                if (shell is null) continue;
                shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
                if (c.PreviousOrder is null) shell.DeleteValue("", throwOnMissingValue: false);
                else shell.SetValue("", c.PreviousOrder);
            }
            DeleteIfEmpty(keyPath);
            DeleteIfEmpty($@"Software\Classes\{c.ProgId}");
        }
        File.Delete(path);
    }

    /// <summary>The account can reach the server for a co-editing session (otherwise the local copy is opened).</summary>
    public static bool CanCoEdit(AgentState state) =>
        state is not (AgentState.Offline or AgentState.NeedsSignIn or AgentState.Revoked or AgentState.Wiped or AgentState.UpdateRequired);

    /// <summary>Opens the local copy with the program the document type normally opens with (its "open" verb, not BlueDrive's).</summary>
    public static void OpenLocally(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception)
        {
            // The program has no "open" verb: let Windows ask which program to use.
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false })?.Dispose();
        }
    }

    private static void Save(string baseDir, List<Change> changes)
    {
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(Path.Combine(baseDir, StateFile), JsonSerializer.Serialize(changes));
    }

    private static void DeleteIfEmpty(string keyPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        bool empty;
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            empty = key is not null && key.SubKeyCount == 0 && key.ValueCount == 0;
        if (empty) Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
    }
}
