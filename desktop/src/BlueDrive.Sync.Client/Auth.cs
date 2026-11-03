using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueDrive.Sync.Client;

/// <summary>Tokens of one account on this computer (device session).</summary>
public sealed record DeviceTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("device_secret")] string? DeviceSecret)
{
    [JsonIgnore] public DateTime AccessExpiresAt { get; init; } = DateTime.UtcNow.AddSeconds(Math.Max(30, ExpiresIn - 30));
}

/// <summary>Secure token storage: Credential Manager / DPAPI on Windows (BlueDrive.Desktop), memory in tests. Never logged.</summary>
public interface ITokenStore
{
    DeviceTokens? Load();
    void Save(DeviceTokens tokens);
    void Clear();
}

public sealed class MemoryTokenStore : ITokenStore
{
    private DeviceTokens? _tokens;
    public DeviceTokens? Load() => _tokens;
    public void Save(DeviceTokens tokens) => _tokens = tokens;
    public void Clear() => _tokens = null;
}

/// <summary>Information sent when the device signs in (shown in "Thiết bị đã kết nối").</summary>
public sealed record DeviceInfo(string Name, string Os, string AppVersion, string MachineId)
{
    public static DeviceInfo Current(string appVersion) => new(Environment.MachineName,
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
        appVersion, MachineIdentity());

    private static string MachineIdentity()
    {
        // Stable per machine, hashed by the server; no hardware serials are read.
        var seed = $"{Environment.MachineName}|{Environment.UserName}|{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }
}

/// <summary>Thrown when the device must sign in again (refresh token expired, device revoked or wiped).</summary>
public sealed class SignInRequiredException(string message) : Exception(message);

/// <summary>
/// BlueDrive Desktop sign-in: OAuth 2.0 authorization code + PKCE (S256) with a loopback redirect (RFC 8252). The system browser
/// opens /connect/authorize on the BlueDrive server; the user signs in there; the code comes back to http://127.0.0.1:{port}/callback.
/// </summary>
public sealed class DesktopAuthenticator(Uri server, HttpMessageHandler? handler = null)
{
    public const string ClientId = "bluedrive-desktop";
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler(), disposeHandler: handler is null) { BaseAddress = server };

    public sealed record PkcePair(string Verifier, string Challenge)
    {
        public static PkcePair Create()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            return new PkcePair(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
    }

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public Uri AuthorizeUrl(string redirectUri, PkcePair pkce, string state, string deviceName) => new(server,
        $"/connect/authorize?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&code_challenge={pkce.Challenge}" +
        $"&code_challenge_method=S256&state={Uri.EscapeDataString(state)}&device_name={Uri.EscapeDataString(deviceName)}");

