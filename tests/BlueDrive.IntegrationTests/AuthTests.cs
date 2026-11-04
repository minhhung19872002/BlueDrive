using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #16 (đăng nhập, đổi/khôi phục mật khẩu), #17 (phiên đăng nhập), #14 (cảnh báo đăng nhập sai).</summary>
[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory factory)
{
    [Fact]
    public async Task First_login_requires_password_change_before_other_endpoints()
    {
        var admin = await factory.AdminClientAsync();
        var username = "pwd" + Guid.NewGuid().ToString("N")[..8];
        await (await admin.PostAsJsonAsync("/api/v1/users", new { username, fullName = "Kiểm thử đổi mật khẩu", isActive = true, password = "Init@Pass123" })).ReadJsonAsync();

        var client = factory.CreateClient();
        var tokens = await TestHelpers.LoginAsync(client, username, "Init@Pass123");
        client.WithToken(tokens.AccessToken);
        (await client.GetAsync("/api/v1/catalogs/options")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var me = await (await client.GetAsync("/api/v1/auth/me")).ReadJsonAsync();
        me.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();

        var weak = await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "Init@Pass123", newPassword = "abc" });
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await weak.Content.ReadAsStringAsync()).Should().Contain("tối thiểu");

        var ok = await (await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "Init@Pass123", newPassword = "New@Pass12345" })).ReadJsonAsync();
        var fresh = factory.CreateClient().WithToken(ok.GetProperty("accessToken").GetString()!);
        (await fresh.GetAsync("/api/v1/catalogs/options")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_and_raise_brute_force_alert()
    {
        var (_, userId, username) = await factory.CreateUserClientAsync("chi_xem");
        var anon = factory.CreateClient();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++) last = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username, password = "Sai@Matkhau1" });
        last.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await last.Content.ReadAsStringAsync()).Should().Contain("tạm khóa");

        // Even the right password is refused while locked out.
        var locked = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username, password = "User@Pass2026" });
        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Alerts.AnyAsync(a => a.RelatedUserId == userId && a.Type == Domain.Enums.AlertType.BruteForce)).Should().BeTrue();
        (await db.AuditLogs.CountAsync(a => a.UserId == userId && a.Action == Domain.Enums.AuditAction.LoginFailed)).Should().BeGreaterThanOrEqualTo(5);
        var adminId = await db.Users.Where(u => u.Username == "admin").Select(u => u.Id).FirstAsync();
        (await db.Notifications.AnyAsync(n => n.UserId == adminId && n.Type == "alert")).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_revokes_the_session()
    {
        var (_, _, username) = await factory.CreateUserClientAsync("chi_xem");
        var anon = factory.CreateClient();
        var t1 = await TestHelpers.LoginAsync(anon, username, "User@Pass2026");
        var r2 = await (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = t1.RefreshToken })).ReadJsonAsync();
        var t2Refresh = r2.GetProperty("refreshToken").GetString()!;
        t2Refresh.Should().NotBe(t1.RefreshToken);

        // Re-using the first (rotated) token is treated as theft: the whole session dies.
        (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = t1.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = t2Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_can_revoke_all_access_and_access_token_stops_working()
    {
        var (client, userId, _) = await factory.CreateUserClientAsync("chi_xem");
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        var admin = await factory.AdminClientAsync();
        var sessions = await (await admin.GetAsync($"/api/v1/sessions?userId={userId}")).ReadJsonAsync();
        sessions.GetProperty("total").GetInt32().Should().BeGreaterThan(0);
        await (await admin.PostAsync($"/api/v1/sessions/users/{userId}/revoke-all", null)).ReadJsonAsync();
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task User_can_list_and_revoke_own_sessions()
    {
        var (client, _, username) = await factory.CreateUserClientAsync("chi_xem");
        var other = await TestHelpers.LoginAsync(factory.CreateClient(), username, "User@Pass2026");
        var mine = await (await client.GetAsync("/api/v1/sessions/mine")).ReadJsonAsync();
        mine.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        await (await client.PostAsync("/api/v1/sessions/mine/revoke-others", null)).ReadJsonAsync();
        (await factory.CreateClient().WithToken(other.AccessToken).GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Forgot_password_sends_single_use_link()
    {
        var (_, _, username) = await factory.CreateUserClientAsync("chi_xem");
        var email = factory.Services.GetRequiredService<FakeEmailSender>();
        var anon = factory.CreateClient();
        await (await anon.PostAsJsonAsync("/api/v1/auth/forgot-password", new { usernameOrEmail = $"{username}@example.gov.vn" })).ReadJsonAsync();
        var mail = email.Sent.Last(m => m.To == $"{username}@example.gov.vn");
        var token = Uri.UnescapeDataString(Regex.Match(mail.Body, "token=([^\"&]+)").Groups[1].Value);

        await (await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Reset@Pass2026" })).ReadJsonAsync();
        (await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Again@Pass2026" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await TestHelpers.LoginAsync(anon, username, "Reset@Pass2026");
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_return_vietnamese_problem_details()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "khong_ton_tai", password = "x" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await res.Content.ReadAsStringAsync()).Should().Contain("không đúng");
    }

    /// <summary>
    /// G7: one address guessing passwords across many accounts is blocked (429) and the admins are alerted; other addresses
    /// (other offices) keep logging in.
    /// </summary>
    [Fact]
    public async Task Many_failed_logins_from_one_address_block_that_address_only()
    {
        var admin = await factory.AdminClientAsync();
        await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["login.ip_max_failures"] = 3 })).ReadJsonAsync();
        try
        {
            var (_, _, victim) = await factory.CreateUserClientAsync("chi_xem");
            const string attacker = "192.0.2.77";
            async Task<HttpContext> LoginFrom(string ip, string username, string password) => await factory.Server.SendAsync(c =>
            {
                c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                c.Request.Method = "POST";
                c.Request.Path = "/api/v1/auth/login";
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(new { username, password })));
            });

            // Spraying: one wrong guess per account, so no account reaches its own lockout.
            (await LoginFrom(attacker, "khong_ton_tai_1", "Doan@Mo2026")).Response.StatusCode.Should().Be(401);
            (await LoginFrom(attacker, "khong_ton_tai_2", "Doan@Mo2026")).Response.StatusCode.Should().Be(401);
            (await LoginFrom(attacker, victim, "Doan@Mo2026")).Response.StatusCode.Should().Be(401);

            var blocked = await LoginFrom(attacker, victim, "User@Pass2026");
            blocked.Response.StatusCode.Should().Be(429);
            using (var reader = new StreamReader(blocked.Response.Body))
                (await reader.ReadToEndAsync()).Should().Contain("địa chỉ mạng");

            (await LoginFrom("192.0.2.78", victim, "User@Pass2026")).Response.StatusCode.Should().Be(200);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Alerts.AnyAsync(a => a.Type == Domain.Enums.AlertType.BruteForce && a.Message.Contains(attacker))).Should().BeTrue();
        }
        finally
        {
            await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["login.ip_max_failures"] = 30 })).ReadJsonAsync();
        }
    }

    /// <summary>
    /// G3: behind Caddy → nginx the session / audit IP is the real client, not Caddy's container address,
    /// and a client cannot forge its address with its own X-Forwarded-For.
    /// </summary>
    [Theory]
    [InlineData("172.18.0.7", "203.0.113.9, 172.18.0.3", "203.0.113.9")]               // client → Caddy → nginx → api
    [InlineData("172.18.0.7", "203.0.113.9, 10.0.0.2, 172.18.0.3", "203.0.113.9")]     // plus an outer proxy on the LAN
    [InlineData("172.18.0.7", "10.20.30.40, 172.18.0.3", "10.20.30.40")]               // client inside the organisation network
    [InlineData("198.51.100.4", "203.0.113.9", "198.51.100.4")]                         // direct hit with a forged header: ignored
    public async Task Session_ip_is_the_real_client_behind_the_proxy_chain(string connectionIp, string forwardedFor, string expectedIp)
    {
        var (_, _, username) = await factory.CreateUserClientAsync("chi_xem");
        var ctx = await factory.Server.SendAsync(c =>
        {
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(connectionIp);
            c.Request.Method = "POST";
            c.Request.Path = "/api/v1/auth/login";
            c.Request.Headers["X-Forwarded-For"] = forwardedFor;
            c.Request.Headers["X-Forwarded-Proto"] = "https";
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(new { username, password = "User@Pass2026" })));
        });
        ctx.Response.StatusCode.Should().Be(200);
        using var reader = new StreamReader(ctx.Response.Body);
        var token = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.GetProperty("accessToken").GetString()!;

        var sessions = await (await factory.CreateClient().WithToken(token).GetAsync("/api/v1/sessions/mine")).ReadJsonAsync();
        sessions.EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("ip").GetString().Should().Be(expectedIp);
    }
}
