using System.IO.Pipes;
using System.Text;
using Avalonia;
using Serilog;

namespace BlueDrive.Desktop;

internal static class Program
{
    /// <summary>Same name as the command line agent: only one BlueDrive syncs on a computer session.</summary>
    internal const string MutexName = @"Local\BlueDrive.Desktop.Agent";
    internal const string PipeName = "BlueDrive.Desktop.Activate";

    [STAThread]
    public static int Main(string[] args)
    {
        using var single = new Mutex(true, MutexName, out var first);
        if (!first)
        {
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

        var home = Environment.GetEnvironmentVariable("BLUEDRIVE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueDrive");
        var logs = Path.Combine(home, "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logs, "bluedrive-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 20_000_000,
                rollOnFileSizeLimit: true, shared: true)
            .CreateLogger();
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

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
