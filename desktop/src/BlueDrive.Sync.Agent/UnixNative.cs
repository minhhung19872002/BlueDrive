using System.Runtime.InteropServices;

namespace BlueDrive.Sync.Agent;

/// <summary>
/// macOS / Linux: the inode identifies a file across renames and moves (the NTFS file id on Windows, spec 6.1). Read with lstat: the
/// first fields of <c>struct stat</c> are st_dev (macOS: int32, Linux: uint64) and st_ino at offset 8 on macOS (64-bit inodes), Linux x64
/// and Linux arm64.
/// </summary>
internal static class UnixNative
{
    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int LStat(string path, byte[] buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat$INODE64")]
    private static extern int LStatMacX64(string path, byte[] buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "__lxstat64")]
    private static extern int LxStat64(int ver, string path, byte[] buf); // glibc < 2.33

    private static int _mode; // 0 = lstat, 1 = lstat$INODE64, 2 = __lxstat64

    /// <summary>"u:&lt;device&gt;:&lt;inode&gt;", or null when the file does not exist.</summary>
    public static string? FileId(string path)
    {
        var buf = new byte[256];
        if (!Stat(path, buf)) return null;
        var dev = OperatingSystem.IsMacOS() ? (ulong)BitConverter.ToUInt32(buf, 0) : BitConverter.ToUInt64(buf, 0);
        var ino = BitConverter.ToUInt64(buf, 8);
        return $"u:{dev:x}:{ino:x}";
    }

    private static bool Stat(string path, byte[] buf)
    {
        while (true)
        {
            try
            {
                return _mode switch
                {
                    0 when OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 => Switch(1, path, buf),
                    0 => LStat(path, buf) == 0,
                    1 => LStatMacX64(path, buf) == 0,
                    _ => LxStat64(1, path, buf) == 0,
                };
            }
            catch (EntryPointNotFoundException) when (_mode == 0 && OperatingSystem.IsLinux())
            {
                _mode = 2;
            }
        }
    }

    private static bool Switch(int mode, string path, byte[] buf)
    {
        _mode = mode;
        return LStatMacX64(path, buf) == 0;
    }
}
