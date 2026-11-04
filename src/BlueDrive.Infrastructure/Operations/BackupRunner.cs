using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// Backup = pg_dump (custom format, full every time) + tar of the stored blobs (local volume or S3 bucket): full weekly (or when
/// requested / no full yet), otherwise incremental (blobs changed since the previous backup). Each backup folder has a manifest with
/// SHA-256 checksums and can be copied to the S3 bucket (<c>backups/</c>) so it survives the loss of the server.
/// </summary>
public sealed class BackupRunner(
    AppDbContext db, IOptions<BackupOptions> backupOptions, IOptions<StorageOptions> storageOptions, IFileStorage storage, IPgTools pgTools, ISettingsService settings,
    IAlertService alerts, TimeProvider clock, ILogger<BackupRunner> logger) : IBackupRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    /// <summary>Blobs written this long before the previous backup started are still re-copied: absorbs the S3 server's clock
    /// skew and its one-second LastModified resolution (copying a blob twice is harmless, missing one is not).</summary>
    private static readonly TimeSpan IncrementalOverlap = TimeSpan.FromMinutes(5);
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

            var dumpPath = Path.Combine(dir, "db.dump");
            await pgTools.DumpAsync(dumpPath, ct);
            var dbPart = new BackupPart("db.dump", new FileInfo(dumpPath).Length, await Sha256Async(dumpPath, ct));
            var since = full ? (DateTime?)null : previous!.StartedAt;
            var (filesPart, count) = await ArchiveFilesAsync(dir, "files.tar", since, ct);
            var manifest = new BackupManifest(job.Id, job.Mode, full ? null : previous!.Id, now,
                Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0", dbPart, filesPart, count, since);
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, Json), ct);

            if (await CopyToS3EnabledAsync(ct))
            {
                using var remote = BackupS3Store.TryCreate(storageOptions.Value.S3) ?? throw new InvalidOperationException("Đã bật sao lưu lên S3 nhưng chưa cấu hình Storage:S3.");
                await remote.UploadFolderAsync(dir, ct);
            }

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

    private async Task<bool> CopyToS3EnabledAsync(CancellationToken ct) => backupOptions.Value.S3Enabled || await settings.GetAsync(SettingKeys.BackupToS3, false, ct);

    /// <summary>Upload scratch areas and the S3 backup copies themselves are never archived.</summary>
    internal static bool IsArchived(string key) =>
        !(key.StartsWith(".tmp/", StringComparison.Ordinal) || key.StartsWith(".tus/", StringComparison.Ordinal) || key.StartsWith(BackupS3Store.Prefix, StringComparison.Ordinal) || key.Contains(".part-"));

    private async Task<(BackupPart Part, int Count)> ArchiveFilesAsync(string dir, string name, DateTime? since, CancellationToken ct)
    {
        var path = Path.Combine(dir, name);
        var localRoot = Path.GetFullPath(storageOptions.Value.LocalRoot);
        var count = 0;
        await using (var fs = File.Create(path))
        await using (var tar = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false))
        {
            await foreach (var o in storage.ListAsync("", ct))
            {
                if (!IsArchived(o.Key) || (since is { } s && o.LastModifiedUtc < s - IncrementalOverlap)) continue;
                if (storage is LocalFileStorage)
                    await tar.WriteEntryAsync(Path.Combine(localRoot, o.Key.Replace('/', Path.DirectorySeparatorChar)), o.Key, ct);
                else
                {
                    // Object storage: stage each blob on local disk so the tar entry gets its exact length.
                    var tmp = Path.Combine(dir, ".object");
                    try
                    {
                        await using (var src = await storage.OpenReadAsync(o.Key, ct))
                        await using (var dst = File.Create(tmp))
                            await src.CopyToAsync(dst, ct);
                        await tar.WriteEntryAsync(tmp, o.Key, ct);
                    }
                    finally { File.Delete(tmp); }
                }
                count++;
            }
        }
        return (new BackupPart(name, new FileInfo(path).Length, await Sha256Async(path, ct)), count);
    }

    internal static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    /// <summary>Keeps the newest N backups plus every full backup their incrementals depend on (locally and in the S3 copy).</summary>
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
        var expired = all.Except(kept).ToList();
        if (expired.Count == 0) return;
        using var remote = await CopyToS3EnabledAsync(ct) ? BackupS3Store.TryCreate(storageOptions.Value.S3) : null;
        foreach (var old in expired)
        {
            var folderName = old.ManifestKey is { } m ? Path.GetDirectoryName(m) : null;
            if (folderName is not null)
            {
                var folder = Path.Combine(Root, folderName);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                if (remote is not null) await remote.DeleteFolderAsync(folderName.Replace('\\', '/'), ct);
            }
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

/// <summary>
/// Restores a backup chain: checksums of every part, pg_restore of the chosen dump, then full + incremental archives in order
/// (into the local volume or back into the S3 bucket). Folders missing on this server are fetched from the S3 copy.
/// </summary>
public sealed class RestoreRunner(IOptions<BackupOptions> backupOptions, IOptions<StorageOptions> storageOptions, IFileStorage storage, IPgTools pgTools,
    Npgsql.NpgsqlDataSource dataSource, ILogger<RestoreRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> RestoreAsync(string backupRef, CancellationToken ct)
    {
        var root = Path.GetFullPath(backupOptions.Value.Root);
        Directory.CreateDirectory(root);
        using var remote = BackupS3Store.TryCreate(storageOptions.Value.S3);
        var wanted = Guid.TryParse(backupRef, out var id) ? id.ToString("N") : backupRef;
        var target = await LocateAsync(root, wanted, remote, ct)
                     ?? throw new DirectoryNotFoundException($"Không tìm thấy bản sao lưu '{backupRef}' trong {root}{(remote is null ? "" : " và trên S3")}.");

        var chain = new List<(string Dir, BackupManifest M)>();
        var current = (Dir: target, M: ReadManifest(target));
        while (true)
        {
            chain.Insert(0, current);
            if (current.M.BasedOn is not { } parent) break;
            var parentDir = await LocateAsync(root, parent.ToString("N"), remote, ct)
                            ?? throw new InvalidOperationException($"Thiếu bản sao lưu gốc {parent} của chuỗi tăng dần.");
            current = (parentDir, ReadManifest(parentDir));
        }
        foreach (var (dir, m) in chain)
            foreach (var part in new[] { m.Database, m.Files }.Where(p => p is not null))
                if (await BackupRunner.Sha256Async(Path.Combine(dir, part!.Name), ct) != part.Sha256)
                    throw new InvalidOperationException($"Sai checksum {part.Name} trong {Path.GetFileName(dir)} — bản sao lưu bị hỏng.");
        logger.LogInformation("Checksum hợp lệ. Phục hồi CSDL từ {Dir}", Path.GetFileName(target));
        await pgTools.RestoreAsync(Path.Combine(target, "db.dump"), ct);
        // pg_restore recreates the extensions (ltree, unaccent…) with new type ids: connections and Npgsql's type cache must forget the old ones.
        Npgsql.NpgsqlConnection.ClearAllPools();
        await using (var connection = await dataSource.OpenConnectionAsync(ct)) await connection.ReloadTypesAsync(ct);
        // The whole database changed outside EF: no cached permission decision may survive it.
        Application.Features.Access.PermissionGeneration.Bump();

        foreach (var (dir, m) in chain)
        {
            logger.LogInformation("Phục hồi tệp {Mode} {Dir} ({Count} tệp)", m.Mode, Path.GetFileName(dir), m.FileCount);
            var archive = Path.Combine(dir, "files.tar");
            if (storage is LocalFileStorage)
            {
                var storageRoot = Path.GetFullPath(storageOptions.Value.LocalRoot);
                Directory.CreateDirectory(storageRoot);
                await TarFile.ExtractToDirectoryAsync(archive, storageRoot, overwriteFiles: true, ct);
                continue;
            }
            await using var fs = File.OpenRead(archive);
            await using var tar = new TarReader(fs);
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data && BackupRunner.IsArchived(entry.Name))
                    await storage.WriteAsync(entry.Name, data, ct);
        }
        logger.LogInformation("Phục hồi hoàn tất từ chuỗi {Count} bản sao lưu.", chain.Count);
        return 0;
    }

    private static BackupManifest ReadManifest(string dir) =>
        JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")), Json)
        ?? throw new InvalidOperationException($"manifest.json hỏng trong {Path.GetFileName(dir)}.");

    /// <summary>Backup folder named "&lt;time&gt;-&lt;id without dashes&gt;": on this server first, else downloaded from the S3 copy.</summary>
    private async Task<string?> LocateAsync(string root, string nameOrId, BackupS3Store? remote, CancellationToken ct)
    {
        var local = Directory.EnumerateDirectories(root).FirstOrDefault(d =>
            (Path.GetFileName(d) == nameOrId || Path.GetFileName(d).EndsWith(nameOrId, StringComparison.OrdinalIgnoreCase)) && File.Exists(Path.Combine(d, "manifest.json")));
        if (local is not null || remote is null) return local;
        var fetched = await remote.DownloadFolderAsync(nameOrId, root, ct);
        if (fetched is null || !File.Exists(Path.Combine(fetched, "manifest.json"))) return null;
        logger.LogInformation("Đã tải bản sao lưu {Dir} từ S3", Path.GetFileName(fetched));
        return fetched;
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
