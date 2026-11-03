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

    /// <summary>Notification with one action button (Windows); elsewhere the text only. (<paramref name="action"/>, <paramref name="value"/>)
    /// come back through <see cref="Activated"/> when the button is clicked.</summary>
    public static void ShowWithAction(string title, string text, string button, string action, string value)
    {
#if WINDOWS
        new ToastContentBuilder().AddText(title).AddText(text)
            .AddButton(new ToastButton().SetContent(button).AddArgument("action", action).AddArgument("value", value))
            .AddButton(new ToastButtonDismiss("Bỏ qua")).Show();
#else
        Show(title, text);
#endif
    }

#pragma warning disable CS0067 // raised by Windows notification buttons only
    /// <summary>(action, value) of a clicked notification button.</summary>
    public static event Action<string, string>? Activated;
#pragma warning restore CS0067

    /// <summary>Starts listening to notification buttons (Windows).</summary>
    public static void Listen()
    {
#if WINDOWS
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            if (args.TryGetValue("action", out var action) && args.TryGetValue("value", out var value)) Activated?.Invoke(action, value);
        };
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