    /// <summary>Full interactive sign-in: opens the browser, waits for the loopback callback, exchanges the code.</summary>
    /// <remarks>The loopback listener is a bare TCP socket on 127.0.0.1 (HttpListener needs a URL ACL on Windows for non-admin users).</remarks>
    public async Task<DeviceTokens> SignInAsync(DeviceInfo device, Action<Uri>? openBrowser = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/callback";
            var pkce = PkcePair.Create();
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var url = AuthorizeUrl(redirect, pkce, state, device.Name);
            (openBrowser ?? (u => Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true })))(url);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, cts.Token);
                if (target is null || !target.StartsWith("/callback", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, "404 Not Found", "", cts.Token); // favicon and other noise from the browser
                    continue;
                }
                var query = System.Web.HttpUtility.ParseQueryString(target.Contains('?') ? target[target.IndexOf('?')..] : "");
                var ok = query["state"] == state && query["code"] is not null;
                await RespondAsync(stream, "200 OK", ok
                    ? "Đã đăng nhập BlueDrive Desktop. Bạn có thể đóng thẻ này."
                    : "Đăng nhập không thành công. Hãy thử lại từ ứng dụng BlueDrive Desktop.", cts.Token);
                if (!ok) throw new SignInRequiredException(query["error"] == "access_denied" ? "Bạn đã từ chối đăng nhập." : "Phản hồi đăng nhập không hợp lệ.");
                return await ExchangeCodeAsync(query["code"]!, pkce.Verifier, redirect, device, ct);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task<string?> ReadRequestTargetAsync(Stream stream, CancellationToken ct)
    {
        const string EndOfHeaders = "\r\n\r\n";
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains(EndOfHeaders, StringComparison.Ordinal))
            {
                var parts = text[..text.IndexOf('\r')].Split(' ');
                return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
            }
        }
        return null;
    }

    /// <summary><paramref name="message"/> is one of our fixed texts (never request data).</summary>
    private static async Task RespondAsync(Stream stream, string status, string message, CancellationToken ct)
    {
        var html = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>BlueDrive</title><p style='font-family:sans-serif'>{message}</p>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(html, ct);
    }

    public Task<DeviceTokens> ExchangeCodeAsync(string code, string verifier, string redirectUri, DeviceInfo device, CancellationToken ct = default) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri, ["client_id"] = ClientId,
            ["device_name"] = device.Name, ["device_os"] = device.Os, ["app_version"] = device.AppVersion, ["machine_id"] = device.MachineId,
        }, ct);

    public Task<DeviceTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        TokenAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = ClientId }, ct);

    private async Task<DeviceTokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await _http.PostAsync("api/v1/connect/token", new FormUrlEncodedContent(form), ct); }
        catch (HttpRequestException ex) { throw new Core.RemoteTransientException("Không kết nối được máy chủ BlueDrive.", ex); }
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = JsonDocument.Parse(text).RootElement;
            throw new SignInRequiredException(err.TryGetProperty("error_description", out var d) ? d.GetString()! : "Cần đăng nhập lại.");
        }
        if (!res.IsSuccessStatusCode) throw new Core.RemoteTransientException($"Máy chủ trả lỗi {(int)res.StatusCode} khi đăng nhập.");
        var tokens = JsonSerializer.Deserialize<DeviceTokens>(text)!;
        return tokens with { AccessExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(30, tokens.ExpiresIn - 30)) };
    }
}

/// <summary>Adds the device access token; refreshes it once on 401 (rotation). A failed refresh means "sign in again".</summary>
public sealed class DeviceAuthHandler(ITokenStore store, DesktopAuthenticator auth) : DelegatingHandler
{
    private readonly SemaphoreSlim _refresh = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var tokens = store.Load() ?? throw new SignInRequiredException("Chưa đăng nhập BlueDrive Desktop.");
        if (tokens.AccessExpiresAt <= DateTime.UtcNow) tokens = await RefreshAsync(tokens, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var res = await base.SendAsync(request, ct);
        if (res.StatusCode != HttpStatusCode.Unauthorized || request.Content is { } c && !CanResend(c)) return res;
        res.Dispose();
        tokens = await RefreshAsync(tokens, ct);
        using var retry = await CloneAsync(request, ct);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return await base.SendAsync(retry, ct);
    }

    public string? CurrentAccessToken() => store.Load()?.AccessToken;

    public async Task<DeviceTokens> RefreshAsync(DeviceTokens stale, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            var current = store.Load();
            if (current is not null && current.AccessToken != stale.AccessToken && current.AccessExpiresAt > DateTime.UtcNow) return current; // refreshed meanwhile
            try
            {
                var fresh = await auth.RefreshAsync((current ?? stale).RefreshToken, ct);
                store.Save(fresh with { DeviceSecret = fresh.DeviceSecret ?? stale.DeviceSecret });
                return store.Load()!;
            }
            catch (SignInRequiredException)
            {
                store.Clear();
                throw;
            }
        }
        finally { _refresh.Release(); }
    }

    private static bool CanResend(HttpContent c) => c is ByteArrayContent or StringContent or FormUrlEncodedContent || c is JsonContent;

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(r.Method, r.RequestUri) { Version = r.Version };
        foreach (var h in r.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (r.Content is not null)
        {
            var bytes = await r.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in r.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return clone;
    }
}

public static class BlueDriveHttp
{
    /// <summary>HTTP client for the device: bearer + refresh, no automatic redirects (content redirects are followed explicitly).</summary>
    public static HttpClient Create(Uri server, ITokenStore store, DesktopAuthenticator auth, HttpMessageHandler? inner = null) =>
        new(new DeviceAuthHandler(store, auth)
        {
            InnerHandler = inner ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
        })
        { BaseAddress = Normalize(server), Timeout = TimeSpan.FromMinutes(10) };

    public static Uri Normalize(Uri server) => server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");
}
