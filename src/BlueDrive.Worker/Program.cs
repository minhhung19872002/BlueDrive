using BlueDrive.Application;
using BlueDrive.Application.Abstractions;
using BlueDrive.Infrastructure;
using BlueDrive.Infrastructure.Jobs;
using BlueDrive.Infrastructure.Logging;
using BlueDrive.Infrastructure.Operations;
using BlueDrive.Infrastructure.Security;
using BlueDrive.Worker;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    // One-off commands instead of the job server: "restore <backup-id>" (scripts/restore.ps1), "backup [full]" (scripts/backup-now.ps1),
    // "wipe-s3" (scripts/secure-wipe.ps1).
    var restoreMode = args.Length >= 2 && args[0] == "restore";
    var backupMode = args.Length >= 1 && args[0] == "backup";
    var wipeMode = args.Length >= 1 && args[0] == "wipe-s3";
    var oneOff = restoreMode || backupMode || wipeMode;
    var builder = Host.CreateApplicationBuilder(oneOff ? [] : args);
    builder.Services.AddSerilog((sp, cfg) => cfg.ConfigureBlueDrive(builder.Configuration, oneOff ? args[0] : "worker"));

    builder.Services.AddScoped<SystemCurrentUser>();
    builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<SystemCurrentUser>());
    builder.Services.AddScoped<BlueDrive.Application.Features.Sync.ISyncNotifier, WorkerSyncNotifier>();
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddBlueDriveHangfire(builder.Configuration, runServer: !oneOff);
    if (!oneOff) builder.Services.AddHostedService<RecurringJobsRegistrar>();

    var host = builder.Build();
    if (restoreMode)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RestoreRunner>().RestoreAsync(args[1], CancellationToken.None);
    }
    if (wipeMode)
    {
        using var scope = host.Services.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<S3Wiper>().WipeAsync(CancellationToken.None);
        // KEY=value lines parsed by scripts/secure-wipe.* into the deletion record.
        if (report is null) { Console.WriteLine("S3_CONFIGURED=false"); return 0; }
        Console.WriteLine("S3_CONFIGURED=true");
        Console.WriteLine($"S3_BUCKET={report.Bucket}");
        Console.WriteLine($"S3_DATA_OBJECTS={report.DataObjects}");
        Console.WriteLine($"S3_DATA_BYTES={report.DataBytes}");
        Console.WriteLine($"S3_BACKUP_OBJECTS={report.BackupObjects}");
        Console.WriteLine($"S3_BACKUP_BYTES={report.BackupBytes}");
        Console.WriteLine($"S3_REMAINING={report.Remaining}");
        return report.Remaining == 0 ? 0 : 2;
    }
    if (backupMode)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueDrive.Infrastructure.Persistence.AppDbContext>();
        var job = new BlueDrive.Domain.Entities.BackupJob { Type = BlueDrive.Domain.Enums.BackupType.Manual, Status = BlueDrive.Domain.Enums.JobStatus.Queued, Mode = args.Length > 1 && args[1] == "full" ? "full" : "auto", CreatedAt = DateTime.UtcNow };
        db.BackupJobs.Add(job);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<BlueDrive.Application.Features.Operations.IBackupRunner>().RunAsync(job.Id, CancellationToken.None);
        Console.WriteLine($"BACKUP_ID={job.Id}");
        return 0;
    }
    await host.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Worker dừng do lỗi không xử lý được");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
