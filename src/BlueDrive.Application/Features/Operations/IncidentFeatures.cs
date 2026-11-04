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

/// <summary>HSMT 3.4: information-security incident records (category per TT 20/2017/TT-BTTTT).</summary>
public static class IncidentNames
{
    public static string Category(IncidentCategory c) => c switch
    {
        IncidentCategory.Malware => "Mã độc",
        IncidentCategory.Intrusion => "Xâm nhập trái phép",
        IncidentCategory.Vulnerability => "Lỗ hổng bảo mật",
        IncidentCategory.DataLeak => "Lộ, lọt dữ liệu",
        IncidentCategory.DenialOfService => "Từ chối dịch vụ",
        IncidentCategory.AccountCompromise => "Chiếm đoạt tài khoản",
        _ => "Khác",
    };

    public static string Severity(IncidentSeverity s) => s switch
    {
        IncidentSeverity.Low => "Thấp", IncidentSeverity.Medium => "Trung bình", IncidentSeverity.High => "Cao", _ => "Nghiêm trọng",
    };

    public static string Status(IncidentStatus s) => s switch
    {
        IncidentStatus.Reported => "Mới phát hiện",
        IncidentStatus.Investigating => "Đang điều tra",
        IncidentStatus.Containing => "Đang ngăn chặn",
        IncidentStatus.Recovering => "Đang khắc phục",
        IncidentStatus.Resolved => "Đã khắc phục",
        _ => "Đã đóng",
    };
}

public sealed record IncidentEventDto(DateTime OccurredAt, string EventType, string? Note, string? UserName);

public sealed record IncidentDto(Guid Id, string Code, string Title, string Description, IncidentCategory Category, IncidentSeverity Severity, IncidentStatus Status,
    DateTime DetectedAt, DateTime ReportedAt, Guid ReporterId, string ReporterName, Guid? HandlerId, string? HandlerName, Guid? AlertId, string? AlertMessage,
    string? AffectedSystems, string? Cause, string? CoordinatingParties, string? Remediation, string? Conclusion, DateTime? ResolvedAt, DateTime? ClosedAt,
    IReadOnlyList<IncidentEventDto>? Events = null);

public sealed record IncidentFilter(IncidentStatus? Status, IncidentSeverity? Severity, IncidentCategory? Category, bool? Open, DateTime? From, DateTime? To);

public static class IncidentQueries
{
    public static IQueryable<SecurityIncident> Apply(IQueryable<SecurityIncident> q, IncidentFilter f, string? text)
    {
        if (f.Status.HasValue) q = q.Where(i => i.Status == f.Status);
        if (f.Severity.HasValue) q = q.Where(i => i.Severity == f.Severity);
        if (f.Category.HasValue) q = q.Where(i => i.Category == f.Category);
        if (f.Open == true) q = q.Where(i => i.Status != IncidentStatus.Resolved && i.Status != IncidentStatus.Closed);
        if (f.Open == false) q = q.Where(i => i.Status == IncidentStatus.Resolved || i.Status == IncidentStatus.Closed);
        if (f.From.HasValue) q = q.Where(i => i.DetectedAt >= f.From.Value.Date.AddHours(-7));
        if (f.To.HasValue) q = q.Where(i => i.DetectedAt < f.To.Value.Date.AddDays(1).AddHours(-7));
        if (!string.IsNullOrWhiteSpace(text))
        {
            var term = TextUtil.Unaccent(text.Trim());
            q = q.Where(i => i.Code.ToLower().Contains(term) || DbFn.Unaccent(i.Title).Contains(term));
        }
        return q;
    }

    public static IQueryable<IncidentDto> Project(IAppDbContext db, IQueryable<SecurityIncident> q) => q.Select(i => new IncidentDto(i.Id, i.Code, i.Title, i.Description,
        i.Category, i.Severity, i.Status, i.DetectedAt, i.ReportedAt, i.ReporterId, db.Users.Where(u => u.Id == i.ReporterId).Select(u => u.FullName).FirstOrDefault() ?? "",
        i.HandlerId, db.Users.Where(u => u.Id == i.HandlerId).Select(u => u.FullName).FirstOrDefault(), i.AlertId,
        db.Alerts.Where(a => a.Id == i.AlertId).Select(a => a.Message).FirstOrDefault(), i.AffectedSystems, i.Cause, i.CoordinatingParties, i.Remediation,
        i.Conclusion, i.ResolvedAt, i.ClosedAt, null));

