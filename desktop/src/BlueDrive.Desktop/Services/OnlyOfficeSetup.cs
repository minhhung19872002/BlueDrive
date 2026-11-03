using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Installs ONLYOFFICE Desktop Editors for co-editing (spec 13A, "Bộ cài BlueDrive Desktop"): downloads the official, unmodified installer
/// from download.onlyoffice.com (x64 / ARM64), checks its Authenticode signature (valid, signed by Ascensio System SIA), runs it silently
/// (<c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART</c>, Inno Setup) together with the copy of the "bluedrive" provider in one elevated step.
/// An administrator can point to a local copy instead (<c>--onlyoffice-installer=\\server\share\DesktopEditors_x64.exe</c>);
/// it is checked the same way.
/// </summary>
public static class OnlyOfficeSetup
{
    /// <summary>Oldest ONLYOFFICE Desktop Editors version offered without "nên cập nhật" (DMS provider API with portal:login / open:document).</summary>
    public static readonly Version MinimumVersion = new(8, 0);
    public const string Publisher = "Ascensio System SIA";

    public static string InstallerUrl(Architecture arch) =>
        $"https://download.onlyoffice.com/install/desktop/editors/windows/distrib/onlyoffice/DesktopEditors_{(arch == Architecture.Arm64 ? "arm64" : "x64")}.exe";

    public static Version? VersionOf(string editorsExe)
    {
        var v = FileVersionInfo.GetVersionInfo(editorsExe);
        return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
    }

    public static bool IsOutdated(string editorsExe) => VersionOf(editorsExe) is { } v && v < MinimumVersion;

    /// <summary>Signed with a valid Authenticode signature whose subject is <paramref name="publisher"/>.</summary>
    public static bool IsSignedBy(string file, string publisher = Publisher)
    {
        if (!OperatingSystem.IsWindows() || !VerifyTrust(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // the signer certificate of a signed file has no X509CertificateLoader equivalent
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false).Contains(publisher, StringComparison.OrdinalIgnoreCase)
                   || cert.Subject.Contains($"O={publisher}", StringComparison.OrdinalIgnoreCase)
                   || cert.Subject.Contains($"O=\"{publisher}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    /// <summary>
    /// Downloads (or takes <paramref name="localInstaller"/>), verifies and installs ONLYOFFICE, then the provider for <paramref name="server"/>.
    /// Returns DesktopEditors.exe. Throws <see cref="InvalidOperationException"/> with a Vietnamese message on failure.
    /// </summary>
    public static async Task<string> InstallAsync(string server, IProgress<string>? progress = null, string? localInstaller = null,
        HttpClient? http = null, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "bluedrive-onlyoffice");
        Directory.CreateDirectory(temp);
        var installer = localInstaller;
        if (installer is null)
        {
            var url = InstallerUrl(RuntimeInformation.OSArchitecture);
            installer = Path.Combine(temp, Path.GetFileName(url));
            progress?.Report("Đang tải ONLYOFFICE Desktop Editors…");
            using var client = http is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(30) } : null;
            using var res = await (http ?? client!).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Không tải được bộ cài ONLYOFFICE ({(int)res.StatusCode}).");
            var total = res.Content.Headers.ContentLength;
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(installer))
            {
                var buf = new byte[1 << 20];
                long done = 0, lastReport = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0 && done - lastReport > total / 50)
                    {
                        lastReport = done;
                        progress?.Report($"Đang tải ONLYOFFICE Desktop Editors… {done * 100 / total}%");
                    }
                }
            }
        }
        progress?.Report("Đang kiểm tra chữ ký số của bộ cài…");
        if (!File.Exists(installer) || !IsSignedBy(installer))
            throw new InvalidOperationException("Bộ cài ONLYOFFICE không có chữ ký số hợp lệ của Ascensio System SIA nên không được cài.");

        progress?.Report("Đang cài ONLYOFFICE Desktop Editors…");
        var staging = OnlyOfficeService.StageProvider(server);
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ONLYOFFICE", "DesktopEditors", "providers", OnlyOfficeService.ProviderFolder);
        // One elevation for both steps: the installer (per machine) and the provider in its Program Files folder.
        var cmd = $"/c \"\"{installer}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART && robocopy \"{staging}\" \"{target}\" /E /NFL /NDL /NJH /NJS & exit 0\"";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            await p.WaitForExitAsync(ct);
        }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Cần quyền quản trị để cài ONLYOFFICE Desktop Editors."); }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(staging)!, recursive: true); }
            catch (IOException) { }
            if (localInstaller is null)
            {
                try { File.Delete(installer); }
                catch (IOException) { }
            }
        }
        var exe = OnlyOfficeService.FindEditors() ?? throw new InvalidOperationException("Cài ONLYOFFICE Desktop Editors không thành công.");
        if (OnlyOfficeService.State(exe, server) != ProviderState.Ready) OnlyOfficeService.InstallProvider(exe, server);
        Log.Information("Đã cài ONLYOFFICE Desktop Editors {Version}", VersionOf(exe));
        return exe;
    }

    /// <summary>ONLYOFFICE's own uninstaller (Inno Setup), run silently and elevated. False when it is not found or the user declined.</summary>
    public static bool Uninstall(string editorsExe)
    {
        var uninstaller = Directory.EnumerateFiles(Path.GetDirectoryName(editorsExe)!, "unins*.exe").FirstOrDefault();
        if (uninstaller is null) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true, Verb = "runas" })!;
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    // ---------------------------------------------------------------- WinVerifyTrust

    private static bool VerifyTrust(string file)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (offline machines)
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);
}
