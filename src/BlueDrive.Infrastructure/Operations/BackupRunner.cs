using System.Diagnostics;
using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Features.Operations;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.Infrastructure.Reporting;
using BlueDrive.Infrastructure.Storage;
using Cronos;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlueDrive.Infrastructure.Operations;

public sealed class BackupOptions
{
    public string Root { get; set; } = "backups";
    public bool S3Enabled { get; set; }
    public string PgDump { get; set; } = "pg_dump";
    public string PgRestore { get; set; } = "pg_restore";
}

public sealed record BackupManifest(Guid Id, string Mode, Guid? BasedOn, DateTime CreatedAt, string AppVersion,
    BackupPart? Database, BackupPart? Files, int FileCount, DateTime? FilesChangedSince);

public sealed record BackupPart(string Name, long Size, string Sha256);

/// <summary>
/// Backup = pg_dump (custom format, full every time) + tar of the storage volume: full weekly (or when requested / no full yet),
/// otherwise incremental (files changed since the previous backup). Each backup folder has a manifest with SHA-256 checksums.
/// </summary>
public sealed class BackupRunner(
    AppDbContext db, IOptions<BackupOptions> backupOptions, IOptions<StorageOptions> storageOptions, IConfiguration config, ISettingsService settings,
    IAlertService alerts, TimeProvider clock, ILogger<BackupRunner> logger) : IBackupRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string Root => Path.GetFullPath(backupOptions.Value.Root);

    public async Task RunAsync(Guid backupJobId, CancellationToken ct)
    {
        var job = await db.BackupJobs.FirstOrDefaultAsync(b => b.Id == backupJobId, ct);
        if (job is null || job.Status is JobStatus.Succeeded) return;
        var now = clock.GetUtcNow().UtcDateTime;
        job.Status = JobStatus.Running;
        job.StartedAt = now;
        await db.SaveChangesAsync(ct);

        var dir = Path.Combine(Root, $"{now.AddHours(7):yyyyMMdd-HHmmss}-{job.Id:N}");
        try
        {
            Directory.CreateDirectory(dir);
            var previous = await db.BackupJobs.AsNoTracking().Where(b => b.Status == JobStatus.Succeeded && b.Id != job.Id).OrderByDescending(b => b.StartedAt).FirstOrDefaultAsync(ct);
            var fullDay = await settings.GetAsync(SettingKeys.BackupFullDayOfWeek, 0, ct);
            var hasFull = await db.BackupJobs.AnyAsync(b => b.Status == JobStatus.Succeeded && b.Mode == "full", ct);
            var fullToday = await db.BackupJobs.AnyAsync(b => b.Status == JobStatus.Succeeded && b.Mode == "full" && b.StartedAt > now.AddHours(7).Date.AddHours(-7), ct);
            var full = job.Mode == "full" || !hasFull || previous is null || ((int)now.AddHours(7).DayOfWeek == fullDay && !fullToday);
            job.Mode = full ? "full" : "incremental";

            var dbPart = await DumpDatabaseAsync(Path.Combine(dir, "db.dump"), ct);
            var since = full ? (DateTime?)null : previous!.StartedAt;
            var (filesPart, count) = await ArchiveFilesAsync(Path.Combine(dir, "files.tar"), since, ct);
            var manifest = new BackupManifest(job.Id, job.Mode, full ? null : previous!.Id, now,
                Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0", dbPart, filesPart, count, since);
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, Json), ct);

            if (await settings.GetAsync(SettingKeys.BackupToS3, false, ct) || backupOptions.Value.S3Enabled) await CopyToS3Async(dir, ct);

            var rel = Path.GetRelativePath(Root, dir).Replace('\\', '/');
            job.DbDumpKey = $"{rel}/db.dump";
            job.FilesArchiveKey = $"{rel}/files.tar";
            job.ManifestKey = $"{rel}/manifest.json";
            job.SizeBytes = dbPart.Size + filesPart.Size;
            job.Checksum = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dbPart.Sha256 + filesPart.Sha256)));
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Sao lưu {Mode} hoàn tất: {Dir} ({Size} byte, {Count} tệp)", job.Mode, dir, job.SizeBytes, count);
            await ApplyRetentionAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sao lưu thất bại");
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            job.FinishedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
            try { Directory.Delete(dir, true); } catch (IOException) { }
            await alerts.RaiseAsync(AlertType.BackupFailed, AlertSeverity.Critical, $"Sao lưu {(job.Type == BackupType.Manual ? "đột xuất" : "định kỳ")} thất bại: {ex.Message}", ct: CancellationToken.None);
            throw;
        }
    }

    private async Task<BackupPart> DumpDatabaseAsync(string path, CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Default"));
        var psi = new ProcessStartInfo(backupOptions.Value.PgDump) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-h", cs.Host ?? "localhost", "-p", cs.Port.ToString(), "-U", cs.Username ?? "", "-d", cs.Database ?? "", "-Fc", "--no-owner", "-f", path })
            psi.ArgumentList.Add(a);
        psi.Environment["PGPASSWORD"] = cs.Password ?? "";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được pg_dump.");
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new IOException($"pg_dump lỗi: {err.Trim()}");
        return new BackupPart("db.dump", new FileInfo(path).Length, await Sha256Async(path, ct));
    }

    private async Task<(BackupPart Part, int Count)> ArchiveFilesAsync(string path, DateTime? since, CancellationToken ct)
    {
        var root = Path.GetFullPath(storageOptions.Value.LocalRoot);
        var count = 0;
        await using (var fs = File.Create(path))
        await using (var tar = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false))
        {
            if (Directory.Exists(root))
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (rel.StartsWith(".tmp/") || rel.StartsWith(".tus/") || rel.Contains(".part-")) continue;
                    if (since is { } s && File.GetLastWriteTimeUtc(file) < s) continue;
                    await tar.WriteEntryAsync(file, rel, ct);
                    count++;
                }
        }
        return (new BackupPart("files.tar", new FileInfo(path).Length, await Sha256Async(path, ct)), count);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    private async Task CopyToS3Async(string dir, CancellationToken ct)
    {
        var s3 = storageOptions.Value.S3;
        if (string.IsNullOrWhiteSpace(s3.Bucket)) throw new InvalidOperationException("Đã bật sao lưu lên S3 nhưng chưa cấu hình Storage:S3.");
        // Same as S3FileStorage: no default aws-chunked checksums, which some S3-compatible servers store verbatim.
        var cfg = new AmazonS3Config
        {
            ForcePathStyle = true, AuthenticationRegion = s3.Region,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(s3.ServiceUrl)) cfg.ServiceURL = s3.ServiceUrl;
        using var client = new AmazonS3Client(s3.AccessKey, s3.SecretKey, cfg);
        foreach (var f in Directory.EnumerateFiles(dir))
            await client.PutObjectAsync(new PutObjectRequest { BucketName = s3.Bucket, Key = $"backups/{Path.GetFileName(dir)}/{Path.GetFileName(f)}", FilePath = f }, ct);
    }

    /// <summary>Keeps the newest N backups plus every full backup their incrementals depend on.</summary>
    private async Task ApplyRetentionAsync(CancellationToken ct)
    {
        var keep = await settings.GetAsync(SettingKeys.BackupRetentionCount, 7, ct);
        var all = await db.BackupJobs.Where(b => b.Status == JobStatus.Succeeded).OrderByDescending(b => b.StartedAt).ToListAsync(ct);
        var kept = all.Take(keep).ToList();
        var oldestKept = kept.LastOrDefault();
        // Walk back to the full backup that the oldest kept incremental chain needs.
        if (oldestKept is { Mode: "incremental" })
        {
            var idx = all.IndexOf(oldestKept);
            for (var i = idx + 1; i < all.Count; i++)
            {
                kept.Add(all[i]);
                if (all[i].Mode == "full") break;
            }
        }
        foreach (var old in all.Except(kept))
        {
            var folder = old.ManifestKey is { } m ? Path.Combine(Root, Path.GetDirectoryName(m)!) : null;
            if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, true);
            old.Status = JobStatus.Expired;
            old.Error = "Đã xóa theo chính sách lưu giữ";
            old.DbDumpKey = old.FilesArchiveKey = old.ManifestKey = null;
        }
        await db.SaveChangesAsync(ct);
    }

    public Task<Stream> OpenArtifactAsync(BackupJob job, string part, CancellationToken ct)
    {
        var key = part switch { "files" => job.FilesArchiveKey, "manifest" => job.ManifestKey, _ => job.DbDumpKey } ?? throw new FileNotFoundException();
        var full = Path.GetFullPath(Path.Combine(Root, key));
        if (!full.StartsWith(Root, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        return Task.FromResult<Stream>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true));
    }
}