    public static string Link(Guid id) => $"/admin/incidents?id={id}";
}

public sealed record ListIncidentsQuery(IncidentFilter Filter) : PageQuery, IRequest<PagedResult<IncidentDto>>;

public sealed class ListIncidentsHandler(IAppDbContext db) : IRequestHandler<ListIncidentsQuery, PagedResult<IncidentDto>>
{
    public Task<PagedResult<IncidentDto>> Handle(ListIncidentsQuery q, CancellationToken ct) =>
        IncidentQueries.Project(db, IncidentQueries.Apply(db.SecurityIncidents.AsNoTracking(), q.Filter, q.Q).OrderByDescending(i => i.DetectedAt)).ToPagedAsync(q, ct);
}

public sealed record GetIncidentQuery(Guid Id) : IRequest<IncidentDto>;

public sealed class GetIncidentHandler(IAppDbContext db) : IRequestHandler<GetIncidentQuery, IncidentDto>
{
    public async Task<IncidentDto> Handle(GetIncidentQuery q, CancellationToken ct)
    {
        var i = await IncidentQueries.Project(db, db.SecurityIncidents.AsNoTracking().Where(x => x.Id == q.Id)).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy hồ sơ sự cố.");
        var events = await db.IncidentEvents.AsNoTracking().Where(e => e.IncidentId == q.Id).OrderBy(e => e.OccurredAt)
            .Select(e => new IncidentEventDto(e.OccurredAt, e.EventType, e.Note, db.Users.Where(u => u.Id == e.UserId).Select(u => u.FullName).FirstOrDefault())).ToListAsync(ct);
        return i with { Events = events };
    }
}

/// <summary>Opens an incident record, optionally from a monitoring alert (the alert is then acknowledged and linked).</summary>
public sealed record CreateIncidentCommand(string Title, string Description, IncidentCategory Category, IncidentSeverity Severity, DateTime? DetectedAt,
    string? AffectedSystems, Guid? AlertId) : IRequest<IncidentDto>;

public sealed class CreateIncidentValidator : AbstractValidator<CreateIncidentCommand>
{
    public CreateIncidentValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("Vui lòng nhập tiêu đề sự cố.").MaximumLength(300).WithMessage("Tiêu đề tối đa 300 ký tự.");
        RuleFor(x => x.Description).NotEmpty().WithMessage("Vui lòng mô tả sự cố.").MaximumLength(10000).WithMessage("Mô tả tối đa 10.000 ký tự.");
        RuleFor(x => x.Category).IsInEnum().WithMessage("Loại sự cố không hợp lệ.");
        RuleFor(x => x.Severity).IsInEnum().WithMessage("Mức độ không hợp lệ.");
        RuleFor(x => x.AffectedSystems).MaximumLength(2000).WithMessage("Phạm vi ảnh hưởng tối đa 2.000 ký tự.");
    }
}

public sealed class CreateIncidentHandler(IAppDbContext db, ICurrentUser me, IUserNotifier notify, IAuditLogger audit, TimeProvider clock, ISender mediator)
    : IRequestHandler<CreateIncidentCommand, IncidentDto>
{
    public async Task<IncidentDto> Handle(CreateIncidentCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        if (cmd.DetectedAt is { } d && d > now.AddMinutes(5)) throw new AppException("Thời điểm phát hiện không được ở tương lai.");
        Alert? alert = null;
        if (cmd.AlertId is { } alertId)
        {
            alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct) ?? throw new NotFoundException("Không tìm thấy cảnh báo.");
            var existing = await db.SecurityIncidents.Where(i => i.AlertId == alertId).Select(i => i.Code).FirstOrDefaultAsync(ct);
            if (existing is not null) throw new AppException($"Cảnh báo này đã được lập hồ sơ sự cố {existing}.", 409);
        }
        var seq = await db.SecurityIncidents.CountAsync(i => i.ReportedAt >= now.AddHours(7).Date.AddHours(-7), ct) + 1;
        var inc = new SecurityIncident
        {
            Code = $"SC-{now.AddHours(7):yyyyMMdd}-{seq:D3}", Title = cmd.Title.Trim(), Description = cmd.Description.Trim(), Category = cmd.Category, Severity = cmd.Severity,
            DetectedAt = cmd.DetectedAt ?? alert?.CreatedAt ?? now, ReportedAt = now, ReporterId = uid, AlertId = alert?.Id, AffectedSystems = cmd.AffectedSystems?.Trim(),
        };
        var note = $"Lập hồ sơ sự cố ({IncidentNames.Category(inc.Category)}, mức độ {IncidentNames.Severity(inc.Severity)})";
        if (alert is not null)
        {
            note += $" từ cảnh báo: {alert.Message}";
            if (!alert.IsAcknowledged) { alert.IsAcknowledged = true; alert.AcknowledgedBy = uid; alert.AcknowledgedAt = now; }
        }
        inc.Events.Add(new IncidentEvent { IncidentId = inc.Id, UserId = uid, EventType = "created", Note = note, OccurredAt = now });
        db.SecurityIncidents.Add(inc);
        await db.SaveChangesAsync(ct);
        await notify.NotifyPermissionAsync(Perm.IncidentView, "incident", $"Sự cố ATTT mới {inc.Code} ({IncidentNames.Severity(inc.Severity)})", inc.Title, IncidentQueries.Link(inc.Id), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Create, ResourceName: $"Sự cố ATTT {inc.Code}",
            Detail: new { incident = inc.Id, inc.Category, inc.Severity, alert = inc.AlertId }), ct);
        return await mediator.Send(new GetIncidentQuery(inc.Id), ct);
    }
}

