using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace BlueDrive.Benchmarks;

/// <summary>The real API (in memory) on a throw-away PostgreSQL 16 container, as the integration tests run it.</summary>
public sealed class PerfHost : WebApplicationFactory<Program>
{
    public const string AdminPassword = "Admin@12345";
    public const string AdminNewPassword = "Admin@Perf2026";
    public const string UserPassword = "User@Perf2026";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("bluedrive_perf").WithUsername("bluedrive").WithPassword("bluedrive_perf")
        // Memory for large sorts and the 1M-row inserts.
        .WithCommand("-c", "shared_buffers=512MB", "-c", "work_mem=64MB", "-c", "maintenance_work_mem=512MB", "-c", "max_wal_size=4GB", "-c", "synchronous_commit=off",
            // Statements slower than 300 ms are written to the container log (docker logs) with their parameters.
            "-c", "log_min_duration_statement=300")
        .Build();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bluedrive-perf-" + Guid.NewGuid().ToString("N"));

    public string ConnectionString => _pg.GetConnectionString();

    public async Task StartAsync()
    {
        await _pg.StartAsync();
        Directory.CreateDirectory(_root);
        _ = Server; // migrations + reference data
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(ApiDirectory());
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Logging:Directory", Path.Combine(_root, "logs"));
        builder.UseSetting("Logging:Database", "false");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Jwt:Secret", "perf_benchmark_secret_perf_benchmark_secret_0001");
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:Demo", "false");
        builder.UseSetting("Storage:LocalRoot", Path.Combine(_root, "storage"));
        builder.UseSetting("Storage:MasterKey", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Backup:Root", Path.Combine(_root, "backups"));
        builder.UseSetting("OnlyOffice:JwtSecret", "perf_onlyoffice_secret_at_least_32_chars");
        builder.UseSetting("OnlyOffice:PublicUrl", "http://public.test/office");
        builder.UseSetting("OnlyOffice:InternalUrl", "http://localhost");
        builder.UseSetting("OnlyOffice:CallbackBaseUrl", "http://localhost");
        builder.UseSetting("RateLimit:Disabled", "true");
    }

    /// <summary>src/BlueDrive.Api, found from where the benchmark runs.</summary>
    private static string ApiDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var api = Path.Combine(dir.FullName, "src", "BlueDrive.Api");
            if (Directory.Exists(api)) return api;
        }
        throw new DirectoryNotFoundException("Không tìm thấy thư mục src/BlueDrive.Api.");
    }

    public async Task<HttpClient> LoginAsync(string username, string password, string? newPassword = null)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        var json = await ReadAsync(res);
        var token = json.GetProperty("accessToken").GetString()!;
        if (newPassword is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            json = await ReadAsync(await client.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = password, newPassword, signOutOtherSessions = false }));
            token = json.GetProperty("accessToken").GetString()!;
        }
        var authed = CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        authed.Timeout = TimeSpan.FromMinutes(10);
        return authed;
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)res.StatusCode} {res.RequestMessage?.RequestUri}: {text}");
        return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _pg.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