/// <summary>Restores a backup chain: pg_restore of the chosen dump, then full + incremental archives in order.</summary>
public sealed class RestoreRunner(IOptions<BackupOptions> backupOptions, IOptions<StorageOptions> storageOptions, IConfiguration config, ILogger<RestoreRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> RestoreAsync(string backupRef, CancellationToken ct)
    {
        var root = Path.GetFullPath(backupOptions.Value.Root);
        var dirs = Directory.EnumerateDirectories(root).ToList();
        var target = dirs.FirstOrDefault(d => Path.GetFileName(d).Contains(backupRef.Replace("-", ""), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d) == backupRef)
                     ?? throw new DirectoryNotFoundException($"Không tìm thấy bản sao lưu '{backupRef}' trong {root}.");
        var manifests = dirs.Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .Select(d => (Dir: d, M: JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(d, "manifest.json")), Json)!)).ToDictionary(x => x.M.Id);
        var chain = new List<(string Dir, BackupManifest M)>();
        var current = manifests.Values.Single(x => x.Dir == target);
        while (true)
        {
            chain.Insert(0, current);
            if (current.M.BasedOn is not { } parent) break;
            if (!manifests.TryGetValue(parent, out current)) throw new InvalidOperationException($"Thiếu bản sao lưu gốc {parent} của chuỗi tăng dần.");
        }
        foreach (var (dir, m) in chain)
            foreach (var part in new[] { m.Database, m.Files }.Where(p => p is not null))
            {
                await using var s = File.OpenRead(Path.Combine(dir, part!.Name));
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
                if (sha != part.Sha256) throw new InvalidOperationException($"Sai checksum {part.Name} trong {Path.GetFileName(dir)} — bản sao lưu bị hỏng.");
            }
        logger.LogInformation("Checksum hợp lệ. Phục hồi CSDL từ {Dir}", Path.GetFileName(target));

        var cs = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Default"));
        var psi = new ProcessStartInfo(backupOptions.Value.PgRestore) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-h", cs.Host ?? "localhost", "-p", cs.Port.ToString(), "-U", cs.Username ?? "", "-d", cs.Database ?? "", "--clean", "--if-exists", "--no-owner", Path.Combine(target, "db.dump") })
            psi.ArgumentList.Add(a);
        psi.Environment["PGPASSWORD"] = cs.Password ?? "";
        using (var p = Process.Start(psi)!)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) logger.LogWarning("pg_restore cảnh báo: {Err}", err.Trim());
        }

        var storageRoot = Path.GetFullPath(storageOptions.Value.LocalRoot);
        foreach (var (dir, m) in chain)
        {
            logger.LogInformation("Giải nén {Mode} {Dir} ({Count} tệp)", m.Mode, Path.GetFileName(dir), m.FileCount);
            await TarFile.ExtractToDirectoryAsync(Path.Combine(dir, "files.tar"), storageRoot, overwriteFiles: true, ct);
        }
        logger.LogInformation("Phục hồi hoàn tất từ chuỗi {Count} bản sao lưu.", chain.Count);
        return 0;
    }
}

