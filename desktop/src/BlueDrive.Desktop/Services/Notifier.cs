using System.Diagnostics;
#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
#endif

namespace BlueDrive.Desktop.Services;

/// <summary>System notifications: Windows toast, macOS Notification Center (osascript), Linux notify-send (libnotify).</summary>
public static class Notifier
{
    public static void Show(string title, string text)
    {
#if WINDOWS
        new ToastContentBuilder().AddText(title).AddText(text).Show();
#else
        if (OperatingSystem.IsMacOS())
            Run("osascript", "-e", $"display notification \"{Escape(text)}\" with title \"{Escape(title)}\"");
        else if (OperatingSystem.IsLinux())
            Run("notify-send", "--app-name=BlueDrive", title, text);
#endif
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
    }
}
