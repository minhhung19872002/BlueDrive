using BlueDrive.Application.Features.Search;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

/// <summary>Tìm kiếm nâng cao toàn hệ thống (HSMT #20, #32, #50–52): có dấu/không dấu, lọc theo loại, danh mục, đơn vị đăng tải, thời gian...</summary>
[Route("api/v1/search")]
public class SearchController : ApiControllerBase
{
    /// <summary>Tìm kiếm (POST để truyền nhiều bộ lọc). Chỉ trả về tư liệu người dùng có quyền xem.</summary>
    [HttpPost]
    public Task<SearchResult> Search(SearchCriteria criteria) => Mediator.Send(new SearchFilesQuery(criteria));

    /// <summary>Tìm nhanh bằng query string (ô tìm kiếm toàn cục, ứng dụng di động).</summary>
    [HttpGet]
    public Task<SearchResult> Quick([FromQuery] string? q, [FromQuery] Guid? workspaceId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Mediator.Send(new SearchFilesQuery(new SearchCriteria { Q = q, WorkspaceId = workspaceId, Page = page, PageSize = pageSize }));

    [HttpGet("saved")]
    public Task<IReadOnlyList<SavedSearchDto>> Saved() => Mediator.Send(new ListSavedSearchesQuery());

    /// <summary>Lưu bộ lọc (cùng tên sẽ ghi đè).</summary>
    [HttpPost("saved")]
    public async Task<IActionResult> Save(SaveSearchCommand cmd) => Ok(new { id = await Mediator.Send(cmd) });

    [HttpDelete("saved/{id:guid}")]
    public async Task<IActionResult> DeleteSaved(Guid id) { await Mediator.Send(new DeleteSavedSearchCommand(id)); return NoContent(); }
}
