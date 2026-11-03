using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using BlueDrive.Desktop.Services;
using BlueDrive.Desktop.ViewModels;
using BlueDrive.Desktop.Views;
using BlueDrive.Sync.Agent;
using Serilog;

namespace BlueDrive.Desktop;

public partial class App : Application
{
    private AppHost _host = null!;
    private TrayIcon _tray = null!;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private WizardWindow? _wizard;
    private string _iconKey = "";
    private readonly CancellationTokenSource _stop = new();
    private Deployment _deployment = null!;
    private Timer? _updateTimer;

    public Updater Updater { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost(Environment.GetEnvironmentVariable("BLUEDRIVE_HOME"));
            _deployment = Deployment.Current(desktop.Args ?? []);
            Sync.Client.CertificatePinning.Pins = _deployment.CertificatePins;
            if (_deployment.SaveInto(_host.Settings)) _host.SaveSettings(_host.Settings);
            Updater = new Updater(() => _host.Sessions.FirstOrDefault()?.Config.ServerUrl ?? _host.Settings.DeploymentServer,
                () => _deployment.Channel ?? _host.Settings.UpdateChannel, () => _deployment.UpdatesDisabled);
            string? updateAnnounced = null;
            Updater.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                BuildTray();
                if (Updater.PendingVersion is { } v && v != updateAnnounced)
                {
                    updateAnnounced = v;
                    if (_host.Settings.Notifications)
                        ShowToast("Có bản cập nhật BlueDrive", $"Phiên bản {v} đã sẵn sàng: chọn \"Khởi động lại để cập nhật\" ở biểu tượng BlueDrive, hoặc bản mới tự cài khi thoát.");
                }
            });
            ApplyTheme(_host.Settings.Theme);
            _host.Notification += ShowToast;
            _host.Changed += () => Dispatcher.UIThread.Post(UpdateTray);
            BuildTray();
            _ = ListenForActivationAsync(_stop.Token);
            var background = desktop.Args?.Contains("--background") == true || PackageInfo.LaunchedAtStartup();
            _ = StartAsync(background);
            desktop.ShutdownRequested += (_, _) => _stop.Cancel();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(bool background)
    {
        await _host.StartAsync();
        _host.WatchConditions();
        CheckOnlyOfficeProvider();
        _host.Sessions.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is { Count: > 0 }) _ = SetUpOnlyOfficeAsync();
        };
        _ = SetUpOnlyOfficeAsync();
        _updateTimer = new Timer(_ => _ = Updater.CheckAsync(_stop.Token), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (_host.Sessions.Count == 0) ShowWizard();
        else if (ExplorerMenu.Parse(args) is not null) await HandleArgsAsync(args);
        else if (!background) ShowFlyout();
        UpdateTray();
    }

    /// <summary>Explorer context menu (or a second start of the app): "--share|--link|--web|--versions|--resolve &lt;path&gt;"; otherwise show the flyout.</summary>
    private async Task HandleArgsAsync(string[] args)
    {
        if (args.Contains("--quit"))
        {
            await QuitAsync(); // uninstaller
            return;
        }
        if (ExplorerMenu.Parse(args) is not { } cmd)
        {
            ShowFlyout();
            return;
        }
        var session = _host.Sessions.FirstOrDefault(s => s.Owns(cmd.Path));
        if (session is null)
        {
            ShowToast("BlueDrive", "Mục này không nằm trong thư mục BlueDrive.");
            return;
        }
        if (cmd.Action == "resolve")
        {
            var name = Path.GetFileName(cmd.Path);
            var conflict = session.Conflicts().FirstOrDefault(c => string.Equals(Sync.Core.NameMapper.NameOf(c.ConflictPath), name, StringComparison.OrdinalIgnoreCase)
                                                                  || string.Equals(Sync.Core.NameMapper.NameOf(c.Path), name, StringComparison.OrdinalIgnoreCase));
            if (conflict is null) ShowToast("BlueDrive", "Tệp này không có xung đột cần xử lý.");
            else ShowConflict(session, conflict);
            return;
        }
        if (session.FindItem(cmd.Path) is not { } hit)
        {
            ShowToast("BlueDrive", "Mục này chưa được đồng bộ lên BlueDrive. Hãy thử lại sau ít phút.");
            return;
        }
        try
        {
            switch (cmd.Action)
            {
                case "share":
                    Show(new ShareWindow(new ShareViewModel(session.Agent.Api, hit.Item.RemoteId, hit.Item.IsFolder, hit.Item.Name)));
                    break;
                case "versions":
                    Show(new VersionsWindow(new VersionsViewModel(session, hit.Item.RemoteId, hit.Item.Name)));
                    break;
                case "coedit":
                case "coedit-web":
                    var editor = await session.Agent.Api.EditorLinkAsync(hit.Item.RemoteId);
                    var exe = cmd.Action == "coedit" ? OnlyOfficeService.FindEditors() : null;
                    if (exe is not null) OnlyOfficeService.Open(exe, editor.Url);
                    else
                    {
                        if (cmd.Action == "coedit") ShowToast("Chưa có ONLYOFFICE Desktop Editors", "Tài liệu được mở để cùng soạn thảo trên trình duyệt.");
                        FlyoutViewModel.OpenPath(editor.BrowserUrl);
                    }
                    break;
                case "link":
                case "web":
                    var url = (await session.Agent.Api.WebUrlAsync(hit.Item.RemoteId)).Url;
                    if (cmd.Action == "web") FlyoutViewModel.OpenPath(url);
                    else
                    {
                        _flyout ??= new FlyoutWindow(new FlyoutViewModel(_host, this));
                        if (_flyout.Clipboard is { } clip) await clip.SetTextAsync(url);
                        ShowToast("Đã sao chép liên kết", $"Liên kết tới \"{hit.Item.Name}\" đã ở trong bộ nhớ tạm. Người nhận cần có quyền truy cập.");
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is Sync.Core.RemoteTransientException or Sync.Core.RemotePermanentException or Sync.Core.RemoteNotFoundException)
        {
            ShowToast("BlueDrive", ex.Message);
        }
    }

    private int _ooSetup;

    /// <summary>
    /// "Cài ONLYOFFICE Desktop Editors để cùng soạn thảo tài liệu" (wizard option / --with-onlyoffice=1, default on): once an account exists,
    /// install it in the background (one elevation), or tell the user when the installed version is too old.
    /// </summary>
    private async Task SetUpOnlyOfficeAsync()
    {
        var settings = _host.Settings;
        var server = _host.Sessions.FirstOrDefault()?.Config.ServerUrl;
        if (!OperatingSystem.IsWindows() || server is null || !settings.InstallOnlyOffice || settings.OnlyOfficeSetupDone) return;
        if (Interlocked.Exchange(ref _ooSetup, 1) == 1) return;
        try
        {
            if (OnlyOfficeService.FindEditors() is { } exe)
            {
                if (OnlyOfficeSetup.IsOutdated(exe))
                    ShowToast("Nên cập nhật ONLYOFFICE Desktop Editors", $"Phiên bản trên máy ({OnlyOfficeSetup.VersionOf(exe)}) cũ hơn {OnlyOfficeSetup.MinimumVersion}. Mở Cài đặt → Giới thiệu để cập nhật.");
            }
            else
            {
                ShowToast("Đang cài ONLYOFFICE Desktop Editors", "Để cùng soạn thảo tài liệu Word, Excel, PowerPoint trực tiếp từ máy tính.");
                await OnlyOfficeSetup.InstallAsync(server, localInstaller: Deployment.Arg(Environment.GetCommandLineArgs(), "onlyoffice-installer"), ct: _stop.Token);
                settings.OnlyOfficeInstalledByBlueDrive = true;
                ShowToast("Đã cài ONLYOFFICE Desktop Editors", "Chuột phải tệp Office trong thư mục BlueDrive → BlueDrive → Cùng soạn thảo bằng ONLYOFFICE.");
            }
            settings.OnlyOfficeSetupDone = true;
            _host.SaveSettings(settings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Log.Warning(ex, "Cài ONLYOFFICE Desktop Editors");
            if (ex is InvalidOperationException { Message: var m } && m.Contains("quyền quản trị"))
            {
                settings.OnlyOfficeSetupDone = true; // declined: not asked again at every start
                _host.SaveSettings(settings);
            }
            if (!_stop.IsCancellationRequested) ShowToast("Chưa cài được ONLYOFFICE Desktop Editors", ex is InvalidOperationException ? ex.Message : "Không tải được bộ cài. Sẽ thử lại lần khởi động sau.");
        }
        finally { Interlocked.Exchange(ref _ooSetup, 0); }
    }

    /// <summary>ONLYOFFICE updates can remove the provider: put it back (silently when the folder is writable), otherwise tell the user.</summary>
    private void CheckOnlyOfficeProvider()
    {
        var server = _host.Sessions.FirstOrDefault()?.Config.ServerUrl;
        if (server is null || OnlyOfficeService.FindEditors() is not { } exe) return;
        try
        {
            var state = OnlyOfficeService.State(exe, server);
            if (state is ProviderState.Missing or ProviderState.Outdated && !OnlyOfficeService.InstallProvider(exe, server, allowElevation: false))
                ShowToast("Cần cài lại BlueDrive cho ONLYOFFICE", "Mở Cài đặt → Giới thiệu → \"Cài BlueDrive vào ONLYOFFICE\" để cùng soạn thảo trong ONLYOFFICE Desktop Editors.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Cần cài lại BlueDrive cho ONLYOFFICE", "Mở Cài đặt → Giới thiệu → \"Cài BlueDrive vào ONLYOFFICE\".");
            Log.Warning(ex, "Provider ONLYOFFICE");
        }
    }

    private static void Show(Window w)
    {
        w.Show();
        w.Activate();
    }

    public static void ApplyTheme(ThemeChoice theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    // ---------------------------------------------------------------- tray

    private void BuildTray()
    {
        if (_tray is null)
        {
            _tray = new TrayIcon { ToolTipText = "BlueDrive", Icon = Icon("ok") };
            _tray.Clicked += (_, _) => ToggleFlyout();
            TrayIcon.SetIcons(this, [_tray]);
        }
        var menu = new NativeMenu();
        if (Updater.PendingVersion is { } v)
        {
            menu.Add(Item($"Khởi động lại để cập nhật lên {v}", () => _ = RestartToUpdateAsync()));
            menu.Add(new NativeMenuItemSeparator());
        }
        menu.Add(Item("Mở BlueDrive", ShowFlyout));
        menu.Add(Item("Mở thư mục BlueDrive", () => FlyoutViewModel.OpenPath(_host.Sessions.FirstOrDefault()?.Config.SyncFolder)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Cài đặt", ShowSettings));
        menu.Add(Item("Thoát BlueDrive", () => _ = QuitAsync()));
        _tray.Menu = menu;
    }

    /// <summary>Stops syncing cleanly, then Velopack installs the downloaded version and starts it again.</summary>
    public async Task RestartToUpdateAsync()
    {
        Log.Information("Khởi động lại để cập nhật lên {Version}", Updater.PendingVersion);
        _tray.IsVisible = false;
        await _host.DisposeAsync();
        Updater.ApplyAndRestart();
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var i = new NativeMenuItem(header);
        i.Click += (_, _) => action();
        return i;
    }

    private static WindowIcon Icon(string key) => new(AssetLoader.Open(new Uri($"avares://BlueDrive/Assets/tray-{key}.ico")));

    /// <summary>The worst state across accounts decides the tray icon and tooltip.</summary>
    private void UpdateTray()
    {
        var states = _host.Sessions.Select(s => s.Agent.Status).ToList();
        var (key, text) = TrayState.Of(states, _host.Sessions.Sum(s => s.Conflicts().Count));
        _tray.ToolTipText = $"BlueDrive — {text}";
        if (key != _iconKey)
        {
            _tray.Icon = Icon(key);
            _iconKey = key;
        }
        _flyout?.ViewModel.Refresh();
    }

    // ---------------------------------------------------------------- windows

    public void ToggleFlyout()
    {
        if (_flyout is { IsVisible: true }) _flyout.Hide();
        else ShowFlyout();
    }

    public void ShowFlyout()
    {
        if (_host.Sessions.Count == 0)
        {
            ShowWizard();
            return;
        }
        _flyout ??= new FlyoutWindow(new FlyoutViewModel(_host, this));
        _flyout.ViewModel.Refresh();
        _flyout.ShowNearTray();
    }

    public void ShowSettings()
    {
        _flyout?.Hide();
        if (_settings is null)
        {
            _settings = new SettingsWindow(new SettingsViewModel(_host, this));
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    public void ShowWizard()
    {
        _flyout?.Hide();
        if (_wizard is null)
        {
            _wizard = new WizardWindow(new WizardViewModel(_host, _deployment.Server ?? _host.Settings.DeploymentServer));
            _wizard.Closed += (_, _) =>
            {
                _wizard = null;
                UpdateTray();
            };
        }
        _wizard.Show();
        _wizard.Activate();
    }

    public void ShowConflict(AccountSession session, Sync.Core.ConflictEntry conflict)
    {
        var w = new ConflictWindow(new ConflictViewModel(session, conflict));
        w.Show();
        w.Activate();
    }

    public async Task QuitAsync()
    {
        Log.Information("BlueDrive Desktop thoát");
        _tray.IsVisible = false;
        if (_updateTimer is not null) await _updateTimer.DisposeAsync();
        await _host.DisposeAsync();
        Updater.ApplyOnExit();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    // ---------------------------------------------------------------- notifications & activation

    private void ShowToast(string title, string text)
    {
        try { Notifier.Show(title, text); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            Log.Warning(ex, "Không hiện được thông báo hệ thống");
        }
    }

    /// <summary>A second start of the app (Start menu, shortcut) shows the running instance instead.</summary>
    private async Task ListenForActivationAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                var text = await reader.ReadToEndAsync(ct);
                string[] args;
                try { args = text.StartsWith('[') ? System.Text.Json.JsonSerializer.Deserialize<string[]>(text) ?? [] : []; }
                catch (System.Text.Json.JsonException) { args = []; }
                Dispatcher.UIThread.Post(() => _ = HandleArgsAsync(args));
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex) { Log.Warning(ex, "Kênh kích hoạt"); }
        }
    }
}

/// <summary>Tray icon + tooltip from the accounts' states (worst first).</summary>
public static class TrayState
{
    public static (string Icon, string Text) Of(IReadOnlyList<AgentStatus> states, int conflicts)
    {
        if (states.Count == 0) return ("signin", "Chưa đăng nhập");
        if (states.Any(s => s.State is AgentState.NeedsSignIn or AgentState.Revoked)) return ("signin", "Cần đăng nhập lại");
        if (states.Any(s => s.State is AgentState.Error or AgentState.UpdateRequired || s.PendingMassDelete > 0)) return ("error", "Cần bạn xử lý");
        if (states.Any(s => s.State == AgentState.Offline)) return ("offline", "Ngoại tuyến – thay đổi sẽ được đồng bộ khi có mạng");
        if (states.Any(s => s.State is AgentState.Syncing or AgentState.Starting)) return ("sync", "Đang đồng bộ…");
        if (states.All(s => s.State == AgentState.Paused)) return ("pause", "Đã tạm dừng");
        if (conflicts > 0 || states.Any(s => s.Problems.Count > 0)) return ("error", "Đã cập nhật, có mục chưa đồng bộ được");
        return ("ok", "Đã cập nhật");
    }
}
