using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BlueDrive.Desktop;
using BlueDrive.Desktop.Services;
using BlueDrive.Desktop.ViewModels;
using BlueDrive.Desktop.Views;
using BlueDrive.Sync.Agent;
using FluentAssertions;

[assembly: AvaloniaTestApplication(typeof(BlueDrive.Desktop.Tests.TestApp))]

namespace BlueDrive.Desktop.Tests;

public sealed class TestApp
{
    public static Avalonia.AppBuilder BuildAvaloniaApp() =>
        Avalonia.AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Sync-Phase 6: view models and every window rendered headless (no real accounts, temporary data folder).</summary>
public sealed class DesktopTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "bd-desktop-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }

    private static AgentStatus S(AgentState state, int problems = 0, int mass = 0) =>
        new(state, "", DateTime.UtcNow, mass, Enumerable.Range(0, problems).Select(i => $"tệp {i}: lỗi").ToList());

    [Fact]
    public void Tray_icon_shows_the_worst_state_of_all_accounts()
    {
        TrayState.Of([], 0).Icon.Should().Be("signin");
        TrayState.Of([S(AgentState.Idle)], 0).Should().Be(("ok", "Đã cập nhật"));
        TrayState.Of([S(AgentState.Idle), S(AgentState.Syncing)], 0).Icon.Should().Be("sync");
        TrayState.Of([S(AgentState.Syncing), S(AgentState.Offline)], 0).Icon.Should().Be("offline");
        TrayState.Of([S(AgentState.Offline), S(AgentState.NeedsSignIn)], 0).Icon.Should().Be("signin");
        TrayState.Of([S(AgentState.Idle, mass: 120)], 0).Icon.Should().Be("error");
        TrayState.Of([S(AgentState.Idle)], 2).Icon.Should().Be("error");
        TrayState.Of([S(AgentState.Paused), S(AgentState.Paused)], 0).Icon.Should().Be("pause");
    }

    [Fact]
    public void Texts_are_vietnamese_and_numbers_use_the_vietnamese_decimal_comma()
    {
        Formatting.Bytes(1_288_490_188).Should().Be("1,2 GB");
        Formatting.Bytes(1536).Should().Be("1,5 KB");
        Formatting.Status(S(AgentState.Offline), false).Item1.Should().Be("Ngoại tuyến");
        Formatting.Status(S(AgentState.Idle, problems: 3), false).Item2.Should().Be("3 mục chưa đồng bộ được.");
        Formatting.Status(S(AgentState.Idle), true).Item1.Should().Be("Đã tạm dừng đồng bộ");
        Formatting.Glyph("Báo cáo.DOCX").Should().Be("W");
        Formatting.Glyph("ảnh.jpg").Should().Be("IMG");
        var now = DateTime.UtcNow;
        ActivityItem.Ago(now.AddSeconds(-5), now).Should().Be("Vừa xong");
        ActivityItem.Ago(now.AddMinutes(-12), now).Should().Be("12 phút trước");
        ActivityItem.Ago(now.AddHours(-3), now).Should().Be("3 giờ trước");
        ConflictViewModel.WhoFromName("kế hoạch (Bản xung đột - Nguyễn Văn A - PC-KE-TOAN - 2026-11-03 0915).docx")
            .Should().Be("Sửa bởi Nguyễn Văn A trên máy PC-KE-TOAN");
    }

    [Fact]
    public void Wizard_checks_the_sync_folder()
    {
        var host = new AppHost(_home);
        var vm = new WizardViewModel(host, "https://bluedrive.example");
        vm.ServerLocked.Should().BeTrue("a server preset by the installer cannot be changed");

        vm.Folder = Path.GetPathRoot(Path.GetTempPath())!;
        vm.FolderOk.Should().BeFalse();
        vm.FolderInfo.Should().Contain("thư mục gốc");

        var folder = Path.Combine(_home, "BlueDrive");
        vm.Folder = folder;
        vm.FolderOk.Should().BeTrue();
        vm.FolderInfo.Should().Contain("còn trống");
        Directory.Exists(folder).Should().BeTrue();

        // A folder already used by another account (or inside it) is refused.
        host.Store.Save(new AccountConfig(Guid.NewGuid(), "https://a.example/", folder, "a", "A", Guid.NewGuid()));
        vm.Folder = Path.Combine(folder, "con");
        vm.FolderOk.Should().BeFalse();
        vm.FolderInfo.Should().Contain("tài khoản BlueDrive khác");
    }

    [Fact]
    public void Settings_round_trip_and_nothing_is_registered_before_the_user_confirms()
    {
        var host = new AppHost(_home);
        File.Exists(Path.Combine(_home, "settings.json")).Should().BeFalse("first run: nothing written before the wizard");
        var s = host.Settings;
        s.UploadLimitKBps = 512;
        s.Theme = ThemeChoice.Dark;
        s.FreeUpAfterDays = 30;
        s.StartWithWindows = false;
        host.SaveSettings(s);
        host.UploadLimit.BytesPerSecond.Should().Be(512 * 1024);
        var again = AppSettings.Load(_home);
        again.Theme.Should().Be(ThemeChoice.Dark);
        again.FreeUpAfterDays.Should().Be(30);
        StartupRegistration.IsSet().Should().BeFalse();
    }

    [Fact]
    public void Metered_network_and_low_battery_pause_unless_turned_off()
    {
        var s = new AppSettings();
        ConditionsMonitor.Reason(s, metered: false, onBattery: false, 100).Should().BeNull();
        ConditionsMonitor.Reason(s, metered: true, onBattery: false, 100).Should().Contain("mạng tính phí");
        ConditionsMonitor.Reason(s, metered: false, onBattery: true, 19).Should().Contain("pin dưới 20%");
        ConditionsMonitor.Reason(s, metered: false, onBattery: true, 20).Should().BeNull();
        ConditionsMonitor.Reason(s, metered: false, onBattery: false, 5).Should().BeNull("plugged in");
        s.PauseOnMetered = false;
        s.PauseOnLowBattery = false;
        ConditionsMonitor.Reason(s, metered: true, onBattery: true, 5).Should().BeNull();
    }

