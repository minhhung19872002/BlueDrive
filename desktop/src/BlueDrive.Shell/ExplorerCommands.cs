using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;

namespace BlueDrive.Shell;

/// <summary>
/// "BlueDrive" in the Windows 11 context menu (spec 7.2) for items inside a BlueDrive folder, with the same commands as the classic menu
/// (BlueDrive.Desktop ExplorerMenu): each one starts BlueDrive.exe "--share|--link|--web|--versions|--resolve|--coedit|--coedit-web &lt;path&gt;",
/// which hands it to the running app.
/// </summary>
[GeneratedComClass]
internal sealed partial class RootCommand : IExplorerCommand
{
    public static readonly Guid Clsid = new("5B1E0D1E-3C6D-4E7F-8B0B-B1DE0D1E0002");

    internal sealed record Item(string Title, string Arg, bool FilesOnly, Func<string, bool>? Applies = null);

    internal static readonly string[] OfficeExtensions = [".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".pdf", ".doc", ".xls", ".ppt", ".rtf", ".csv"];

    internal static readonly Item[] Items =
    [
        new("Chia sẻ…", "--share", false),
        new("Sao chép liên kết BlueDrive", "--link", false),
        new("Xem trên web", "--web", false),
        new("Lịch sử phiên bản…", "--versions", true),
        new("Giải quyết xung đột…", "--resolve", true, p => Path.GetFileName(p).Contains("(Bản xung đột -", StringComparison.Ordinal)),
        new("Cùng soạn thảo bằng ONLYOFFICE", "--coedit", true, p => OfficeExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())),
        new("Cùng soạn thảo trên trình duyệt", "--coedit-web", true, p => OfficeExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())),
    ];

    public int GetTitle(IShellItemArray? items, out nint name) => Native.Str("BlueDrive", out name);
    public int GetIcon(IShellItemArray? items, out nint icon) => Native.Str(Native.ExePath + ",0", out icon);
    public int GetToolTip(IShellItemArray? items, out nint tip) { tip = 0; return Native.E_NOTIMPL; }
    public int GetCanonicalName(out Guid guid) { guid = Clsid; return 0; }

    public int GetState(IShellItemArray? items, bool okToBeSlow, out uint state)
    {
        // Only for one item inside a BlueDrive folder.
        state = Native.Path(items) is { } p && SyncFolders.Contains(p) ? Native.ECS_ENABLED : Native.ECS_HIDDEN;
        return 0;
    }

    public int Invoke(IShellItemArray? items, nint bindCtx) => 0;
    public int GetFlags(out uint flags) { flags = Native.ECF_HASSUBCOMMANDS; return 0; }

    public int EnumSubCommands(out IEnumExplorerCommand? e)
    {
        e = new CommandEnum(Items.Select(i => (IExplorerCommand)new SubCommand(i)).ToArray());
        return 0;
    }
}

[GeneratedComClass]
internal sealed partial class SubCommand(RootCommand.Item item) : IExplorerCommand
{
    public int GetTitle(IShellItemArray? items, out nint name) => Native.Str(item.Title, out name);
    public int GetIcon(IShellItemArray? items, out nint icon) { icon = 0; return Native.E_NOTIMPL; }
    public int GetToolTip(IShellItemArray? items, out nint tip) { tip = 0; return Native.E_NOTIMPL; }
    public int GetCanonicalName(out Guid guid) { guid = Guid.Empty; return 0; }

    public int GetState(IShellItemArray? items, bool okToBeSlow, out uint state)
    {
        var p = Native.Path(items);
        var visible = p is not null && (!item.FilesOnly || File.Exists(p)) && (item.Applies?.Invoke(p) ?? true);
        state = visible ? Native.ECS_ENABLED : Native.ECS_HIDDEN;
        return 0;
    }

    public int Invoke(IShellItemArray? items, nint bindCtx)
    {
        if (Native.Path(items) is not { } p) return 0;
        try
        {
            var psi = new ProcessStartInfo(Native.ExePath) { UseShellExecute = false };
            psi.ArgumentList.Add(item.Arg);
            psi.ArgumentList.Add(p);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return 0;
    }

    public int GetFlags(out uint flags) { flags = 0; return 0; }
    public int EnumSubCommands(out IEnumExplorerCommand? e) { e = null; return Native.E_NOTIMPL; }
}

[GeneratedComClass]
internal sealed partial class CommandEnum(IExplorerCommand[] commands) : IEnumExplorerCommand
{
    private int _next;

