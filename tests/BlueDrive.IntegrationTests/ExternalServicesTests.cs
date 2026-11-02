using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlueDrive.Infrastructure.Email;
using BlueDrive.Infrastructure.Storage;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlueDrive.IntegrationTests;

/// <summary>Real S3-compatible storage (SeaweedFS) and real SMTP delivery (Mailpit).</summary>
public class ExternalServicesTests
{
    [Fact]
    public async Task S3_storage_with_encryption_supports_write_ranged_read_list_and_delete()
    {
        // SeaweedFS (pinned) as the S3 server: MinIO no longer publishes container images (minio/minio is gone from Docker Hub).
        const string accessKey = "bdtest", secretKey = "bdtest-secret-key";
        var s3Config = $$"""{"identities":[{"name":"bd","credentials":[{"accessKey":"{{accessKey}}","secretKey":"{{secretKey}}"}],"actions":["Admin","Read","Write","List","Tagging"]}]}""";
        await using var server = new ContainerBuilder("chrislusf/seaweedfs:3.97")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(s3Config), "/etc/seaweedfs/s3.json")
            .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-s3.config=/etc/seaweedfs/s3.json")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8333).ForStatusCodeMatching(_ => true)))
            .Build();
        await server.StartAsync();
        var serviceUrl = $"http://{server.Hostname}:{server.GetMappedPublicPort(8333)}";
        var options = Options.Create(new StorageOptions
        {
            Provider = "s3", EncryptAtRest = true, MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            LocalRoot = Path.Combine(Path.GetTempPath(), "bd-s3-" + Guid.NewGuid().ToString("N")),
            S3 = new S3Options { ServiceUrl = serviceUrl, Bucket = "bluedrive", AccessKey = accessKey, SecretKey = secretKey },
        });
        using (var admin = new Amazon.S3.AmazonS3Client(accessKey, secretKey,
                   new Amazon.S3.AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
            await admin.PutBucketAsync("bluedrive");
        using var s3 = new S3FileStorage(options);
        var content = new FileContentService(s3, options);

        var data = RandomNumberGenerator.GetBytes(300_000);
        var stored = await content.SaveAsync(new MemoryStream(data), "files");
        stored.IsEncrypted.Should().BeTrue();
        (await s3.ExistsAsync(stored.StorageKey)).Should().BeTrue();

        await using (var read = await content.OpenReadAsync(stored.StorageKey, true, stored.WrappedKey))
        {
            read.Seek(150_000, SeekOrigin.Begin);
            var buf = new byte[1000];
            var n = 0;
            while (n < buf.Length) n += await read.ReadAsync(buf.AsMemory(n));
            buf.Should().Equal(data[150_000..151_000], "ranged reads from S3 decrypt correctly");
        }
        var keys = new List<string>();
        await foreach (var k in s3.ListKeysAsync("files/")) keys.Add(k);
        keys.Should().Contain(stored.StorageKey);
        await content.DeleteAsync(stored.StorageKey);
        (await s3.ExistsAsync(stored.StorageKey)).Should().BeFalse();
    }

    [Fact]
    public async Task Smtp_sender_delivers_vietnamese_mail()
    {
        var mailpit = new ContainerBuilder("axllent/mailpit:latest").WithPortBinding(1025, true).WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v1/info"))).Build();
        await mailpit.StartAsync();
        try
        {
            var sender = new SmtpEmailSender(Options.Create(new SmtpOptions
            {
                Host = mailpit.Hostname, Port = mailpit.GetMappedPublicPort(1025), From = "bluedrive@example.gov.vn", UseSsl = false,
            }), NullLogger<SmtpEmailSender>.Instance);
            await sender.SendAsync("canbo@example.gov.vn", "[BlueDrive] Khôi phục mật khẩu", "<p>Xin chào, đây là thư kiểm thử.</p>");

            using var http = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
            var list = await http.GetFromJsonAsync<JsonElement>("/api/v1/messages");
            var msg = list.GetProperty("messages")[0];
            msg.GetProperty("Subject").GetString().Should().Be("[BlueDrive] Khôi phục mật khẩu");
            msg.GetProperty("To")[0].GetProperty("Address").GetString().Should().Be("canbo@example.gov.vn");
        }
        finally { await mailpit.DisposeAsync(); }
    }
}
