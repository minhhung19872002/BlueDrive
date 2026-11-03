using BlueDrive.Desktop.ViewModels;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;

namespace BlueDrive.Desktop.Services;

/// <summary>Sync state shown in Explorer's cloud flyout (mirrors Windows.Storage.Provider.StorageProviderState).</summary>
public enum ProviderStatus { InSync, Syncing, Paused, Error, Warning, Offline }

/// <summary>What Explorer's Status UI shows for one BlueDrive folder (spec 7.2): state, its text and the storage used.</summary>
public sealed record StatusUiSnapshot(ProviderStatus State, string Label, string Detail, long QuotaUsed, long QuotaTotal, string QuotaLabel);

public static class StatusUiModel
{
    public static StatusUiSnapshot Of(AccountSession s) => Of(s.Agent.Status, s.Agent.IsPaused, s.Personal, s.Conflicts().Count);

    public static StatusUiSnapshot Of(AgentStatus st, bool paused, HttpRemoteStore.RootDto? personal, int conflicts)
    {
        var (label, detail) = Formatting.Status(st, paused);
        var state = paused ? ProviderStatus.Paused : st.State switch
        {
            AgentState.Syncing or AgentState.Starting => ProviderStatus.Syncing,
            AgentState.Offline => ProviderStatus.Offline,
            AgentState.Paused => ProviderStatus.Paused,
            AgentState.Idle when st.PendingMassDelete > 0 || st.Problems.Count > 0 || conflicts > 0 => ProviderStatus.Warning,
            AgentState.Idle => ProviderStatus.InSync,
            _ => ProviderStatus.Error,
        };
        if (!paused && st.State == AgentState.Idle && conflicts > 0 && st.PendingMassDelete == 0)
            (label, detail) = ("Có xung đột", $"{conflicts} tệp bị sửa ở cả máy này và nơi khác: mở BlueDrive để chọn bản giữ lại.");
        if (personal is { QuotaBytes: > 0 } p)
        {
            var percent = (int)Math.Min(100, p.UsedBytes * 100 / p.QuotaBytes);
            return new StatusUiSnapshot(state, label, detail, p.UsedBytes, p.QuotaBytes,
                $"Đã dùng {Formatting.Bytes(p.UsedBytes)} / {Formatting.Bytes(p.QuotaBytes)} ({percent}%)");
        }
        return new StatusUiSnapshot(state, label, detail, personal?.UsedBytes ?? 0, 0,
            personal is null ? "" : $"Đã dùng {Formatting.Bytes(personal.UsedBytes)}");
    }

    public static StatusUiSnapshot SignedOut { get; } =
        new(ProviderStatus.Error, "Chưa kết nối", "Tài khoản này không còn trong BlueDrive: mở BlueDrive để đăng nhập.", 0, 0, "");
}
