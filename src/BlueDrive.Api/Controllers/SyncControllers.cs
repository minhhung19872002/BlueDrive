using System.Text.Json;
using BlueDrive.Api.Infrastructure;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Sharing;
using BlueDrive.Application.Features.Sync;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlueDrive.Api.Controllers;

/// <summary>Đồng bộ BlueDrive Desktop: thiết bị, thư mục gốc, ảnh chụp cây, nhật ký thay đổi, nội dung, thao tác hàng loạt.</summary>
[Route("api/v1/sync")]
public class SyncController : ApiControllerBase
{
    /// <summary>Cập nhật đăng ký của thiết bị đang gọi (tên máy, hệ điều hành, phiên bản ứng dụng).</summary>
    [HttpPost("devices")]
    public Task<DeviceDto> RegisterDevice(RegisterDeviceCommand cmd) => Mediator.Send(cmd);

    /// <summary>Trạng thái thiết bị (thu hồi / lệnh xóa dữ liệu từ xa đã ký / yêu cầu cập nhật ứng dụng).</summary>
    [HttpGet("devices/current")]
    public Task<DeviceStatusDto> CurrentDevice([FromQuery] string? appVersion) => Mediator.Send(new GetDeviceStatusQuery(appVersion));

    /// <summary>Ứng dụng báo đã xóa xong dữ liệu theo lệnh xóa từ xa.</summary>
    [HttpPost("devices/current/wiped")]
    public async Task<IActionResult> Wiped() { await Mediator.Send(new ConfirmWipedCommand()); return NoContent(); }

    [HttpGet("policy")]
    public Task<SyncPolicyDto> Policy() => Mediator.Send(new GetSyncPolicyQuery());

    [HttpGet("roots")]
    public Task<IReadOnlyList<SyncRootDto>> Roots() => Mediator.Send(new ListSyncRootsQuery());

    /// <summary>Liệt kê toàn bộ cây của một thư mục gốc (1000 mục/trang), kèm cursor tại thời điểm bắt đầu.</summary>
    [HttpGet("snapshot")]
    public Task<SyncSnapshotDto> Snapshot([FromQuery(Name = "root_id")] string rootId, [FromQuery(Name = "page_token")] string? pageToken,
        [FromQuery(Name = "folder_id")] Guid? folderId) => Mediator.Send(new SyncSnapshotQuery(rootId, pageToken, folderId));

    /// <summary>Các thay đổi sau cursor mà người dùng được phép thấy; 410 khi cursor quá cũ (cần đồng bộ lại toàn bộ).</summary>
    [HttpGet("changes")]
    public Task<SyncChangesDto> Changes([FromQuery] long cursor, [FromQuery] int limit = 500) => Mediator.Send(new SyncChangesQuery(cursor, limit));

    [HttpGet("items/{id:guid}")]
    public async Task<SyncItemDto> Item(Guid id)
    {
        var item = await Mediator.Send(new GetSyncItemQuery(id));
        Response.Headers.ETag = item.ETag;
        return item;
    }

    /// <summary>Nội dung tệp: chuyển hướng tới URL ký ngắn hạn, hỗ trợ HTTP Range (tải tiếp, nạp từng đoạn).</summary>
    [HttpGet("items/{id:guid}/content")]
    public async Task<IActionResult> Content(Guid id, [FromQuery] Guid? version)
    {
        var url = await Mediator.Send(new GetSyncContentUrlQuery(id, version));
        return Redirect(url.Url);
    }

    [HttpGet("items/{id:guid}/web-url")]
    public Task<SyncWebUrlDto> WebUrl(Guid id) => Mediator.Send(new GetSyncWebUrlQuery(id));

    [HttpPost("items/{id:guid}/share-link")]
    public Task<ShareLinkDto> ShareLink(Guid id, ShareLinkBody body) =>
        Mediator.Send(new CreateSyncShareLinkCommand(id, body.AllowDownload, body.Password, body.ExpiresAt, body.RequireLogin));

    /// <summary>Nhiều thao tác trong một yêu cầu (tạo cây thư mục, di chuyển, đổi tên, xóa) — kết quả theo từng thao tác.</summary>
    [HttpPost("batch")]
    public Task<IReadOnlyList<SyncBatchResult>> Batch(SyncBatchCommand cmd) => Mediator.Send(cmd);

    public sealed record ShareLinkBody(bool AllowDownload = true, string? Password = null, DateTime? ExpiresAt = null, bool RequireLogin = false);
}

/// <summary>Thiết bị đã kết nối: của tôi, hoặc toàn hệ thống (quản trị).</summary>
[Route("api/v1/devices")]
public class DevicesController : ApiControllerBase
{
    [HttpGet("mine")]
    public Task<IReadOnlyList<DeviceDto>> Mine() => Mediator.Send(new ListMyDevicesQuery());

    [HttpGet, HasPermission(Perm.DeviceView)]
    public Task<PagedResult<DeviceDto>> All([FromQuery] string? query, [FromQuery] DeviceStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Mediator.Send(new ListDevicesQuery(query, status, page, pageSize));

    /// <summary>Thu hồi: dừng đồng bộ và đăng xuất ứng dụng trên máy đó.</summary>
    [HttpPost("{id:guid}/revoke")]
    public Task<DeviceDto> Revoke(Guid id) => Mediator.Send(new DeviceActionCommand(id, false));

    /// <summary>Xóa dữ liệu từ xa: ứng dụng xóa thư mục đồng bộ, bộ nhớ đệm và thông tin đăng nhập ở lần kết nối tới.</summary>
    [HttpPost("{id:guid}/wipe")]
    public Task<DeviceDto> Wipe(Guid id) => Mediator.Send(new DeviceActionCommand(id, true));
}

/// <summary>Đăng nhập BlueDrive Desktop: OAuth 2.0 Authorization Code + PKCE với địa chỉ quay lại loopback.</summary>
[Route("api/v1/connect")]
public class ConnectController : ApiControllerBase
{
    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Gọi từ trang web /connect/authorize sau khi người dùng đã đăng nhập và đồng ý.</summary>
    [HttpPost("authorize")]
    public Task<AuthorizeDesktopResult> Authorize(AuthorizeDesktopCommand cmd) => Mediator.Send(cmd);

    /// <summary>Đổi mã lấy token (grant_type=authorization_code) hoặc làm mới (grant_type=refresh_token). Nhận form hoặc JSON.</summary>
    [HttpPost("token"), AllowAnonymous, EnableRateLimiting(SecuritySetup.RefreshRateLimit)]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    public async Task<IActionResult> Token()
    {
        Dictionary<string, string?> p;
        if (Request.HasFormContentType)
            p = (await Request.ReadFormAsync()).ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());
        else
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body);
            p = doc.RootElement.EnumerateObject().ToDictionary(e => e.Name, e => e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() : e.Value.ToString());
        }
        string? V(string key) => p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        try
        {
            var r = await Mediator.Send(new DesktopTokenCommand(V("grant_type") ?? "", V("code"), V("code_verifier"), V("redirect_uri"), V("client_id"),
                V("refresh_token"), V("device_name"), V("device_os"), V("app_version"), V("machine_id")));
            Response.Headers.CacheControl = "no-store";
            return new JsonResult(r, SnakeCase);
        }
        catch (OAuthException ex)
        {
            return new JsonResult(new { error = ex.Error, error_description = ex.Message }, SnakeCase) { StatusCode = 400 };
        }
        catch (ForbiddenException ex)
        {
            return new JsonResult(new { error = "access_denied", error_description = ex.Message }, SnakeCase) { StatusCode = 400 };
        }
    }
}
