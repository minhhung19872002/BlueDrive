#if WINDOWS
using Windows.Networking.Connectivity;
using Windows.System.Power;
#endif

namespace BlueDrive.Desktop.Services;

/// <summary>
/// Pauses syncing on a metered connection (mobile hotspot, data limit) or when the laptop runs on battery below 20 % (spec 6.5); each rule can be
/// turned off in Cài đặt → Mạng. Re-evaluated when Windows reports a change and every minute. macOS (pmset) and Linux (sysfs): battery only.
/// </summary>
public sealed class ConditionsMonitor : IDisposable
{
    public const int LowBatteryPercent = 20;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string?> _apply;
    private readonly Timer _timer;
    private string? _last = "";

    public ConditionsMonitor(Func<AppSettings> settings, Action<string?> apply)
    {
        _settings = settings;
        _apply = apply;
#if WINDOWS
        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetwork;
            PowerManager.RemainingChargePercentChanged += OnChanged;
            PowerManager.PowerSupplyStatusChanged += OnChanged;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or TypeLoadException) { }
#endif
        _timer = new Timer(_ => Evaluate(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private void OnChanged(object? sender, object? e) => Evaluate();
    private void OnNetwork(object? sender) => Evaluate();

    /// <summary>Current reason to pause, or null.</summary>
    public static string? Reason(AppSettings s, bool metered, bool onBattery, int batteryPercent)
    {
        if (s.PauseOnMetered && metered) return "Đã tạm dừng: đang dùng mạng tính phí (có thể đổi trong Cài đặt → Mạng).";
        if (s.PauseOnLowBattery && onBattery && batteryPercent < LowBatteryPercent)
            return $"Đã tạm dừng: pin dưới {LowBatteryPercent}% và không cắm sạc (có thể đổi trong Cài đặt → Mạng).";
        return null;
    }

    public void Evaluate()
    {
        bool metered = false, onBattery = false;
        var percent = 100;
#if WINDOWS
        try
        {
            var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            metered = cost is not null && (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.OverDataLimit || cost.Roaming);
            onBattery = PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent && PowerManager.BatteryStatus != BatteryStatus.NotPresent;
            percent = PowerManager.RemainingChargePercent;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
#else
        (onBattery, percent) = UnixBattery();
#endif
        var reason = Reason(_settings(), metered, onBattery, percent);
        if (reason == _last) return;
        _last = reason;
        _apply(reason);
    }

    /// <summary>macOS: "pmset -g batt" ("Now drawing from 'Battery Power'" … "85%;"); Linux: /sys/class/power_supply.</summary>
    public static (bool OnBattery, int Percent) ParsePmset(string output)
    {
        var onBattery = output.Contains("'Battery Power'", StringComparison.Ordinal);
        var m = System.Text.RegularExpressions.Regex.Match(output, @"(\d{1,3})%");
        return (onBattery, m.Success ? int.Parse(m.Groups[1].Value) : 100);
    }

    private static (bool, int) UnixBattery()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pmset", "-g batt")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                })!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return ParsePmset(output);
            }
            var bat = Directory.Exists("/sys/class/power_supply") ? Directory.EnumerateDirectories("/sys/class/power_supply", "BAT*").FirstOrDefault() : null;
            if (bat is null) return (false, 100);
            var status = File.ReadAllText(Path.Combine(bat, "status")).Trim();
            var capacity = int.TryParse(File.ReadAllText(Path.Combine(bat, "capacity")).Trim(), out var c) ? c : 100;
            return (status == "Discharging", capacity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return (false, 100); }
    }

    public void Dispose()
    {
        _timer.Dispose();
#if WINDOWS
        try
        {
            NetworkInformation.NetworkStatusChanged -= OnNetwork;
            PowerManager.RemainingChargePercentChanged -= OnChanged;
            PowerManager.PowerSupplyStatusChanged -= OnChanged;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
#endif
    }
}
