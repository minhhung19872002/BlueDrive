using System.Collections.ObjectModel;
using BlueDrive.Desktop.Services;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueDrive.Desktop.ViewModels;

public sealed record PrincipalChoice(string Type, Guid Id, string Name, string Detail)
{
    public string Label => Type == "group" ? $"Nhóm: {Name}" : string.IsNullOrEmpty(Detail) ? Name : $"{Name} — {Detail}";
}

public sealed record ShareRow(Guid Id, string Name, string Level, string? Inherited)
{
    public bool CanRemove => Inherited is null;
}

/// <summary>"Chia sẻ…" from Explorer (spec 7.2): invite people / groups with a permission, or make a link (password, expiry).</summary>
public partial class ShareViewModel : ObservableObject
{
    private readonly BlueDriveApi _api;
    private readonly string _itemId;
    private readonly string _type;
    private CancellationTokenSource? _search;

    public ShareViewModel(BlueDriveApi api, string itemId, bool isFolder, string name)
    {
        _api = api;
        _itemId = itemId;
        _type = isFolder ? "folder" : "file";
        Title = $"Chia sẻ \"{name}\"";
        _ = LoadSharesAsync();
    }

    public string Title { get; }
    public string[] LevelLabels { get; } = ["Chỉ xem", "Xem và tải xuống", "Chỉnh sửa"];
    private static readonly string[] Levels = ["view", "download", "edit"];
    public static string LevelText(string level) => level switch { "view" => "Chỉ xem", "download" => "Xem và tải xuống", "edit" => "Chỉnh sửa", "manage" => "Quản lý", _ => level };

    public ObservableCollection<PrincipalChoice> Results { get; } = [];
    public ObservableCollection<ShareRow> Shares { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PrincipalChoice? _selected;
    [ObservableProperty] private int _levelIndex = 1;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _allowDownload = true;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private DateTimeOffset? _expires;
    [ObservableProperty] private bool _requireLogin;
    [ObservableProperty] private string? _linkUrl;

    /// <summary>The view copies text to the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    partial void OnQueryChanged(string value)
    {
        _search?.Cancel();
        _search = new CancellationTokenSource();
        _ = SearchAsync(value.Trim(), _search.Token);
    }

    private async Task SearchAsync(string q, CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            if (q.Length < 2) { Results.Clear(); return; }
            var users = await _api.FindUsersAsync(q, ct);
            IReadOnlyList<BlueDriveApi.Group> groups;
            try { groups = await _api.FindGroupsAsync(q, ct); }
            catch (RemotePermanentException) { groups = []; }
            if (ct.IsCancellationRequested) return;
            Results.Clear();
            foreach (var u in users) Results.Add(new PrincipalChoice("user", u.Id, u.FullName, $"{u.Username}{(u.OrganizationName is { } o ? " · " + o : "")}"));
            foreach (var g in groups) Results.Add(new PrincipalChoice("group", g.Id, g.Name, ""));
            Selected = Results.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException) { Message = ex.Message; }
    }

    private async Task LoadSharesAsync()
    {
        try
        {
            var shares = await _api.SharesAsync(_type, _itemId);
            Shares.Clear();
            foreach (var s in shares)
                Shares.Add(new ShareRow(s.Id, s.SharedWithName, LevelText(s.AccessLevel), s.InheritedFromName is { } f ? $"Kế thừa từ \"{f}\"" : null));
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task InviteAsync()
    {
        if (Selected is null) { Message = "Hãy tìm và chọn người hoặc nhóm."; return; }
        Busy = true;
        try
        {
            await _api.ShareAsync(_type, _itemId, Selected.Type, Selected.Id, Levels[Math.Clamp(LevelIndex, 0, 2)], Note);
            Message = $"Đã chia sẻ với {Selected.Name} ({LevelLabels[LevelIndex].ToLowerInvariant()}).";
            Query = "";
            Note = "";
            await LoadSharesAsync();
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException) { Message = ex.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RemoveAsync(ShareRow? row)
    {
        if (row is null || !row.CanRemove) return;
        try
        {
            await _api.RemoveShareAsync(row.Id);
            Shares.Remove(row);
            Message = $"Đã hủy chia sẻ với {row.Name}.";
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task CreateLinkAsync()
    {
        Busy = true;
        try
        {
            var link = await _api.CreateLinkAsync(_itemId, AllowDownload, Password, Expires?.UtcDateTime, RequireLogin);
            LinkUrl = link.Url;
            if (CopyToClipboard is not null) await CopyToClipboard(link.Url);
            Message = "Đã tạo liên kết và sao chép vào bộ nhớ tạm" + (link.HasPassword ? " (người nhận cần mật khẩu)." : ".");
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException) { Message = ex.Message; }
        finally { Busy = false; }
    }
}

/// <summary>"Lịch sử phiên bản…": versions of a file; save an old one, or make it current again.</summary>
public partial class VersionsViewModel : ObservableObject
{
    private readonly AccountSession _session;
    private readonly string _fileId;

    public VersionsViewModel(AccountSession session, string fileId, string name)
    {
        _session = session;
        _fileId = fileId;
        Title = $"Lịch sử phiên bản — {name}";
        FileName = name;
        _ = LoadAsync();
    }

    public sealed record Row(Guid Id, string Label, string Detail, bool IsCurrent)
    {
        public bool CanRestore => !IsCurrent;
    }

    public string Title { get; }
    public string FileName { get; }
    public ObservableCollection<Row> Versions { get; } = [];
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _busy;

    /// <summary>The view asks where to save an old version.</summary>
    public Func<string, Task<string?>>? SaveAs { get; set; }

    private async Task LoadAsync()
    {
        try
        {
            var list = await _session.Agent.Api.VersionsAsync(_fileId);
            Versions.Clear();
            foreach (var v in list.OrderByDescending(v => v.VersionNo))
                Versions.Add(new Row(v.Id, $"Phiên bản {v.VersionNo}{(v.IsCurrent ? " (hiện tại)" : "")}",
                    $"{v.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {v.CreatedByName} · {Formatting.Bytes(v.SizeBytes)}{(string.IsNullOrEmpty(v.ChangeNote) ? "" : " · " + v.ChangeNote)}",
                    v.IsCurrent));
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException or RemoteNotFoundException) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task RestoreAsync(Row? row)
    {
        if (row is null) return;
        Busy = true;
        try
        {
            await _session.Agent.Api.RestoreVersionAsync(_fileId, row.Id);
            _session.Agent.SyncNow();
            Message = $"Đã khôi phục {row.Label.ToLowerInvariant()} thành phiên bản hiện tại; tệp trên máy sẽ được cập nhật.";
            await LoadAsync();
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException) { Message = ex.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SaveCopyAsync(Row? row)
    {
        if (row is null || SaveAs is null) return;
        var (stem, ext) = NameMapper.Split(FileName);
        var target = await SaveAs($"{stem} (phiên bản {row.Label.Split(' ')[2]}){ext}");
        if (target is null) return;
        Busy = true;
        try
        {
            await using (var f = File.Create(target)) await _session.Agent.Api.DownloadVersionAsync(_fileId, row.Id, f);
            Message = $"Đã lưu: {target}";
        }
        catch (Exception ex) when (ex is RemoteTransientException or RemotePermanentException or IOException) { Message = ex.Message; }
        finally { Busy = false; }
    }
}
