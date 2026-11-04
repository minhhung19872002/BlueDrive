using BlueDrive.Api.Infrastructure;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Features.Operations;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

/// <summary>Sao lưu &amp; phục hồi (yêu cầu chất lượng 3.5).</summary>
[Route("api/v1/backups")]
public class BackupsController : ApiControllerBase
{
    [HttpGet, HasPermission(Perm.BackupView)]
    public Task<PagedResult<BackupJobDto>> List([FromQuery] ListBackupsQuery query) => Mediator.Send(query);

    [HttpGet("schedule"), HasPermission(Perm.BackupView)]
    public Task<BackupScheduleDto> Schedule() => Mediator.Send(new GetBackupScheduleQuery());

    /// <summary>Cấu hình lịch tự động (cron), số bản giữ lại, ngày sao lưu đầy đủ, sao chép lên S3.</summary>
    [HttpPut("schedule"), HasPermission(Perm.BackupAdmin)]
    public async Task<IActionResult> UpdateSchedule(UpdateBackupScheduleCommand cmd) { await Mediator.Send(cmd); return NoContent(); }

    /// <summary>Sao lưu ngay (đột xuất).</summary>
    [HttpPost, HasPermission(Perm.BackupAdmin)]
    public Task<BackupJobDto> Start([FromQuery] bool full = false) => Mediator.Send(new StartBackupCommand(full));

    /// <summary>Tải phần sao lưu: part = db | files | manifest.</summary>
    [HttpGet("{id:guid}/download-url"), HasPermission(Perm.BackupAdmin)]
    public Task<DownloadUrlDto> Download(Guid id, [FromQuery] string part = "db") => Mediator.Send(new GetBackupDownloadUrlQuery(id, part));
}

/// <summary>Xuất toàn bộ dữ liệu bàn giao (yêu cầu chất lượng 4.2).</summary>
[Route("api/v1/data-export")]
public class DataExportController : ApiControllerBase
{
    [HttpPost, HasPermission(Perm.DataExportAdmin)]
    public Task<ReportJobDto> Start([FromBody] StartDataExportCommand? cmd) => Mediator.Send(cmd ?? new StartDataExportCommand(false, true));
}

/// <summary>Tiếp nhận và xử lý yêu cầu hỗ trợ/sự cố, SLA (yêu cầu chất lượng 5.1).</summary>
[Route("api/v1/tickets")]
public class TicketsController : ApiControllerBase
{
    /// <summary>Người dùng thấy yêu cầu của mình; người có quyền ticket.view thấy toàn bộ.</summary>
    [HttpPost("search")]
    public Task<PagedResult<TicketDto>> Search([FromBody] TicketFilter filter, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? q = null)
        => Mediator.Send(new ListTicketsQuery(filter) { Page = page, PageSize = pageSize, Q = q });

    [HttpGet("{id:guid}")]
    public Task<TicketDto> Get(Guid id) => Mediator.Send(new GetTicketQuery(id));

    [HttpPost, HasPermission(Perm.TicketCreate)]
    public Task<TicketDto> Create(CreateTicketCommand cmd) => Mediator.Send(cmd);

    [HttpPut("{id:guid}")]
    public Task<TicketDto> Update(Guid id, UpdateTicketCommand cmd) => Mediator.Send(cmd with { Id = id });

    [HttpGet("sla"), HasPermission(Perm.TicketView)]
    public Task<SlaSummaryDto> Sla([FromQuery] DateTime? from, [FromQuery] DateTime? to) => Mediator.Send(new GetSlaSummaryQuery(from, to));

    [HttpPost("sla/export"), HasPermission(Perm.TicketView)]
    public async Task<IActionResult> ExportSla([FromBody] TicketFilter filter, [FromQuery] string format = "xlsx")
        => FileResult(await Mediator.Send(new ExportSlaReportQuery(filter, format, ExportedBy)));
}

/// <summary>Nhật ký thay đổi hệ thống (yêu cầu chất lượng 5.2) và trạng thái hệ thống.</summary>
[Route("api/v1/change-logs")]
public class ChangeLogsController : ApiControllerBase
{
    [HttpGet, HasPermission(Perm.ChangeLogView)]
    public Task<PagedResult<ChangeLogDto>> List([FromQuery] ListChangeLogsQuery query) => Mediator.Send(query);