/// <summary>Records handling progress: status, handler, cause, coordination, remediation, conclusion, or a timeline note.</summary>
public sealed record UpdateIncidentCommand(Guid Id, string? Title, string? Description, IncidentCategory? Category, IncidentSeverity? Severity, IncidentStatus? Status,
    Guid? HandlerId, string? AffectedSystems, string? Cause, string? CoordinatingParties, string? Remediation, string? Conclusion, string? Note) : IRequest<IncidentDto>;

public sealed class UpdateIncidentValidator : AbstractValidator<UpdateIncidentCommand>
{
    public UpdateIncidentValidator()
    {
        RuleFor(x => x.Title).MaximumLength(300).WithMessage("Tiêu đề tối đa 300 ký tự.");
        RuleFor(x => x.Category).IsInEnum().When(x => x.Category.HasValue).WithMessage("Loại sự cố không hợp lệ.");
        RuleFor(x => x.Severity).IsInEnum().When(x => x.Severity.HasValue).WithMessage("Mức độ không hợp lệ.");
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue).WithMessage("Trạng thái không hợp lệ.");
        RuleFor(x => x.Description).MaximumLength(10000).WithMessage("Mô tả tối đa 10.000 ký tự.");
        RuleFor(x => x.Cause).MaximumLength(10000).WithMessage("Nguyên nhân tối đa 10.000 ký tự.");
        RuleFor(x => x.Remediation).MaximumLength(10000).WithMessage("Biện pháp khắc phục tối đa 10.000 ký tự.");
        RuleFor(x => x.Conclusion).MaximumLength(10000).WithMessage("Kết luận tối đa 10.000 ký tự.");
        RuleFor(x => x.CoordinatingParties).MaximumLength(2000).WithMessage("Đơn vị phối hợp tối đa 2.000 ký tự.");
        RuleFor(x => x.AffectedSystems).MaximumLength(2000).WithMessage("Phạm vi ảnh hưởng tối đa 2.000 ký tự.");
        RuleFor(x => x.Note).MaximumLength(4000).WithMessage("Ghi chú tối đa 4.000 ký tự.");
    }
}

