using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.Infrastructure.Jobs;

public static class HangfireSetup
{
    public const string QueueDefault = "default", QueueScan = "scan", QueueMedia = "media", QueueReports = "reports", QueueMaintenance = "maintenance";

    /// <summary>Registers Hangfire with PostgreSQL storage. API enqueues only; the Worker runs the server.</summary>
    public static IServiceCollection AddBlueDriveHangfire(this IServiceCollection services, IConfiguration config, bool runServer)
    {
        var cs = config.GetConnectionString("Default")!;
        services.AddHangfire(c => c
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(cs), new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(2),
            }));
        if (runServer)
        {
            services.AddHangfireServer(o =>
            {
                o.ServerName = $"worker-{Environment.MachineName}";
                o.Queues = [QueueDefault, QueueMedia, QueueReports, QueueMaintenance];
                o.WorkerCount = config.GetValue("Worker:Concurrency", Math.Max(2, Environment.ProcessorCount / 2));
            });
            // Malware scans get their own workers: they gate downloads by other users, and are quick next to thumbnails.
            services.AddHangfireServer(o =>
            {
                o.ServerName = $"scan-{Environment.MachineName}";
                o.Queues = [QueueScan];
                o.WorkerCount = config.GetValue("Worker:ScanConcurrency", 2);
            });
        }
        return services;
    }
}

/// <summary>Implemented by modules that own recurring Hangfire jobs; invoked by the Worker at startup.</summary>
public interface IRecurringJobRegistration
{
    Task RegisterAsync(CancellationToken ct);
}
