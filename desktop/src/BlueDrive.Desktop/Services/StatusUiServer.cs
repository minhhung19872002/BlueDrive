#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using BlueDrive.Sync.Windows;
using Serilog;
using Windows.Foundation;
using Windows.Storage.Provider;

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Explorer's cloud Status UI (spec 7.2, Windows 11 22H2+): clicking the BlueDrive folder's cloud icon in Explorer's address bar shows the sync
/// state and the storage used, with "Mở BlueDrive" / "Xem trên web". Explorer asks the package's
/// <c>cloudFiles2:StorageProviderStatusUISourceFactory</c> class, served out of process by the running BlueDrive (CoRegisterClassObject; when
/// BlueDrive is not running COM starts it with <c>-Embedding</c>). The MSIX package declares the class in its manifest; the EXE (Velopack)
/// install registers it per user (HKCU\Software\Classes\CLSID) and records it on each sync root, as OneDrive does.
/// </summary>
public sealed partial class StatusUiServer : IDisposable
{
    public static readonly Guid Clsid = new("5B1E0D1E-3C6D-4E7F-8B0B-B1DE0D1E0003");
    private const uint CLSCTX_LOCAL_SERVER = 4, REGCLS_MULTIPLEUSE = 1;

    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;

    private StatusUiServer(AppHost host, Action openApp)
    {
        var factory = new SourceFactory(host, openApp);
        // MTA: Explorer's calls arrive on RPC threads instead of waiting for the UI thread.
        _thread = new Thread(() => Serve(factory)) { IsBackground = true, Name = "BlueDrive Status UI" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Starts serving Explorer on Windows 11 22H2+ (before the accounts start, so their sync roots point to it); otherwise null.</summary>
    public static StatusUiServer? Start(AppHost host, Action openApp)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return null;
        if (!PackageInfo.IsPackaged && Environment.ProcessPath is { } exe)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ClassKey + @"\LocalServer32");
                key.SetValue("", $"\"{exe}\" -Embedding");
                SyncRootRegistrar.StatusUiFactory = Clsid;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Log.Warning(ex, "Không đăng ký được Status UI cho Explorer");
                return null;
            }
        }
        return new StatusUiServer(host, openApp);
    }

    private static string ClassKey => $@"Software\Classes\CLSID\{Clsid:B}";

    /// <summary>Uninstall: removes the per-user class registration (EXE install).</summary>
    public static void Unregister()
    {
        if (OperatingSystem.IsWindows()) Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);
    }

    private void Serve(SourceFactory factory)
    {
        var wrappers = new StrategyBasedComWrappers();
        var unknown = wrappers.GetOrCreateComInterfaceForObject(new ClassFactory(factory), CreateComInterfaceFlags.None);
        try
        {
            var hr = CoRegisterClassObject(Clsid, unknown, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out var cookie);
            if (hr < 0)
            {
                Log.Warning("Không đăng ký được Status UI cho Explorer (0x{Hr:X8})", hr);
                return;
            }
            Log.Information("Đã bật Status UI của BlueDrive trong Explorer");
            _stop.Wait();
            CoRevokeClassObject(cookie);
        }
        finally { Marshal.Release(unknown); }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid clsid, nint unknown, uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);

    // ---------------------------------------------------------------- COM class factory

    [GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint outer, in Guid iid, out nint obj);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    [GeneratedComClass]
    internal sealed partial class ClassFactory(SourceFactory factory) : IClassFactory
    {
        private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

        public int CreateInstance(nint outer, in Guid iid, out nint obj)
        {
            obj = 0;
            if (outer != 0) return CLASS_E_NOAGGREGATION;
            // The WinRT object (CsWinRT's COM wrapper) answers IUnknown / IInspectable / IStorageProviderStatusUISourceFactory.
            var inspectable = WinRT.MarshalInspectable<object>.FromManaged(factory);
            try { return Marshal.QueryInterface(inspectable, iid, out obj); }
            finally { Marshal.Release(inspectable); }
        }

        public int LockServer(bool fLock) => 0;
    }

    // ---------------------------------------------------------------- WinRT objects Explorer talks to

    internal sealed partial class SourceFactory(AppHost host, Action openApp) : IStorageProviderStatusUISourceFactory
    {
        public IStorageProviderStatusUISource GetStatusUISource(string syncRootId)
        {
            Log.Debug("Explorer hỏi Status UI của {SyncRoot}", syncRootId);
            return new Source(host, syncRootId, openApp);
        }
    }

    internal sealed partial class Source : IStorageProviderStatusUISource
    {
        // File paths, as in Microsoft's CloudMirror sample (Assets next to BlueDrive.exe, from desktop/packaging/msix/Assets).
        private static readonly Uri Icon = new(Path.Combine(AppContext.BaseDirectory, "Assets", "BlueDrive.svg"));
        private static readonly Uri WebIcon = new(Path.Combine(AppContext.BaseDirectory, "Assets", "Web.svg"));
        private readonly AppHost _host;
        private readonly string _syncRootId;
        private readonly Action _openApp;
        private readonly Lock _gate = new();
        private TypedEventHandler<IStorageProviderStatusUISource, object>? _changed;
        private StatusUiSnapshot? _last;

        public Source(AppHost host, string syncRootId, Action openApp)
        {
            _host = host;
            _syncRootId = syncRootId;
            _openApp = openApp;
        }

        private AccountSession? Session => _host.Sessions.ToList().FirstOrDefault(s => string.Equals(SyncRootRegistrar.Id(s.Config.Id), _syncRootId, StringComparison.OrdinalIgnoreCase));

        private StatusUiSnapshot Snapshot() => Session is { } s ? StatusUiModel.Of(s) : StatusUiModel.SignedOut;

        public event TypedEventHandler<IStorageProviderStatusUISource, object> StatusUIChanged
        {
            add
            {
                lock (_gate)
                {
                    if (_changed is null) _host.Changed += OnHostChanged;
                    _changed += value;
                }
            }
            remove
            {
                lock (_gate)
                {
                    _changed -= value;
                    if (_changed is null) _host.Changed -= OnHostChanged;
                }
            }
        }

        /// <summary>Tells Explorer to ask again, only when what it shows changed (progress events are frequent).</summary>
        private void OnHostChanged()
        {
            TypedEventHandler<IStorageProviderStatusUISource, object>? handler;
            lock (_gate) handler = _changed;
            if (handler is null) return;
            var now = Snapshot();
            lock (_gate)
            {
                if (now == _last) return;
                _last = now;
            }
            try { handler(this, null!); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                Log.Debug(ex, "Explorer không còn nghe Status UI"); // Explorer closed the flyout / restarted
                lock (_gate)
                {
                    _changed = null;
                    _host.Changed -= OnHostChanged;
                }
            }
        }

        public StorageProviderStatusUI GetStatusUI()
        {
            try { return Build(); }
            catch (Exception ex)
            {
                Log.Error(ex, "Không tạo được Status UI cho Explorer");
                throw;
            }
        }

        private StorageProviderStatusUI Build()
        {
            var snap = Snapshot();
            lock (_gate) _last = snap;
            var session = Session;
            var ui = new StorageProviderStatusUI
            {
                ProviderState = snap.State switch
                {
                    ProviderStatus.InSync => StorageProviderState.InSync,
                    ProviderStatus.Syncing => StorageProviderState.Syncing,
                    ProviderStatus.Paused => StorageProviderState.Paused,
                    ProviderStatus.Warning => StorageProviderState.Warning,
                    ProviderStatus.Offline => StorageProviderState.Offline,
                    _ => StorageProviderState.Error,
                },
                ProviderStateLabel = snap.Label,
                ProviderStateIcon = Icon,
                ProviderPrimaryCommand = new Command("Mở BlueDrive", "Xem hoạt động gần đây, xung đột và cài đặt", Icon, _openApp),
            };
            if (snap.QuotaTotal > 0)
                ui.QuotaUI = new StorageProviderQuotaUI
                {
                    QuotaTotalInBytes = (ulong)snap.QuotaTotal,
                    QuotaUsedInBytes = (ulong)Math.Min(snap.QuotaUsed, snap.QuotaTotal),
                    QuotaUsedLabel = snap.QuotaLabel,
                };
            if (snap.State is (ProviderStatus.Warning or ProviderStatus.Error or ProviderStatus.Paused or ProviderStatus.Offline) && snap.Detail.Length > 0)
                ui.MoreInfoUI = new StorageProviderMoreInfoUI
                {
                    Message = snap.Detail,
                    Command = new Command("Xem chi tiết", "Mở BlueDrive để xử lý", Icon, _openApp),
                };
            else if (snap.Detail.Length > 0)
                ui.SyncStatusCommand = new Command(snap.Label, snap.Detail, Icon, _openApp);
            if (session is not null)
                ui.ProviderSecondaryCommands = new List<IStorageProviderUICommand>
                {
                    new Command("Xem trên web", "Mở BlueDrive trên trình duyệt", WebIcon,
                        () => Process.Start(new ProcessStartInfo(session.Config.ServerUrl) { UseShellExecute = true })?.Dispose()),
                };
            return ui;
        }
    }

    internal sealed partial class Command(string label, string description, Uri icon, Action run) : IStorageProviderUICommand
    {
        public string Label => label;
        public string Description => description;
        public Uri Icon => icon;
        public StorageProviderUICommandState State => StorageProviderUICommandState.Enabled;

        public void Invoke()
        {
            try { run(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Log.Warning(ex, "Không thực hiện được lệnh {Label}", label); }
        }
    }
}
#endif
