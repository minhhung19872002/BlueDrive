using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueDrive.Sync.Core;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace BlueDrive.Sync.Client;

public sealed class RemoteStoreOptions
{
    /// <summary>Files up to this size go in one multipart request; larger ones use resumable tus uploads.</summary>
    public long SmallUploadBytes { get; set; } = 4L * 1024 * 1024;

    public int ChunkBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Retries of idempotent requests (listing, downloads, tus chunks). Mutations are never blindly re-sent.</summary>
    public int MaxRetries { get; set; } = 5;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public BandwidthLimiter UploadLimit { get; set; } = new();
    public BandwidthLimiter DownloadLimit { get; set; } = new();
}

/// <summary>
/// <see cref="IRemoteStore"/> over the BlueDrive REST API (/api/v1/sync/*, tus /tus, /api/v1/files/upload). Server status codes become the
/// engine's exceptions: 409 name conflict, 412 precondition (with the current item), 404, 410 cursor expired, 403/413/415/422/423 permanent,
/// 408/429/5xx/network transient.
/// </summary>
public sealed class HttpRemoteStore : IRemoteStore, IRemoteRangeReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) } };
    private readonly HttpClient _http;
    private readonly RemoteStoreOptions _opt;
    private readonly ResiliencePipeline _idempotent;
    private readonly ConcurrentDictionary<string, Guid> _workspaceOf = new();
    private readonly ConcurrentDictionary<string, string> _nameOf = new();

    public HttpRemoteStore(HttpClient http, RemoteStoreOptions? options = null)
    {
        _http = http;
        _opt = options ?? new RemoteStoreOptions();
        _idempotent = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RemoteTransientException>(),
                MaxRetryAttempts = _opt.MaxRetries, Delay = _opt.RetryBaseDelay, BackoffType = DelayBackoffType.Exponential, UseJitter = true,
                MaxDelay = TimeSpan.FromMinutes(2),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RemoteTransientException>(),
                FailureRatio = 0.8, MinimumThroughput = 10, SamplingDuration = TimeSpan.FromSeconds(60), BreakDuration = TimeSpan.FromSeconds(30),
            })
            .Build();
    }

    // ------------------------------------------------------------ DTOs (server JSON, camelCase)

    internal sealed record ItemDto(Guid Id, string Type, string RootId, Guid WorkspaceId, Guid? ParentId, string Name, string ETag, long Revision,
        Guid? VersionId, long? Size, string? Hash, DateTime ModifiedAt, bool CanDownload, bool CanEdit, bool Deleted);

    private sealed record SnapshotDto(List<ItemDto> Items, string? NextPageToken, long Cursor);
    private sealed record ChangeDto(long Seq, string Op, string ItemType, Guid ItemId, Guid WorkspaceId, ItemDto? Item, DateTime OccurredAt, Guid? DeviceId);
    private sealed record ChangesDto(List<ChangeDto> Changes, long NextCursor, bool HasMore);
    private sealed record UploadResultDto(Guid FileId, Guid VersionId, int VersionNo, string Name);
    private sealed record BatchResultDto(string? ClientRef, int Status, ItemDto? Item, string? Error);
    public sealed record RootDto(string RootId, string Kind, string Name, Guid? WorkspaceId, bool CanUpload, bool CanEdit, bool Locked, bool OnlineOnly, long QuotaBytes, long UsedBytes);

    private RemoteItem Map(ItemDto d)
    {
        _workspaceOf[d.Id.ToString()] = d.WorkspaceId;
        _nameOf[d.Id.ToString()] = d.Name;
        return new RemoteItem(d.Id.ToString(), d.ParentId?.ToString(), d.Name, d.Type == "folder", d.Revision, d.Hash, d.Size ?? 0,
            DateTime.SpecifyKind(d.ModifiedAt, DateTimeKind.Utc), d.CanEdit, d.CanDownload);
    }

    // ------------------------------------------------------------ listing

    public Task<IReadOnlyList<RootDto>> RootsAsync(CancellationToken ct) =>
        Idempotent(async t => (IReadOnlyList<RootDto>)(await GetJsonAsync<List<RootDto>>("api/v1/sync/roots", t))!, ct);

    public sealed record WipeCommand(DateTime RequestedAt, string Signature);
    public sealed record DeviceStatusInfo(Guid DeviceId, string Status, bool UpdateRequired, string MinClientVersion, WipeCommand? Wipe);
    public sealed record UserInfo(Guid Id, string Username, string FullName);

    /// <summary>Revocation / signed remote wipe / minimum app version for this device (also records "last seen").</summary>
    public Task<DeviceStatusInfo> DeviceStatusAsync(string appVersion, CancellationToken ct) =>
        Idempotent(async t => (await GetJsonAsync<DeviceStatusInfo>($"api/v1/sync/devices/current?appVersion={Uri.EscapeDataString(appVersion)}", t))!, ct);

    /// <summary>Reports that the remote wipe has been carried out: the server ends the device.</summary>
    public async Task ConfirmWipedAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/sync/devices/current/wiped");
        using var res = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(res, "", ct);
    }

    public Task<UserInfo> MeAsync(CancellationToken ct) => Idempotent(async t => (await GetJsonAsync<UserInfo>("api/v1/auth/me", t))!, ct);

    public Task<RemoteSnapshotPage> SnapshotAsync(string rootId, string? pageToken, string? folderId, CancellationToken ct) => Idempotent(async t =>
    {
        var url = $"api/v1/sync/snapshot?root_id={Uri.EscapeDataString(rootId)}" + (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}")
                  + (folderId is null ? "" : $"&folder_id={folderId}");
        var page = (await GetJsonAsync<SnapshotDto>(url, t))!;
        return new RemoteSnapshotPage(page.Items.Where(i => !i.Deleted).Select(Map).ToList(), page.NextPageToken, page.Cursor);
    }, ct);

    public Task<RemoteChangePage> ChangesAsync(long cursor, CancellationToken ct) => Idempotent(async t =>
    {
        var page = (await GetJsonAsync<ChangesDto>($"api/v1/sync/changes?cursor={cursor}", t))!;
        var list = new List<RemoteChange>(page.Changes.Count);
        foreach (var c in page.Changes)
        {
            _workspaceOf[c.ItemId.ToString()] = c.WorkspaceId;
            var kind = c.Op switch
            {
                "delete" or "purge" => RemoteChangeKind.Delete,
                "access_revoked" => RemoteChangeKind.AccessRevoked,
                "permission_changed" => RemoteChangeKind.PermissionChanged,
                _ => RemoteChangeKind.Upsert,
            };
            // An upsert whose item is gone (deleted later in the journal, or no longer visible) carries no item: treat as delete.
            if (kind == RemoteChangeKind.Upsert && (c.Item is null || c.Item.Deleted)) kind = RemoteChangeKind.Delete;
            list.Add(new RemoteChange(c.Seq, kind, c.ItemId.ToString(), c.Item?.RootId, kind == RemoteChangeKind.Upsert ? Map(c.Item!) : null, c.DeviceId?.ToString()));
        }
        return new RemoteChangePage(list, page.NextCursor, page.HasMore);
    }, ct);

    public Task<RemoteItem> GetItemAsync(string itemId, CancellationToken ct) =>
        Idempotent(async t => Map((await GetJsonAsync<ItemDto>($"api/v1/sync/items/{itemId}", t))!), ct);

    // ------------------------------------------------------------ download (Range resume)

    public async Task DownloadAsync(string itemId, Stream destination, CancellationToken ct)
    {
        long written = 0;
        await Idempotent<object?>(async t =>
        {
            var location = await ContentLocationAsync(itemId, t);
            using var req = new HttpRequestMessage(HttpMethod.Get, location);
            if (written > 0) req.Headers.Range = new RangeHeaderValue(written, null);
            using var res = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, t);
            await EnsureAsync(res, itemId, t);
            if (written > 0 && res.StatusCode != HttpStatusCode.PartialContent)
            {
                // Server ignored the range: start over.
                if (!destination.CanSeek) throw new RemoteTransientException("Không tải tiếp được tệp.");
                destination.SetLength(0);
                written = 0;
            }
            await using var body = await res.Content.ReadAsStreamAsync(t);
            var buffer = new byte[81920];
            try
            {
                int n;
                while ((n = await body.ReadAsync(buffer, t)) > 0)
                {
                    await _opt.DownloadLimit.WaitAsync(n, t);
                    await destination.WriteAsync(buffer.AsMemory(0, n), t);
                    written += n;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !t.IsCancellationRequested)
            {
                throw new RemoteTransientException("Mất kết nối khi tải xuống.", ex);
            }
            return null;
        }, ct);
    }

    /// <summary>Files On-Demand hydration: streams bytes [offset, offset + length) to <paramref name="sink"/>, resuming after a dropped connection.</summary>
    public async Task ReadRangeAsync(string itemId, long offset, long length, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink, CancellationToken ct)
    {
        long done = 0;
        await Idempotent<object?>(async t =>
        {
            if (done >= length) return null;
            var location = await ContentLocationAsync(itemId, t);
            using var req = new HttpRequestMessage(HttpMethod.Get, location);
            req.Headers.Range = new RangeHeaderValue(offset + done, offset + length - 1);
            using var res = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, t);
            await EnsureAsync(res, itemId, t);
            await using var body = await res.Content.ReadAsStreamAsync(t);
            if (res.StatusCode != HttpStatusCode.PartialContent && offset + done > 0)
            {
                // No range support on the way: skip to the requested position.
                var skip = offset + done;
                var junk = new byte[81920];
                while (skip > 0)
                {
                    var r = await body.ReadAsync(junk.AsMemory(0, (int)Math.Min(junk.Length, skip)), t);
                    if (r == 0) throw new RemoteTransientException("Máy chủ trả thiếu dữ liệu.");
                    skip -= r;
                }
            }
            var buffer = new byte[81920];
            try
            {
                while (done < length)
                {
                    var n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), t);
                    if (n == 0) break;
                    await _opt.DownloadLimit.WaitAsync(n, t);
                    await sink(buffer.AsMemory(0, n), t);
                    done += n;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !t.IsCancellationRequested)
            {
                throw new RemoteTransientException("Mất kết nối khi tải xuống.", ex);
            }
            if (done < length) throw new RemoteTransientException("Kết nối bị ngắt giữa chừng, đang tải tiếp.");
            return null;
        }, ct);
    }

    /// <summary>The API answers /content with a redirect to a short-lived signed URL; follow it explicitly (works with any handler).</summary>
    private async Task<Uri> ContentLocationAsync(string itemId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v1/sync/items/{itemId}/content");
        using var res = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc)
            return loc.IsAbsoluteUri ? loc : new Uri(_http.BaseAddress!, loc);
        await EnsureAsync(res, itemId, ct);
        throw new RemoteTransientException("Máy chủ không trả về địa chỉ tải xuống.");
    }

    // ------------------------------------------------------------ uploads

    public async Task<RemoteItem> UploadNewAsync(string rootId, string? parentId, string name, Stream content, string hash, CancellationToken ct)
    {
        var ws = WorkspaceFor(rootId, parentId);
        var fields = new Dictionary<string, string> { ["workspaceId"] = ws.ToString(), ["content_hash"] = hash, ["conflict"] = "fail" };
        if (parentId is not null) fields["folderId"] = parentId;
        var fileId = await UploadAsync(name, fields, content, parentId ?? rootId, ct);
        return await GetItemAsync(fileId.ToString(), ct);
    }

    public async Task<RemoteItem> UploadVersionAsync(string itemId, long baseRevision, Stream content, string hash, CancellationToken ct)
    {
        if (!_workspaceOf.ContainsKey(itemId) || !_nameOf.ContainsKey(itemId)) await TryGetAsync(itemId, ct);
        var ws = _workspaceOf.TryGetValue(itemId, out var w) ? w : throw new RemoteNotFoundException(itemId);
        var fields = new Dictionary<string, string>
        {
            ["workspaceId"] = ws.ToString(), ["targetFileId"] = itemId, ["base_etag"] = $"\"{baseRevision}\"", ["content_hash"] = hash, ["conflict"] = "fail",
        };
        try
        {
            await UploadAsync(_nameOf.GetValueOrDefault(itemId) ?? "file", fields, content, itemId, ct);
        }
        catch (RemotePreconditionFailedException ex) when (ex.Current is null)
        {
            // tus refuses before any byte with a plain-text 412: fetch the current item for the engine.
            throw new RemotePreconditionFailedException(await TryGetAsync(itemId, ct));
        }
        return await GetItemAsync(itemId, ct);
    }

    private async Task<Guid> UploadAsync(string name, Dictionary<string, string> fields, Stream content, string subject, CancellationToken ct)
    {
        var length = content.CanSeek ? content.Length - content.Position : -1;
        if (length >= 0 && length <= _opt.SmallUploadBytes) return await MultipartAsync(name, fields, content, subject, ct);
        return await TusAsync(name, fields, content, length, subject, ct);
    }

    private async Task<Guid> MultipartAsync(string name, Dictionary<string, string> fields, Stream content, string subject, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        var file = new StreamContent(new ThrottledStream(content, _opt.UploadLimit, leaveOpen: true));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name.Length > 0 ? name : "file");
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/files/upload") { Content = form };
        using var res = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(res, subject, ct, name);
        return (await res.Content.ReadFromJsonAsync<UploadResultDto>(Json, ct))!.FileId;
    }

    /// <summary>tus 1.0 (creation): POST /tus → PATCH chunks; after a network error HEAD tells the offset to resume from.</summary>
    private async Task<Guid> TusAsync(string name, Dictionary<string, string> fields, Stream content, long length, string subject, CancellationToken ct)
    {
        if (length < 0) throw new ArgumentException("Nội dung tải lên phải có độ dài xác định.", nameof(content));
        var start = content.Position;
        var meta = new Dictionary<string, string>(fields) { ["filename"] = name.Length > 0 ? name : "file" };
        if (fields.TryGetValue("folderId", out var parent)) meta["parent_id"] = parent;
        if (fields.TryGetValue("targetFileId", out var target)) meta["target_file_id"] = target;
        if (fields.TryGetValue("workspaceId", out var ws)) meta["workspace_id"] = ws;

        Uri location;
        using (var create = new HttpRequestMessage(HttpMethod.Post, "tus"))
        {
            create.Headers.Add("Tus-Resumable", "1.0.0");
            create.Headers.Add("Upload-Length", length.ToString());
            create.Headers.Add("Upload-Metadata", string.Join(",", meta.Select(kv => $"{kv.Key} {Convert.ToBase64String(Encoding.UTF8.GetBytes(kv.Value))}")));
            create.Content = new ByteArrayContent([]);
            using var res = await SendAsync(create, HttpCompletionOption.ResponseContentRead, ct);
            await EnsureAsync(res, subject, ct, name);
            var loc = res.Headers.Location ?? throw new RemoteTransientException("Máy chủ không tạo được phiên tải lên.");
            location = loc.IsAbsoluteUri ? loc : new Uri(_http.BaseAddress!, loc);
        }
        var tusId = location.Segments[^1].TrimEnd('/');

        long offset = 0;
        var buffer = new byte[Math.Max(64 * 1024, _opt.ChunkBytes)];
        while (offset < length)
        {
            offset = await Idempotent(async t =>
            {
                // Re-sync the offset with the server (a previous PATCH may have been partly stored).
                using (var head = new HttpRequestMessage(HttpMethod.Head, location))
                {
                    head.Headers.Add("Tus-Resumable", "1.0.0");
                    using var hr = await SendAsync(head, HttpCompletionOption.ResponseHeadersRead, t);
                    await EnsureAsync(hr, subject, t, name);
                    if (hr.Headers.TryGetValues("Upload-Offset", out var o)) offset = long.Parse(o.First());
                }
                if (offset >= length) return offset;
                if (!content.CanSeek) throw new RemotePermanentException("Không tải tiếp được tệp.", 0);
                content.Position = start + offset;
                var n = await content.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), 1, false, t);
                await _opt.UploadLimit.WaitAsync(n, t);
                using var patch = new HttpRequestMessage(HttpMethod.Patch, location) { Content = new ByteArrayContent(buffer, 0, n) };
                patch.Headers.Add("Tus-Resumable", "1.0.0");
                patch.Headers.Add("Upload-Offset", offset.ToString());
                patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
                using var pr = await SendAsync(patch, HttpCompletionOption.ResponseContentRead, t);
                await EnsureAsync(pr, subject, t, name);
                return pr.Headers.TryGetValues("Upload-Offset", out var no) ? long.Parse(no.First()) : offset + n;
            }, ct);
        }

        var body = new
        {
            fileName = meta["filename"], workspaceId = Guid.Parse(fields["workspaceId"]),
            folderId = fields.TryGetValue("folderId", out var f) ? Guid.Parse(f) : (Guid?)null,
            targetFileId = target is null ? (Guid?)null : Guid.Parse(target),
            baseETag = fields.GetValueOrDefault("base_etag"), contentHash = fields.GetValueOrDefault("content_hash"), failOnConflict = true,
        };
        using var complete = new HttpRequestMessage(HttpMethod.Post, $"api/v1/uploads/{tusId}/complete") { Content = JsonContent.Create(body, options: Json) };
        using var cr = await SendAsync(complete, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(cr, subject, ct, name);
        return (await cr.Content.ReadFromJsonAsync<UploadResultDto>(Json, ct))!.FileId;
    }

    // ------------------------------------------------------------ folder / move / delete (via /sync/batch)

    public async Task<RemoteItem> CreateFolderAsync(string rootId, string? parentId, string name, CancellationToken ct)
    {
        var ws = WorkspaceFor(rootId, parentId);
        var r = await BatchOneAsync(new { op = "create_folder", workspaceId = ws, parentId = parentId is null ? (Guid?)null : Guid.Parse(parentId), name }, parentId ?? rootId, name, ct);
        return Map(r.Item!);
    }

    public async Task<RemoteItem> MoveAsync(string itemId, long revision, string rootId, string? newParentId, string newName, CancellationToken ct)
    {
        var current = await TryGetAsync(itemId, ct) ?? throw new RemoteNotFoundException(itemId);
        if (current.Revision != revision) throw new RemotePreconditionFailedException(current);
        var targetWs = WorkspaceFor(rootId, newParentId);
        var ifMatch = $"\"{revision}\"";
        var ops = new List<object>();
        var renamed = !string.Equals(current.Name, newName, StringComparison.Ordinal);
        var moved = current.ParentId != newParentId || _workspaceOf.GetValueOrDefault(itemId) != targetWs;
        if (renamed) ops.Add(new { op = "rename", clientRef = "rename", itemId = Guid.Parse(itemId), name = newName, ifMatch });
        if (moved)
            ops.Add(new
            {
                op = "move", clientRef = "move", itemId = Guid.Parse(itemId), targetWorkspaceId = targetWs,
                targetFolderId = newParentId is null ? (Guid?)null : Guid.Parse(newParentId), ifMatch = renamed ? null : ifMatch,
            });
        if (ops.Count == 0) return current;
        var results = await BatchAsync(ops, ct);
        ItemDto? last = null;
        foreach (var r in results)
        {
            if (r.Status is >= 200 and < 300) { last = r.Item ?? last; continue; }
            var notFound = r.Status == 404 ? await MissingAsync(itemId, newParentId, ct) : itemId;
            throw Error(r.Status, r.Error, notFound, newName, r.Item is null ? null : Map(r.Item));
        }
        return last is not null ? Map(last) : await GetItemAsync(itemId, ct);
    }

    public async Task DeleteAsync(string itemId, long revision, CancellationToken ct) =>
        await BatchOneAsync(new { op = "delete", itemId = Guid.Parse(itemId), ifMatch = $"\"{revision}\"" }, itemId, null, ct);

    private async Task<BatchResultDto> BatchOneAsync(object op, string subject, string? name, CancellationToken ct)
    {
        var r = (await BatchAsync([op], ct))[0];
        if (r.Status is >= 200 and < 300) return r;
        throw Error(r.Status, r.Error, subject, name, r.Item is null ? null : Map(r.Item));
    }

    private async Task<List<BatchResultDto>> BatchAsync(IReadOnlyList<object> ops, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/sync/batch") { Content = JsonContent.Create(new { operations = ops }, options: Json) };
        using var res = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(res, "", ct);
        return (await res.Content.ReadFromJsonAsync<List<BatchResultDto>>(Json, ct))!;
    }

    /// <summary>A 404 during a move is either the item or the destination folder; ask the server which one.</summary>
    private async Task<string> MissingAsync(string itemId, string? parentId, CancellationToken ct) =>
        await TryGetAsync(itemId, ct) is null || parentId is null ? itemId : parentId;

    // ------------------------------------------------------------ helpers

    private Guid WorkspaceFor(string rootId, string? parentId)
    {
        if (parentId is not null && _workspaceOf.TryGetValue(parentId, out var ws)) return ws;
        if (rootId.StartsWith("ws:", StringComparison.Ordinal) && Guid.TryParse(rootId[3..], out ws)) return ws;
        if (parentId is null) throw new RemotePermanentException("Không thể tạo mục ở cấp cao nhất của \"Được chia sẻ với tôi\".", 403);
        throw new RemoteNotFoundException(parentId);
    }

    private async Task<RemoteItem?> TryGetAsync(string itemId, CancellationToken ct)
    {
        try { return await GetItemAsync(itemId, ct); }
        catch (RemoteNotFoundException) { return null; }
        catch (RemotePermanentException) { return null; }
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(res, IdFromUrl(url), ct);
        return await res.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static string IdFromUrl(string url)
    {
        var path = url.Split('?')[0].Split('/');
        var i = Array.IndexOf(path, "items");
        return i >= 0 && i + 1 < path.Length ? path[i + 1] : "";
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption completion, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, completion, ct); }
        catch (HttpRequestException ex) { throw new RemoteTransientException("Không kết nối được máy chủ BlueDrive.", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new RemoteTransientException("Máy chủ BlueDrive không phản hồi.", ex); }
    }

    private async Task<T> Idempotent<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try { return await _idempotent.ExecuteAsync(async t => await action(t), ct); }
        catch (BrokenCircuitException ex) { throw new RemoteTransientException("Máy chủ BlueDrive đang gặp sự cố, sẽ thử lại sau.", ex); }
    }

    private async Task EnsureAsync(HttpResponseMessage res, string subject, CancellationToken ct, string? name = null)
    {
        if (res.IsSuccessStatusCode || (int)res.StatusCode is >= 300 and < 400) return;
        var text = res.Content is null ? "" : await res.Content.ReadAsStringAsync(ct);
        string? detail = null;
        RemoteItem? current = null;
        if (text.StartsWith('{'))
        {
            try
            {
                var doc = JsonDocument.Parse(text).RootElement;
                if (doc.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) detail = d.GetString();
                if (doc.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.Object) current = Map(c.Deserialize<ItemDto>(Json)!);
            }
            catch (JsonException) { }
        }
        else if (text.Length is > 0 and < 500) detail = text;
        throw Error((int)res.StatusCode, detail, subject, name, current);
    }

    private static Exception Error(int status, string? detail, string subject, string? name, RemoteItem? current) => status switch
    {
        409 => new RemoteNameConflictException(name ?? subject),
        412 => new RemotePreconditionFailedException(current),
        404 => new RemoteNotFoundException(subject),
        410 => new RemoteCursorExpiredException(),
        401 => new SignInRequiredException(detail ?? "Cần đăng nhập lại."),
        408 or 429 or >= 500 or 0 => new RemoteTransientException(detail ?? $"Máy chủ tạm thời lỗi ({status})."),
        _ => new RemotePermanentException(detail ?? $"Máy chủ từ chối thao tác ({status}).", status),
    };
}
