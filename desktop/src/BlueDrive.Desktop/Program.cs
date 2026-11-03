using System.IO.Pipes;
using System.Text;
using Avalonia;
using BlueDrive.Desktop.Services;
using Serilog;
using Velopack;

namespace BlueDrive.Desktop;

internal static class Program
{
    /// <summary>Same name as the command line agent: only one BlueDrive syncs on a computer session.</summary>
    internal const string MutexName = @"Local\BlueDrive.Desktop.Agent";
    internal const string PipeName = "BlueDrive.Desktop.Activate";

    /// <summary>First start after Setup (Velopack sets VELOPACK_FIRSTRUN).</summary>
    internal static bool FirstRun { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var home = Environment.GetEnvironmentVariable("BLUEDRIVE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueDrive");
        // Velopack install / update / uninstall hooks run and exit here, before the single-instance check.
        var velopack = VelopackApp.Build()
            .OnFirstRun(_ => FirstRun = true)
            // A downloaded update is applied when BlueDrive quits or restarts (Updater), never by force-stopping a running sync.
            .SetAutoApplyOnStartup(false);
        if (OperatingSystem.IsWindows())
            velopack = velopack
                .OnBeforeUninstallFastCallback(_ =>
                {
                    StartLog(home);
                    QuitRunningInstance();
                    try { Uninstaller.Run(home, Uninstaller.MessageBoxAsk(12)); }
                    finally { Log.CloseAndFlush(); }
                })
                // Silent installs do not start the app: it starts at the next sign-in ("Khởi động cùng Windows", changeable in Cài đặt).
                .OnAfterInstallFastCallback(_ => StartupRegistration.Set(true));
        velopack.Run();
        if (args.Contains("--quit") && !IsRunning()) return 0; // uninstaller / script: nothing to stop

        using var single = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Started by COM for Explorer (Status UI) while BlueDrive starts up: the running one serves it.
            if (args.Contains("-Embedding")) return 0;
            // Already running: ask it to show its window, then leave.
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(2000);
                pipe.Write(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(args)));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException) { }
            return 0;
        }

        StartLog(home);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Lỗi không xử lý được");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Lỗi tác vụ nền");
            e.SetObserved();
        };
        try
        {
            Log.Information("BlueDrive Desktop khởi động ({Args})", string.Join(' ', args));
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally { Log.CloseAndFlush(); }
    }

    private static bool IsRunning()
    {
        using var probe = new Mutex(false, MutexName);
        try
        {
            if (!probe.WaitOne(0)) return true;
            probe.ReleaseMutex();
            return false;
        }
        catch (AbandonedMutexException)
        {
            probe.ReleaseMutex(); // previous instance crashed: we got it, give it back
            return false;
        }
    }

    /// <summary>Uninstall: the running BlueDrive stops syncing and exits (up to 10 s) before its accounts are removed.</summary>
    private static void QuitRunningInstance()
    {
        using var probe = new Mutex(false, MutexName);
        try
        {
            if (probe.WaitOne(0)) { probe.ReleaseMutex(); return; } // not running
        }
        catch (AbandonedMutexException) { return; }
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            pipe.Write(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] { "--quit" })));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return; }
        try
        {
            if (probe.WaitOne(TimeSpan.FromSeconds(10))) probe.ReleaseMutex();
        }
        catch (AbandonedMutexException) { }
    }

    private static void StartLog(string home) =>
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(home, "logs", "bluedrive-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 20_000_000,
                rollOnFileSizeLimit: true, shared: true)
            .CreateLogger();

    // macOS: a menu bar app (no Dock icon), like the Windows tray app.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
        .With(new MacOSPlatformOptions { ShowInDock = false });
}
