using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Operations;

/// <summary>Service-quality item 5.1: support desk with SLA (urgent 4h, non-urgent 24h).</summary>
public static class Sla
{
    public static TimeSpan For(TicketPriority p) => p == TicketPriority.KhanCap ? TimeSpan.FromHours(4) : TimeSpan.FromHours(24);
    public static string PriorityName(TicketPriority p) => p == TicketPriority.KhanCap ? "Khẩn cấp" : "Không khẩn cấp";
    public static string StatusName(TicketStatus s) => s switch
    {
        TicketStatus.Moi => "Mới tiếp nhận", TicketStatus.DangXuLy => "Đang xử lý", TicketStatus.ChoPhanHoi => "Chờ phản hồi", TicketStatus.DaXuLy => "Đã xử lý", _ => "Đã đóng",
    };
}

public sealed record TicketEventDto(DateTime OccurredAt, string EventType, string? Note, string? UserName);

public sealed record TicketDto(Guid Id, string Code, string Title, string Content, TicketPriority Priority, TicketStatus Status, Guid ReporterId, string ReporterName,
    Guid? AssigneeId, string? AssigneeName, DateTime ReceivedAt, DateTime DueAt, DateTime? ResolvedAt, string? Resolution, bool IsOverdue, double? ResolutionHours,
    IReadOnlyList<Guid> AttachmentFileIds, IReadOnlyList<TicketEventDto>? Events = null);

public static class TicketQueries
{
    public static IQueryable<TicketDto> Project(IAppDbContext db, IQueryable<SupportTicket> q, DateTime now) => q.Select(t => new TicketDto(t.Id, t.Code, t.Title, t.Content,
        t.Priority, t.Status, t.ReporterId, db.Users.Where(u => u.Id == t.ReporterId).Select(u => u.FullName).FirstOrDefault() ?? "", t.AssigneeId,
        db.Users.Where(u => u.Id == t.AssigneeId).Select(u => u.FullName).FirstOrDefault(), t.ReceivedAt, t.DueAt, t.ResolvedAt, t.Resolution,
        (t.ResolvedAt ?? now) > t.DueAt, null, t.AttachmentFileIds, null));

    public static TicketDto WithHours(TicketDto t) => t with { ResolutionHours = t.ResolvedAt is { } r ? Math.Round((r - t.ReceivedAt).TotalHours, 1) : null };
}

public sealed record TicketFilter(TicketStatus? Status, TicketPriority? Priority, Guid? AssigneeId, bool? Overdue, DateTime? From, DateTime? To, bool Mine = false);

public sealed record ListTicketsQuery(TicketFilter Filter) : PageQuery, IRequest<PagedResult<TicketDto>>;

public sealed class ListTicketsHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, TimeProvider clock) : IRequestHandler<ListTicketsQuery, PagedResult<TicketDto>>
{
    public async Task<PagedResult<TicketDto>> Handle(ListTicketsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        var all = await perms.HasAsync(uid, Perm.TicketView, ct);
        var query = TicketFiltering.Apply(db.SupportTickets.AsNoTracking(), q.Filter, now);
        if (!all || q.Filter.Mine) query = query.Where(t => t.ReporterId == uid);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(t => t.Code.ToLower().Contains(term) || DbFn.Unaccent(t.Title).Contains(term));
        }
        var page = await TicketQueries.Project(db, query.OrderByDescending(t => t.ReceivedAt), now).ToPagedAsync(q, ct);
        return page with { Items = page.Items.Select(TicketQueries.WithHours).ToList() };
    }
}

public static class TicketFiltering
{
    public static IQueryable<SupportTicket> Apply(IQueryable<SupportTicket> q, TicketFilter f, DateTime now)
    {
        if (f.Status.HasValue) q = q.Where(t => t.Status == f.Status);
        if (f.Priority.HasValue) q = q.Where(t => t.Priority == f.Priority);
        if (f.AssigneeId.HasValue) q = q.Where(t => t.AssigneeId == f.AssigneeId);
        if (f.Overdue == true) q = q.Where(t => (t.ResolvedAt ?? now) > t.DueAt);
        if (f.Overdue == false) q = q.Where(t => (t.ResolvedAt ?? now) <= t.DueAt);
        if (f.From.HasValue) q = q.Where(t => t.ReceivedAt >= f.From.Value.Date.AddHours(-7));
        if (f.To.HasValue) q = q.Where(t => t.ReceivedAt < f.To.Value.Date.AddDays(1).AddHours(-7));
        return q;
    }
}

