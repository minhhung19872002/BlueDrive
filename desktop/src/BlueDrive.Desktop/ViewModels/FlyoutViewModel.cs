using System.Collections.ObjectModel;
using System.Diagnostics;
using BlueDrive.Desktop.Services;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueDrive.Desktop.ViewModels;

/// <summary>One line of "Hoạt động gần đây".</summary>
public sealed record ActivityItem(string Name, string Folder, string Action, string When, string Glyph, string FullPath)
{
    public static ActivityItem From(ActivityEntry e, AccountSession s, DateTime now)
    {
        var name = NameMapper.NameOf(e.Path);
        return new ActivityItem(name, NameMapper.ParentOf(e.Path) ?? "", e.Action, Ago(e.At, now), Formatting.Glyph(name), s.Locate("", e.Path));
    }

    public static string Ago(DateTime at, DateTime now)
    {
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return "Vừa xong";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} phút trước";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} giờ trước";
        return at.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    }
}

public partial class FlyoutViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly App? _app;

    public FlyoutViewModel(AppHost host, App? app)
    {
        _host = host;
        _app = app;
    }

    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<AccountSession> Accounts => _host.Sessions;

    [ObservableProperty] private AccountSession? _session;
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _quotaText = "";
    [ObservableProperty] private double _quotaPercent;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string? _problemBanner;
    /// <summary>"Đang cùng soạn trực tuyến: …" (spec 13A: who is editing).</summary>
    [ObservableProperty] private string? _coEditBanner;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string? _transferText;
    [ObservableProperty] private double _transferPercent;
    [ObservableProperty] private bool _massDeletePending;
    [ObservableProperty] private bool _multipleAccounts;

    partial void OnSessionChanged(AccountSession? value) => Refresh();

    /// <summary>Reads the current state (called on the UI thread whenever something changed).</summary>
    public void Refresh()
    {
        MultipleAccounts = _host.Sessions.Count > 1;
        var s = Session is not null && _host.Sessions.Contains(Session) ? Session : _host.Sessions.FirstOrDefault();
        if (!ReferenceEquals(s, Session))
        {
            Session = s;
            return;
        }
        if (s is null) return;
        AccountName = $"{s.Config.DisplayName} ({s.Config.Username})";
        if (s.Personal is { QuotaBytes: > 0 } p)
        {
            QuotaText = $"Đã dùng {Formatting.Bytes(p.UsedBytes)} / {Formatting.Bytes(p.QuotaBytes)}";
            QuotaPercent = s.QuotaPercent;
        }
        else QuotaText = new Uri(s.Config.ServerUrl).Host;
        var st = s.Agent.Status;
        (StatusText, StatusDetail) = Formatting.Status(st, s.Agent.IsPaused);
        IsPaused = s.Agent.IsPaused;
        MassDeletePending = st.PendingMassDelete > 0;
        var conflicts = s.Conflicts().Count;
        var problems = st.Problems.Count + conflicts;
        ProblemBanner = problems > 0 ? $"{problems} mục không đồng bộ được hoặc bị xung đột – Xem" : null;
        CoEditBanner = s.Editing.Count == 0 ? null
            : "Đang cùng soạn trực tuyến: " + string.Join("; ", s.Editing.Values.Take(3).Select(e => $"\"{e.Name}\" — {string.Join(", ", e.Editors.Take(3))}"))
              + (s.Editing.Count > 3 ? $" và {s.Editing.Count - 3} tài liệu khác" : "");
        if (s.Transfer is { } t)
        {
            TransferText = $"{(t.Upload ? "Đang tải lên" : "Đang tải xuống")} {t.Name}" + (t.Total > 0 ? $" — {Formatting.Bytes(t.Done)} / {Formatting.Bytes(t.Total)}" : "");
            TransferPercent = t.Percent;
        }
        else TransferText = null;
        var now = DateTime.UtcNow;
        var items = s.Activity(30).Select(e => ActivityItem.From(e, s, now)).ToList();
        if (Activity.Count != items.Count || !Activity.SequenceEqual(items))
        {
            Activity.Clear();
            foreach (var i in items) Activity.Add(i);
        }
    }

    [RelayCommand]
    private void OpenFolder() => OpenPath(Session?.Config.SyncFolder);

    [RelayCommand]
    private void OpenWeb()
    {
        if (Session is not null) OpenPath(Session.Config.ServerUrl);
    }

    [RelayCommand]
    private void Help()
    {
        if (Session is not null) OpenPath(new Uri(new Uri(Session.Config.ServerUrl), "support").ToString());
    }

    [RelayCommand]
    private void OpenActivity(ActivityItem? item)
    {
        if (item is not null && File.Exists(item.FullPath)) OpenPath(item.FullPath);
    }

    [RelayCommand]
    private void OpenActivityLocation(ActivityItem? item)
    {
        if (item is null) return;
        if (File.Exists(item.FullPath)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
        else OpenPath(Path.GetDirectoryName(item.FullPath));
    }

    /// <summary>Tạm dừng: "2", "8", "24" (hours) or "0" (until resumed).</summary>
    [RelayCommand]
    private void Pause(string hours)
    {
        if (Session is null) return;
        Session.SetPaused(hours == "0" ? Timeout.InfiniteTimeSpan : TimeSpan.FromHours(int.Parse(hours)));
        Refresh();
    }

    [RelayCommand]
    private void Resume()
    {
        Session?.SetPaused(null);
        Refresh();
    }

    [RelayCommand]
    private void SyncNow() => Session?.Agent.SyncNow();

    [RelayCommand]
    private void ConfirmMassDelete() => Session?.Agent.ResolveMassDelete(deleteOnServer: true);

    [RelayCommand]
    private void RestoreMassDelete() => Session?.Agent.ResolveMassDelete(deleteOnServer: false);

    [RelayCommand]
    private void Settings() => _app?.ShowSettings();

    [RelayCommand]
    private void Problems() => _app?.ShowSettings();

    [RelayCommand]
    private void Quit() => _ = _app?.QuitAsync();

    public static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public static class Formatting
{
    public static string Bytes(long b) => (b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
    }).Replace('.', ',');

    /// <summary>Headline + detail of the flyout.</summary>
    public static (string, string) Status(AgentStatus s, bool paused)
    {
        if (paused) return ("Đã tạm dừng đồng bộ", s.Message);
        return s.State switch
        {
            AgentState.Idle when s.PendingMassDelete > 0 => ("Cần xác nhận xóa", s.Message),
            AgentState.Idle when s.Problems.Count > 0 => ("Đã cập nhật", $"{s.Problems.Count} mục chưa đồng bộ được."),
            AgentState.Idle => ("Đã cập nhật", s.LastSyncAt is { } at ? $"Lần đồng bộ gần nhất: {ActivityItem.Ago(at, DateTime.UtcNow).ToLowerInvariant()}" : ""),
            AgentState.Syncing or AgentState.Starting => ("Đang đồng bộ…", ""),
            AgentState.Offline => ("Ngoại tuyến", "Thay đổi sẽ được đồng bộ khi có mạng."),
            AgentState.NeedsSignIn or AgentState.Revoked => ("Cần đăng nhập lại", s.Message),
            AgentState.UpdateRequired => ("Cần cập nhật BlueDrive", s.Message),
            AgentState.Wiped => ("Đã xóa dữ liệu trên máy", s.Message),
            AgentState.Paused => ("Đã tạm dừng đồng bộ", s.Message),
            _ => ("Có lỗi", s.Message),
        };
    }

    /// <summary>File type glyph for the activity list (Segoe Fluent / MDL2 compatible emoji-free letters).</summary>
    public static string Glyph(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".odt" or ".rtf" => "W",
        ".xls" or ".xlsx" or ".ods" or ".csv" => "X",
        ".ppt" or ".pptx" or ".odp" => "P",
        ".pdf" => "PDF",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "IMG",
        ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => "VID",
        ".mp3" or ".wav" or ".m4a" or ".flac" => "AUD",
        ".zip" or ".rar" or ".7z" => "ZIP",
        "" => "DIR",
        _ => "TXT",
    };
}
