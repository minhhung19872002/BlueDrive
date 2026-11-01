using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Features.Operations;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Media;
using BlueDrive.Infrastructure.Office;
using BlueDrive.Infrastructure.Persistence;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlueDrive.Infrastructure.Operations;

/// <summary>Internal status page: database, storage, ONLYOFFICE, ClamAV, disk, last backup, job queues.</summary>
public sealed class SystemStatusService(AppDbContext db, IFileStorage storage, IDiskInfo disk, IHttpClientFactory http, IOptions<OnlyOfficeOptions> office,
    IOptions<ClamAvOptions> clam, ISender mediator) : ISystemStatusService
{
    public async Task<SystemStatusDto> GetAsync(CancellationToken ct)
    {
        var checks = new List<ServiceCheck>
        {
            await TimeAsync("Cơ sở dữ liệu PostgreSQL", async () => $"PostgreSQL {await db.Database.SqlQueryRaw<string>("SELECT split_part(version(), ' ', 2) AS \"Value\"").FirstAsync(ct)}"),
            await TimeAsync("Kho lưu trữ tệp", async () =>
            {
                var key = $".tmp/health-{Guid.NewGuid():N}";
                await storage.WriteAsync(key, new MemoryStream("ok"u8.ToArray()), ct);
                await storage.DeleteAsync(key, ct);
                return "Ghi/xóa thử thành công";
            }),
            await TimeAsync("ONLYOFFICE Document Server", async () =>
            {
                var res = await http.CreateClient("onlyoffice").GetStringAsync($"{office.Value.InternalUrl.TrimEnd('/')}/healthcheck", ct);
                return res.Trim() == "true" ? "Hoạt động" : throw new InvalidOperationException($"Phản hồi: {res}");
            }),
        };
        if (clam.Value.Enabled)
            checks.Add(await TimeAsync("ClamAV (quét mã độc)", async () => await new nClam.ClamClient(clam.Value.Host, clam.Value.Port).PingAsync(ct) ? "Hoạt động" : throw new InvalidOperationException("Không phản hồi")));
        else checks.Add(new ServiceCheck("ClamAV (quét mã độc)", "Disabled", "Chưa bật (CLAMAV_ENABLED=false)", null));

        int queued = 0, failed = 0, servers = 0;
        try
        {
            var mon = JobStorage.Current.GetMonitoringApi();
            var stats = mon.GetStatistics();
            queued = (int)stats.Enqueued;
            failed = (int)stats.Failed;
            servers = (int)stats.Servers;
            checks.Add(new ServiceCheck("Dịch vụ xử lý nền (Worker)", servers > 0 ? "Healthy" : "Unhealthy", servers > 0 ? $"{servers} máy chủ xử lý" : "Không có Worker đang chạy", null));
        }
        catch (InvalidOperationException ex) { checks.Add(new ServiceCheck("Dịch vụ xử lý nền (Worker)", "Unhealthy", ex.Message, null)); }

        var last = (await mediator.Send(new ListBackupsQuery { PageSize = 1 }, ct)).Items.FirstOrDefault();
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        var parts = info.Split('+', 2);
        return new SystemStatusDto(parts[0], parts.Length > 1 ? parts[1] : Environment.GetEnvironmentVariable("GIT_SHA") ?? "dev", DateTime.UtcNow, checks,
            disk.FreeBytes(), disk.TotalBytes(), last, queued, failed, servers);
    }

    private static async Task<ServiceCheck> TimeAsync(string name, Func<Task<string>> probe)
    {
        var sw = Stopwatch.StartNew();
        try { return new ServiceCheck(name, "Healthy", await probe(), sw.Elapsed.TotalMilliseconds); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ServiceCheck(name, "Unhealthy", ex.Message, sw.Elapsed.TotalMilliseconds); }
    }
}

/// <summary>Encrypts Data Protection key XML at rest with the storage master key (AES-256-GCM).</summary>
public sealed class MasterKeyXmlEncryptor(byte[] masterKey) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(masterKey, 16)) gcm.Encrypt(nonce, plain, cipher, tag);
        var el = new XElement("encryptedKey", new XAttribute("alg", "AES-256-GCM"), Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
        return new EncryptedXmlInfo(el, typeof(MasterKeyXmlDecryptor));
    }
}

public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var key = Storage.FileContentService.LoadMasterKey(((IOptions<Storage.StorageOptions>)services.GetService(typeof(IOptions<Storage.StorageOptions>))!).Value)
                  ?? throw new CryptographicException("Thiếu khóa chủ để giải mã khóa Data Protection.");
        var raw = Convert.FromBase64String(encryptedElement.Value);
        var plain = new byte[raw.Length - 28];
        using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plain));
    }
}
