using System.Collections.ObjectModel;
using BlueDrive.Desktop.Services;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Core;
using BlueDrive.Sync.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueDrive.Desktop.ViewModels;

public sealed record AccountRow(AccountSession Session, string Name, string Server, string Folder, string Quota, string Mode);

public partial class RootToggle(SettingsViewModel owner, string id, string name, bool included, string? rootId = null) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>Null for a root; the root of a folder.</summary>
    public string? RootId { get; } = rootId;
    public ObservableCollection<RootToggle> Children { get; } = [];
    private bool _loading = true;
    [ObservableProperty] private bool _included = included;

    public void Loaded() => _loading = false;

    /// <summary>Puts the checkbox back without acting on it (refused or cancelled).</summary>
    public void Reset(bool value)
    {
        _loading = true;
        Included = value;
        _loading = false;
    }

    partial void OnIncludedChanged(bool value)
    {
        if (!_loading) _ = RootId is null ? owner.ToggleRootAsync(this, value) : owner.ToggleFolderAsync(this, value);
    }
}

/// <summary>A row of "Vấn đề": a conflict or an item that could not be synced.</summary>
public sealed record ProblemItem(AccountSession Session, string Title, string Detail, string Path, ConflictEntry? Conflict)
{
    public bool IsConflict => Conflict is not null;
    public bool IsError => Conflict is null;
}

