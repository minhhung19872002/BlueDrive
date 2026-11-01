using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace BlueDrive.IntegrationTests.Infrastructure;

/// <summary>Boots the real API against a throw-away PostgreSQL 16 container.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("bluedrive_test")
        .WithUsername("bluedrive")
        .WithPassword("bluedrive_test")
        .Build();

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "bluedrive-it-" + Guid.NewGuid().ToString("N"));
    public string ConnectionString => _pg.GetConnectionString();
    public const string AdminPassword = "Admin@12345";

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        Directory.CreateDirectory(StorageRoot);
        // Force host startup (migrations + seed) once for the whole collection.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Logging:Directory", Path.Combine(StorageRoot, "logs"));
        builder.UseSetting("Logging:Database", "false");
        builder.UseSetting("Jwt:Secret", "integration_test_secret_integration_test_secret");
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:Demo", "false");
        builder.UseSetting("Storage:LocalRoot", Path.Combine(StorageRoot, "storage"));
        builder.UseSetting("Storage:MasterKey", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Backup:Root", Path.Combine(StorageRoot, "backups"));
        builder.UseSetting("OnlyOffice:JwtSecret", "integration_onlyoffice_secret_32_chars!!");
        builder.UseSetting("RateLimit:Disabled", "true");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(this);
            services.AddSingleton<FakeEmailSender>();
            services.AddScoped<BlueDrive.Application.Abstractions.IEmailSender>(sp => sp.GetRequiredService<FakeEmailSender>());
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _pg.DisposeAsync();
        try { Directory.Delete(StorageRoot, true); } catch (IOException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
