using System.IO.Compression;
using System.Security.Claims;
using BlueDrive.Api.Infrastructure;
using BlueDrive.Api.Storage;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using tusdotnet.Stores;

namespace BlueDrive.Api.Controllers;

/// <summary>Nhóm tài nguyên/không gian lưu trữ (HSMT #28, #57–60).</summary>
[Route("api/v1/workspaces")]
public class WorkspacesController : ApiControllerBase
{
    /// <summary>Các không gian của tôi (cá nhân + nhóm/đơn vị tham gia).</summary>
    [HttpGet]
    public Task<IReadOnlyList<WorkspaceDto>> Mine([FromQuery] WorkspaceType? type) => Mediator.Send(new ListMyWorkspacesQuery(type));

    /// <summary>Toàn bộ nhóm tài nguyên (màn hình Cấu hình nhóm tài nguyên).</summary>
    [HttpGet("all"), HasPermission(Perm.WorkspaceAdmin)]
    public Task<PagedResult<WorkspaceDto>> All([FromQuery] ListAllWorkspacesQuery query) => Mediator.Send(query);

    [HttpGet("{id:guid}")]
    public Task<WorkspaceDto> Get(Guid id) => Mediator.Send(new GetWorkspaceQuery(id));

    [HttpPost, HasPermission(Perm.WorkspaceCreate)]
    public async Task<IActionResult> Create(SaveWorkspaceCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, SaveWorkspaceCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = id }) });

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new DeleteWorkspaceCommand(id));
        return NoContent();
    }

    /// <summary>Khóa nhóm (chỉ xem, không ghi được).</summary>
    [HttpPost("{id:guid}/lock")]
    public async Task<IActionResult> Lock(Guid id) { await Mediator.Send(new SetWorkspaceLockCommand(id, true)); return NoContent(); }

    [HttpPost("{id:guid}/unlock")]
    public async Task<IActionResult> Unlock(Guid id) { await Mediator.Send(new SetWorkspaceLockCommand(id, false)); return NoContent(); }

    [HttpGet("{id:guid}/members")]
    public Task<IReadOnlyList<WorkspaceMemberDto>> Members(Guid id) => Mediator.Send(new ListWorkspaceMembersQuery(id));

    /// <summary>Thêm thành viên hoặc đổi vai trò thành viên.</summary>
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> SetMembers(Guid id, SetWorkspaceMembersCommand cmd) => Ok(new { updated = await Mediator.Send(cmd with { WorkspaceId = id }) });

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId) { await Mediator.Send(new RemoveWorkspaceMemberCommand(id, userId)); return NoContent(); }

    [HttpPost("{id:guid}/transfer-owner")]
    public async Task<IActionResult> TransferOwner(Guid id, TransferWorkspaceOwnerCommand cmd) { await Mediator.Send(cmd with { WorkspaceId = id }); return NoContent(); }

    /// <summary>Cấu hình dung lượng nhóm (#60).</summary>
    [HttpPut("{id:guid}/quota"), HasPermission(Perm.WorkspaceAdmin)]
    public async Task<IActionResult> SetQuota(Guid id, [FromBody] QuotaBody body) { await Mediator.Send(new SetWorkspaceQuotaCommand(id, body.QuotaGb)); return NoContent(); }

    /// <summary>Danh sách thư mục/tệp trong thư mục (folderId trống = gốc).</summary>
    [HttpGet("{id:guid}/items")]
    public Task<PagedResult<DriveItemDto>> Items(Guid id, [FromQuery] ListItemsQuery query) => Mediator.Send(query with { WorkspaceId = id });

    /// <summary>Thông tin thư mục hiện tại: đường dẫn, quyền.</summary>
    [HttpGet("{id:guid}/folder-info")]
    public Task<FolderInfoDto> FolderInfo(Guid id, [FromQuery] Guid? folderId) => Mediator.Send(new GetFolderInfoQuery(id, folderId));

    /// <summary>Cây thư mục (#82).</summary>
    [HttpGet("{id:guid}/folder-tree")]
    public Task<IReadOnlyList<FolderTreeNode>> FolderTree(Guid id) => Mediator.Send(new GetFolderTreeQuery(id));

    /// <summary>Thùng rác của không gian (#61).</summary>
    [HttpGet("{id:guid}/trash"), HasPermission(Perm.TrashView)]
    public Task<PagedResult<TrashItemDto>> Trash(Guid id, [FromQuery] ListTrashQuery query) => Mediator.Send(query with { WorkspaceId = id });

    /// <summary>Dọn sạch thùng rác.</summary>
    [HttpPost("{id:guid}/trash/empty"), HasPermission(Perm.TrashPurge)]
    public async Task<IActionResult> EmptyTrash(Guid id) => Ok(new { purged = await Mediator.Send(new PurgeItemsCommand(null, id)) });

    public sealed record QuotaBody(double QuotaGb);
}

