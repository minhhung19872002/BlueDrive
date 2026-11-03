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
        try { Uninstaller.DeleteTree(_home); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
    public void The_bluedrive_folder_is_named_after_the_organisation()
    {
        WizardViewModel.DefaultFolderName("Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số", "bluedrive.bluestar.com.vn", false)
            .Should().Be("BlueDrive - Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số");
        WizardViewModel.DefaultFolderName("Phòng: Kế hoạch", "drive.example.gov.vn", false).Should().Be("BlueDrive - Phòng꞉ Kế hoạch", "':' is mapped like other names");
        WizardViewModel.DefaultFolderName(null, "drive.example.gov.vn", false).Should().Be("BlueDrive");
        WizardViewModel.DefaultFolderName(" ", "drive.example.gov.vn", true).Should().Be("BlueDrive - drive.example.gov.vn");
    }

    [Fact]
    public void Syncing_shows_files_and_size_left_and_the_time_once_the_speed_is_known()
    {
        Formatting.Syncing(12, 35L * 1024 * 1024, null).Should().Be("Đang đồng bộ 12 tệp (35 MB còn lại)");
        Formatting.Syncing(12, 35L * 1024 * 1024, 300 * 1024).Should().Be("Đang đồng bộ 12 tệp (35 MB còn lại, ~2 phút)");
        Formatting.Syncing(1, 10 * 1024, 1024 * 1024).Should().Be("Đang đồng bộ 1 tệp (10 KB còn lại, ~1 phút)");
        Formatting.Syncing(3, 9L * 1024 * 1024 * 1024, 1024 * 1024).Should().Be("Đang đồng bộ 3 tệp (9 GB còn lại, ~2,6 giờ)");
        Formatting.Syncing(500, 0, null).Should().Be("Đang đồng bộ 500 tệp", "placeholders only (Files On-Demand)");
    }

    [Fact]
    public void Explorer_status_ui_shows_the_state_and_the_storage_used()
    {
        var personal = new Sync.Client.HttpRemoteStore.RootDto("r", "personal", "Tài liệu của tôi", null, true, true, false, false,
            QuotaBytes: 10L * 1024 * 1024 * 1024, UsedBytes: 1_288_490_188);
        var ok = StatusUiModel.Of(S(AgentState.Idle), false, personal, 0);
        ok.State.Should().Be(ProviderStatus.InSync);
        ok.Label.Should().Be("Đã cập nhật");
        ok.QuotaLabel.Should().Be("Đã dùng 1,2 GB / 10 GB (11%)");
        (ok.QuotaUsed, ok.QuotaTotal).Should().Be((1_288_490_188, 10L * 1024 * 1024 * 1024));

        StatusUiModel.Of(S(AgentState.Syncing), false, personal, 0).State.Should().Be(ProviderStatus.Syncing);
        StatusUiModel.Of(S(AgentState.Syncing), true, personal, 0).State.Should().Be(ProviderStatus.Paused);
        StatusUiModel.Of(S(AgentState.Offline), false, personal, 0).State.Should().Be(ProviderStatus.Offline);
        StatusUiModel.Of(S(AgentState.NeedsSignIn), false, personal, 0).State.Should().Be(ProviderStatus.Error);
        StatusUiModel.Of(S(AgentState.Idle, mass: 120), false, personal, 0).State.Should().Be(ProviderStatus.Warning);
        var conflict = StatusUiModel.Of(S(AgentState.Idle), false, personal, 2);
        (conflict.State, conflict.Label).Should().Be((ProviderStatus.Warning, "Có xung đột"));
        conflict.Detail.Should().StartWith("2 tệp bị sửa ở cả máy này và nơi khác");

        // No quota on the server: no bar, only what is used.
        var noQuota = StatusUiModel.Of(S(AgentState.Idle), false, personal with { QuotaBytes = 0 }, 0);
        (noQuota.QuotaTotal, noQuota.QuotaLabel).Should().Be((0, "Đã dùng 1,2 GB"));
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
    public async Task The_bluedrive_folder_moves_safely_to_another_place()
    {
        await using var host = new AppHost(_home);
        var folder = Path.Combine(_home, "BlueDrive");
        Directory.CreateDirectory(Path.Combine(folder, "Tài liệu của tôi"));
        await File.WriteAllTextAsync(Path.Combine(folder, "Tài liệu của tôi", "báo cáo.txt"), "nội dung");
        File.SetAttributes(Path.Combine(folder, "Tài liệu của tôi", "báo cáo.txt"), FileAttributes.ReadOnly); // view-only file
        var other = Path.Combine(_home, "Khác");
        host.Store.Save(new AccountConfig(Guid.NewGuid(), "http://127.0.0.1:9/", other, "b", "B", Guid.NewGuid()));
        var cfg = new AccountConfig(Guid.NewGuid(), "http://127.0.0.1:9/", folder, "a", "A", Guid.NewGuid());
        host.Store.Save(cfg);
        var session = new AccountSession(host.Store, cfg, host.Settings, new(), new(), "1.0.0");
        host.Sessions.Add(session);

        host.MoveProblem(session, folder).Should().Contain("đã là thư mục");
        host.MoveProblem(session, Path.Combine(folder, "con")).Should().Contain("bên trong");
        host.MoveProblem(session, Path.Combine(other, "BlueDrive")).Should().Contain("tài khoản BlueDrive khác");
        Directory.CreateDirectory(Path.Combine(_home, "Đầy"));
        await File.WriteAllTextAsync(Path.Combine(_home, "Đầy", "x.txt"), "x");
        host.MoveProblem(session, Path.Combine(_home, "Đầy")).Should().Contain("trống");

        var target = Path.Combine(_home, "Tài liệu", "BlueDrive");
        host.MoveProblem(session, target).Should().BeNull();
        var moved = await host.MoveSyncFolderAsync(session, target);
        moved.Config.SyncFolder.Should().Be(Path.GetFullPath(target));
        host.Store.Load().Single(a => a.Id == cfg.Id).SyncFolder.Should().Be(Path.GetFullPath(target), "remembered for the next start");
        (await File.ReadAllTextAsync(Path.Combine(target, "Tài liệu của tôi", "báo cáo.txt"))).Should().Be("nội dung");
        File.GetAttributes(Path.Combine(target, "Tài liệu của tôi", "báo cáo.txt")).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        Directory.Exists(folder).Should().BeFalse();
        host.Sessions.Should().ContainSingle(s => s.Config.Id == cfg.Id);
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
