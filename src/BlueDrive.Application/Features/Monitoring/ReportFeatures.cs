using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Reporting;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Monitoring;

/// <summary>#15 monitoring reports. Large exports run in the Worker; the user gets a notification + download link.</summary>
public static class ReportTypes
{
    public const string AuditLogs = "audit_logs", StorageUsers = "storage_users", StorageOrganizations = "storage_organizations",
        StorageWorkspaces = "storage_workspaces", Alerts = "alerts";

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        [AuditLogs] = "Báo cáo nhật ký hoạt động",
        [StorageUsers] = "Báo cáo dung lượng lưu trữ theo tài khoản",
        [StorageOrganizations] = "Báo cáo dung lượng lưu trữ theo đơn vị",
        [StorageWorkspaces] = "Báo cáo dung lượng lưu trữ theo nhóm tài nguyên",
        [Alerts] = "Báo cáo cảnh báo bất thường",
        [Operations.DataExport.Type] = "Gói dữ liệu bàn giao",
    };
}

public sealed record ReportJobDto(Guid Id, string ReportType, string Title, string Format, JobStatus Status, int Progress, string? FileName, string? Error,
    DateTime CreatedAt, DateTime? FinishedAt, string Message);

public interface IReportQueue
{
    void Enqueue(Guid reportJobId);
}

public sealed record CreateReportCommand(string ReportType, string Format, JsonElement? Parameters) : IRequest<ReportJobDto>;

public sealed class CreateReportHandler(IAppDbContext db, ICurrentUser me, IReportQueue queue, TimeProvider clock) : IRequestHandler<CreateReportCommand, ReportJobDto>
{
    public async Task<ReportJobDto> Handle(CreateReportCommand cmd, CancellationToken ct)
    {
        if (!ReportTypes.Titles.ContainsKey(cmd.ReportType) || cmd.ReportType == Operations.DataExport.Type) throw new AppException("Loại báo cáo không hợp lệ.");
        var job = new ReportJob
        {
            RequestedBy = me.RequireUserId(), ReportType = cmd.ReportType, Format = ExportFormat.Normalize(cmd.Format),
            ParametersJson = cmd.Parameters?.GetRawText() ?? "{}", Status = JobStatus.Queued, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.ReportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(job.Id);
        return ReportMapping.ToDto(job);
    }
}

public static class ReportMapping
{
    public static ReportJobDto ToDto(ReportJob j) => new(j.Id, j.ReportType, ReportTypes.Titles.GetValueOrDefault(j.ReportType, j.ReportType), j.Format, j.Status, j.Progress,
        j.ResultFileName, j.Error, j.CreatedAt, j.FinishedAt, j.Status switch
        {
            JobStatus.Queued or JobStatus.Running => "Hệ thống đang xử lý báo cáo, bạn sẽ nhận thông báo khi hoàn tất.",
            JobStatus.Succeeded => "Báo cáo đã sẵn sàng để tải xuống.",
            _ => "Tạo báo cáo thất bại. Vui lòng thử lại.",
        });
}

public sealed record GetReportJobQuery(Guid Id) : IRequest<ReportJobDto>;

public sealed class GetReportJobHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<GetReportJobQuery, ReportJobDto>
{
    public async Task<ReportJobDto> Handle(GetReportJobQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var j = await db.ReportJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id && x.RequestedBy == uid, ct) ?? throw new NotFoundException("Không tìm thấy báo cáo.");
        return ReportMapping.ToDto(j);
    }
}

public sealed record ListMyReportsQuery : IRequest<IReadOnlyList<ReportJobDto>>;

public sealed class ListMyReportsHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListMyReportsQuery, IReadOnlyList<ReportJobDto>>
{
    public async Task<IReadOnlyList<ReportJobDto>> Handle(ListMyReportsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var rows = await db.ReportJobs.AsNoTracking().Where(x => x.RequestedBy == uid).OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct);
        return rows.Select(ReportMapping.ToDto).ToList();
    }
}

public sealed record GetReportDownloadUrlQuery(Guid Id) : IRequest<DownloadUrlDto>;

