using System.Runtime.InteropServices;
using BlueDrive.Sync.Agent;
using Serilog;

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Clean uninstall (spec 11 / 13A), run by Velopack's uninstaller through <c>--veloapp-uninstall</c> before the app folder is removed
/// (≤ 30 s, the app is no longer running): sync roots unregistered (placeholders of files never downloaded removed, like "Hủy liên kết"),
/// Explorer menu, "Khởi động cùng Windows" and the ONLYOFFICE provider removed, device tokens deleted. The user is asked whether to keep
/// the files in the BlueDrive folder (default: keep) and, when BlueDrive installed ONLYOFFICE, whether to remove it too (default: keep).
/// </summary>
public static class Uninstaller
{
    /// <summary>(question, default answer) → answer; the default is used when nobody answers in time (silent uninstall).</summary>
    public delegate bool Ask(string question, bool defaultAnswer);

    public sealed record Result(int Accounts, bool FilesDeleted, bool ProviderRemoved, bool OnlyOfficeRemoved);

    /// <param name="registry">False in tests: leaves this computer's Explorer menu and Run key alone.</param>
    public static Result Run(string baseDir, Ask ask, string? editorsExe = null, bool registry = true)
    {
        var store = new AccountStore(baseDir);
        var settings = AppSettings.Load(baseDir);
        var accounts = store.Load();
        var deleteFiles = accounts.Count > 0 && !ask(
            "Giữ lại các tệp đã tải về trong thư mục BlueDrive trên máy này?\n\n" +
            string.Join("\n", accounts.Select(a => "• " + a.SyncFolder)) +
            "\n\nCó: giữ lại (mặc định). Không: xóa thư mục khỏi máy (tệp vẫn còn trên BlueDrive).", true);

        foreach (var acc in accounts)
        {
            try { RemoveAccount(acc, deleteFiles); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { Log.Warning(ex, "Gỡ tài khoản {Account}", acc.Username); }
        }
        if (registry)
        {
            TryRun(ExplorerMenu.Unregister, "menu chuột phải");
            TryRun(() => OfficeDoubleClick.Unregister(baseDir), "nhấp đúp tệp Office");
#if WINDOWS
            TryRun(StatusUiServer.Unregister, "Status UI của Explorer");
#endif
            TryRun(() => StartupRegistration.Set(false), "khởi động cùng Windows");
            editorsExe ??= OnlyOfficeService.FindEditors();
        }

        bool providerRemoved = true, ooRemoved = false;
        if (editorsExe is not null)
        {
            if (settings.OnlyOfficeInstalledByBlueDrive
                && ask("BlueDrive đã cài ONLYOFFICE Desktop Editors để cùng soạn thảo. Gỡ luôn ONLYOFFICE Desktop Editors?\n\nKhông: giữ lại (mặc định).", false))
                ooRemoved = OnlyOfficeSetup.Uninstall(editorsExe);
            providerRemoved = OnlyOfficeService.RemoveProvider(editorsExe, allowElevation: true);
        }

        // Tokens, state, settings (the logs folder is in use by this process: removed with the rest when possible).
        foreach (var dir in Directory.Exists(baseDir) ? Directory.EnumerateDirectories(baseDir) : [])
            if (!string.Equals(Path.GetFileName(dir), "logs", StringComparison.OrdinalIgnoreCase)) TryRun(() => Directory.Delete(dir, true), dir);
        foreach (var file in Directory.Exists(baseDir) ? Directory.EnumerateFiles(baseDir) : []) TryRun(() => File.Delete(file), file);
        Log.Information("Đã gỡ BlueDrive Desktop: {Count} tài khoản, xóa tệp: {Delete}, provider ONLYOFFICE: {Provider}", accounts.Count, deleteFiles, providerRemoved);
        return new(accounts.Count, deleteFiles, providerRemoved, ooRemoved);
    }

    private static void RemoveAccount(AccountConfig acc, bool deleteFiles)
    {
        if (acc.OnDemand && Directory.Exists(acc.SyncFolder))
        {
            if (!deleteFiles)
                foreach (var f in Directory.EnumerateFiles(acc.SyncFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                             .Where(CloudFiles.IsDehydrated).ToList())
                    TryRun(() => File.Delete(f), f);
            TryRun(() => CloudFiles.Unregister(acc.SyncFolder, acc.Id, ""), "sync root");
        }
        if (deleteFiles) DeleteTree(acc.SyncFolder);
    }

    /// <summary>Deletes a folder even when it holds read-only files (items shared view-only are marked ReadOnly).</summary>
    public static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            var attrs = File.GetAttributes(f);
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, recursive: true);
    }

    private static void TryRun(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { Log.Warning(ex, "Gỡ cài đặt: {What}", what); }
    }

    /// <summary>Yes/No box that answers itself with the default after <paramref name="seconds"/> (Velopack allows the hook 30 s).</summary>
    public static Ask MessageBoxAsk(int seconds) => (question, defaultAnswer) =>
    {
        if (!OperatingSystem.IsWindows()) return defaultAnswer;
        const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000, MB_DEFBUTTON2 = 0x100;
        var flags = MB_YESNO | MB_ICONQUESTION | MB_TOPMOST | MB_SETFOREGROUND | (defaultAnswer ? 0 : MB_DEFBUTTON2);
        var r = MessageBoxTimeoutW(IntPtr.Zero, question + $"\n\n(Tự chọn mặc định sau {seconds} giây.)", "Gỡ cài đặt BlueDrive", flags, 0, (uint)seconds * 1000);
        return r switch { 6 => true, 7 => false, _ => defaultAnswer }; // IDYES, IDNO, 32000 = timed out
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxTimeoutW(IntPtr hWnd, string text, string caption, uint type, ushort language, uint milliseconds);
}