public sealed class HangfireBackupScheduler(IBackgroundJobClient client, IRecurringJobManager recurring) : IBackupScheduler
{
    public const string RecurringId = "backup-scheduled";

    public void Enqueue(Guid backupJobId) => client.Create<BackupJobRunner>(j => j.RunAsync(backupJobId, CancellationToken.None), new Hangfire.States.EnqueuedState(Jobs.HangfireSetup.QueueMaintenance));

    public void Reschedule(string cron)
        => recurring.AddOrUpdate<BackupJobRunner>(RecurringId, j => j.RunScheduledAsync(CancellationToken.None), cron, new RecurringJobOptions { TimeZone = VietnamTime.Zone });

    public DateTime? NextRun(string cron)
    {
        try { return CronExpression.Parse(cron).GetNextOccurrence(DateTime.UtcNow, VietnamTime.Zone); }
        catch (CronFormatException) { return null; }
    }
}

public sealed class BackupJobRunner(IBackupRunner runner, AppDbContext db, TimeProvider clock)
{
    [AutomaticRetry(Attempts = 0)]
    [Queue(Jobs.HangfireSetup.QueueMaintenance)]
    public Task RunAsync(Guid id, CancellationToken ct) => runner.RunAsync(id, ct);

    [AutomaticRetry(Attempts = 0)]
    [Queue(Jobs.HangfireSetup.QueueMaintenance)]
    public async Task RunScheduledAsync(CancellationToken ct)
    {
        var job = new BackupJob { Type = BackupType.Scheduled, Status = JobStatus.Queued, Mode = "auto", CreatedAt = clock.GetUtcNow().UtcDateTime };
        db.BackupJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await runner.RunAsync(job.Id, ct);
    }
}

public sealed class BackupRecurringRegistration(IBackupScheduler scheduler, ISettingsService settings) : Jobs.IRecurringJobRegistration
{
    public async Task RegisterAsync(CancellationToken ct) => scheduler.Reschedule(await settings.GetAsync(SettingKeys.BackupCron, "0 2 * * *", ct));
}
