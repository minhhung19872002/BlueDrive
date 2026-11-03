using System.Diagnostics;
using System.Net;
using System.Text;
using BlueDrive.Sync.Client;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>BlueDrive.Sync.Client without a server: PKCE loopback sign-in, bandwidth limit, streaming hash.</summary>
public class ClientTests
{
    /// <summary>Answers /api/v1/connect/token like the server; records the form it received.</summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public Dictionary<string, string> Form = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(p => p.Split('='))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            if (Form["grant_type"] == "refresh_token" && Form["refresh_token"] == "dead")
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    { Content = new StringContent("""{"error":"invalid_grant","error_description":"Thiết bị đã bị thu hồi."}""") };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"at-{{Guid.NewGuid()}}","refresh_token":"rt-2","expires_in":900,"token_type":"Bearer","device_id":"{{Guid.Empty}}","device_secret":"s"}"""),
            };
        }
    }

    [Fact]
    public async Task Pkce_loopback_sign_in_receives_the_code_and_exchanges_it_with_the_verifier()
    {
        var endpoint = new TokenEndpoint();
        var auth = new DesktopAuthenticator(new Uri("https://bluedrive.example/"), endpoint);
        Uri? opened = null;
        using var browser = new HttpClient();
        var signIn = auth.SignInAsync(new DeviceInfo("PC-1", "Windows 11", "1.0.0", "m"), u => opened = u, TimeSpan.FromSeconds(20));

        // What the browser does after the user signs in on the web: the server redirects to the loopback address.
        while (opened is null) await Task.Delay(10);
        var q = System.Web.HttpUtility.ParseQueryString(opened.Query);
        opened.AbsolutePath.Should().Be("/connect/authorize");
        q["client_id"].Should().Be("bluedrive-desktop");
        q["code_challenge_method"].Should().Be("S256");
        var redirect = new Uri(q["redirect_uri"]!);
        redirect.Host.Should().Be("127.0.0.1");
        (await browser.GetAsync(new Uri(redirect, "/favicon.ico"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var page = await browser.GetStringAsync($"{redirect}?code=abc&state={Uri.EscapeDataString(q["state"]!)}");
        page.Should().Contain("Đã đăng nhập BlueDrive Desktop");

        var tokens = await signIn;
        tokens.RefreshToken.Should().Be("rt-2");
        endpoint.Form["code"].Should().Be("abc");
        endpoint.Form["redirect_uri"].Should().Be(redirect.ToString());
        // The challenge sent to the browser is S256(verifier).
        DesktopAuthenticator.Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(endpoint.Form["code_verifier"])))
            .Should().Be(q["code_challenge"]);
    }

    [Fact]
    public async Task Callback_with_a_wrong_state_is_refused()
    {
        var auth = new DesktopAuthenticator(new Uri("https://bluedrive.example/"), new TokenEndpoint());
        Uri? opened = null;
        var signIn = auth.SignInAsync(new DeviceInfo("PC-1", "Windows 11", "1.0.0", "m"), u => opened = u, TimeSpan.FromSeconds(20));
        while (opened is null) await Task.Delay(10);
        var redirect = System.Web.HttpUtility.ParseQueryString(opened.Query)["redirect_uri"];
        using var browser = new HttpClient();
        await browser.GetStringAsync($"{redirect}?code=abc&state=forged");
        await FluentActions.Awaiting(() => signIn).Should().ThrowAsync<SignInRequiredException>();
    }

    [Fact]
    public async Task Failed_refresh_clears_the_tokens()
    {
        var auth = new DesktopAuthenticator(new Uri("https://bluedrive.example/"), new TokenEndpoint());
        var store = new MemoryTokenStore();
        store.Save(new DeviceTokens("old", "dead", 900, Guid.Empty, "s") { AccessExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
        var handler = new DeviceAuthHandler(store, auth);
        await FluentActions.Awaiting(() => handler.RefreshAsync(store.Load()!, CancellationToken.None)).Should().ThrowAsync<SignInRequiredException>();
        store.Load().Should().BeNull();
    }

    [Fact]
    public async Task Bandwidth_limit_paces_reads_and_can_be_lifted()
    {
        var limiter = new BandwidthLimiter(200 * 1024);
        await using var s = new ThrottledStream(new MemoryStream(new byte[600 * 1024]), limiter);
        var watch = Stopwatch.StartNew();
        await s.CopyToAsync(Stream.Null);
        // 600 KB at 200 KB/s with a one-second burst allowance: at least ~2 s.
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.7));

        limiter.BytesPerSecond = 0;
        await using var fast = new ThrottledStream(new MemoryStream(new byte[5 * 1024 * 1024]), limiter);
        watch.Restart();
        await fast.CopyToAsync(Stream.Null);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Streaming_hash_matches_the_engine_hash()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Báo cáo dân số ", 200_000)));
        (await ContentHash.Sha256Async(new MemoryStream(data))).Should().Be(Content.Hash(data));
    }
}