/// <summary>Settings (spec 8.3): Tài khoản, Đồng bộ, Mạng, Chung, Vấn đề, Giới thiệu.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly App? _app;

    /// <summary>View services: confirmation dialog and "save as" picker.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }
    public Func<string, Task<string?>>? SaveFile { get; set; }

    public SettingsViewModel(AppHost host, App? app)
    {
        _host = host;
        _app = app;
        var s = host.Settings;
        _uploadLimit = s.UploadLimitKBps;
        _downloadLimit = s.DownloadLimitKBps;
        _proxyIndex = (int)s.Proxy;
        _proxyAddress = s.ProxyAddress ?? "";
        _startWithWindows = s.StartWithWindows;
        _pauseOnMetered = s.PauseOnMetered;
        _pauseOnLowBattery = s.PauseOnLowBattery;
        _notifications = s.Notifications;
        _themeIndex = (int)s.Theme;
        _freeUpIndex = Array.IndexOf(FreeUpDays, s.FreeUpAfterDays) is var i and >= 0 ? i : 0;
        Version = $"BlueDrive Desktop {host.AppVersion}";
        Refresh();
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<RootToggle> RootToggles { get; } = [];
    public ObservableCollection<ProblemItem> Problems { get; } = [];
    public string Version { get; }
    public string IgnoreList { get; } = "~$*, *.tmp, ~*.tmp, .~lock.*#, Thumbs.db, desktop.ini, .DS_Store, *.partial, *.crdownload, *.part, $RECYCLE.BIN, "
                                        + "System Volume Information, liên kết tượng trưng / junction, tệp vừa ẩn vừa hệ thống";

    public static readonly int[] FreeUpDays = [0, 1, 7, 14, 30, 60];
    public string[] FreeUpLabels { get; } = ["Không bao giờ", "Sau 1 ngày", "Sau 7 ngày", "Sau 14 ngày", "Sau 30 ngày", "Sau 60 ngày"];
    public string[] ThemeLabels { get; } = ["Theo hệ thống", "Sáng", "Tối"];
    public string[] ProxyLabels { get; } = ["Theo cài đặt của Windows", "Không dùng proxy", "Tự nhập"];

    [ObservableProperty] private AccountRow? _selectedAccount;
    [ObservableProperty] private bool _onDemand;
    [ObservableProperty] private bool _onDemandAvailable;
    [ObservableProperty] private string? _syncMessage;
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private int _uploadLimit;
    [ObservableProperty] private int _downloadLimit;
    [ObservableProperty] private int _proxyIndex;
    [ObservableProperty] private string _proxyAddress = "";
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _pauseOnMetered;
    [ObservableProperty] private bool _pauseOnLowBattery;
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _freeUpIndex;
    [ObservableProperty] private string? _aboutMessage;
    [ObservableProperty] private bool _busy;

    public bool ManualProxy => ProxyIndex == (int)ProxyChoice.Manual;

    partial void OnProxyIndexChanged(int value) => OnPropertyChanged(nameof(ManualProxy));

    public void Refresh()
    {
        var selectedId = SelectedAccount?.Session.Config.Id;
        Accounts.Clear();
        foreach (var s in _host.Sessions)
        {
            var quota = s.Personal is { QuotaBytes: > 0 } p ? $"{Formatting.Bytes(p.UsedBytes)} / {Formatting.Bytes(p.QuotaBytes)}" : "";
            Accounts.Add(new AccountRow(s, $"{s.Config.DisplayName} ({s.Config.Username})", s.Config.ServerUrl, s.Config.SyncFolder, quota,
                s.Config.OnDemand ? "Tệp theo yêu cầu" : "Đồng bộ đầy đủ"));
        }
        SelectedAccount = Accounts.FirstOrDefault(a => a.Session.Config.Id == selectedId) ?? Accounts.FirstOrDefault();
        LoadProblems();
    }

    partial void OnSelectedAccountChanged(AccountRow? value)
    {
        RootToggles.Clear();
        if (value is null) return;
        var s = value.Session;
        Folder = s.Config.SyncFolder;
        OnDemandAvailable = OnDemandSupport.IsSupported(s.Config.SyncFolder);
        _suppressMode = true;
        OnDemand = s.Config.OnDemand;
        _suppressMode = false;
        var excluded = s.Agent.ExcludedRoots;
        foreach (var r in s.Agent.Roots)
        {
            var t = new RootToggle(this, r.RootId, r.Kind == "personal" ? "Tài liệu của tôi" : r.Name, !excluded.Contains(r.RootId));
            if (!excluded.Contains(r.RootId)) AddFolders(s, r.RootId, null, t.Children, s.ExcludedFoldersOf(r.RootId), s.FoldersOf(r.RootId).ToLookup(f => f.ParentId));
            t.Loaded();
            RootToggles.Add(t);
        }
    }

    private bool _suppressMode;

    partial void OnOnDemandChanged(bool value)
    {
        if (_suppressMode || SelectedAccount is null) return;
        _ = ChangeModeAsync(SelectedAccount.Session, value);
    }

    private async Task ChangeModeAsync(AccountSession s, bool onDemand)
    {
        Busy = true;
        try
        {
            SyncMessage = onDemand ? "Đang bật tệp theo yêu cầu…" : "Đang tải mọi tệp về máy trước khi tắt tệp theo yêu cầu…";
            await _host.SetOnDemandAsync(s, onDemand, new Progress<string>(m => SyncMessage = m));
            SyncMessage = onDemand ? "Đã bật tệp theo yêu cầu." : "Đã chuyển sang đồng bộ đầy đủ.";
            Refresh();
        }
        catch (IOException ex) { SyncMessage = $"Không đổi được chế độ: {ex.Message}"; }
        finally { Busy = false; }
    }

    private void AddFolders(AccountSession s, string rootId, string? parentId, ObservableCollection<RootToggle> into, IReadOnlySet<string> excluded,
        ILookup<string?, Sync.Core.RemoteItem> byParent)
    {
        foreach (var f in byParent[parentId].OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var t = new RootToggle(this, f.Id, f.Name, !excluded.Contains(f.Id), rootId);
            if (!excluded.Contains(f.Id)) AddFolders(s, rootId, f.Id, t.Children, excluded, byParent);
            t.Loaded();
            into.Add(t);
        }
    }

    public async Task ToggleFolderAsync(RootToggle t, bool include)
    {
        if (SelectedAccount is null || t.RootId is null) return;
        if (!include && Confirm is not null && !await Confirm("Bỏ đồng bộ thư mục",
                $"Thư mục \"{t.Name}\" và mọi thứ bên trong sẽ được gỡ khỏi máy này (vào Thùng rác) sau khi các thay đổi đã lên BlueDrive. Tư liệu trên BlueDrive không bị ảnh hưởng.", "Bỏ đồng bộ"))
        {
            t.Reset(true);
            return;
        }
        Busy = true;
        try
        {
            var ok = await SelectedAccount.Session.Agent.SetFolderIncludedAsync(t.RootId, t.Id, include);
            SyncMessage = ok
                ? include ? $"Thư mục \"{t.Name}\" sẽ được tải về máy." : $"Đã bỏ thư mục \"{t.Name}\" khỏi máy này."
                : $"\"{t.Name}\" còn thay đổi chưa lên BlueDrive, chưa thể bỏ đồng bộ. Hãy thử lại sau.";
            if (!ok) t.Reset(true);
            else if (!include) t.Children.Clear();
        }
        finally { Busy = false; }
    }

    public async Task ToggleRootAsync(RootToggle t, bool include)
    {
        if (SelectedAccount is null) return;
        if (!include && Confirm is not null && !await Confirm("Bỏ đồng bộ thư mục",
                $"\"{t.Name}\" sẽ được gỡ khỏi máy này (vào Thùng rác) sau khi mọi thay đổi đã lên BlueDrive. Tư liệu trên BlueDrive không bị ảnh hưởng.", "Bỏ đồng bộ"))
        {
            t.Reset(true);
            return;
        }
        Busy = true;
        try
        {
            var ok = await SelectedAccount.Session.Agent.SetRootIncludedAsync(t.Id, include);
            SyncMessage = ok
                ? include ? $"Đã thêm \"{t.Name}\" vào đồng bộ." : $"Đã bỏ \"{t.Name}\" khỏi máy này."
                : $"\"{t.Name}\" còn thay đổi chưa lên BlueDrive, chưa thể bỏ đồng bộ. Hãy thử lại sau.";
            if (!ok) t.Reset(true);
        }
        finally { Busy = false; }
    }

    private void LoadProblems()
    {
        Problems.Clear();
        foreach (var s in _host.Sessions)
        {
            foreach (var c in s.Conflicts())
                Problems.Add(new ProblemItem(s, NameMapper.NameOf(c.Path), c.Kind switch
                {
                    "content" => $"Đã sửa ở cả hai nơi — bản trên máy được lưu thành \"{NameMapper.NameOf(c.ConflictPath)}\"",
                    "read_only" => "Bạn chỉ có quyền xem: bản sửa được chuyển vào \"_Không đồng bộ được\"",
                    _ => "Không còn quyền truy cập: thay đổi được chuyển vào \"_Không đồng bộ được\"",
                }, s.Agent.LocalPathOf(c.RootId, c.Path), c));
            foreach (var p in s.Agent.Status.Problems)
            {
                var sep = p.IndexOf(": ", StringComparison.Ordinal);
                var path = sep > 0 ? p[..sep] : "";
                Problems.Add(new ProblemItem(s, sep > 0 ? NameMapper.NameOf(path) : p, sep > 0 ? p[(sep + 2)..] : "", s.Locate("", path), null));
            }
        }
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void AddAccount() => _app?.ShowWizard();

    [RelayCommand]
    private async Task UnlinkAsync(AccountRow? row)
    {
        if (row is null) return;
        if (Confirm is not null && !await Confirm("Hủy liên kết máy này",
                $"Ngừng đồng bộ {row.Name} trên máy này. Tệp đã tải về máy được giữ nguyên; tệp chỉ có trên mạng (chưa tải) được gỡ khỏi máy và vẫn còn trên BlueDrive.", "Hủy liên kết"))
            return;
        await _host.RemoveAccountAsync(row.Session, deleteFiles: false);
        Refresh();
    }

    [RelayCommand]
    private void OpenFolder() => FlyoutViewModel.OpenPath(Folder);

    [RelayCommand]
    private void ResolveProblem(ProblemItem? p)
    {
        if (p?.Conflict is { } c) _app?.ShowConflict(p.Session, c);
    }

    [RelayCommand]
    private void Retry(ProblemItem? p) => p?.Session.Agent.SyncNow();

    [RelayCommand]
    private void OpenLocation(ProblemItem? p)
    {
        if (p is null) return;
        if (File.Exists(p.Path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{p.Path}\"") { UseShellExecute = true });
        else FlyoutViewModel.OpenPath(Path.GetDirectoryName(p.Path));
    }

    [RelayCommand]
    private async Task IgnoreAsync(ProblemItem? p)
    {
        if (p?.Conflict is { } c) await p.Session.Agent.ResolveConflictAsync(c, SyncAgent.ConflictChoice.KeepBoth);
        LoadProblems();
    }

    [RelayCommand]
    private void RefreshProblems() => LoadProblems();

    [RelayCommand]
    private void Save()
    {
        var s = _host.Settings;
        s.UploadLimitKBps = Math.Max(0, UploadLimit);
        s.DownloadLimitKBps = Math.Max(0, DownloadLimit);
        s.Proxy = (ProxyChoice)ProxyIndex;
        s.ProxyAddress = ProxyAddress.Trim();
        s.StartWithWindows = StartWithWindows;
        s.PauseOnMetered = PauseOnMetered;
        s.PauseOnLowBattery = PauseOnLowBattery;
        s.Notifications = Notifications;
        s.Theme = (ThemeChoice)ThemeIndex;
        s.FreeUpAfterDays = FreeUpDays[Math.Clamp(FreeUpIndex, 0, FreeUpDays.Length - 1)];
        _host.SaveSettings(s);
        App.ApplyTheme(s.Theme);
        SyncMessage = AboutMessage = "Đã lưu cài đặt.";
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var target = SaveFile is null ? null : await SaveFile($"BlueDrive-chan-doan-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (target is null) return;
        Busy = true;
        try
        {
            await Task.Run(() => Diagnostics.Export(_host.Store, _host.LogsDir, target, _host.AppVersion));
            AboutMessage = $"Đã xuất gói chẩn đoán: {target}";
        }
        catch (IOException ex) { AboutMessage = $"Không xuất được: {ex.Message}"; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void Support()
    {
        var server = _host.Sessions.FirstOrDefault()?.Config.ServerUrl;
        if (server is not null) FlyoutViewModel.OpenPath(new Uri(new Uri(server), "support").ToString());
    }
}
