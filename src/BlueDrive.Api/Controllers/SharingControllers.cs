using BlueDrive.Api.Infrastructure;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Sharing;
using BlueDrive.Application.Features.Users;
using BlueDrive.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Api.Controllers;

/// <summary>Phân quyền theo thư mục/tệp/nhóm (HSMT #30, #55, #56).</summary>
[Route("api/v1/access")]
public class AccessController : ApiControllerBase
{
    /// <summary>Danh sách quyền truy cập (trực tiếp, kế thừa, thành viên, chia sẻ).</summary>
    [HttpGet("{type}/{id:guid}")]
    public Task<ResourceAccessListDto> Get(ResourceType type, Guid id) => Mediator.Send(new GetResourceAccessQuery(type, id));

    /// <summary>Thay thế quyền trực tiếp và bật/tắt ngắt kế thừa (thư mục).</summary>
    [HttpPut("{type}/{id:guid}")]
    public async Task<IActionResult> Set(ResourceType type, Guid id, SetResourceAccessCommand cmd)
    {
        await Mediator.Send(cmd with { Type = type, Id = id });
        return NoContent();
    }
}

/// <summary>Chia sẻ nội bộ (HSMT #19, #49, #53).</summary>
[Route("api/v1/shares")]
public class SharesController : ApiControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateShareCommand cmd) => Ok(new { shared = await Mediator.Send(cmd) });

    [HttpGet]
    public Task<IReadOnlyList<ShareDto>> OfResource([FromQuery] ResourceType resourceType, [FromQuery] Guid resourceId) => Mediator.Send(new ListSharesOfResourceQuery(resourceType, resourceId));

    /// <summary>Hủy chia sẻ.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) { await Mediator.Send(new DeleteShareCommand(id)); return NoContent(); }

    /// <summary>Được chia sẻ với tôi.</summary>
    [HttpGet("with-me")]
    public Task<PagedResult<SharedItemDto>> WithMe([FromQuery] ListSharedWithMeQuery query) => Mediator.Send(query);

    [HttpGet("with-me/sharers")]
    public Task<IReadOnlyList<UserLookupDto>> Sharers() => Mediator.Send(new ListSharersQuery());
}

/// <summary>Chia sẻ ra ngoài qua liên kết (HSMT #49).</summary>
[Route("api/v1/share-links")]
public class ShareLinksController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ShareLinkDto>> List([FromQuery] ResourceType resourceType, [FromQuery] Guid resourceId) => Mediator.Send(new ListShareLinksQuery(resourceType, resourceId));

    [HttpPost]
    public Task<ShareLinkDto> Create(SaveShareLinkCommand cmd) => Mediator.Send(cmd with { Id = null });

    [HttpPut("{id:guid}")]
    public Task<ShareLinkDto> Update(Guid id, SaveShareLinkCommand cmd) => Mediator.Send(cmd with { Id = id });

    /// <summary>Thu hồi liên kết.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id) { await Mediator.Send(new RevokeShareLinkCommand(id)); return NoContent(); }

    /// <summary>Mã QR của liên kết (PNG).</summary>
    [HttpGet("{id:guid}/qr")]
    public async Task<IActionResult> Qr(Guid id, [FromServices] IAppDbContext db, [FromServices] IConfiguration config)
    {
        var link = await db.ShareLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id) ?? throw new NotFoundException("Không tìm thấy liên kết chia sẻ.");
        var links = await Mediator.Send(new ListShareLinksQuery(link.ResourceType, link.ResourceId));
        var dto = links.FirstOrDefault(l => l.Id == id) ?? throw new ForbiddenException();
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(dto.Url, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        return File(png, "image/png", $"qr-{link.Id.ToString()[..8]}.png");
    }
}

/// <summary>Trang công khai /s/{token}: xem ảnh, video, PDF, Office (chỉ xem), tải xuống nếu được phép.</summary>
[Route("api/v1/public/s/{token}")]
[AllowAnonymous]
[EnableRateLimiting(SecuritySetup.PublicRateLimit)]
public class PublicShareController(IAppDbContext db, IPublicLinkTickets tickets, IFileContentService content, IAuditLogger audit, TimeProvider clock) : ApiControllerBase
{
    [HttpGet]
    public Task<PublicShareInfoDto> Info(string token) => Mediator.Send(new GetPublicShareQuery(token));

    /// <summary>Mở liên kết (kiểm tra mật khẩu/đăng nhập, tính 1 lượt truy cập).</summary>
    [HttpPost("open")]
    public Task<PublicAccessGrant> Open(string token) => Mediator.Send(new OpenPublicShareCommand(token));

    [HttpGet("items")]
    public Task<PublicFolderListing> Items(string token, [FromQuery] string access, [FromQuery] Guid? folderId) => Mediator.Send(new ListPublicShareItemsQuery(token, access, folderId));

    /// <summary>Nội dung tệp (Range). download=true chỉ khi liên kết cho phép tải xuống.</summary>
    [HttpGet("content")]
    public async Task Content(string token, [FromQuery] string access, [FromQuery] Guid? fileId, [FromQuery] bool download = false)
    {
        var link = await PublicLinks.LoadActiveAsync(db, token, clock.GetUtcNow().UtcDateTime, HttpContext.RequestAborted);
        if (tickets.Read(access) is not { } t || t.LinkId != link.Id) throw new AppException("Phiên xem liên kết đã hết hạn. Vui lòng mở lại liên kết.", 401);
        if (download && !link.AllowDownload) throw new ForbiddenException("Liên kết này không cho phép tải xuống.");
        var file = await PublicLinks.FileInScopeAsync(db, link, fileId, HttpContext.RequestAborted);
        var version = await db.FileVersions.AsNoTracking().FirstAsync(v => v.Id == file.CurrentVersionId);
        // Count downloads (not every Range chunk of a video): only full/first-range requests are logged.
        var range = Request.Headers.Range.ToString();
        if (download && (string.IsNullOrEmpty(range) || range.StartsWith("bytes=0-")))
            await audit.LogAsync(new AuditEntry(AuditAction.Download, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { shareLink = link.Id, via = "public_link" }, t.UserId));
        await ContentStreaming.WriteFileAsync(HttpContext, content, version, file.Name, file.MimeType, !download);
    }

}

/// <summary>Phê duyệt tư liệu (vai trò Phê duyệt).</summary>
[Route("api/v1/approvals")]
public class ApprovalsController : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<PendingApprovalDto>> List([FromQuery] ListPendingApprovalsQuery query) => Mediator.Send(query);

    [HttpPost("{fileId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid fileId) { await Mediator.Send(new ReviewFileCommand(fileId, true, null)); return NoContent(); }

    [HttpPost("{fileId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid fileId, [FromBody] RejectBody body) { await Mediator.Send(new ReviewFileCommand(fileId, false, body.Reason)); return NoContent(); }

    /// <summary>Gửi (lại) tư liệu để phê duyệt.</summary>
    [HttpPost("{fileId:guid}/submit")]
    public async Task<IActionResult> Submit(Guid fileId) { await Mediator.Send(new SubmitForApprovalCommand(fileId)); return NoContent(); }

    public sealed record RejectBody(string Reason);
}
