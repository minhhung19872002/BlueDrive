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
using Microsoft.Toolkit.Uwp.Notifications;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost(Environment.GetEnvironmentVariable("BLUEDRIVE_HOME"));
            ApplyTheme(_host.Settings.Theme);
            _host.Notification += ShowToast;
            _host.Changed += () => Dispatcher.UIThread.Post(UpdateTray);
            BuildTray();
            _ = ListenForActivationAsync(_stop.Token);
            var background = desktop.Args?.Contains("--background") == true;
            _ = StartAsync(background);
            desktop.ShutdownRequested += (_, _) => _stop.Cancel();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(bool background)
    {
        await _host.StartAsync();
        if (_host.Sessions.Count == 0) ShowWizard();
        else if (!background) ShowFlyout();
        UpdateTray();
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
        _tray = new TrayIcon { ToolTipText = "BlueDrive", Icon = Icon("ok") };
        _tray.Clicked += (_, _) => ToggleFlyout();
        var menu = new NativeMenu();
        menu.Add(Item("Mở BlueDrive", ShowFlyout));
        menu.Add(Item("Mở thư mục BlueDrive", () => FlyoutViewModel.OpenPath(_host.Sessions.FirstOrDefault()?.Config.SyncFolder)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Cài đặt", ShowSettings));
        menu.Add(Item("Thoát BlueDrive", () => _ = QuitAsync()));
        _tray.Menu = menu;
        TrayIcon.SetIcons(this, [_tray]);
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
            _wizard = new WizardWindow(new WizardViewModel(_host, ServerFromDeployment()));
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

    /// <summary>Server preset by the installer (/server=…) or a deploy.json next to the app (mass deployment).</summary>
    private static string? ServerFromDeployment()
    {
        var args = Environment.GetCommandLineArgs();
        var arg = args.FirstOrDefault(a => a.StartsWith("/server=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--server=", StringComparison.OrdinalIgnoreCase));
        if (arg is not null) return arg[(arg.IndexOf('=') + 1)..];
        var file = Path.Combine(AppContext.BaseDirectory, "deploy.json");
        if (!File.Exists(file)) return null;
        try { return System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("server").GetString(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException) { return null; }
    }

    public async Task QuitAsync()
    {
        Log.Information("BlueDrive Desktop thoát");
        _tray.IsVisible = false;
        await _host.DisposeAsync();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    // ---------------------------------------------------------------- notifications & activation

    private void ShowToast(string title, string text)
    {
        try { new ToastContentBuilder().AddText(title).AddText(text).Show(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Không hiện được thông báo Windows");
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
                Dispatcher.UIThread.Post(ShowFlyout);
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
