using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Client;

/// <summary>
/// BlueDrive API calls behind the Explorer context menu (Sync-Phase 7): web link, sharing (people / groups / links), version history.
/// Uses the device's authenticated client; errors become the same exceptions as <see cref="HttpRemoteStore"/>.
/// </summary>
public sealed class BlueDriveApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) } };

    public sealed record WebUrl(string Url);
    public sealed record ShareLink(Guid Id, string Url, bool AllowDownload, bool HasPassword, DateTime? ExpiresAt, bool RequireLogin);
    public sealed record UserLookup(Guid Id, string Username, string FullName, string? OrganizationName);
    public sealed record Group(Guid Id, string Name);
    public sealed record Paged<T>(List<T> Items, int Total);
    public sealed record Share(Guid Id, string SharedWithType, Guid SharedWithId, string SharedWithName, string AccessLevel, DateTime CreatedAt,
        Guid? InheritedFromId, string? InheritedFromName);
    public sealed record Version(Guid Id, int VersionNo, long SizeBytes, string? ChangeNote, string? CreatedByName, DateTime CreatedAt, bool IsCurrent);

    public sealed record EditorLink(string Url, string BrowserUrl, bool CanEdit);
    public sealed record EditingFile(Guid FileId, string Name, IReadOnlyList<string> Editors);

    /// <summary>Documents being co-edited now (ONLYOFFICE) that the user can see, with who is editing.</summary>
    public async Task<IReadOnlyList<EditingFile>> EditingAsync(CancellationToken ct = default) => await GetAsync<List<EditingFile>>("api/v1/sync/editing", ct);

    public sealed record Notification(Guid Id, string Type, string Title, string? Body, string? Link, bool IsRead, DateTime CreatedAt);

    /// <summary>The user's latest unread notifications (the web's bell): "X đã chia sẻ … với bạn"…</summary>
    public async Task<IReadOnlyList<Notification>> UnreadNotificationsAsync(CancellationToken ct = default) =>
        (await GetAsync<Paged<Notification>>("api/v1/notifications?unreadOnly=true&page=1&pageSize=20", ct)).Items;

    /// <summary>One-time link that opens the document in ONLYOFFICE Desktop Editors signed in (2 minutes, single use).</summary>
    public async Task<EditorLink> EditorLinkAsync(string fileId, CancellationToken ct = default)
    {
        var l = await SendAsync<EditorLink>(HttpMethod.Post, "api/v1/onlyoffice-desktop/tickets", new { fileId = Guid.Parse(fileId) }, ct);
        // Relative when the server has no public address configured: make it absolute against the server.
        string Abs(string u) => Uri.TryCreate(u, UriKind.Absolute, out _) ? u : new Uri(http.BaseAddress!, u.TrimStart('/')).ToString();
        return l with { Url = Abs(l.Url), BrowserUrl = Abs(l.BrowserUrl) };
    }

    public Task<WebUrl> WebUrlAsync(string itemId, CancellationToken ct = default) => GetAsync<WebUrl>($"api/v1/sync/items/{itemId}/web-url", ct);

    public Task<ShareLink> CreateLinkAsync(string itemId, bool allowDownload, string? password, DateTime? expiresAt, bool requireLogin, CancellationToken ct = default) =>
        SendAsync<ShareLink>(HttpMethod.Post, $"api/v1/sync/items/{itemId}/share-link",
            new { allowDownload, password = string.IsNullOrWhiteSpace(password) ? null : password, expiresAt, requireLogin }, ct);

    public async Task<IReadOnlyList<UserLookup>> FindUsersAsync(string q, CancellationToken ct = default) =>
        await GetAsync<List<UserLookup>>($"api/v1/users/lookup?q={Uri.EscapeDataString(q)}&take=20", ct);

    public async Task<IReadOnlyList<Group>> FindGroupsAsync(string q, CancellationToken ct = default) =>
        (await GetAsync<Paged<Group>>($"api/v1/user-groups?q={Uri.EscapeDataString(q)}&page=1&pageSize=20", ct)).Items;

    /// <param name="type">"file" or "folder".</param>
    /// <param name="principalType">"user" / "group" / "organization".</param>
    /// <param name="accessLevel">"view" / "download" / "edit".</param>
    public Task ShareAsync(string type, string itemId, string principalType, Guid principalId, string accessLevel, string? message, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "api/v1/shares", new
        {
            resourceType = type, resourceId = Guid.Parse(itemId), targets = new[] { new { type = principalType, id = principalId } }, accessLevel,
            message = string.IsNullOrWhiteSpace(message) ? null : message,
        }, ct);

    public async Task<IReadOnlyList<Share>> SharesAsync(string type, string itemId, CancellationToken ct = default) =>
        await GetAsync<List<Share>>($"api/v1/shares?resourceType={type}&resourceId={itemId}", ct);

    public async Task RemoveShareAsync(Guid shareId, CancellationToken ct = default)
    {
        using var res = await Send(new HttpRequestMessage(HttpMethod.Delete, $"api/v1/shares/{shareId}"), ct);
    }

    public async Task<IReadOnlyList<Version>> VersionsAsync(string fileId, CancellationToken ct = default) =>
        await GetAsync<List<Version>>($"api/v1/files/{fileId}/versions", ct);

    /// <summary>The chosen version becomes the current one (a new version; nothing is lost).</summary>
    public Task<Version> RestoreVersionAsync(string fileId, Guid versionId, CancellationToken ct = default) =>
        SendAsync<Version>(HttpMethod.Post, $"api/v1/files/{fileId}/versions/{versionId}/restore", null, ct);

    /// <summary>Saves an older version somewhere (the API redirects to a short-lived signed URL).</summary>
    public async Task DownloadVersionAsync(string fileId, Guid versionId, Stream destination, CancellationToken ct = default)
    {
        using var res = await Send(new HttpRequestMessage(HttpMethod.Get, $"api/v1/sync/items/{fileId}/content?version={versionId}"), ct);
        var location = res.Headers.Location ?? throw new RemoteTransientException("Máy chủ không trả về địa chỉ tải xuống.");
        using var content = await Send(new HttpRequestMessage(HttpMethod.Get, location.IsAbsoluteUri ? location : new Uri(http.BaseAddress!, location)), ct);
        await (await content.Content.ReadAsStreamAsync(ct)).CopyToAsync(destination, ct);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var res = await Send(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        using var res = await Send(req, ct);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException ex) { throw new RemoteTransientException("Không kết nối được máy chủ BlueDrive.", ex); }
        finally { req.Dispose(); }
        if (res.IsSuccessStatusCode || (int)res.StatusCode is >= 300 and < 400) return res;
        var text = await res.Content.ReadAsStringAsync(ct);
        var status = (int)res.StatusCode;
        res.Dispose();
        string? detail = null;
        try { if (text.StartsWith('{')) detail = JsonDocument.Parse(text).RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null; }
        catch (JsonException) { }
        throw status switch
        {
            401 => new SignInRequiredException(detail ?? "Cần đăng nhập lại."),
            404 => new RemoteNotFoundException(req.RequestUri?.ToString() ?? ""),
            >= 500 or 408 or 429 => new RemoteTransientException(detail ?? $"Máy chủ tạm thời lỗi ({status})."),
            _ => new RemotePermanentException(detail ?? $"Máy chủ từ chối thao tác ({status}).", status),
        };
    }
}
