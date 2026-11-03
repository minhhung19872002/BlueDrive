using System.Diagnostics;
using Microsoft.Win32;

namespace BlueDrive.Desktop.Services;

public enum ProviderState { NotInstalled, Missing, Outdated, Ready }

/// <summary>
/// ONLYOFFICE Desktop Editors on this computer (spec 13A): finds the app, keeps the "bluedrive" DMS provider in its "providers" folder (built
/// from the template shipped with BlueDrive Desktop for the account's server), and opens a document for co-editing. The providers folder is
/// under Program Files: when it is not writable the copy runs elevated (one UAC prompt), otherwise silently.
/// </summary>
public static class OnlyOfficeService
{
    public const string ProviderFolder = "bluedrive";

    /// <summary>Extensions offered "Cùng soạn thảo bằng ONLYOFFICE".</summary>
    public static readonly string[] Extensions = [".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".pdf", ".doc", ".xls", ".ppt", ".rtf", ".csv"];

    public static string TemplateDir => Path.Combine(AppContext.BaseDirectory, "onlyoffice-provider", ProviderFolder);

    /// <summary>DesktopEditors.exe, or null when ONLYOFFICE Desktop Editors is not installed.</summary>
    public static string? FindEditors()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\DesktopEditors.exe");
            if (key?.GetValue("") is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
        }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var p = Path.Combine(Environment.GetFolderPath(root), "ONLYOFFICE", "DesktopEditors", "DesktopEditors.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>config.json of the provider for one BlueDrive server.</summary>
    public static string ConfigFor(string server, string templateDir) =>
        File.ReadAllText(Path.Combine(templateDir, "config.template.json")).Replace("{{SERVER}}", server.TrimEnd('/'));

    public static ProviderState State(string? editorsExe, string server, string? templateDir = null)
    {
        if (editorsExe is null) return ProviderState.NotInstalled;
        var config = Path.Combine(Path.GetDirectoryName(editorsExe)!, "providers", ProviderFolder, "config.json");
        if (!File.Exists(config)) return ProviderState.Missing;
        return File.ReadAllText(config) == ConfigFor(server, templateDir ?? TemplateDir) ? ProviderState.Ready : ProviderState.Outdated;
    }

    /// <summary>Writes the provider (config + icons). Returns false when the user declined the elevation prompt.</summary>
    public static bool InstallProvider(string editorsExe, string server, string? templateDir = null, bool allowElevation = true)
    {
        templateDir ??= TemplateDir;
        var dest = Path.Combine(Path.GetDirectoryName(editorsExe)!, "providers", ProviderFolder);
        var staging = Path.Combine(Path.GetTempPath(), "bluedrive-oo-" + Guid.NewGuid().ToString("N")[..8], ProviderFolder);
        Directory.CreateDirectory(Path.Combine(staging, "assets"));
        foreach (var f in Directory.EnumerateFiles(Path.Combine(templateDir, "assets"))) File.Copy(f, Path.Combine(staging, "assets", Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(staging, "config.json"), ConfigFor(server, templateDir));
        try
        {
            CopyTree(staging, dest);
            return true;
        }
        catch (UnauthorizedAccessException) when (allowElevation)
        {
            // Program Files: one elevated robocopy (ONLYOFFICE reads providers from its install folder only).
            try
            {
                using var p = Process.Start(new ProcessStartInfo("robocopy.exe", $"\"{staging}\" \"{dest}\" /E /NFL /NDL /NJH /NJS")
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                })!;
                p.WaitForExit();
                return p.ExitCode < 8; // robocopy: 0-7 = success
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC declined
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(staging)!, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }

    /// <summary>Opens the BlueDrive editor page as a tab of ONLYOFFICE Desktop Editors (the app accepts http(s) URLs on its command line).</summary>
    public static void Open(string editorsExe, string url) =>
        Process.Start(new ProcessStartInfo(editorsExe) { UseShellExecute = false, ArgumentList = { url } });
}