public sealed record GetTicketQuery(Guid Id) : IRequest<TicketDto>;

public sealed class GetTicketHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, TimeProvider clock) : IRequestHandler<GetTicketQuery, TicketDto>
{
    public async Task<TicketDto> Handle(GetTicketQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var t = await TicketQueries.Project(db, db.SupportTickets.AsNoTracking().Where(x => x.Id == q.Id), clock.GetUtcNow().UtcDateTime).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy yêu cầu hỗ trợ.");
        if (t.ReporterId != uid && !await perms.HasAsync(uid, Perm.TicketView, ct)) throw new NotFoundException("Không tìm thấy yêu cầu hỗ trợ.");
        var events = await db.TicketEvents.AsNoTracking().Where(e => e.TicketId == q.Id).OrderBy(e => e.OccurredAt)
            .Select(e => new TicketEventDto(e.OccurredAt, e.EventType, e.Note, db.Users.Where(u => u.Id == e.UserId).Select(u => u.FullName).FirstOrDefault())).ToListAsync(ct);
        return TicketQueries.WithHours(t) with { Events = events };
    }
}

public sealed record CreateTicketCommand(string Title, string Content, TicketPriority Priority, IReadOnlyList<Guid>? AttachmentFileIds) : IRequest<TicketDto>;

public sealed class CreateTicketValidator : AbstractValidator<CreateTicketCommand>
{
    public CreateTicketValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("Vui lòng nhập tiêu đề.").MaximumLength(300);
        RuleFor(x => x.Content).NotEmpty().WithMessage("Vui lòng mô tả yêu cầu/sự cố.").MaximumLength(10000);
    }
}

public sealed class CreateTicketHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IUserNotifier notify, IAuditLogger audit, TimeProvider clock, ISender mediator)
    : IRequestHandler<CreateTicketCommand, TicketDto>
{
    public async Task<TicketDto> Handle(CreateTicketCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        foreach (var f in cmd.AttachmentFileIds ?? []) await auth.EnsureAsync(uid, ResourceType.File, f, AccessLevel.Download, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var seq = await db.SupportTickets.CountAsync(t => t.ReceivedAt >= now.AddHours(7).Date.AddHours(-7), ct) + 1;
        var t = new SupportTicket
        {
            Code = $"HT-{now.AddHours(7):yyyyMMdd}-{seq:D3}", Title = cmd.Title.Trim(), Content = cmd.Content.Trim(), Priority = cmd.Priority, ReporterId = uid,
            ReceivedAt = now, DueAt = now.Add(Sla.For(cmd.Priority)), AttachmentFileIds = cmd.AttachmentFileIds?.ToList() ?? [],
        };
        t.Events.Add(new TicketEvent { TicketId = t.Id, UserId = uid, EventType = "created", Note = $"Tiếp nhận yêu cầu ({Sla.PriorityName(cmd.Priority)}), hạn xử lý {t.DueAt.AddHours(7):dd/MM/yyyy HH:mm}", OccurredAt = now });
        db.SupportTickets.Add(t);
        await db.SaveChangesAsync(ct);
        await notify.NotifyPermissionAsync(Perm.TicketAdmin, "ticket", $"Yêu cầu hỗ trợ mới {t.Code} ({Sla.PriorityName(t.Priority)})", t.Title, $"/admin/tickets?id={t.Id}", ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Create, ResourceName: $"Yêu cầu hỗ trợ {t.Code}", Detail: new { ticket = t.Id, t.Priority }), ct);
        return await mediator.Send(new GetTicketQuery(t.Id), ct);
    }
}

/// <summary>Assign, change status, add a note or resolve.</summary>
public sealed record UpdateTicketCommand(Guid Id, Guid? AssigneeId, TicketStatus? Status, TicketPriority? Priority, string? Note, string? Resolution) : IRequest<TicketDto>;