public sealed class UpdateIncidentHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, IUserNotifier notify, IAuditLogger audit, TimeProvider clock, ISender mediator)
    : IRequestHandler<UpdateIncidentCommand, IncidentDto>
{
    public async Task<IncidentDto> Handle(UpdateIncidentCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var inc = await db.SecurityIncidents.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy hồ sơ sự cố.");
        var isAdmin = await perms.HasAsync(uid, Perm.IncidentAdmin, ct);
        if (inc.Status == IncidentStatus.Closed && !(isAdmin && cmd.Status is { } reopen && reopen != IncidentStatus.Closed))
            throw new AppException("Hồ sơ sự cố đã đóng. Người điều phối cần mở lại trước khi cập nhật.");
        var now = clock.GetUtcNow().UtcDateTime;
        var changes = new List<string>();

        void Text(string? value, string? current, Action<string> set, string label)
        {
            if (value is null) return;
            var v = value.Trim();
            if (v == (current ?? "")) return;
            set(v);
            changes.Add($"Cập nhật {label}");
        }

        if (!string.IsNullOrWhiteSpace(cmd.Title)) Text(cmd.Title, inc.Title, v => inc.Title = v, "tiêu đề");
        if (!string.IsNullOrWhiteSpace(cmd.Description)) Text(cmd.Description, inc.Description, v => inc.Description = v, "mô tả");
        Text(cmd.AffectedSystems, inc.AffectedSystems, v => inc.AffectedSystems = v, "phạm vi ảnh hưởng");
        Text(cmd.Cause, inc.Cause, v => inc.Cause = v, "nguyên nhân");
        Text(cmd.CoordinatingParties, inc.CoordinatingParties, v => inc.CoordinatingParties = v, "đơn vị phối hợp");
        Text(cmd.Remediation, inc.Remediation, v => inc.Remediation = v, "biện pháp khắc phục");
        Text(cmd.Conclusion, inc.Conclusion, v => inc.Conclusion = v, "kết luận");
        if (cmd.Category is { } c && c != inc.Category) { inc.Category = c; changes.Add($"Loại sự cố: {IncidentNames.Category(c)}"); }
        if (cmd.Severity is { } sev && sev != inc.Severity) { inc.Severity = sev; changes.Add($"Mức độ: {IncidentNames.Severity(sev)}"); }

        Guid? newHandler = null;
        if (cmd.HandlerId is { } h && h != inc.HandlerId)
        {
            if (!isAdmin) throw new ForbiddenException("Chỉ người điều phối mới được phân công xử lý sự cố.");
            var name = await db.Users.Where(u => u.Id == h && u.IsActive).Select(u => u.FullName).FirstOrDefaultAsync(ct)
                       ?? throw new NotFoundException("Không tìm thấy người xử lý.");
            inc.HandlerId = h;
            newHandler = h;
            changes.Add($"Phân công: {name}");
        }

        var statusChanged = false;
        if (cmd.Status is { } s && s != inc.Status)
        {
            if (s == IncidentStatus.Closed)
            {
                if (!isAdmin) throw new ForbiddenException("Chỉ người điều phối mới được đóng hồ sơ sự cố.");
                if (string.IsNullOrWhiteSpace(inc.Cause) || string.IsNullOrWhiteSpace(inc.Remediation) || string.IsNullOrWhiteSpace(inc.Conclusion))
                    throw new AppException("Cần ghi nhận nguyên nhân, biện pháp khắc phục và kết luận trước khi đóng hồ sơ sự cố.");
                inc.ClosedAt = now;
            }
            else inc.ClosedAt = null;
            if (s is IncidentStatus.Resolved or IncidentStatus.Closed)
            {
                if (string.IsNullOrWhiteSpace(inc.Remediation)) throw new AppException("Cần ghi nhận biện pháp khắc phục trước khi chuyển sang \"Đã khắc phục\".");
                inc.ResolvedAt ??= now;
            }
            else inc.ResolvedAt = null;
            inc.Status = s;
            statusChanged = true;
            changes.Insert(0, $"Trạng thái: {IncidentNames.Status(s)}");
        }
        if (!string.IsNullOrWhiteSpace(cmd.Note)) changes.Add(cmd.Note.Trim());
        if (changes.Count == 0) return await mediator.Send(new GetIncidentQuery(inc.Id), ct);

        var summary = string.Join("; ", changes);
        db.IncidentEvents.Add(new IncidentEvent
        {
            IncidentId = inc.Id, UserId = uid, OccurredAt = now, Note = summary,
            EventType = statusChanged ? inc.Status.ToString().ToLowerInvariant() : string.IsNullOrWhiteSpace(cmd.Note) || changes.Count > 1 ? "update" : "note",
        });
        await db.SaveChangesAsync(ct);
        if (newHandler is { } nh && nh != uid)
            await notify.NotifyUserAsync(nh, "incident", $"Bạn được phân công xử lý sự cố {inc.Code}", inc.Title, IncidentQueries.Link(inc.Id), ct);
        if (statusChanged)
            await notify.NotifyPermissionAsync(Perm.IncidentAdmin, "incident", $"Sự cố {inc.Code}: {IncidentNames.Status(inc.Status)}", inc.Title, IncidentQueries.Link(inc.Id), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceName: $"Sự cố ATTT {inc.Code}", Detail: new { incident = inc.Id, changes }), ct);
        return await mediator.Send(new GetIncidentQuery(inc.Id), ct);
    }
}

