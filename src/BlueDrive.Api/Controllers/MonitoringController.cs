using System.Text.Json;
using BlueDrive.Api.Infrastructure;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

/// <summary>Nhật ký hoạt động (HSMT #11, #12).</summary>
[Route("api/v1/audit-logs")]
public class AuditLogsController : ApiControllerBase
{
    /// <summary>Tra cứu nhật ký: người dùng, đơn vị, thời gian, hành động, loại tài nguyên, tên tư liệu, IP (phân trang server).</summary>
    [HttpPost("search"), HasPermission(Perm.AuditView)]
    public Task<PagedResult<AuditLogDto>> Search([FromBody] AuditLogFilter filter, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Mediator.Send(new SearchAuditLogsQuery(filter, page, pageSize));

    /// <summary>Kiểm tra toàn vẹn chuỗi hash của nhật ký (phát hiện sửa đổi trái phép).</summary>
    [HttpGet("verify"), HasPermission(Perm.AuditAdmin)]
    public Task<AuditChainReport> Verify([FromQuery] long? fromId) => Mediator.Send(new VerifyAuditChainQuery(fromId));
}

/// <summary>Thống kê dung lượng (HSMT #13).</summary>
[Route("api/v1/storage-stats")]
public class StorageStatsController : ApiControllerBase
{
    /// <summary>by = user | organization | workspace.</summary>
    [HttpGet, HasPermission(Perm.StatsView)]
    public Task<PagedResult<UsageRow>> Get([FromQuery] string by = "user", [FromQuery] Guid? organizationId = null, [FromQuery] string? q = null,
        [FromQuery] string? sort = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Mediator.Send(new StorageStatsQuery(by, organizationId, q, sort, page, pageSize));

    [HttpGet("totals"), HasPermission(Perm.StatsView)]
    public Task<StorageTotals> Totals() => Mediator.Send(new StorageTotalsQuery());
}

/// <summary>Cảnh báo bất thường (HSMT #14).</summary>
[Route("api/v1/alerts")]
public class AlertsController : ApiControllerBase
{
    [HttpPost("search"), HasPermission(Perm.AlertView)]
    public Task<PagedResult<AlertDto>> Search([FromBody] AlertFilter filter, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Mediator.Send(new ListAlertsQuery(filter, page, pageSize));

    [HttpGet("unacknowledged-count"), HasPermission(Perm.AlertView)]
    public async Task<IActionResult> Count() => Ok(new { count = await Mediator.Send(new UnacknowledgedAlertCountQuery()) });

    /// <summary>Xác nhận đã xử lý.</summary>
    [HttpPost("acknowledge"), HasPermission(Perm.AlertEdit)]
    public async Task<IActionResult> Acknowledge([FromBody] IReadOnlyList<Guid> ids) => Ok(new { updated = await Mediator.Send(new AcknowledgeAlertsCommand(ids)) });
}

/// <summary>Báo cáo giám sát xuất Excel/PDF chạy nền (HSMT #15).</summary>
[Route("api/v1/reports")]
public class ReportsController : ApiControllerBase
{
    /// <summary>Tạo báo cáo (audit_logs | storage_users | storage_organizations | storage_workspaces | alerts); trả về ngay, xử lý nền.</summary>
    [HttpPost("export")]
    public async Task<ReportJobDto> Export([FromBody] ExportBody body, [FromServices] IPermissionService perms)
    {
        var required = body.ReportType switch
        {
            ReportTypes.AuditLogs => Perm.AuditView,
            ReportTypes.Alerts => Perm.AlertView,
            _ => Perm.StatsView,
        };
        var uid = Guid.Parse(User.FindFirst("sub")!.Value);
        if (!await perms.HasAsync(uid, required)) throw new ForbiddenException();
        return await Mediator.Send(new CreateReportCommand(body.ReportType, body.Format, body.Parameters));
    }

    [HttpGet]
    public Task<IReadOnlyList<ReportJobDto>> Mine() => Mediator.Send(new ListMyReportsQuery());

    [HttpGet("{id:guid}")]
    public Task<ReportJobDto> Get(Guid id) => Mediator.Send(new GetReportJobQuery(id));

    [HttpGet("{id:guid}/download-url")]
    public Task<DownloadUrlDto> Download(Guid id) => Mediator.Send(new GetReportDownloadUrlQuery(id));

    public sealed record ExportBody(string ReportType, string Format, JsonElement? Parameters);
}
