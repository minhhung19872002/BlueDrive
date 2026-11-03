using System.Runtime.InteropServices;
using BlueDrive.Sync.Core;
using Microsoft.Win32.SafeHandles;

namespace BlueDrive.Sync.Agent;

/// <summary>
/// The sync folder on a real disk (full sync mode, spec 7.3). Paths given to / returned by the engine are relative to
/// <see cref="Root"/>, "/" separated. Windows: NTFS File ID identifies a file across renames/moves (spec 6.1), deletions go to the
/// Recycle Bin. Symbolic links / junctions and hidden+system files are never followed nor synced (spec 6.2, 9).
/// </summary>
public class PhysicalFileSystem(string root, bool useRecycleBin = true) : ILocalFileSystem
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Entries skipped during a scan (links, inaccessible): shown as warnings in the UI.</summary>
    public event Action<string, string>? Skipped;

    public string Abs(string path) => path.Length == 0 ? Root : Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Relative, "/" separated; NFC on macOS (its file system hands out decomposed NFD names; the engine and server use NFC).</summary>
    private string Rel(string abs)
    {
        var rel = Path.GetRelativePath(Root, abs).Replace(Path.DirectorySeparatorChar, '/');
        return OperatingSystem.IsMacOS() ? NameMapper.Nfc(rel) : rel;
    }

    public IReadOnlyList<LocalEntry> Scan(string path)
    {
        var list = new List<LocalEntry>();
        var start = new DirectoryInfo(Abs(path));
        if (!start.Exists) return list;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(start);
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            // Windows: one call per folder returns every child with its file id (no handle per file: 100 000 files scan in seconds).
            if (OperatingSystem.IsWindows() && FastList(dir.FullName) is { } fast)
            {
                foreach (var c in fast)
                {
                    var abs = Path.Combine(dir.FullName, c.Name);
                    if (c.Link)
                    {
                        Skipped?.Invoke(Rel(abs), "Liên kết tượng trưng / junction không được đồng bộ.");
                        continue;
                    }
                    if (c.Attributes.HasFlag(FileAttributes.Hidden) && c.Attributes.HasFlag(FileAttributes.System)) continue;
                    var isFolder = c.Attributes.HasFlag(FileAttributes.Directory);
                    list.Add(new LocalEntry(Rel(abs), isFolder, c.FileId, isFolder ? 0 : c.Size, isFolder ? default : c.MTime));
                    if (isFolder) stack.Push(new DirectoryInfo(abs));
                }
                continue;
            }
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skipped?.Invoke(Rel(dir.FullName), "Không đọc được thư mục: " + ex.Message);
                continue;
            }
            foreach (var info in children)
            {
                if (!Include(info)) continue;
                var entry = Entry(info);
                if (entry is null) continue;
                list.Add(entry);
                if (info is DirectoryInfo d) stack.Push(d);
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    private bool Include(FileSystemInfo info)
    {
        var a = info.Attributes;
        // Symbolic links / junctions only: Files On-Demand placeholders are reparse points too, and are synced.
        if (a.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null)
        {
            Skipped?.Invoke(Rel(info.FullName), "Liên kết tượng trưng / junction không được đồng bộ.");
            return false;
        }
        return !(a.HasFlag(FileAttributes.Hidden) && a.HasFlag(FileAttributes.System));
    }

    private LocalEntry? Entry(FileSystemInfo info)
    {
        try
        {
            info.Refresh();
            if (!info.Exists) return null;
            var isFolder = info is DirectoryInfo;
            return new LocalEntry(Rel(info.FullName), isFolder, FileId(info.FullName, isFolder), isFolder ? 0 : ((FileInfo)info).Length,
                isFolder ? default : DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skipped?.Invoke(Rel(info.FullName), ex.Message);
            return null;
        }
    }

    public LocalEntry? Stat(string path)
    {
        if (path.Length == 0) return new LocalEntry("", true, "root", 0, default);
        var abs = Abs(path);
        if (OperatingSystem.IsWindows() && FastStat(abs) is { } fast)
            return fast.Link
                ? null
                : new LocalEntry(path, fast.Attributes.HasFlag(FileAttributes.Directory), fast.FileId,
                    fast.Attributes.HasFlag(FileAttributes.Directory) ? 0 : fast.Size, fast.Attributes.HasFlag(FileAttributes.Directory) ? default : fast.MTime);
        FileSystemInfo info = Directory.Exists(abs) ? new DirectoryInfo(abs) : new FileInfo(abs);
        if (!info.Exists || info.LinkTarget is not null) return null;
        var e = Entry(info);
        // Keep the caller's spelling of the path (the disk may differ only in case).
        return e is null ? null : e with { Path = ActualCase(path, e.Path) };
    }

    /// <summary>Attributes only (no handle, no file id): used before creating items, 100 000s of times on a first sync.</summary>
    public bool Exists(string path, out bool isFolder)
    {
        isFolder = false;
        if (path.Length == 0) { isFolder = true; return true; }
        var info = new FileInfo(Abs(path));
        if (!info.Exists && !Directory.Exists(info.FullName)) return false;
        var attrs = info.Attributes;
        if (attrs.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null) return false; // symbolic links are not ours
        isFolder = attrs.HasFlag(FileAttributes.Directory);
        return true;
    }

    private static string ActualCase(string requested, string onDisk) =>
        string.Equals(requested, onDisk, StringComparison.OrdinalIgnoreCase) ? onDisk : requested;

    /// <summary>Read without allowing other writers: fails (IOException) while an editor still has the file open for writing.</summary>
    public Stream OpenRead(string path) => new FileStream(Abs(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    public async Task WriteAtomicAsync(string path, Func<Stream, Task> write, DateTime mtime, CancellationToken ct)
    {
        var abs = Abs(path);
        var dir = Path.GetDirectoryName(abs)!;
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Thiếu thư mục cha: {NameMapper.ParentOf(path)}");
        if (Directory.Exists(abs)) throw new IOException($"Là thư mục: {path}");
        var tmp = Path.Combine(dir, $".{Guid.NewGuid():N}{IgnoreRules.TempSuffix}");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await write(fs);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(tmp, mtime);
            Writable(abs); // a view-only file being replaced by the server's copy
            // Replaces the destination in one step (MoveFileEx REPLACE_EXISTING on Windows, rename(2) elsewhere).
            File.Move(tmp, abs, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public void CreateDirectory(string path)
    {
        var abs = Abs(path);
        if (File.Exists(abs)) throw new IOException($"Là tệp: {path}");
        Directory.CreateDirectory(abs);
    }

    public void Move(string from, string to)
    {
        var src = Abs(from);
        var dst = Abs(to);
        var parent = Path.GetDirectoryName(dst)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException($"Thiếu thư mục cha: {NameMapper.ParentOf(to)}");
        var caseOnly = string.Equals(src, dst, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(src))
        {
            if (NameMapper.IsUnder(to, from)) throw new IOException("Không thể chuyển thư mục vào chính nó.");
            if (caseOnly)
            {
                // Case-only rename of a folder: go through a temporary name.
                var tmp = Path.Combine(parent, $".{Guid.NewGuid():N}{IgnoreRules.TempSuffix}");
                Directory.Move(src, tmp);
                Directory.Move(tmp, dst);
            }
            else Directory.Move(src, dst);
        }
        else if (File.Exists(src)) File.Move(src, dst, overwrite: false);
        else throw new FileNotFoundException(from);
    }

    public virtual void Delete(string path)
    {
        var abs = Abs(path);
        var isDir = Directory.Exists(abs);
        if (!isDir && !File.Exists(abs)) return;
        if (!isDir) Writable(abs);
        if (useRecycleBin && (OperatingSystem.IsWindows() ? RecycleBin.TrySend(abs) : UnixTrash.TrySend(abs))) return;
        if (isDir) Directory.Delete(abs, recursive: true);
        else File.Delete(abs);
    }

    public void Copy(string from, string to) => File.Copy(Abs(from), Abs(to), overwrite: false);

    public void SetReadOnly(string path, bool readOnly)
    {
        var abs = Abs(path);
        if (!File.Exists(abs)) return;
        var a = File.GetAttributes(abs);
        var next = readOnly ? a | FileAttributes.ReadOnly : a & ~FileAttributes.ReadOnly;
        if (next != a) File.SetAttributes(abs, next);
    }

    private static void Writable(string abs)
    {
        if (File.Exists(abs) && File.GetAttributes(abs).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(abs, File.GetAttributes(abs) & ~FileAttributes.ReadOnly);
    }

    // ---------------------------------------------------------------- file identity

    /// <summary>Paths over MAX_PATH need the "\\?\" prefix for direct Win32 calls (.NET adds it by itself).</summary>
    public static string LongPath(string abs) =>
        abs.Length >= 248 && !abs.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + abs : abs;

    /// <summary>NTFS / ReFS file id (volume serial + 128-bit id) on Windows; elsewhere the path (renames are then seen as delete + create).</summary>
    private readonly record struct FastEntry(string Name, FileAttributes Attributes, string FileId, long Size, DateTime MTime, bool Link);

    /// <summary>
    /// Children of a folder with GetFileInformationByHandleEx(FileIdExtdDirectoryInfo): ids, sizes, dates and attributes in a few calls.
    /// Null when the volume does not support it (FAT, some network shares): the caller falls back to one handle per file.
    /// </summary>
    private static List<FastEntry>? FastList(string absDir)
    {
        using var h = Native.CreateFileW(LongPath(absDir), Native.FILE_LIST_DIRECTORY, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (!Native.GetFileInformationByHandleEx(h, Native.FileIdInfo, out var self, (uint)Marshal.SizeOf<Native.FILE_ID_INFO>())) return null;
        var volume = self.VolumeSerialNumber.ToString("x");
        var list = new List<FastEntry>();
        var buffer = new byte[64 * 1024];
        var infoClass = Native.FileIdExtdDirectoryRestartInfo;
        while (true)
        {
            if (!Native.GetFileInformationByHandleEx(h, infoClass, buffer, (uint)buffer.Length))
            {
                var err = Marshal.GetLastPInvokeError();
                return err == Native.ERROR_NO_MORE_FILES ? list : null;
            }
            infoClass = Native.FileIdExtdDirectoryInfo;
            var offset = 0;
            while (true)
            {
                var span = buffer.AsSpan(offset);
                var next = BitConverter.ToInt32(span);
                var attrs = (FileAttributes)BitConverter.ToUInt32(span[56..]);
                var nameBytes = BitConverter.ToInt32(span[60..]);
                var tag = BitConverter.ToUInt32(span[68..]);
                var name = System.Text.Encoding.Unicode.GetString(span.Slice(88, nameBytes));
                if (name is not ("." or ".."))
                {
                    var link = attrs.HasFlag(FileAttributes.ReparsePoint) && tag is Native.IO_REPARSE_TAG_SYMLINK or Native.IO_REPARSE_TAG_MOUNT_POINT;
                    var id = $"{volume}:{Convert.ToHexStringLower(span.Slice(72, 16))}";
                    var mtime = DateTime.FromFileTimeUtc(BitConverter.ToInt64(span[24..]));
                    list.Add(new FastEntry(name, attrs, id, BitConverter.ToInt64(span[40..]), mtime, link));
                }
                if (next == 0) break;
                offset += next;
            }
        }
    }

    /// <summary>One handle, three queries: id, attributes / reparse tag, size and date. Null when missing (or on failure: slow path).</summary>
    private static FastEntry? FastStat(string abs)
    {
        using var h = Native.CreateFileW(LongPath(abs), 0x80 /* FILE_READ_ATTRIBUTES */, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (!Native.GetFileInformationByHandleEx(h, Native.FileIdInfo, out var id, (uint)Marshal.SizeOf<Native.FILE_ID_INFO>())) return null;
        if (!Native.GetFileInformationByHandle(h, out var basic)) return null;
        var attrs = (FileAttributes)basic.FileAttributes;
        var link = false;
        if (attrs.HasFlag(FileAttributes.ReparsePoint) && Native.GetAttributeTagInfo(h, Native.FileAttributeTagInfo, out var tag,
                (uint)Marshal.SizeOf<Native.FILE_ATTRIBUTE_TAG_INFO>()))
            link = tag.ReparseTag is Native.IO_REPARSE_TAG_SYMLINK or Native.IO_REPARSE_TAG_MOUNT_POINT;
        var size = ((long)basic.FileSizeHigh << 32) | basic.FileSizeLow;
        var mtime = DateTime.FromFileTimeUtc(((long)(uint)basic.LastWriteTime.dwHighDateTime << 32) | (uint)basic.LastWriteTime.dwLowDateTime);
        return new FastEntry(Path.GetFileName(abs), attrs, $"{id.VolumeSerialNumber:x}:{Convert.ToHexStringLower(id.FileId)}", size, mtime, link);
    }

    internal static string FileId(string abs, bool isFolder)
    {
        if (!OperatingSystem.IsWindows()) return UnixNative.FileId(abs) ?? throw new IOException($"Không đọc được mã tệp của {abs}.");
        using var h = Native.CreateFileW(LongPath(abs), 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Không mở được {abs} (mã lỗi {Marshal.GetLastPInvokeError()}).");
        if (Native.GetFileInformationByHandleEx(h, Native.FileIdInfo, out var info, (uint)Marshal.SizeOf<Native.FILE_ID_INFO>()))
            return $"{info.VolumeSerialNumber:x}:{Convert.ToHexStringLower(info.FileId)}";
        if (Native.GetFileInformationByHandle(h, out var basic))
            return $"{basic.VolumeSerialNumber:x}:{((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow:x}";
        throw new IOException($"Không đọc được mã tệp của {abs} (mã lỗi {Marshal.GetLastPInvokeError()}).");
    }

    private static class Native
    {
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        public const int FileIdInfo = 18;
        public const int FileIdExtdDirectoryInfo = 19, FileIdExtdDirectoryRestartInfo = 20, FileAttributeTagInfo = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_ATTRIBUTE_TAG_INFO
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
        public static extern bool GetAttributeTagInfo(SafeFileHandle h, int infoClass, out FILE_ATTRIBUTE_TAG_INFO info, uint size);
        public const uint FILE_LIST_DIRECTORY = 0x1;
        public const int ERROR_NO_MORE_FILES = 18;
        public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C, IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct FILE_ID_INFO
        {
            public ulong VolumeSerialNumber;
            public fixed byte Id[16];

            public readonly byte[] FileId
            {
                get
                {
                    var b = new byte[16];
                    for (var i = 0; i < 16; i++) b[i] = Id[i];
                    return b;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_ID_INFO info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, byte[] buffer, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
    }

    /// <summary>Windows Recycle Bin through SHFileOperation (silent: no confirmation, no progress, no error UI).</summary>
    private static class RecycleBin
    {
        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

        public static bool TrySend(string abs)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE, pFrom = abs + "\0\0", fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperationW(ref op) == 0 && op.fAnyOperationsAborted == 0 && !File.Exists(abs) && !Directory.Exists(abs);
        }
    }

    /// <summary>
    /// macOS: ~/.Trash (Finder's Trash); Linux: the freedesktop.org trash (~/.local/share/Trash, with the .trashinfo restore record).
    /// False when the trash is on another volume (the move would copy everything): the item is then deleted.
    /// </summary>
    private static class UnixTrash
    {
        public static bool TrySend(string abs)
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var name = Path.GetFileName(abs.TrimEnd('/'));
                if (OperatingSystem.IsMacOS())
                {
                    var trash = Path.Combine(home, ".Trash");
                    if (!Directory.Exists(trash)) return false;
                    return MoveUnique(abs, trash, name, out _);
                }
                var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");
                var files = Path.Combine(dataHome, "Trash", "files");
                var info = Path.Combine(dataHome, "Trash", "info");
                Directory.CreateDirectory(files);
                Directory.CreateDirectory(info);
                if (!MoveUnique(abs, files, name, out var used)) return false;
                File.WriteAllText(Path.Combine(info, used + ".trashinfo"),
                    $"[Trash Info]\nPath={Uri.EscapeDataString(abs).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private static bool MoveUnique(string abs, string dir, string name, out string used)
        {
            used = name;
            for (var i = 2; File.Exists(Path.Combine(dir, used)) || Directory.Exists(Path.Combine(dir, used)); i++)
                used = $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}";
            var target = Path.Combine(dir, used);
            if (Path.GetPathRoot(abs) != Path.GetPathRoot(target)) return false;
            if (Directory.Exists(abs)) Directory.Move(abs, target);
            else File.Move(abs, target);
            return true;
        }
    }
}
