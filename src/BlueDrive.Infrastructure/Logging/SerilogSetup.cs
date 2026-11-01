using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace BlueDrive.Infrastructure.Logging;

public static class SerilogSetup
{
    /// <summary>Console + rolling file + system_logs table (Warning+). Never logs request bodies (no passwords/tokens).</summary>
    public static LoggerConfiguration ConfigureBlueDrive(this LoggerConfiguration cfg, IConfiguration config, string app)
    {
        var logDir = config["Logging:Directory"] ?? "logs";
        cfg.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Hangfire", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", app)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(logDir, $"{app}-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30);

        var cs = config.GetConnectionString("Default");
        if (!string.IsNullOrEmpty(cs) && config.GetValue("Logging:Database", true))
            cfg.WriteTo.Sink(new PostgresLogSink(cs), new Serilog.Configuration.BatchingOptions { BatchSizeLimit = 100, BufferingTimeLimit = TimeSpan.FromSeconds(5) },
                restrictedToMinimumLevel: LogEventLevel.Warning);
        return cfg;
    }
}