/// <summary>Cấu hình dung lượng cá nhân (#59).</summary>
[Route("api/v1/quotas")]
public class QuotasController : ApiControllerBase
{
    [HttpPut("personal"), HasPermission(Perm.WorkspaceAdmin)]
    public async Task<IActionResult> SetPersonal(SetPersonalQuotaCommand cmd) => Ok(new { updated = await Mediator.Send(cmd) });
}

/// <summary>Thư mục và tệp (HSMT #18–24, #39–40, #44–48).</summary>
[Route("api/v1")]
public class DriveController : ApiControllerBase
{
    [HttpPost("folders")]
    public Task<DriveItemDto> CreateFolder(CreateFolderCommand cmd) => Mediator.Send(cmd);

    /// <summary>Đổi tên thư mục / bật "cần phê duyệt".</summary>
    [HttpPut("folders/{id:guid}")]
    public async Task<IActionResult> UpdateFolder(Guid id, UpdateFolderCommand cmd) { await Mediator.Send(cmd with { Id = id }); return NoContent(); }

    /// <summary>Chi tiết tệp (panel thông tin).</summary>
    [HttpGet("files/{id:guid}")]
    public Task<FileDetailDto> File(Guid id) => Mediator.Send(new GetFileDetailQuery(id));

    [HttpPut("files/{id:guid}/name")]
    public async Task<IActionResult> RenameFile(Guid id, [FromBody] NameBody body) { await Mediator.Send(new RenameFileCommand(id, body.Name)); return NoContent(); }

    /// <summary>Cập nhật tiêu đề, mô tả, thẻ, danh mục (một hoặc nhiều tệp).</summary>
    [HttpPut("files/metadata")]
    public async Task<IActionResult> Metadata(UpdateFileMetadataCommand cmd) => Ok(new { updated = await Mediator.Send(cmd) });

    [HttpPost("items/move")]
    public async Task<IActionResult> Move(MoveItemsCommand cmd) => Ok(new { moved = await Mediator.Send(cmd) });

    [HttpPost("items/copy")]
    public async Task<IActionResult> Copy(CopyItemsCommand cmd) => Ok(new { copied = await Mediator.Send(cmd) });

    /// <summary>Xóa (chuyển vào thùng rác).</summary>
    [HttpPost("items/delete")]
    public async Task<IActionResult> Delete(DeleteItemsCommand cmd) => Ok(new { deleted = await Mediator.Send(cmd) });

    [HttpPost("trash/restore"), HasPermission(Perm.TrashRestore)]
    public async Task<IActionResult> Restore(RestoreItemsCommand cmd) => Ok(new { restored = await Mediator.Send(cmd) });

    /// <summary>Xóa vĩnh viễn các mục được chọn.</summary>
    [HttpPost("trash/purge"), HasPermission(Perm.TrashPurge)]
    public async Task<IActionResult> Purge([FromBody] IReadOnlyList<ItemRef> items) => Ok(new { purged = await Mediator.Send(new PurgeItemsCommand(items, null)) });