public sealed class UpdateTicketHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, IUserNotifier notify, IAuditLogger audit, TimeProvider clock, ISender mediator)
    : IRequestHandler<UpdateTicketCommand, TicketDto>
{
    public async Task<TicketDto> Handle(UpdateTicketCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var t = await db.SupportTickets.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy yêu cầu hỗ trợ.");
        var isAdmin = await perms.HasAsync(uid, Perm.TicketAdmin, ct);
        var isEditor = isAdmin || await perms.HasAsync(uid, Perm.TicketEdit, ct);
        if (!isEditor && !(t.ReporterId == uid && cmd.AssigneeId is null && cmd.Status is null or TicketStatus.Dong && cmd.Priority is null))
            throw new ForbiddenException("Bạn không có quyền xử lý yêu cầu hỗ trợ này.");
        var now = clock.GetUtcNow().UtcDateTime;
        var changes = new List<string>();
        if (cmd.AssigneeId is { } a && a != t.AssigneeId)
        {
            if (!isAdmin) throw new ForbiddenException("Chỉ người điều phối mới được giao việc.");
            var name = await db.Users.Where(u => u.Id == a).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy người xử lý.");
            t.AssigneeId = a;
            changes.Add($"Giao cho {name}");
            await notify.NotifyUserAsync(a, "ticket", $"Bạn được giao xử lý {t.Code}", t.Title, $"/admin/tickets?id={t.Id}", ct);
        }
        if (cmd.Priority is { } p && p != t.Priority)
        {
            t.Priority = p;
            t.DueAt = t.ReceivedAt.Add(Sla.For(p));
            changes.Add($"Đổi mức độ: {Sla.PriorityName(p)}");
        }
        if (cmd.Status is { } s && s != t.Status)
        {
            t.Status = s;
            changes.Add($"Trạng thái: {Sla.StatusName(s)}");
            if (s is TicketStatus.DaXuLy or TicketStatus.Dong) t.ResolvedAt ??= now;
            else t.ResolvedAt = null;
        }
        if (!string.IsNullOrWhiteSpace(cmd.Resolution)) { t.Resolution = cmd.Resolution.Trim(); changes.Add("Cập nhật kết quả xử lý"); }
        if (!string.IsNullOrWhiteSpace(cmd.Note)) changes.Add(cmd.Note.Trim());
        if (changes.Count == 0) return await mediator.Send(new GetTicketQuery(t.Id), ct);
        db.TicketEvents.Add(new TicketEvent { TicketId = t.Id, UserId = uid, EventType = cmd.Status?.ToString().ToLowerInvariant() ?? "update", Note = string.Join("; ", changes), OccurredAt = now });
        await db.SaveChangesAsync(ct);
        if (t.ReporterId != uid) await notify.NotifyUserAsync(t.ReporterId, "ticket", $"Yêu cầu {t.Code} được cập nhật", string.Join("; ", changes), $"/support?id={t.Id}", ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceName: $"Yêu cầu hỗ trợ {t.Code}", Detail: new { ticket = t.Id, changes }), ct);
        return await mediator.Send(new GetTicketQuery(t.Id), ct);
    }
}

public sealed record SlaSummaryDto(int Total, int Open, int Resolved, int Overdue, int ResolvedOnTime, double OnTimePercent, double? AvgHoursUrgent, double? AvgHoursNormal);

public sealed record GetSlaSummaryQuery(DateTime? From, DateTime? To) : IRequest<SlaSummaryDto>;

public sealed class GetSlaSummaryHandler(IAppDbContext db, TimeProvider clock) : IRequestHandler<GetSlaSummaryQuery, SlaSummaryDto>
{
    public async Task<SlaSummaryDto> Handle(GetSlaSummaryQuery q, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await TicketFiltering.Apply(db.SupportTickets.AsNoTracking(), new TicketFilter(null, null, null, null, q.From, q.To), now)
            .Select(t => new { t.Priority, t.ReceivedAt, t.DueAt, t.ResolvedAt }).ToListAsync(ct);
        var resolved = rows.Where(r => r.ResolvedAt.HasValue).ToList();
        var onTime = resolved.Count(r => r.ResolvedAt <= r.DueAt);
        double? Avg(TicketPriority p) => resolved.Where(r => r.Priority == p).Select(r => (r.ResolvedAt!.Value - r.ReceivedAt).TotalHours).DefaultIfEmpty().Average() is var a && resolved.Any(r => r.Priority == p) ? Math.Round(a, 1) : null;
        return new SlaSummaryDto(rows.Count, rows.Count - resolved.Count, resolved.Count, rows.Count(r => (r.ResolvedAt ?? now) > r.DueAt), onTime,
            resolved.Count == 0 ? 100 : Math.Round(onTime * 100.0 / resolved.Count, 1), Avg(TicketPriority.KhanCap), Avg(TicketPriority.KhongKhanCap));
    }
}

public sealed record ExportSlaReportQuery(TicketFilter Filter, string Format, string ExportedBy) : IRequest<ExportedFile>;

