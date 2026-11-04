using System.Security.Cryptography;
using BlueDrive.Infrastructure.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace BlueDrive.IntegrationTests.Infrastructure;

/// <summary>
/// Throw-away S3-compatible server with an empty "bluedrive" bucket. SeaweedFS (pinned): MinIO no longer publishes container
/// images (minio/minio is gone from Docker Hub).
/// </summary>
public sealed class S3TestServer : IAsyncDisposable
{
    public const string AccessKey = "bdtest", SecretKey = "bdtest-secret-key", Bucket = "bluedrive";
    private readonly IContainer _container;

    private S3TestServer(IContainer container) => _container = container;

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8333)}";

    public static async Task<S3TestServer> StartAsync()
    {
        var s3Config = $$"""{"identities":[{"name":"bd","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],"actions":["Admin","Read","Write","List","Tagging"]}]}""";
        var container = new ContainerBuilder("chrislusf/seaweedfs:3.97")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(s3Config), "/etc/seaweedfs/s3.json")
            .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-s3.config=/etc/seaweedfs/s3.json")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8333).ForStatusCodeMatching(_ => true)))
            .Build();
        await container.StartAsync();
        var server = new S3TestServer(container);
        using var admin = server.CreateClient();
        await admin.PutBucketAsync(Bucket);
        return server;
    }

    public Amazon.S3.AmazonS3Client CreateClient() =>
        new(AccessKey, SecretKey, new Amazon.S3.AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    /// <summary>Encrypted S3 storage options; <paramref name="localRoot"/> only holds the scratch directory.</summary>
    public StorageOptions StorageOptions(string localRoot, string? masterKey = null) => new()
    {
        Provider = "s3", EncryptAtRest = true, MasterKey = masterKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), LocalRoot = localRoot,
        S3 = new S3Options { ServiceUrl = ServiceUrl, Bucket = Bucket, AccessKey = AccessKey, SecretKey = SecretKey },
    };

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