    [HttpGet("recent")]
    public Task<IReadOnlyList<DriveItemDto>> Recent([FromQuery] int take = 50) => Mediator.Send(new ListRecentQuery(take));

    [HttpGet("favorites")]
    public Task<IReadOnlyList<DriveItemDto>> Favorites() => Mediator.Send(new ListStarredQuery());

    /// <summary>Đánh dấu / bỏ đánh dấu quan trọng.</summary>
    [HttpPut("favorites")]
    public async Task<IActionResult> SetFavorite(SetFavoriteCommand cmd) { await Mediator.Send(cmd); return NoContent(); }

    /// <summary>URL ký ngắn hạn để xem (inline=true) hoặc tải xuống tệp; hỗ trợ HTTP Range.</summary>
    [HttpGet("files/{id:guid}/download-url")]
    public Task<DownloadUrlDto> DownloadUrl(Guid id, [FromQuery] Guid? versionId, [FromQuery] bool inline = false) => Mediator.Send(new GetDownloadUrlQuery(id, versionId, inline));

    /// <summary>Tải nhiều tệp/thư mục dạng zip (nén trực tiếp khi truyền).</summary>
    [HttpPost("downloads/zip")]
    public Task<DownloadUrlDto> Zip(GetZipUrlCommand cmd) => Mediator.Send(cmd);

    /// <summary>Tải lên nhanh qua multipart (tệp nhỏ, ứng dụng di động). Tệp lớn dùng tus tại /tus.</summary>
    [HttpPost("files/upload"), RequestSizeLimit(512L * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 512L * 1024 * 1024)]
    public async Task<UploadResult> Upload(IFormFile file, [FromForm] Guid workspaceId, [FromForm] Guid? folderId, [FromForm] Guid? targetFileId,
        [FromForm] string? title, [FromForm] string? description, [FromForm] string? tags, [FromForm] string? relativePath, [FromForm] string? changeNote,
        [FromServices] Microsoft.Extensions.Options.IOptions<BlueDrive.Infrastructure.Storage.StorageOptions> storage)
    {
        var meta = new UploadMetadata(file.FileName, workspaceId, folderId, relativePath, targetFileId, title, description,
            tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), null, changeNote);
        await Mediator.Send(new ValidateUploadCommand(meta, file.Length));
        Directory.CreateDirectory(storage.Value.TempDirectory);
        var tmp = Path.Combine(storage.Value.TempDirectory, "mp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = System.IO.File.Create(tmp)) await file.CopyToAsync(fs);
            return await Mediator.Send(new CompleteUploadCommand(tmp, meta));
        }
        finally { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
    }

    public sealed record NameBody(string Name);
}

/// <summary>Hoàn tất phiên tải lên tus (tạo tệp/phiên bản).</summary>
[Route("api/v1/uploads")]
public class UploadsController : ApiControllerBase
{
    [HttpPost("{tusId}/complete")]
    public async Task<UploadResult> Complete(string tusId, UploadMetadata meta)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(tusId, "^[a-zA-Z0-9]{8,64}$")) throw new NotFoundException("Không tìm thấy phiên tải lên.");
        var dir = TusSetup.TusDirectory(HttpContext.RequestServices);
        var ownerFile = Path.Combine(dir, tusId + ".owner");
        if (!System.IO.File.Exists(ownerFile) || (await System.IO.File.ReadAllTextAsync(ownerFile)).Trim() != User.FindFirstValue("sub"))
            throw new NotFoundException("Không tìm thấy phiên tải lên.");
        var store = new TusDiskStore(dir);
        var length = await store.GetUploadLengthAsync(tusId, HttpContext.RequestAborted);
        var offset = await store.GetUploadOffsetAsync(tusId, HttpContext.RequestAborted);
        if (length is null || offset != length) throw new AppException("Tệp chưa được tải lên đầy đủ.", 409);
        try
        {
            return await Mediator.Send(new CompleteUploadCommand(Path.Combine(dir, tusId), meta));
        }
        finally
        {
            await store.DeleteFileAsync(tusId, CancellationToken.None);
            System.IO.File.Delete(ownerFile);
        }
    }
}