public sealed class ExportSlaReportHandler(IAppDbContext db, ITableExporter exporter, TimeProvider clock) : IRequestHandler<ExportSlaReportQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(ExportSlaReportQuery q, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = (await TicketQueries.Project(db, TicketFiltering.Apply(db.SupportTickets.AsNoTracking(), q.Filter, now).OrderBy(t => t.ReceivedAt), now).ToListAsync(ct))
            .Select(TicketQueries.WithHours).ToList();
        static string Vn(DateTime? d) => d is { } x ? x.AddHours(7).ToString("dd/MM/yyyy HH:mm") : "";
        return await exporter.ExportAsync(new TableExport("Báo cáo xử lý yêu cầu hỗ trợ, sự cố (SLA)",
            [new("Mã", 1.3f), new("Tiêu đề", 2.5f), new("Mức độ", 1.1f), new("Người gửi", 1.5f), new("Người xử lý", 1.5f), new("Tiếp nhận", 1.3f), new("Hạn xử lý", 1.3f),
             new("Hoàn thành", 1.3f), new("Số giờ", 0.8f), new("Trạng thái", 1.2f), new("Đúng hạn", 0.9f)],
            rows.Select(t => (IReadOnlyList<string?>)[t.Code, t.Title, Sla.PriorityName(t.Priority), t.ReporterName, t.AssigneeName, Vn(t.ReceivedAt), Vn(t.DueAt), Vn(t.ResolvedAt),
                t.ResolutionHours?.ToString("0.#"), Sla.StatusName(t.Status), t.IsOverdue ? "Quá hạn" : "Đúng hạn"]).ToList(),
            "Khẩn cấp: xử lý trong 4 giờ; Không khẩn cấp: trong 24 giờ", q.ExportedBy), q.Format, ct);
    }
}

// ---------- Item 5.2: change log ----------
public sealed record ChangeLogDto(Guid Id, string Version, string ChangeType, string Description, string? AppliedByName, DateTime AppliedAt);

public sealed record ListChangeLogsQuery(string? ChangeType) : PageQuery, IRequest<PagedResult<ChangeLogDto>>;

public sealed class ListChangeLogsHandler(IAppDbContext db) : IRequestHandler<ListChangeLogsQuery, PagedResult<ChangeLogDto>>
{
    public Task<PagedResult<ChangeLogDto>> Handle(ListChangeLogsQuery q, CancellationToken ct)
    {
        var query = db.ChangeLogs.AsNoTracking();
        if (!string.IsNullOrEmpty(q.ChangeType)) query = query.Where(c => c.ChangeType == q.ChangeType);
        return query.OrderByDescending(c => c.AppliedAt)
            .Select(c => new ChangeLogDto(c.Id, c.Version, c.ChangeType, c.Description, db.Users.Where(u => u.Id == c.AppliedBy).Select(u => u.FullName).FirstOrDefault(), c.AppliedAt))
            .ToPagedAsync(q, ct);
    }
}

public sealed record AddChangeLogCommand(string Version, string ChangeType, string Description) : IRequest<Guid>;

public sealed class AddChangeLogValidator : AbstractValidator<AddChangeLogCommand>
{
    public AddChangeLogValidator()
    {
        RuleFor(x => x.Version).NotEmpty().WithMessage("Vui lòng nhập phiên bản.").MaximumLength(50);
        RuleFor(x => x.ChangeType).Must(t => t is "deploy" or "config" or "feature" or "fix" or "maintenance" or "security").WithMessage("Loại thay đổi không hợp lệ.");
        RuleFor(x => x.Description).NotEmpty().WithMessage("Vui lòng mô tả thay đổi.").MaximumLength(4000);
    }
}

public sealed class AddChangeLogHandler(IAppDbContext db, ICurrentUser me, IAuditLogger audit, TimeProvider clock) : IRequestHandler<AddChangeLogCommand, Guid>
{
    public async Task<Guid> Handle(AddChangeLogCommand cmd, CancellationToken ct)
    {
        var c = new ChangeLog { Version = cmd.Version.Trim(), ChangeType = cmd.ChangeType, Description = cmd.Description.Trim(), AppliedBy = me.UserId, AppliedAt = clock.GetUtcNow().UtcDateTime };
        db.ChangeLogs.Add(c);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceName: $"Nhật ký thay đổi {c.Version}", Detail: new { c.ChangeType, c.Description }), ct);
        return c.Id;
    }
}
