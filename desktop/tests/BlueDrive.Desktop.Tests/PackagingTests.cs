using BlueDrive.Desktop.Services;
using BlueDrive.Sync.Agent;
using FluentAssertions;

namespace BlueDrive.Desktop.Tests;

/// <summary>Sync-Phase 9: mass deployment settings, clean uninstall, ONLYOFFICE installer checks, update feed.</summary>
public sealed class PackagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bd-pack-" + Guid.NewGuid().ToString("N")[..8]);

    public PackagingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Uninstaller.DeleteTree(_dir); }
        catch (IOException) { }
    }

    [Fact]
    public void Installer_arguments_and_deploy_json_preset_the_server_onlyoffice_and_channel()
    {
        var d = Deployment.FromArgs(["--background", "--server=https://drive.example.gov.vn/", "--with-onlyoffice=0"], _dir);
        d.Should().Be(new Deployment("https://drive.example.gov.vn", false, null, false));
        Deployment.FromArgs(["/server=https://a.vn", "/with-onlyoffice=1", "/channel=BETA"], _dir).Should().Be(new Deployment("https://a.vn", true, "beta", false));
        Deployment.FromArgs(["--server=not a url"], _dir).Server.Should().BeNull();

        File.WriteAllText(Path.Combine(_dir, "deploy.json"), """{ "server": "https://json.vn", "withOnlyOffice": false, "channel": "stable" }""");
        Deployment.FromArgs([], _dir).Should().Be(new Deployment("https://json.vn", false, "stable", false));
        Deployment.FromArgs(["--server=https://arg.vn"], _dir).Server.Should().Be("https://arg.vn", "arguments win over deploy.json");

        // Arguments only reach the first start: they are kept in the settings.
        var settings = new AppSettings();
        settings.InstallOnlyOffice.Should().BeTrue("ONLYOFFICE is installed by default");
        d.SaveInto(settings).Should().BeTrue();
        (settings.DeploymentServer, settings.InstallOnlyOffice).Should().Be(("https://drive.example.gov.vn", false));
        d.SaveInto(settings).Should().BeFalse();
    }

    [Fact]
    public void Uninstall_keeps_the_files_by_default_and_removes_accounts_tokens_and_settings()
    {
        var (home, folder) = Account("giu");
        var questions = new List<string>();
        var r = Uninstaller.Run(home, (q, def) =>
        {
            questions.Add(q);
            return def;
        }, registry: false);

        r.Should().Be(new Uninstaller.Result(1, false, true, false));
        questions.Should().ContainSingle().Which.Should().Contain(folder);
        File.ReadAllText(Path.Combine(folder, "bao-cao.docx")).Should().Be("nội dung");
        new AccountStore(home).Load().Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(home).Should().BeEmpty("tokens, state and settings are removed");
    }

    [Fact]
    public void Uninstall_can_delete_the_sync_folder_even_with_read_only_files()
    {
        var (home, folder) = Account("xoa");
        File.SetAttributes(Path.Combine(folder, "bao-cao.docx"), FileAttributes.ReadOnly); // shared view-only
        Uninstaller.Run(home, (_, _) => false, registry: false).FilesDeleted.Should().BeTrue();
        Directory.Exists(folder).Should().BeFalse();
    }

    [Fact]
    public void Uninstall_removes_the_onlyoffice_provider_and_asks_before_removing_onlyoffice()
    {
        var (home, _) = Account("oo");
        var editors = Path.Combine(_dir, "ONLYOFFICE", "DesktopEditors.exe");
        Directory.CreateDirectory(Path.Combine(_dir, "ONLYOFFICE", "providers", "bluedrive"));
        File.WriteAllText(editors, "");
        var settings = AppSettings.Load(home);
        settings.OnlyOfficeInstalledByBlueDrive = true;
        settings.Save(home);
        var asked = 0;
        var r = Uninstaller.Run(home, (_, def) =>
        {
            // Velopack stops the hook after 30 s: by the ONLYOFFICE question (which may wait), the tokens are already gone.
            if (++asked == 2) Directory.EnumerateFileSystemEntries(home).Should().BeEmpty("tokens, state and settings are removed first");
            return def;
        }, editors, registry: false);
        asked.Should().Be(2, "keep files? and remove ONLYOFFICE? (default: keep)");
        r.OnlyOfficeRemoved.Should().BeFalse();
        r.ProviderRemoved.Should().BeTrue();
        Directory.Exists(Path.Combine(_dir, "ONLYOFFICE", "providers", "bluedrive")).Should().BeFalse();
    }

    [Fact]
    public void Onlyoffice_installer_must_be_signed_by_ascensio()
    {
        var unsigned = Path.Combine(_dir, "DesktopEditors_x64.exe");
        File.WriteAllBytes(unsigned, [0x4d, 0x5a, 0, 0]);
        OnlyOfficeSetup.IsSignedBy(unsigned).Should().BeFalse();
        // A validly signed file by someone else is refused too.
        var dotnet = Environment.ProcessPath!;
        if (OnlyOfficeSetup.IsSignedBy(dotnet, "Microsoft")) OnlyOfficeSetup.IsSignedBy(dotnet).Should().BeFalse();
        OnlyOfficeSetup.InstallerUrl(System.Runtime.InteropServices.Architecture.Arm64).Should().EndWith("DesktopEditors_arm64.exe");
        OnlyOfficeSetup.InstallerUrl(System.Runtime.InteropServices.Architecture.X64).Should().StartWith("https://download.onlyoffice.com/").And.EndWith("_x64.exe");
    }

    /// <summary>
    /// Real installation (downloads ~300 MB, needs administrator rights): BLUEDRIVE_TEST_ONLYOFFICE_SERVER=http://localhost:8088 dotnet test --filter
    /// Onlyoffice_installs_for_real. Skipped otherwise.
    /// </summary>
    [Fact]
    public async Task Onlyoffice_installs_for_real_with_the_bluedrive_provider()
    {
        if (Environment.GetEnvironmentVariable("BLUEDRIVE_TEST_ONLYOFFICE_SERVER") is not { Length: > 0 } server) return;
        var exe = await OnlyOfficeSetup.InstallAsync(server, new Progress<string>(Console.WriteLine));
        File.Exists(exe).Should().BeTrue();
        OnlyOfficeSetup.IsOutdated(exe).Should().BeFalse();
        OnlyOfficeService.State(exe, server).Should().Be(ProviderState.Ready);
    }

    [Fact]
    public void Macos_and_linux_start_with_the_computer_and_read_the_battery()
    {
        var plist = System.Xml.Linq.XDocument.Parse(StartupRegistration.MacAgent("/Applications/BlueDrive.app/Contents/MacOS/BlueDrive"));
        var values = plist.Descendants("string").Select(e => e.Value).ToList();
        values.Should().ContainInOrder(StartupRegistration.MacLabel, "/Applications/BlueDrive.app/Contents/MacOS/BlueDrive", "--background");
        StartupRegistration.LinuxDesktopEntry("/opt/bluedrive/BlueDrive").Should().Contain("Exec=\"/opt/bluedrive/BlueDrive\" --background")
            .And.StartWith("[Desktop Entry]");

        ConditionsMonitor.ParsePmset("Now drawing from 'Battery Power'\n -InternalBattery-0 (id=1234)\t15%; discharging; 0:42 remaining present: true")
            .Should().Be((true, 15));
        ConditionsMonitor.ParsePmset("Now drawing from 'AC Power'\n -InternalBattery-0 (id=1234)\t100%; charged; 0:00 remaining").Should().Be((false, 100));
    }

    [Fact]
    public void Machine_wide_deploy_json_presets_macos_and_linux_computers()
    {
        var old = Deployment.MachineFile;
        try
        {
            Deployment.MachineFile = Path.Combine(_dir, "machine-deploy.json");
            File.WriteAllText(Deployment.MachineFile,
                """{ "server": "https://drive.example.gov.vn", "withOnlyOffice": false, "disableUpdates": true, "certificatePins": ["pin1=", "pin2="] }""");
            var d = Deployment.FromArgs([], Path.Combine(_dir, "app"));
            d.Should().Be(new Deployment("https://drive.example.gov.vn", false, null, true));
            d.CertificatePins.Should().Equal("pin1=", "pin2=");
            Deployment.FromArgs(["--server=https://khac.vn"], Path.Combine(_dir, "app")).Server.Should().Be("https://khac.vn");
        }
        finally { Deployment.MachineFile = old; }
    }

    [Fact]
    public void Office_owner_files_point_to_the_document_opened_locally()
    {
        File.WriteAllText(Path.Combine(_dir, "Báo cáo quý.docx"), "x");
        File.WriteAllText(Path.Combine(_dir, "Kế hoạch.xlsx"), "x");
        // Word drops the first two characters of the name, Excel keeps the whole name.
        AccountSession.DocumentForLock(Path.Combine(_dir, "~$o cáo quý.docx")).Should().Be(Path.Combine(_dir, "Báo cáo quý.docx"));
        AccountSession.DocumentForLock(Path.Combine(_dir, "~$Kế hoạch.xlsx")).Should().Be(Path.Combine(_dir, "Kế hoạch.xlsx"));
        AccountSession.DocumentForLock(Path.Combine(_dir, "~$không có.docx")).Should().BeNull();
    }

    [Fact]
    public void Msix_package_serves_explorer_status_ui_from_bluedrive()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "desktop", "packaging", "msix", "AppxManifest.template.xml"))) dir = dir.Parent;
        dir.Should().NotBeNull("the manifest template is in the repository");
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(dir!.FullName, "desktop", "packaging", "msix", "AppxManifest.template.xml"));
        var clsid = StatusUiServer.Clsid.ToString().ToUpperInvariant();
        xml.Descendants().Should().Contain(e => e.Name.LocalName == "StorageProviderStatusUISourceFactory" && (string?)e.Attribute("Clsid") == clsid);
        var server = xml.Descendants().Single(e => e.Name.LocalName == "Class" && (string?)e.Attribute("Id") == clsid).Parent!;
        (server.Name.LocalName, (string?)server.Attribute("Executable"), (string?)server.Attribute("Arguments"))
            .Should().Be(("ExeServer", "BlueDrive.exe", "-Embedding"));
    }

    [Fact]
    public void Double_click_on_office_documents_can_open_co_editing_only_inside_bluedrive_folders()
    {
        OfficeDoubleClick.Order(null).Should().Be("BlueDriveCoEdit,open");
        OfficeDoubleClick.Order("Open").Should().Be("BlueDriveCoEdit,Open");
        OfficeDoubleClick.Order("BlueDriveCoEdit,Edit, Open").Should().Be("BlueDriveCoEdit,Edit,Open");
        OfficeDoubleClick.CanCoEdit(AgentState.Idle).Should().BeTrue();
        OfficeDoubleClick.CanCoEdit(AgentState.Syncing).Should().BeTrue();
        OfficeDoubleClick.CanCoEdit(AgentState.Offline).Should().BeFalse("offline: the local copy opens with the usual program");
        ExplorerMenu.Parse(["--open", @"C:\BlueDrive\a.docx"]).Should().Be(("open", @"C:\BlueDrive\a.docx"));
        if (!OperatingSystem.IsWindows()) return;

        // A made-up document program (per user) with its own verb order "Edit,Open", which must come back.
        var progId = "BlueDrive.Test." + Guid.NewGuid().ToString("N")[..8];
        var key = $@"Software\Classes\{progId}\shell";
        using (var open = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key + @"\Open\command")) open.SetValue("", "notepad.exe \"%1\"");
        using (var shell = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key)) shell.SetValue("", "Edit,Open");
        try
        {
            OfficeDoubleClick.Register(_dir, [@"C:\Users\a\BlueDrive"], @"C:\App\BlueDrive.exe", [progId]);
            using (var shell = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key)!)
            {
                shell.GetValue("").Should().Be("BlueDriveCoEdit,Edit,Open");
                using var verb = shell.OpenSubKey("BlueDriveCoEdit")!;
                verb.GetValue("AppliesTo").Should().Be(@"System.ItemPathDisplay:~<""C:\Users\a\BlueDrive\""");
                using var cmd = verb.OpenSubKey("command")!;
                cmd.GetValue("").Should().Be(@"""C:\App\BlueDrive.exe"" --open ""%1""");
            }

            // Registering again (folders changed) does not stack; turning it off puts the program's order back.
            OfficeDoubleClick.Register(_dir, [@"D:\BlueDrive"], @"C:\App\BlueDrive.exe", [progId]);
            using (var shell = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key)!) shell.GetValue("").Should().Be("BlueDriveCoEdit,Edit,Open");
            OfficeDoubleClick.Unregister(_dir);
            using (var shell = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key)!)
            {
                shell.GetValue("").Should().Be("Edit,Open");
                shell.OpenSubKey("BlueDriveCoEdit").Should().BeNull();
            }
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Lan_sync_is_off_unless_the_user_or_the_organisation_turns_it_on()
    {
        var settings = new AppSettings();
        settings.LanSync.Should().BeFalse("Windows would ask about the firewall on every computer");
        new Deployment(null, null, null, false).SaveInto(settings).Should().BeFalse();
        new Deployment(null, null, null, false) { LanSync = true }.SaveInto(settings).Should().BeTrue();
        settings.LanSync.Should().BeTrue();
        new Deployment(null, null, null, false) { LanSync = false }.SaveInto(settings).Should().BeTrue();
        settings.LanSync.Should().BeFalse();
    }

    [Fact]
    public void Updates_come_from_the_bluedrive_server()
    {
        Updater.FeedUrl("https://drive.example.gov.vn/").Should().Be("https://drive.example.gov.vn/downloads/desktop/");
        Updater.IsInstalled.Should().BeFalse("a test or development build does not update itself");
    }

    [Fact]
    public void Only_a_beta_going_back_to_stable_may_install_an_older_version()
    {
        Updater.AllowDowngrade("stable", "1.1.0-beta.2").Should().BeTrue("beta → stable installs the stable version, even if older");
        Updater.AllowDowngrade("osx-stable", "1.1.0-beta.2").Should().BeTrue();
        Updater.AllowDowngrade("stable", "1.0.2").Should().BeFalse("a stable copy newer than the server's feed is kept");
        Updater.AllowDowngrade("beta", "1.1.0-beta.2").Should().BeFalse();
        Updater.AllowDowngrade("stable", null).Should().BeFalse();
    }

    private (string Home, string Folder) Account(string name)
    {
        var home = Path.Combine(_dir, "home-" + name);
        var folder = Path.Combine(_dir, "BlueDrive-" + name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "bao-cao.docx"), "nội dung");
        var store = new AccountStore(home);
        var acc = new AccountConfig(Guid.NewGuid(), "https://drive.example.gov.vn", folder, "an", "Nguyễn Văn An", Guid.NewGuid());
        store.Save(acc);
        store.Tokens(acc.Id).Save(new Sync.Client.DeviceTokens("a", "r", 3600, acc.DeviceId, "s"));
        new AppSettings().Save(home);
        return (home, folder);
    }
}