/// <summary>Truyền nội dung tệp theo URL ký (hỗ trợ Range để tua video), hoặc zip nhiều mục.</summary>
[Route("api/v1/content")]
[AllowAnonymous]
[EnableRateLimiting(SecuritySetup.PublicRateLimit)]
public class ContentController(IContentTicketService tickets, IFileContentService content, IResourceAuthorizationService auth, IAppDbContext db) : ApiControllerBase
{
    [HttpGet("{token}")]
    public async Task Get(string token)
    {
        var ticket = tickets.Read(token);
        if (ticket is null)
        {
            Response.StatusCode = 410;
            await Response.WriteAsJsonAsync(ProblemFactory.Create(410, "Liên kết đã hết hạn hoặc không hợp lệ. Vui lòng mở lại tài liệu."));
            return;
        }
        if (ticket.Kind == "zip")
        {
            await StreamZipAsync(ticket);
            return;
        }
        // Access is re-checked on every request (revoked shares stop working immediately).
        var level = ticket.Inline ? AccessLevel.View : AccessLevel.Download;
        if (!await auth.CanAsync(ticket.UserId, ResourceType.File, ticket.FileId!.Value, level, HttpContext.RequestAborted))
        {
            Response.StatusCode = 403;
            await Response.WriteAsJsonAsync(ProblemFactory.Create(403, "Bạn không còn quyền truy cập tài liệu này."));
            return;
        }
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == ticket.FileId);
        var version = await db.FileVersions.AsNoTracking().FirstAsync(v => v.Id == ticket.VersionId);
        await ContentStreaming.WriteFileAsync(HttpContext, content, version, file.Name, file.MimeType, ticket.Inline);
    }

    private async Task StreamZipAsync(ContentTicket ticket)
    {
        var entries = await Mediator.Send(new ResolveZipEntriesQuery(ticket.UserId, ticket.Items ?? []));
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = ticket.ZipName ?? "BlueDrive.zip" }.ToString();
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await using var zip = new ZipArchive(Response.BodyWriter.AsStream(), ZipArchiveMode.Create, leaveOpen: true, System.Text.Encoding.UTF8);
        foreach (var e in entries)
        {
            var v = await db.FileVersions.AsNoTracking().FirstAsync(x => x.Id == e.VersionId);
            var entry = zip.CreateEntry(e.EntryPath, CompressionLevel.Fastest);
            entry.LastWriteTime = e.ModifiedAt.AddHours(7);
            await using var src = await content.OpenReadAsync(v, HttpContext.RequestAborted);
            await using var dst = entry.Open();
            await src.CopyToAsync(dst, 1 << 16, HttpContext.RequestAborted);
        }
    }
}

public static class ContentStreaming
{
    private static readonly HashSet<string> ActiveTypes = new(StringComparer.OrdinalIgnoreCase) { "image/svg+xml", "text/html", "application/xhtml+xml", "text/xml" };

    /// <summary>Range-enabled streaming of a (possibly encrypted) version without loading it into memory.</summary>
    public static async Task WriteFileAsync(HttpContext ctx, IFileContentService content, Domain.Entities.FileVersion version, string fileName, string mime, bool inline)
    {
        var stream = await content.OpenReadAsync(version, ctx.RequestAborted);
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment") { FileNameStar = fileName };
        ctx.Response.Headers.ContentDisposition = disposition.ToString();
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Never let user content run scripts in our origin (SVG/HTML).
        if (ActiveTypes.Contains(mime)) ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'";
        var result = Results.File(stream, mime, enableRangeProcessing: stream.CanSeek, lastModified: version.CreatedAt);
        await result.ExecuteAsync(ctx);
    }
}