public sealed class GetReportDownloadUrlHandler(IAppDbContext db, ICurrentUser me, IContentTicketService tickets, TimeProvider clock) : IRequestHandler<GetReportDownloadUrlQuery, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> Handle(GetReportDownloadUrlQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var j = await db.ReportJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id && x.RequestedBy == uid, ct) ?? throw new NotFoundException("Không tìm thấy báo cáo.");
        if (j.Status != JobStatus.Succeeded || j.ResultKey is null) throw new AppException("Báo cáo chưa sẵn sàng.", 409);
        var token = tickets.Issue(new ContentTicket(uid, "report", j.Id, null, false, null, j.ResultFileName), TimeSpan.FromMinutes(30));
        return new DownloadUrlDto($"/api/v1/content/{Uri.EscapeDataString(token)}", clock.GetUtcNow().UtcDateTime.AddMinutes(30), j.ResultFileName ?? "bao-cao", "application/octet-stream", 0);
    }
}

/// <summary>Builds a report file (runs in the Worker), stores it encrypted, and notifies the requester.</summary>
public sealed record RunReportJobCommand(Guid Id) : IRequest<Unit>;

public sealed class RunReportJobHandler(IAppDbContext db, ITableExporter exporter, IFileContentService content, IUserNotifier notify, IAuditLogger audit, TimeProvider clock,
    Operations.IDataExporter dataExporter)
    : IRequestHandler<RunReportJobCommand, Unit>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) } };

    public async Task<Unit> Handle(RunReportJobCommand cmd, CancellationToken ct)
    {
        var job = await db.ReportJobs.FirstOrDefaultAsync(j => j.Id == cmd.Id, ct);
        if (job is null || job.Status is JobStatus.Succeeded) return Unit.Value;
        job.Status = JobStatus.Running;
        job.Progress = 10;
        await db.SaveChangesAsync(ct);
        var requester = await db.Users.AsNoTracking().Where(u => u.Id == job.RequestedBy).Select(u => new { u.FullName, u.Username }).FirstAsync(ct);
        try
        {
            if (job.ReportType == Operations.DataExport.Type)
            {
                var (key, name, size) = await dataExporter.ExportAsync(job, async p => { job.Progress = p; await db.SaveChangesAsync(ct); }, ct);
                job.ResultKey = Operations.DataExport.KeyPrefix + key;
                job.ResultFileName = name;
                job.Status = JobStatus.Succeeded;
                job.Progress = 100;
                job.FinishedAt = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                await notify.NotifyUserAsync(job.RequestedBy, "report", "Gói dữ liệu bàn giao đã sẵn sàng", $"{name} ({TextUtil.FormatBytes(size)})", $"/reports?id={job.Id}", ct);
                return Unit.Value;
            }
            var table = await BuildAsync(job, requester.FullName, ct);
            job.Progress = 70;
            await db.SaveChangesAsync(ct);
            var file = await exporter.ExportAsync(table, job.Format, ct);
            await using var ms = new MemoryStream(file.Content);
            var stored = await content.SaveAsync(ms, "reports", ct);
            job.ResultKey = stored.WrappedKey is null ? stored.StorageKey : $"{stored.StorageKey}#{stored.WrappedKey}";
            job.ResultFileName = file.FileName;
            job.Status = JobStatus.Succeeded;
            job.Progress = 100;
            job.FinishedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await notify.NotifyUserAsync(job.RequestedBy, "report", $"{ReportTypes.Titles[job.ReportType]} đã sẵn sàng", $"Tệp {file.FileName} ({table.Rows.Count} dòng)", $"/reports?id={job.Id}", ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: file.FileName, Detail: new { report = job.ReportType, rows = table.Rows.Count, job.Format },
                UserId: job.RequestedBy, Username: requester.Username), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            job.FinishedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
            await notify.NotifyUserAsync(job.RequestedBy, "report", "Tạo báo cáo thất bại", ReportTypes.Titles.GetValueOrDefault(job.ReportType), "/reports", CancellationToken.None);
            throw;
        }
        return Unit.Value;
    }

    private async Task<TableExport> BuildAsync(ReportJob job, string exportedBy, CancellationToken ct)
    {
        var title = ReportTypes.Titles[job.ReportType];
        switch (job.ReportType)
        {
            case ReportTypes.AuditLogs:
            {
                var f = JsonSerializer.Deserialize<AuditLogFilter>(job.ParametersJson, Json) ?? new AuditLogFilter();
                var rows = await AuditQueries.Project(db, (await AuditQueries.FilterAsync(db, f, ct)).OrderByDescending(a => a.Id).Take(200_000)).ToListAsync(ct);
                return new TableExport(title,
                    [new("Thời gian", 1.4f), new("Người dùng", 1.6f), new("Đơn vị", 1.8f), new("Hành động", 1.2f), new("Đối tượng", 2.4f), new("Nhóm", 1.4f), new("IP", 1.1f)],
                    rows.Select(r => (IReadOnlyList<string?>)[Vn(r.OccurredAt), r.FullName ?? r.Username, r.OrganizationName, AuditQueries.ActionNames.GetValueOrDefault(r.Action, r.Action.ToString()),
                        r.ResourceName, r.WorkspaceName, r.Ip]).ToList(), DescribeAudit(f), exportedBy);
            }
            case ReportTypes.Alerts:
            {
                var f = JsonSerializer.Deserialize<AlertFilter>(job.ParametersJson, Json) ?? new AlertFilter();
                var rows = (await AlertQueries.Project(db, AlertQueries.Filter(db, f)).Take(100_000).ToListAsync(ct)).Select(AlertQueries.Named).ToList();
                return new TableExport(title, [new("Thời gian", 1.3f), new("Loại", 1.5f), new("Mức độ", 0.9f), new("Nội dung", 4), new("Trạng thái", 1.6f)],
                    rows.Select(r => (IReadOnlyList<string?>)[Vn(r.CreatedAt), r.TypeName, r.Severity switch { AlertSeverity.Critical => "Nghiêm trọng", AlertSeverity.Warning => "Cảnh báo", _ => "Thông tin" },
                        r.Message, r.IsAcknowledged ? $"Đã xử lý ({r.AcknowledgedByName})" : "Chưa xử lý"]).ToList(), null, exportedBy);
            }
            default:
            {
                var by = job.ReportType switch { ReportTypes.StorageOrganizations => "organization", ReportTypes.StorageWorkspaces => "workspace", _ => "user" };
                var p = JsonSerializer.Deserialize<StorageReportParams>(job.ParametersJson, Json) ?? new StorageReportParams(null, null);
                var rows = (await StorageStats.RowsAsync(db, by, p.OrganizationId, p.Q, ct)).OrderByDescending(r => r.UsedBytes).ToList();
                var nameHeader = by switch { "organization" => "Đơn vị", "workspace" => "Nhóm tài nguyên", _ => "Người dùng" };
                return new TableExport(title, [new(nameHeader, 2.4f), new("Mã/Tài khoản", 1.3f), new("Ghi chú", 2), new("Đã dùng", 1.1f), new("Hạn mức", 1.1f), new("Còn lại", 1.1f), new("Tỷ lệ", 0.8f), new("Số tệp", 0.8f)],
                    rows.Select(r => (IReadOnlyList<string?>)[r.Name, r.Code, r.Extra, TextUtil.FormatBytes(r.UsedBytes), TextUtil.FormatBytes(r.QuotaBytes),
                        TextUtil.FormatBytes(Math.Max(0, r.QuotaBytes - r.UsedBytes)), $"{r.Percent}%", r.Files.ToString()]).ToList(), null, exportedBy);
            }
        }
    }

    private static string Vn(DateTime utc) => utc.AddHours(7).ToString("dd/MM/yyyy HH:mm:ss");

    private static string? DescribeAudit(AuditLogFilter f)
    {
        var parts = new List<string>();
        if (f.From.HasValue || f.To.HasValue) parts.Add($"Thời gian: {f.From:dd/MM/yyyy} - {f.To:dd/MM/yyyy}");
        if (f.Actions is { Count: > 0 }) parts.Add("Hành động: " + string.Join(", ", f.Actions.Select(a => AuditQueries.ActionNames.GetValueOrDefault(a, a.ToString()))));
        if (!string.IsNullOrWhiteSpace(f.Q)) parts.Add($"Từ khóa: {f.Q}");
        if (!string.IsNullOrWhiteSpace(f.Ip)) parts.Add($"IP: {f.Ip}");
        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }
}

public sealed record StorageReportParams(Guid? OrganizationId, string? Q);