    public int Next(uint celt, out IExplorerCommand? command, out uint fetched)
    {
        if (celt == 0 || _next >= commands.Length)
        {
            command = null;
            fetched = 0;
            return 1; // S_FALSE
        }
        command = commands[_next++];
        fetched = 1;
        return celt == 1 ? 0 : 1;
    }

    public int Skip(uint celt) { _next = (int)Math.Min(commands.Length, _next + celt); return 0; }
    public int Reset() { _next = 0; return 0; }
    public int Clone(out IEnumExplorerCommand? e) { e = new CommandEnum(commands) { _next = _next }; return 0; }
}

/// <summary>The BlueDrive folders of this user (accounts.json of BlueDrive Desktop), re-read every 10 seconds.</summary>
internal static class SyncFolders
{
    private static string[] _folders = [];
    private static DateTime _read = DateTime.MinValue;

    public static bool Contains(string path)
    {
        if (DateTime.UtcNow - _read > TimeSpan.FromSeconds(10)) Load();
        var full = System.IO.Path.GetFullPath(path);
        return _folders.Any(f => full.StartsWith(f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void Load()
    {
        _read = DateTime.UtcNow;
        try
        {
            var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueDrive", "accounts.json");
            if (!File.Exists(file)) { _folders = []; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            _folders = doc.RootElement.EnumerateArray()
                .Select(a => a.TryGetProperty("syncFolder", out var f) ? f.GetString() : null).OfType<string>()
                .Select(f => System.IO.Path.GetFullPath(f).TrimEnd('\\')).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { _folders = []; }
    }
}

[GeneratedComClass]
internal sealed partial class Factory : IClassFactory
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public int CreateInstance(nint outer, in Guid iid, out nint obj)
    {
        obj = 0;
        if (outer != 0) return Native.CLASS_E_NOAGGREGATION;
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(new RootCommand(), CreateComInterfaceFlags.None);
        try { return Marshal.QueryInterface(unknown, iid, out obj); }
        finally { Marshal.Release(unknown); }
    }

    public int LockServer(bool fLock) => 0;
}

internal static unsafe class Native
{
    public const int E_NOTIMPL = unchecked((int)0x80004001), CLASS_E_NOAGGREGATION = unchecked((int)0x80040110),
        CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const uint ECS_ENABLED = 0, ECS_HIDDEN = 2, ECF_HASSUBCOMMANDS = 1, SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>BlueDrive.exe next to this DLL (same package folder).</summary>
    public static readonly string ExePath = System.IO.Path.Combine(ModuleDirectory(), "BlueDrive.exe");

    private static string ModuleDirectory()
    {
        // This DLL: the module containing one of its exported functions.
        delegate* unmanaged<int> any = &Exports.DllCanUnloadNow;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (nint)any, out var module))
            return AppContext.BaseDirectory;
        var buf = new char[1024];
        fixed (char* p = buf)
        {
            var len = GetModuleFileNameW(module, p, (uint)buf.Length);
            return System.IO.Path.GetDirectoryName(new string(p, 0, (int)len)) ?? AppContext.BaseDirectory;
        }
    }

    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 4, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

    /// <summary>The file system path of the single selected item, or null.</summary>
    public static string? Path(IShellItemArray? items)
    {
        if (items is null || items.GetCount(out var count) != 0 || count != 1) return null;
        if (items.GetItemAt(0, out var item) != 0) return null;
        if (item.GetDisplayName(SIGDN_FILESYSPATH, out var name) != 0 || name == 0) return null;
        try { return Marshal.PtrToStringUni(name); }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    /// <summary>Returns a CoTaskMem copy of <paramref name="s"/> (the caller frees it).</summary>
    public static int Str(string s, out nint ptr)
    {
        ptr = Marshal.StringToCoTaskMemUni(s);
        return 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(nint module, char* buffer, uint size);
}

internal static unsafe class Exports
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, nint* obj)
    {
        *obj = 0;
        if (*clsid != RootCommand.Clsid) return Native.CLASS_E_CLASSNOTAVAILABLE;
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(new Factory(), CreateComInterfaceFlags.None);
        try { return Marshal.QueryInterface(unknown, *iid, out *obj); }
        finally { Marshal.Release(unknown); }
    }

    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => 1; // S_FALSE: stay loaded (the surrogate process ends with the menu)
}
