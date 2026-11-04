using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlueDrive.Infrastructure.Email;
using BlueDrive.Infrastructure.Storage;
using BlueDrive.IntegrationTests.Infrastructure;
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
        await using var server = await S3TestServer.StartAsync();
        var options = Options.Create(server.StorageOptions(Path.Combine(Path.GetTempPath(), "bd-s3-" + Guid.NewGuid().ToString("N"))));
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
        await foreach (var o in s3.ListAsync("files/")) keys.Add(o.Key);
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
