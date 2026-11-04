using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Operations;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Operations;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.Infrastructure.Storage;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlueDrive.IntegrationTests;

/// <summary>
/// HSMT 3.6 (sao lưu / phục hồi): a real backup → disaster → restore cycle on the local volume, and the S3 variant where the backup
/// is copied off the server, the server's copy is lost, the restore fetches the chain back from S3, and the end-of-contract wipe
/// empties the bucket.
/// </summary>
[Collection(ApiCollection.Name)]
public class BackupRestoreTests(ApiFactory factory)
{
    private static async Task<Guid> BackupAsync(IServiceProvider sp, IBackupRunner runner, string mode)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var job = new BackupJob { Type = BackupType.Manual, Status = JobStatus.Queued, Mode = mode, CreatedAt = DateTime.UtcNow };
        db.BackupJobs.Add(job);
        await db.SaveChangesAsync();
        await runner.RunAsync(job.Id, CancellationToken.None);
        return job.Id;
    }

    private static string FolderOf(string backupRoot, Guid jobId) => Directory.EnumerateDirectories(backupRoot).Single(d => d.EndsWith(jobId.ToString("N")));

    [Fact]
    public async Task Restore_brings_back_deleted_rows_and_blobs_with_a_valid_audit_chain()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm kiểm thử sao lưu", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, null, "sao-luu.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        Guid jobId;
        string blob;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            jobId = await BackupAsync(sp, sp.GetRequiredService<IBackupRunner>(), "full");
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.BackupJobs.AsNoTracking().SingleAsync(b => b.Id == jobId)).Status.Should().Be(JobStatus.Succeeded);
            var key = (await db.FileVersions.AsNoTracking().SingleAsync(v => v.FileId == file)).StorageKey;
            blob = Path.Combine(factory.StorageRoot, "storage", key.Replace('/', Path.DirectorySeparatorChar));

            // Disaster: the database rows and the stored blob are gone.
            await db.FileVersions.Where(v => v.FileId == file).ExecuteDeleteAsync();
            await db.Files.Where(f => f.Id == file).ExecuteDeleteAsync();
            File.Delete(blob);
        }
        (await owner.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<RestoreRunner>().RestoreAsync(jobId.ToString(), CancellationToken.None)).Should().Be(0);

        File.Exists(blob).Should().BeTrue("the files archive is extracted back into the storage volume");
        (await owner.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK, factory.PgTools.LastRestoreOutput);
        var url = (await (await owner.GetAsync($"/api/v1/files/{file}/download-url")).ReadJsonAsync()).GetProperty("url").GetString()!;
        (await factory.CreateClient().GetByteArrayAsync(url)).Should().Equal(StorageTests.Png, "the restored blob still decrypts with its wrapped key");
        var chain = await (await admin.GetAsync("/api/v1/audit-logs/verify")).ReadJsonAsync();
        chain.GetProperty("valid").GetBoolean().Should().BeTrue(chain.GetProperty("message").GetString());
    }

    [Fact]
    public async Task S3_backup_survives_losing_the_server_and_secure_wipe_empties_the_bucket()
    {
        await using var server = await S3TestServer.StartAsync();
        var scratch = Path.Combine(factory.StorageRoot, "s3-backup-" + Guid.NewGuid().ToString("N"));
        var storageOptions = Options.Create(server.StorageOptions(Path.Combine(scratch, "storage")));
        var backupOptions = Options.Create(new BackupOptions { Root = Path.Combine(scratch, "backups"), S3Enabled = true });
        using var s3 = new S3FileStorage(storageOptions);
        var content = new FileContentService(s3, storageOptions);
        var data1 = RandomNumberGenerator.GetBytes(200_000);
        var data2 = RandomNumberGenerator.GetBytes(70_000);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var runner = ActivatorUtilities.CreateInstance<BackupRunner>(sp, backupOptions, storageOptions, s3);

        var first = await content.SaveAsync(new MemoryStream(data1), "files");
        var fullId = await BackupAsync(sp, runner, "full");
        var second = await content.SaveAsync(new MemoryStream(data2), "files");
        var incrementalId = await BackupAsync(sp, runner, "auto");

        var fullManifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(Path.Combine(FolderOf(backupOptions.Value.Root, fullId), "manifest.json")), JsonSerializerOptions.Web)!;
        fullManifest.FileCount.Should().Be(1, "the full backup archives the bucket's blobs, not only the local volume");
        var incManifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(Path.Combine(FolderOf(backupOptions.Value.Root, incrementalId), "manifest.json")), JsonSerializerOptions.Web)!;
        incManifest.Mode.Should().Be("incremental");
        incManifest.BasedOn.Should().Be(fullId);
        var copies = new List<string>();
        await foreach (var o in s3.ListAsync(BackupS3Store.Prefix)) copies.Add(o.Key);
        copies.Should().HaveCount(6, "db.dump, files.tar and manifest.json of both backups are copied to the bucket");

        // Disaster: the server (with its local backup folders) is lost and the blobs are deleted from the bucket.
        await s3.DeleteAsync(first.StorageKey);
        await s3.DeleteAsync(second.StorageKey);
        Directory.Delete(backupOptions.Value.Root, true);

        var restore = ActivatorUtilities.CreateInstance<RestoreRunner>(sp, backupOptions, storageOptions, s3);
        (await restore.RestoreAsync(incrementalId.ToString(), CancellationToken.None)).Should().Be(0);
        foreach (var (stored, data) in new[] { (first, data1), (second, data2) })
        {
            await using var read = await content.OpenReadAsync(stored.StorageKey, stored.IsEncrypted, stored.WrappedKey);
            using var ms = new MemoryStream();
            await read.CopyToAsync(ms);
            ms.ToArray().Should().Equal(data, "the chain full + incremental is fetched from S3 and written back into the bucket");
        }

        // Retention also removes expired backups from the S3 copy.
        var settings = sp.GetRequiredService<ISettingsService>();
        var keep = await settings.GetAsync(SettingKeys.BackupRetentionCount, 7);
        await settings.SetAsync(SettingKeys.BackupRetentionCount, 1);
        try { await BackupAsync(sp, runner, "full"); }
        finally { await settings.SetAsync(SettingKeys.BackupRetentionCount, keep); }
        copies.Clear();
        await foreach (var o in s3.ListAsync(BackupS3Store.Prefix)) copies.Add(o.Key);
        copies.Should().NotContain(k => k.Contains(fullId.ToString("N")), "the expired full backup is deleted from the bucket too");

        var report = await new S3Wiper(storageOptions, NullLogger<S3Wiper>.Instance).WipeAsync(CancellationToken.None);
        report!.DataObjects.Should().Be(2);
        report.BackupObjects.Should().BeGreaterThan(0);
        report.Remaining.Should().Be(0);
        using var client = server.CreateClient();
        (await client.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = S3TestServer.Bucket })).S3Objects.Should().BeNullOrEmpty();
    }
}