/// <summary>Full incident record (fields + timeline) as PDF/Excel, for reporting to the competent authority.</summary>
public sealed record ExportIncidentQuery(Guid Id, string Format, string ExportedBy) : IRequest<ExportedFile>;

public sealed class ExportIncidentHandler(ITableExporter exporter, IAuditLogger audit, ISender mediator) : IRequestHandler<ExportIncidentQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(ExportIncidentQuery q, CancellationToken ct)
    {
        var i = await mediator.Send(new GetIncidentQuery(q.Id), ct);
        static string Vn(DateTime? d) => d is { } x ? x.AddHours(7).ToString("dd/MM/yyyy HH:mm") : "";
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "Mã hồ sơ", i.Code }, new[] { "Tiêu đề", i.Title }, new[] { "Loại sự cố", IncidentNames.Category(i.Category) },
            new[] { "Mức độ", IncidentNames.Severity(i.Severity) }, new[] { "Trạng thái", IncidentNames.Status(i.Status) },
            new[] { "Thời điểm phát hiện", Vn(i.DetectedAt) }, new[] { "Thời điểm lập hồ sơ", Vn(i.ReportedAt) }, new[] { "Người lập", i.ReporterName },
            new[] { "Người xử lý", i.HandlerName }, new[] { "Cảnh báo liên quan", i.AlertMessage }, new[] { "Mô tả", i.Description },
            new[] { "Phạm vi ảnh hưởng", i.AffectedSystems }, new[] { "Nguyên nhân", i.Cause }, new[] { "Đơn vị phối hợp", i.CoordinatingParties },
            new[] { "Biện pháp khắc phục", i.Remediation }, new[] { "Kết luận", i.Conclusion }, new[] { "Thời điểm khắc phục", Vn(i.ResolvedAt) },
            new[] { "Thời điểm đóng", Vn(i.ClosedAt) },
        };
        rows.AddRange((i.Events ?? []).Select(e => (IReadOnlyList<string?>)[$"Diễn biến {Vn(e.OccurredAt)}", $"{e.UserName ?? "Hệ thống"}: {e.Note}"]));
        var file = await exporter.ExportAsync(new TableExport($"Hồ sơ sự cố an toàn thông tin {i.Code}", [new("Mục", 1.2f), new("Nội dung", 4f)], rows,
            "Lập theo quy trình ứng phó sự cố an toàn thông tin (TT 20/2017/TT-BTTTT)", q.ExportedBy), q.Format, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: $"Sự cố ATTT {i.Code}", Detail: new { incident = i.Id, q.Format }), ct);
        return file;
    }
}

/// <summary>Incident register (list) export.</summary>
public sealed record ExportIncidentsQuery(IncidentFilter Filter, string Format, string ExportedBy) : IRequest<ExportedFile>;

public sealed class ExportIncidentsHandler(IAppDbContext db, ITableExporter exporter, IAuditLogger audit) : IRequestHandler<ExportIncidentsQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(ExportIncidentsQuery q, CancellationToken ct)
    {
        var rows = await IncidentQueries.Project(db, IncidentQueries.Apply(db.SecurityIncidents.AsNoTracking(), q.Filter, null).OrderBy(i => i.DetectedAt)).ToListAsync(ct);
        static string Vn(DateTime? d) => d is { } x ? x.AddHours(7).ToString("dd/MM/yyyy HH:mm") : "";
        var file = await exporter.ExportAsync(new TableExport("Sổ theo dõi sự cố an toàn thông tin",
            [new("Mã", 1.3f), new("Tiêu đề", 2.5f), new("Loại", 1.3f), new("Mức độ", 1f), new("Phát hiện", 1.3f), new("Người xử lý", 1.4f),
             new("Trạng thái", 1.2f), new("Khắc phục", 1.3f), new("Kết luận", 2.5f)],
            rows.Select(i => (IReadOnlyList<string?>)[i.Code, i.Title, IncidentNames.Category(i.Category), IncidentNames.Severity(i.Severity), Vn(i.DetectedAt),
                i.HandlerName, IncidentNames.Status(i.Status), Vn(i.ResolvedAt), i.Conclusion]).ToList(), null, q.ExportedBy), q.Format, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: "Sổ theo dõi sự cố ATTT", Detail: new { count = rows.Count, q.Format }), ct);
        return file;
    }
}
