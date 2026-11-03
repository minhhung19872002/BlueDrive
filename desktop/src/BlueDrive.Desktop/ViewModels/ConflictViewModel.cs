using Avalonia.Media.Imaging;
using BlueDrive.Desktop.Services;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueDrive.Desktop.ViewModels;

/// <summary>One side of the conflict window: size, modified time, who / which computer, preview.</summary>
public sealed record VersionInfo(string Title, string Path, string Size, string Modified, string Who, string? TextPreview, Bitmap? ImagePreview);

/// <summary>Conflict window (spec 8.4): the two versions side by side; keep both (default), the computer's, or BlueDrive's.</summary>
public partial class ConflictViewModel : ObservableObject
{
    private readonly AccountSession _session;
    private readonly ConflictEntry _conflict;

    public ConflictViewModel(AccountSession session, ConflictEntry conflict)
    {
        _session = session;
        _conflict = conflict;
        FileName = NameMapper.NameOf(conflict.Path);
        var original = session.Agent.LocalPathOf(conflict.RootId, conflict.Path);
        var copy = session.Agent.LocalPathOf(conflict.RootId, conflict.ConflictPath);
        Server = Describe("Bản trên BlueDrive", original, "Phiên bản hiện tại trên máy chủ");
        Local = Describe("Bản trên máy này", copy, WhoFromName(NameMapper.NameOf(conflict.ConflictPath)));
        CanChoose = conflict.Kind == "content" && File.Exists(copy);
    }

    public string FileName { get; }
    public VersionInfo Server { get; }
    public VersionInfo Local { get; }
    public bool CanChoose { get; }
    [ObservableProperty] private string? _message;

    public event Action? Done;

    /// <summary>"Tên (Bản xung đột - Họ tên - Máy - yyyy-MM-dd HHmm).ext" → "Họ tên · Máy".</summary>
    internal static string WhoFromName(string conflictName)
    {
        var start = conflictName.IndexOf("(Bản xung đột - ", StringComparison.Ordinal);
        var end = conflictName.LastIndexOf(')');
        if (start < 0 || end <= start) return "";
        var parts = conflictName[(start + "(Bản xung đột - ".Length)..end].Split(" - ");
        return parts.Length >= 2 ? $"Sửa bởi {parts[0]} trên máy {parts[1]}" : "";
    }

    private static VersionInfo Describe(string title, string path, string who)
    {
        if (!File.Exists(path)) return new VersionInfo(title, path, "Không còn tệp", "", who, null, null);
        var info = new FileInfo(path);
        string? text = null;
        Bitmap? image = null;
        var ext = info.Extension.ToLowerInvariant();
        try
        {
            if (ext is ".txt" or ".csv" or ".md" or ".json" or ".xml" or ".log" && info.Length < 5_000_000)
            {
                using var r = new StreamReader(path);
                var buf = new char[2000];
                text = new string(buf, 0, r.Read(buf, 0, buf.Length));
            }
            else if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" && info.Length < 30_000_000)
            {
                using var s = File.OpenRead(path);
                image = Bitmap.DecodeToWidth(s, 320);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new VersionInfo(title, path, Formatting.Bytes(info.Length), info.LastWriteTime.ToString("dd/MM/yyyy HH:mm"), who, text, image);
    }

    [RelayCommand]
    private Task KeepBothAsync() => ResolveAsync(SyncAgent.ConflictChoice.KeepBoth);

    [RelayCommand]
    private Task KeepLocalAsync() => ResolveAsync(SyncAgent.ConflictChoice.KeepLocal);

    [RelayCommand]
    private Task KeepServerAsync() => ResolveAsync(SyncAgent.ConflictChoice.KeepServer);

    private async Task ResolveAsync(SyncAgent.ConflictChoice choice)
    {
        try
        {
            await _session.Agent.ResolveConflictAsync(_conflict, choice);
            Done?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Chưa xử lý được (tệp đang mở?): {ex.Message}";
        }
    }

    [RelayCommand]
    private void Open(VersionInfo? v)
    {
        if (v is not null) FlyoutViewModel.OpenPath(v.Path);
    }
}
