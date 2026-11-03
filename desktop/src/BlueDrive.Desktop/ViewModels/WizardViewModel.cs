using System.Collections.ObjectModel;
using BlueDrive.Desktop.Services;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueDrive.Desktop.ViewModels;

public partial class RootChoice(string id, string name, string kind, bool onlineOnly) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string Note { get; } = onlineOnly ? "Chỉ xem trực tuyến theo chính sách" : kind switch
    {
        "personal" => "Tài liệu cá nhân của bạn",
        "shared" => "Tài liệu người khác chia sẻ với bạn",
        _ => "Không gian nhóm / đơn vị",
    };
    [ObservableProperty] private bool _selected = true;
}

/// <summary>First run (spec 8.1): server → sign in in the browser → sync folder → what to sync → options → done.</summary>
public partial class WizardViewModel : ObservableObject
{
    public const int Steps = 6;
    private readonly AppHost _host;
    private readonly Guid _accountId = Guid.NewGuid();
    private CancellationTokenSource? _signIn;
    private DeviceTokens? _tokens;
    private HttpRemoteStore.UserInfo? _me;
    private bool _finished;

    public WizardViewModel(AppHost host, string? presetServer)
    {
        _host = host;
        Server = presetServer ?? "";
        ServerLocked = presetServer is not null;
        StartWithWindows = host.Settings.StartWithWindows;
        Notifications = host.Settings.Notifications;
        InstallOnlyOffice = host.Settings.InstallOnlyOffice;
    }

