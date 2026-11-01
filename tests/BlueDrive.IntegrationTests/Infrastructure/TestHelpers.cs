using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using FluentAssertions;

namespace BlueDrive.IntegrationTests.Infrastructure;

public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        Sent.Enqueue((to, subject, htmlBody));
        return Task.CompletedTask;
    }
}

public sealed record TokenPair(string AccessToken, string RefreshToken);

public static class TestHelpers
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string AdminNewPassword = "Admin@NewPass2026";
    private static readonly SemaphoreSlim AdminLock = new(1, 1);
    private static TokenPair? _adminTokens;

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.IsSuccessStatusCode.Should().BeTrue($"request should succeed but returned {(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
    }

    public static async Task<TokenPair> LoginAsync(HttpClient client, string username, string password)
    {
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        var json = await res.ReadJsonAsync();
        return new TokenPair(json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!);
    }

    public static HttpClient WithToken(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Admin client; performs the mandatory first-login password change once per test run.</summary>
    public static async Task<HttpClient> AdminClientAsync(this ApiFactory factory)
    {
        await AdminLock.WaitAsync();
        try
        {
            if (_adminTokens is null)
            {
                var anon = factory.CreateClient();
                var first = await LoginAsync(anon, "admin", ApiFactory.AdminPassword);
                var changed = await anon.WithToken(first.AccessToken).PostAsJsonAsync("/api/v1/auth/change-password",
                    new { currentPassword = ApiFactory.AdminPassword, newPassword = AdminNewPassword, signOutOtherSessions = false });
                var json = await changed.ReadJsonAsync();
                _adminTokens = new TokenPair(json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!);
            }
            return factory.CreateClient().WithToken(_adminTokens.AccessToken);
        }
        finally { AdminLock.Release(); }
    }

    /// <summary>Creates a user with the given role codes and returns a logged-in client (password already changed).</summary>
    public static async Task<(HttpClient Client, Guid UserId, string Username)> CreateUserClientAsync(this ApiFactory factory, params string[] roleCodes)
    {
        var admin = await factory.AdminClientAsync();
        var roles = await (await admin.GetAsync("/api/v1/roles")).ReadJsonAsync();
        var roleIds = roles.EnumerateArray().Where(r => roleCodes.Contains(r.GetProperty("code").GetString())).Select(r => r.GetProperty("id").GetGuid()).ToList();
        var username = "u" + Guid.NewGuid().ToString("N")[..10];
        const string initial = "Init@Pass123";
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username, fullName = "Người dùng " + username, email = $"{username}@example.gov.vn", isActive = true, roleIds, password = initial,
        })).ReadJsonAsync();
        var userId = created.GetProperty("id").GetGuid();
        var anon = factory.CreateClient();
        var t = await LoginAsync(anon, username, initial);
        var changed = await (await anon.WithToken(t.AccessToken).PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = initial, newPassword = "User@Pass2026", signOutOtherSessions = false })).ReadJsonAsync();
        return (factory.CreateClient().WithToken(changed.GetProperty("accessToken").GetString()!), userId, username);
    }
}