    [Fact]
    public void Explorer_context_menu_is_limited_to_the_sync_folders_and_parses_its_commands()
    {
        ExplorerMenu.AppliesTo([@"C:\Users\a\BlueDrive", @"D:\BlueDrive - x\"])
            .Should().Be(@"System.ItemPathDisplay:~<""C:\Users\a\BlueDrive\"" OR System.ItemPathDisplay:~<""D:\BlueDrive - x\""");
        ExplorerMenu.Parse(["--share", @"C:\Users\a\BlueDrive\Tài liệu của tôi\báo cáo.docx"])
            .Should().Be(("share", @"C:\Users\a\BlueDrive\Tài liệu của tôi\báo cáo.docx"));
        ExplorerMenu.Parse(["--background"]).Should().BeNull();
        ExplorerMenu.Commands.Select(c => c.Text).Should().Equal("Chia sẻ…", "Sao chép liên kết BlueDrive", "Xem trên web", "Lịch sử phiên bản…", "Giải quyết xung đột…",
            "Cùng soạn thảo bằng ONLYOFFICE", "Cùng soạn thảo trên trình duyệt");
        ExplorerMenu.Parse(["--coedit-web", @"C:\x\a.docx"]).Should().Be(("coedit-web", @"C:\x\a.docx"));
        ExplorerMenu.OfficeCondition.Should().Contain("System.FileExtension:=\".docx\"").And.Contain(" OR ");

        // Real registration (per user), then removed.
        var exe = Path.Combine(_home, "BlueDrive.exe");
        try
        {
            ExplorerMenu.Register([Path.Combine(_home, "BlueDrive")], exe);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\BlueDrive\shell\01share\command")!;
            ((string)key.GetValue("")!).Should().Be($"\"{exe}\" --share \"%1\"");
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\BlueDrive\shell\04versions").Should().BeNull("versions are for files");
        }
        finally { ExplorerMenu.Unregister(); }
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\BlueDrive").Should().BeNull();
    }

    [Fact]
    public void OnlyOffice_provider_is_generated_for_the_server_and_installed_into_the_editors_folder()
    {
        var template = Path.Combine(AppContext.BaseDirectory, "onlyoffice-provider", "bluedrive");
        Directory.Exists(template).Should().BeTrue("the provider template ships with BlueDrive Desktop");
        var config = System.Text.Json.JsonDocument.Parse(OnlyOfficeService.ConfigFor("https://bluedrive.example/", template)).RootElement;
        config.GetProperty("provider").GetString().Should().Be("bluedrive");
        config.GetProperty("entryPage").GetString().Should().Be("https://bluedrive.example/oo-desktop");
        config.GetProperty("extraLogout").GetString().Should().Be("https://bluedrive.example/oo-desktop/logout");
        config.GetProperty("editorPage").GetString().Should().StartWith("regex:");

        // A fake ONLYOFFICE install folder (writable): missing → installed → ready; another server → outdated.
        var install = Path.Combine(_home, "ONLYOFFICE", "DesktopEditors");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "DesktopEditors.exe");
        File.WriteAllText(exe, "");
        OnlyOfficeService.State(null, "https://bluedrive.example").Should().Be(ProviderState.NotInstalled);
        OnlyOfficeService.State(exe, "https://bluedrive.example", template).Should().Be(ProviderState.Missing);
        OnlyOfficeService.InstallProvider(exe, "https://bluedrive.example", template, allowElevation: false).Should().BeTrue();
        OnlyOfficeService.State(exe, "https://bluedrive.example", template).Should().Be(ProviderState.Ready);
        File.Exists(Path.Combine(install, "providers", "bluedrive", "assets", "listicon.svg")).Should().BeTrue();
        OnlyOfficeService.State(exe, "https://khac.example", template).Should().Be(ProviderState.Outdated);
    }

    [AvaloniaFact]
    public void Share_window_renders_with_both_tabs()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var w = new ShareWindow(new ShareViewModel(new Sync.Client.BlueDriveApi(http), Guid.NewGuid().ToString(), false, "báo cáo.docx"));
        w.Show();
        w.GetVisualDescendants().OfType<TabItem>().Select(t => t.Header).Should().Equal("Mời người", "Liên kết");
        w.Title.Should().Be("Chia sẻ \"báo cáo.docx\"");
        w.Close();
    }

    [AvaloniaFact]
    public void Every_window_renders_in_vietnamese()
    {
        var host = new AppHost(_home);
        var windows = new Window[]
        {
            new WizardWindow(new WizardViewModel(host, null)),
            new FlyoutWindow(new FlyoutViewModel(host, null)),
            new SettingsWindow(new SettingsViewModel(host, null)),
        };
        foreach (var w in windows)
        {
            w.Show();
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            texts.Should().NotBeEmpty();
            w.Close();
        }
        var wizard = new WizardWindow(new WizardViewModel(host, null));
        wizard.Show();
        wizard.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("Chào mừng đến với BlueDrive");
        wizard.Close();
        var settings = new SettingsWindow(new SettingsViewModel(host, null));
        settings.Show();
        settings.GetVisualDescendants().OfType<TabItem>().Select(t => t.Header).Should()
            .Equal("Tài khoản", "Đồng bộ", "Mạng", "Chung", "Vấn đề", "Giới thiệu");
        settings.Close();
    }
}