    [HttpPost, HasPermission(Perm.ChangeLogEdit)]
    public async Task<IActionResult> Add(AddChangeLogCommand cmd) => Ok(new { id = await Mediator.Send(cmd) });
}

[Route("api/v1/system/status")]
public class SystemStatusController : ApiControllerBase
{
    /// <summary>Trạng thái nội bộ: CSDL, kho lưu trữ, ONLYOFFICE, ClamAV, dung lượng đĩa, sao lưu gần nhất, hàng đợi job.</summary>
    [HttpGet, HasPermission(Perm.SettingsView)]
    public Task<SystemStatusDto> Get() => Mediator.Send(new GetSystemStatusQuery());
}

/// <summary>Hồ sơ sự cố an toàn thông tin (HSMT 3.4): ghi nhận, điều phối, khắc phục, kết luận, xuất hồ sơ.</summary>
[Route("api/v1/security-incidents")]
public class SecurityIncidentsController : ApiControllerBase
{
    [HttpPost("search"), HasPermission(Perm.IncidentView)]
    public Task<PagedResult<IncidentDto>> Search([FromBody] IncidentFilter filter, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? q = null)
        => Mediator.Send(new ListIncidentsQuery(filter) { Page = page, PageSize = pageSize, Q = q });

    [HttpGet("{id:guid}"), HasPermission(Perm.IncidentView)]
    public Task<IncidentDto> Get(Guid id) => Mediator.Send(new GetIncidentQuery(id));

    /// <summary>Lập hồ sơ sự cố (có thể từ một cảnh báo: truyền alertId).</summary>
    [HttpPost, HasPermission(Perm.IncidentCreate)]
    public Task<IncidentDto> Create(CreateIncidentCommand cmd) => Mediator.Send(cmd);

    /// <summary>Cập nhật xử lý; phân công và đóng hồ sơ cần quyền incident.admin.</summary>
    [HttpPut("{id:guid}"), HasPermission(Perm.IncidentEdit)]
    public Task<IncidentDto> Update(Guid id, UpdateIncidentCommand cmd) => Mediator.Send(cmd with { Id = id });

    [HttpGet("{id:guid}/export"), HasPermission(Perm.IncidentView)]
    public async Task<IActionResult> Export(Guid id, [FromQuery] string format = "pdf") => FileResult(await Mediator.Send(new ExportIncidentQuery(id, format, ExportedBy)));

    [HttpPost("export"), HasPermission(Perm.IncidentView)]
    public async Task<IActionResult> ExportList([FromBody] IncidentFilter filter, [FromQuery] string format = "xlsx")
        => FileResult(await Mediator.Send(new ExportIncidentsQuery(filter, format, ExportedBy)));
}

/// <summary>Thông báo hệ thống hiển thị cho mọi người dùng (yêu cầu chất lượng 5.2).</summary>
[Route("api/v1/announcements")]
public class AnnouncementsController : ApiControllerBase
{
    /// <summary>Thông báo đang hiệu lực — mọi người dùng đã đăng nhập.</summary>
    [HttpGet("active")]
    public Task<IReadOnlyList<AnnouncementDto>> Active() => Mediator.Send(new ActiveAnnouncementsQuery());

    [HttpGet, HasPermission(Perm.AnnouncementView)]
    public Task<PagedResult<AnnouncementDto>> List([FromQuery] ListAnnouncementsQuery query) => Mediator.Send(query);

    [HttpPost, HasPermission(Perm.AnnouncementEdit)]
    public Task<AnnouncementDto> Create(SaveAnnouncementCommand cmd) => Mediator.Send(cmd with { Id = null });

    [HttpPut("{id:guid}"), HasPermission(Perm.AnnouncementEdit)]
    public Task<AnnouncementDto> Update(Guid id, SaveAnnouncementCommand cmd) => Mediator.Send(cmd with { Id = id });

    [HttpDelete("{id:guid}"), HasPermission(Perm.AnnouncementEdit)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new DeleteAnnouncementCommand(id));
        return NoContent();
    }
}
