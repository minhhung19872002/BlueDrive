using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace BlueDrive.Sync.Windows;

/// <summary>Small helpers over the generated Cloud Files P/Invoke (CsWin32).</summary>
public static class CloudNative
{
    public const uint STATUS_SUCCESS = 0;
    public const uint STATUS_UNSUCCESSFUL = 0xC0000001;
    public const uint STATUS_ACCESS_DENIED = 0xC0000022;
    public const uint STATUS_CLOUD_FILE_UNSUCCESSFUL = 0xC000CF0D;

    /// <summary>File attribute bits set by the cloud filter.</summary>
    public const FileAttributes Pinned = (FileAttributes)0x00080000;
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>ERROR_NOT_A_CLOUD_FILE as HRESULT.</summary>
    public const int NotACloudFile = unchecked((int)0x80070178);

    internal static void Check(HRESULT hr, string what)
    {
        if (hr.Failed) throw new IOException($"{what} thất bại (0x{(uint)hr.Value:X8}).", hr.Value);
    }

    /// <summary>
    /// A handle good enough for placeholder updates (WRITE_DAC is accepted by CfUpdatePlaceholder / CfSetInSyncState / CfSetPinState),
    /// shared with every other reader / writer and without reading data (no hydration).
    /// </summary>
    internal static SafeFileHandle OpenForPlaceholder(string path)
    {
        var h = PInvoke.CreateFile(Agent.PhysicalFileSystem.LongPath(path), (uint)(FILE_ACCESS_RIGHTS.WRITE_DAC | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (h.IsInvalid) throw new IOException($"Không mở được {path} (mã lỗi {Marshal.GetLastPInvokeError()}).", Marshal.GetHRForLastWin32Error());
        return h;
    }

    public static long FileTime(DateTime utc) => utc.ToUniversalTime().ToFileTimeUtc();
}
