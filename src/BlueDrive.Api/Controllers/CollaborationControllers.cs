using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Comments;
using BlueDrive.Application.Features.Office;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

/// <summary>Soạn thảo trực tuyến (ONLYOFFICE) và tạo mới tài liệu (HSMT #21, #33, #41, #42).</summary>
[Route("api/v1/office")]
public class OfficeController : ApiControllerBase
{
    /// <summary>Cấu hình mở trình soạn thảo cho phiên bản hiện tại (chế độ sửa nếu có quyền; nhiều người đồng soạn thảo).</summary>
    /// <param name="fileId">Mã tệp.</param>
    /// <param name="view">true: luôn mở ở chế độ chỉ xem.</param>
    /// <param name="mobile">true: giao diện ONLYOFFICE cho điện thoại (ứng dụng di động).</param>
    [HttpGet("config/{fileId:guid}")]
    public Task<OfficeEditorConfig> Config(Guid fileId, [FromQuery] bool view = false, [FromQuery] bool mobile = false) => Mediator.Send(new GetOfficeConfigQuery(fileId, view, mobile));

    /// <summary>Cấu hình chỉ xem cho trang liên kết công khai.</summary>
    [HttpGet("public-config/{token}"), AllowAnonymous]
    public Task<OfficeEditorConfig> PublicConfig(string token, [FromQuery] string access, [FromQuery] Guid? fileId) => Mediator.Send(new GetPublicOfficeConfigQuery(token, access, fileId));

    /// <summary>Tạo tài liệu Word/Excel/PowerPoint/văn bản mới từ mẫu trống.</summary>
    [HttpPost("new")]
    public Task<UploadResult> New(CreateDocumentCommand cmd) => Mediator.Send(cmd);
}

/// <summary>Callback của ONLYOFFICE Document Server khi lưu (xác thực bằng JWT trong nội dung).</summary>
[ApiController]
[AllowAnonymous]
[Route("api/office/callback")]
public class OfficeCallbackController(MediatR.ISender mediator) : ControllerBase
{
    [HttpPost("{fileId:guid}")]
    public async Task<IActionResult> Callback(Guid fileId)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        var error = await mediator.Send(new OfficeCallbackCommand(fileId, body, Request.Headers.Authorization.ToString()));
        return Ok(new { error });
    }
}

/// <summary>Quản lý phiên bản (HSMT #26, #37, #43).</summary>
[Route("api/v1/files/{fileId:guid}/versions")]
public class VersionsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<FileVersionDto>> List(Guid fileId) => Mediator.Send(new ListVersionsQuery(fileId));

    /// <summary>Khôi phục phiên bản cũ thành phiên bản hiện tại (tạo phiên bản mới).</summary>
    [HttpPost("{versionId:guid}/restore")]
    public Task<FileVersionDto> Restore(Guid fileId, Guid versionId) => Mediator.Send(new RestoreVersionCommand(fileId, versionId));
}

/// <summary>Thảo luận trong màn hình chi tiết tài liệu (HSMT #27, #38).</summary>
[Route("api/v1")]
public class CommentsController : ApiControllerBase
{
    [HttpGet("files/{fileId:guid}/comments")]
    public Task<IReadOnlyList<CommentDto>> List(Guid fileId) => Mediator.Send(new ListCommentsQuery(fileId));

    [HttpPost("files/{fileId:guid}/comments")]
    public Task<CommentDto> Add(Guid fileId, AddCommentCommand cmd) => Mediator.Send(cmd with { FileId = fileId });

    [HttpPut("comments/{id:guid}")]
    public Task<CommentDto> Edit(Guid id, [FromBody] ContentBody body) => Mediator.Send(new EditCommentCommand(id, body.Content));

    [HttpDelete("comments/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) { await Mediator.Send(new DeleteCommentCommand(id)); return NoContent(); }

    [HttpGet("comments/{id:guid}/attachments/{fileId:guid}/url")]
    public Task<DownloadUrlDto> Attachment(Guid id, Guid fileId, [FromQuery] bool inline = true) => Mediator.Send(new GetAttachmentUrlQuery(id, fileId, inline));

    /// <summary>Tệp đính kèm trong các thảo luận tôi được xem (#19).</summary>
    [HttpGet("comments/attachments")]
    public Task<IReadOnlyList<AttachmentListItemDto>> MyAttachments([FromQuery] int take = 100) => Mediator.Send(new ListMyAttachmentsQuery(take));

    public sealed record ContentBody(string Content);
}

/// <summary>Tổng hợp tư liệu nhóm (#29) và lịch sử chỉnh sửa theo thành viên (#31).</summary>
[Route("api/v1/workspaces/{workspaceId:guid}")]
public class WorkspaceInsightsController : ApiControllerBase
{
    [HttpGet("summary")]
    public Task<WorkspaceSummaryDto> Summary(Guid workspaceId, [FromQuery] GetWorkspaceSummaryQuery query) => Mediator.Send(query with { WorkspaceId = workspaceId });

    [HttpGet("activity")]
    public Task<PagedResult<ActivityDto>> Activity(Guid workspaceId, [FromQuery] GetWorkspaceActivityQuery query) => Mediator.Send(query with { WorkspaceId = workspaceId });
}
