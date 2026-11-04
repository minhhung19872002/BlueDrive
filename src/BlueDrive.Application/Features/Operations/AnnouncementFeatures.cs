using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Operations;

/// <summary>SignalR groups shared by the API hub and application handlers.</summary>
public static class RealtimeGroups
{
    /// <summary>Every signed-in web/mobile connection.</summary>
    public const string AllUsers = "all";
}

// ---------- Item 5.2: system announcements (banner shown to every signed-in user) ----------
public sealed record AnnouncementDto(Guid Id, string Title, string Content, AnnouncementLevel Level, DateTime StartsAt, DateTime? EndsAt, bool IsActive,
    string? CreatedByName, DateTime CreatedAt, DateTime? UpdatedAt, bool IsCurrent);

public static class AnnouncementQueries
{
    public static IQueryable<SystemAnnouncement> Current(IQueryable<SystemAnnouncement> q, DateTime now) =>
        q.Where(a => a.IsActive && a.StartsAt <= now && (a.EndsAt == null || a.EndsAt > now));

    public static IQueryable<AnnouncementDto> Project(IAppDbContext db, IQueryable<SystemAnnouncement> q, DateTime now) => q.Select(a => new AnnouncementDto(a.Id,
        a.Title, a.Content, a.Level, a.StartsAt, a.EndsAt, a.IsActive, db.Users.Where(u => u.Id == a.CreatedBy).Select(u => u.FullName).FirstOrDefault(),
        a.CreatedAt, a.UpdatedAt, a.IsActive && a.StartsAt <= now && (a.EndsAt == null || a.EndsAt > now)));

    /// <summary>Tells every open client to reload the banner.</summary>
    public static Task BroadcastAsync(IUserNotifier notify, CancellationToken ct) =>
        notify.BroadcastToGroupAsync(RealtimeGroups.AllUsers, "announcement", new { action = "changed" }, ct);
}

public sealed record ListAnnouncementsQuery : PageQuery, IRequest<PagedResult<AnnouncementDto>>;

public sealed class ListAnnouncementsHandler(IAppDbContext db, TimeProvider clock) : IRequestHandler<ListAnnouncementsQuery, PagedResult<AnnouncementDto>>
{
    public Task<PagedResult<AnnouncementDto>> Handle(ListAnnouncementsQuery q, CancellationToken ct)
    {
        var query = db.SystemAnnouncements.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(a => DbFn.Unaccent(a.Title).Contains(term));
        }
        return AnnouncementQueries.Project(db, query.OrderByDescending(a => a.StartsAt), clock.GetUtcNow().UtcDateTime).ToPagedAsync(q, ct);
    }
}

/// <summary>Announcements currently in effect, for the banner (any signed-in user).</summary>
public sealed record ActiveAnnouncementsQuery : IRequest<IReadOnlyList<AnnouncementDto>>;

public sealed class ActiveAnnouncementsHandler(IAppDbContext db, TimeProvider clock) : IRequestHandler<ActiveAnnouncementsQuery, IReadOnlyList<AnnouncementDto>>
{
    public async Task<IReadOnlyList<AnnouncementDto>> Handle(ActiveAnnouncementsQuery q, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return await AnnouncementQueries.Project(db, AnnouncementQueries.Current(db.SystemAnnouncements.AsNoTracking(), now).OrderByDescending(a => a.StartsAt), now)
            .Take(5).ToListAsync(ct);
    }
}

/// <summary>Creates (Id null) or updates an announcement.</summary>
public sealed record SaveAnnouncementCommand(Guid? Id, string Title, string Content, AnnouncementLevel Level, DateTime? StartsAt, DateTime? EndsAt, bool IsActive = true)
    : IRequest<AnnouncementDto>;

public sealed class SaveAnnouncementValidator : AbstractValidator<SaveAnnouncementCommand>
{
    public SaveAnnouncementValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("Vui lòng nhập tiêu đề thông báo.").MaximumLength(200).WithMessage("Tiêu đề tối đa 200 ký tự.");
        RuleFor(x => x.Content).NotEmpty().WithMessage("Vui lòng nhập nội dung thông báo.").MaximumLength(2000).WithMessage("Nội dung tối đa 2.000 ký tự.");
        RuleFor(x => x.Level).IsInEnum().WithMessage("Loại thông báo không hợp lệ.");
        RuleFor(x => x.EndsAt).Must((x, end) => end is null || end > (x.StartsAt ?? DateTime.MinValue))
            .WithMessage("Thời điểm kết thúc phải sau thời điểm bắt đầu.");
    }
}

public sealed class SaveAnnouncementHandler(IAppDbContext db, ICurrentUser me, IUserNotifier notify, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<SaveAnnouncementCommand, AnnouncementDto>
{
    public async Task<AnnouncementDto> Handle(SaveAnnouncementCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        SystemAnnouncement a;
        if (cmd.Id is { } id)
        {
            a = await db.SystemAnnouncements.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy thông báo.");
            a.UpdatedAt = now;
        }
        else
        {
            a = new SystemAnnouncement { CreatedBy = uid, CreatedAt = now };
            db.SystemAnnouncements.Add(a);
        }
        a.Title = cmd.Title.Trim();
        a.Content = cmd.Content.Trim();
        a.Level = cmd.Level;
        a.StartsAt = cmd.StartsAt ?? (cmd.Id is null ? now : a.StartsAt);
        a.EndsAt = cmd.EndsAt;
        a.IsActive = cmd.IsActive;
        await db.SaveChangesAsync(ct);
        await AnnouncementQueries.BroadcastAsync(notify, ct);
        await audit.LogAsync(new AuditEntry(cmd.Id is null ? AuditAction.Create : AuditAction.Edit, ResourceName: $"Thông báo hệ thống: {a.Title}",
            Detail: new { announcement = a.Id, a.Level, a.StartsAt, a.EndsAt, a.IsActive }), ct);
        return await AnnouncementQueries.Project(db, db.SystemAnnouncements.AsNoTracking().Where(x => x.Id == a.Id), now).FirstAsync(ct);
    }
}

public sealed record DeleteAnnouncementCommand(Guid Id) : IRequest;

public sealed class DeleteAnnouncementHandler(IAppDbContext db, IUserNotifier notify, IAuditLogger audit) : IRequestHandler<DeleteAnnouncementCommand>
{
    public async Task Handle(DeleteAnnouncementCommand cmd, CancellationToken ct)
    {
        var a = await db.SystemAnnouncements.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy thông báo.");
        db.SystemAnnouncements.Remove(a);
        await db.SaveChangesAsync(ct);
        await AnnouncementQueries.BroadcastAsync(notify, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: $"Thông báo hệ thống: {a.Title}", Detail: new { announcement = a.Id }), ct);
    }
}
