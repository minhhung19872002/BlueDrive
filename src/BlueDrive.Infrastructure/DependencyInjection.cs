using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Security;
using BlueDrive.Infrastructure.Auditing;
using BlueDrive.Infrastructure.Email;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.Infrastructure.Persistence.Seed;
using BlueDrive.Infrastructure.Reporting;
using BlueDrive.Infrastructure.Security;
using BlueDrive.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BlueDrive.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("Thiếu cấu hình ConnectionStrings:Default.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(cs).Build());
        services.AddScoped<AuditableInterceptor>();
        services.AddScoped<ChangeJournalInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npg => npg.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditableInterceptor>(), sp.GetRequiredService<ChangeJournalInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddMemoryCache();

        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.Configure<SmtpOptions>(config.GetSection("Smtp"));
        services.AddDataProtection().SetApplicationName("BlueDrive").PersistKeysToDbContext<AppDbContext>();
        // Key XML is encrypted with the storage master key when one is configured.
        if (Storage.FileContentService.LoadMasterKey(config.GetSection("Storage").Get<Storage.StorageOptions>() ?? new()) is { } masterKey)
            services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o => o.XmlEncryptor = new Operations.MasterKeyXmlEncryptor(masterKey));

        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IPasswordService, PasswordService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddSingleton<ISessionValidator, SessionValidator>();
        services.AddSingleton<ISensitiveDataProtector, SensitiveDataProtector>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<ITableExporter, TableExporter>();
        services.AddSingleton<IExcelReader, ExcelReader>();
        services.AddScoped<IUserNotifier, Realtime.DbNotificationPublisher>();
        services.Configure<Storage.StorageOptions>(config.GetSection("Storage"));
        if (string.Equals(config["Storage:Provider"], "s3", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IFileStorage, Storage.S3FileStorage>();
        else
            services.AddSingleton<IFileStorage, Storage.LocalFileStorage>();
        services.AddSingleton<IFileContentService, Storage.FileContentService>();
        services.AddSingleton<IFileTypeInspector, Storage.FileTypeInspector>();
        services.AddScoped<IBackgroundJobs, Jobs.HangfireBackgroundJobs>();
        services.AddScoped<Media.IFileProcessor, Media.FileProcessor>();
        services.AddScoped<Jobs.FileProcessingJob>();
        services.AddScoped<Jobs.TrashRetentionJob>();
        services.AddScoped<Jobs.IRecurringJobRegistration, Jobs.StorageRecurringJobs>();
        services.Configure<Office.OnlyOfficeOptions>(config.GetSection("OnlyOffice"));
        services.Configure<Media.ClamAvOptions>(config.GetSection("ClamAv"));
        services.AddHttpClient("onlyoffice", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<IOfficeEditorProvider, Office.OnlyOfficeProvider>();
        services.AddSingleton<Application.Features.Office.IFileStorageTemp, Storage.FileStorageTemp>();
        services.AddScoped<Media.PlainCopy>();
        services.AddSingleton<IMalwareScanPolicy, Media.ClamAvScanPolicy>();
        services.AddScoped<Media.IFileProcessingStep, Media.MalwareScanStep>();
        services.AddScoped<Media.IFileProcessingStep, Media.ThumbnailStep>();

        services.AddScoped<Application.Features.Search.IFileSearchService, Search.FileSearchService>();
        services.AddSingleton<IAuditObserver, UnusualDownloadObserver>();
        services.AddScoped<Application.Features.Monitoring.IAuditChainVerifier, AuditChainVerifier>();
        services.AddSingleton<Application.Features.Monitoring.IDiskInfo, DiskInfo>();
        services.AddScoped<Application.Features.Monitoring.IReportQueue, Jobs.HangfireReportQueue>();
        services.AddScoped<Jobs.ReportJobRunner>();
        services.Configure<Operations.BackupOptions>(config.GetSection("Backup"));
        services.AddScoped<Application.Features.Operations.IBackupRunner, Operations.BackupRunner>();
        services.AddScoped<Application.Features.Operations.IBackupScheduler, Operations.HangfireBackupScheduler>();
        services.AddScoped<Operations.BackupJobRunner>();
        services.AddScoped<Operations.RestoreRunner>();
        services.AddSingleton<Operations.IPgTools, Operations.ProcessPgTools>();
        services.AddScoped<Operations.S3Wiper>();
        services.AddScoped<Jobs.IRecurringJobRegistration, Operations.BackupRecurringRegistration>();
        services.AddScoped<Application.Features.Operations.IDataExporter, Operations.DataExporter>();
        services.AddScoped<Application.Features.Operations.ISystemStatusService, Operations.SystemStatusService>();

        services.AddScoped<IDataSeeder, ReferenceDataSeeder>();
        services.AddScoped<IDataSeeder, DemoDataSeeder>();
        return services;
    }
}