    public ObservableCollection<RootChoice> Roots { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title), nameof(CanBack), nameof(NextText))] private int _step;
    [ObservableProperty] private string _server = "";
    [ObservableProperty] private bool _serverLocked;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _signedIn;
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string? _folderInfo;
    [ObservableProperty] private bool _folderOk;
    [ObservableProperty] private bool _onDemand = true;
    [ObservableProperty] private bool _onDemandAvailable;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private bool _installOnlyOffice;
    /// <summary>Only offered when ONLYOFFICE Desktop Editors is not on this computer yet.</summary>
    public bool OnlyOfficeOffered { get; } = OperatingSystem.IsWindows() && OnlyOfficeService.FindEditors() is null;

    /// <summary>Raised when the wizard is done (or cancelled) and the window should close.</summary>
    public event Action? Close;

    public string Title => Step switch
    {
        0 => "Chào mừng đến với BlueDrive",
        1 => "Đăng nhập",
        2 => "Thư mục BlueDrive trên máy này",
        3 => "Chọn nội dung đồng bộ",
        4 => "Tùy chọn",
        _ => "BlueDrive đã sẵn sàng",
    };

    public bool CanBack => Step is > 0 and < Steps - 1;
    public string NextText => Step switch { 4 => "Hoàn tất", Steps - 1 => "Đóng", _ => "Tiếp" };

    private Uri? ServerUri =>
        Uri.TryCreate(Server.Trim().Contains("://") ? Server.Trim() : "https://" + Server.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http"
            ? BlueDriveHttp.Normalize(new Uri(u.GetLeftPart(UriPartial.Path)))
            : null;

    [RelayCommand]
    private void Back()
    {
        Error = null;
        if (CanBack) Step--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Error = null;
        Busy = true;
        try
        {
            switch (Step)
            {
                case 0:
                    if (ServerUri is not { } u) { Error = "Địa chỉ máy chủ không hợp lệ, ví dụ: https://bluedrive.bluestar.com.vn"; return; }
                    if (u.Scheme == "http" && !u.IsLoopback) { Error = "Máy chủ phải dùng HTTPS."; return; }
                    using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    {
                        try { (await probe.GetAsync(new Uri(u, "api/health"))).EnsureSuccessStatusCode(); }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                        {
                            Error = "Không kết nối được máy chủ BlueDrive. Kiểm tra địa chỉ và kết nối mạng.";
                            return;
                        }
                    }
                    Step = 1;
                    break;
                case 1:
                    if (_tokens is null) { Error = "Hãy đăng nhập trước."; return; }
                    PrepareFolder();
                    Step = 2;
                    break;
                case 2:
                    CheckFolder();
                    if (!FolderOk) return;
                    await LoadRootsAsync();
                    Step = 3;
                    break;
                case 3:
                    if (!Roots.Any(r => r.Selected)) { Error = "Chọn ít nhất một mục để đồng bộ."; return; }
                    Step = 4;
                    break;
                case 4:
                    await FinishAsync();
                    Step = 5;
                    break;
                default:
                    Close?.Invoke();
                    break;
            }
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (ServerUri is not { } server) return;
        Error = null;
        Busy = true;
        _signIn = new CancellationTokenSource();
        try
        {
            var auth = new DesktopAuthenticator(server);
            _tokens = await auth.SignInAsync(DeviceInfo.Current(_host.AppVersion), null, TimeSpan.FromMinutes(10), _signIn.Token);
            var store = _host.Store.Tokens(_accountId);
            store.Save(_tokens);
            using var http = BlueDriveHttp.Create(server, store, auth);
            _me = await new HttpRemoteStore(http).MeAsync(CancellationToken.None);
            if (_host.Sessions.Any(s => string.Equals(s.Config.ServerUrl, server.ToString(), StringComparison.OrdinalIgnoreCase) && s.Config.Username == _me.Username))
            {
                Error = $"Tài khoản {_me.Username} đã được thêm trên máy này.";
                _tokens = null;
                return;
            }
            SignedIn = $"Đã đăng nhập: {_me.FullName} ({_me.Username})";
        }
        catch (OperationCanceledException) { Error = "Đã hủy đăng nhập."; }
        catch (TimeoutException) { Error = "Hết thời gian chờ đăng nhập. Hãy thử lại."; }
        catch (SignInRequiredException ex) { Error = ex.Message; }
        catch (Exception ex) when (ex is Sync.Core.RemoteTransientException or HttpRequestException) { Error = "Không kết nối được máy chủ BlueDrive."; }
        finally { Busy = false; }
    }

    private void PrepareFolder()
    {
        if (Folder.Length > 0) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Folder = _host.Sessions.Count == 0 ? Path.Combine(home, "BlueDrive") : Path.Combine(home, $"BlueDrive - {ServerUri!.Host}");
        CheckFolder();
    }

    partial void OnFolderChanged(string value) => CheckFolder();

    /// <summary>Folder checks (spec 8.1): not used by another account, writable, free space; NTFS for Files On-Demand.</summary>
    public void CheckFolder()
    {
        FolderOk = false;
        if (string.IsNullOrWhiteSpace(Folder)) { FolderInfo = "Chọn thư mục."; return; }
        string full;
        try { full = Path.GetFullPath(Folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { FolderInfo = "Đường dẫn không hợp lệ."; return; }
        if (_host.Store.Load().Any(a => IsSameOrInside(full, a.SyncFolder) || IsSameOrInside(a.SyncFolder, full)))
        {
            FolderInfo = "Thư mục này đang được một tài khoản BlueDrive khác dùng.";
            return;
        }
        if (Path.GetPathRoot(full) == full) { FolderInfo = "Không dùng trực tiếp thư mục gốc của ổ đĩa."; return; }
        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".bluedrive-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FolderInfo = "Không ghi được vào thư mục này. Hãy chọn thư mục khác.";
            return;
        }
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        OnDemandAvailable = OnDemandSupport.IsSupported(full);
        if (!OnDemandAvailable) OnDemand = false;
        var existing = Directory.EnumerateFileSystemEntries(full).Any();
        FolderInfo = $"Ổ {drive.Name} ({drive.DriveFormat}) còn trống {Formatting.Bytes(drive.AvailableFreeSpace)}."
                     + (existing ? " Thư mục đã có dữ liệu: tệp trùng với BlueDrive sẽ được liên kết, khác nhau thì giữ cả hai, không ghi đè." : "")
                     + (OnDemandAvailable ? "" : " Ổ này không hỗ trợ tệp theo yêu cầu: mọi tệp sẽ được tải về máy.");
        FolderOk = true;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadRootsAsync()
    {
        var server = ServerUri!;
        var store = _host.Store.Tokens(_accountId);
        using var http = BlueDriveHttp.Create(server, store, new DesktopAuthenticator(server));
        var roots = await new HttpRemoteStore(http).RootsAsync(CancellationToken.None);
        Roots.Clear();
        foreach (var r in roots)
            Roots.Add(new RootChoice(r.RootId, r.Kind == "personal" ? "Tài liệu của tôi" : r.Name, r.Kind, r.OnlineOnly && !OnDemand) { Selected = !r.OnlineOnly || OnDemand });
    }

    private async Task FinishAsync()
    {
        var settings = _host.Settings;
        settings.StartWithWindows = StartWithWindows;
        settings.Notifications = Notifications;
        if (OnlyOfficeOffered) settings.InstallOnlyOffice = InstallOnlyOffice;
        _host.SaveSettings(settings);
        var acc = new AccountConfig(_accountId, ServerUri!.ToString(), Path.GetFullPath(Folder), _me!.Username, _me.FullName, _tokens!.DeviceId) { OnDemand = OnDemand };
        await _host.AddAccountAsync(acc, Roots.Where(r => !r.Selected).Select(r => r.Id).ToList());
        _finished = true;
    }

    [RelayCommand]
    private void OpenFolder() => FlyoutViewModel.OpenPath(Folder);

    /// <summary>Window closed before finishing: forget the half-made account (tokens).</summary>
    public void Abandon()
    {
        _signIn?.Cancel();
        if (_finished) return;
        _host.Store.Tokens(_accountId).Clear();
        var dir = _host.Store.AccountDir(_accountId);
        if (Directory.Exists(dir) && !_host.Store.Load().Any(a => a.Id == _accountId)) Directory.Delete(dir, recursive: true);
    }
}
