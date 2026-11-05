using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Spec 8.5: administrators do not see others' content by default. Emergency access gives one of them, for at most 24 hours and with a
/// reason, the content of a Drive or a Space (its folders with own permissions included); it is audited, the owner / Space managers are told
/// — unless "điều tra" is chosen, which needs the investigation permission (audited too). It ends by itself.
/// </summary>
public sealed record GrantEmergencyAccessCommand(Guid WorkspaceId, int Hours, string Reason, bool Investigation = false) : IRequest<Guid>;

public sealed record EndEmergencyAccessCommand(Guid Id) : IRequest<Unit>;

public sealed record ListEmergencyAccessQuery : IRequest<IReadOnlyList<EmergencyAccessDto>>;

public sealed record EmergencyAccessDto(Guid Id, Guid WorkspaceId, string WorkspaceName, bool IsPersonal, Guid AdminId, string AdminName, string Reason, bool Investigation,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? EndedAt, string? EndedByName, bool Active);

public sealed class EmergencyAccessHandlers(IAppDbContext db, ICurrentUser me, IPermissionService perms, IAuditLogger audit, TimeProvider clock, AclEditor acl,
    IUserNotifier notifier)
    : IRequestHandler<GrantEmergencyAccessCommand, Guid>, IRequestHandler<EndEmergencyAccessCommand, Unit>,
      IRequestHandler<ListEmergencyAccessQuery, IReadOnlyList<EmergencyAccessDto>>
{
    public const int MaxHours = 24;

    public async Task<Guid> Handle(GrantEmergencyAccessCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.EmergencyAdmin, ct)) throw new ForbiddenException("Bạn không có quyền truy cập khẩn cấp.");
        if (cmd.Investigation && !await perms.HasAsync(uid, Perm.AuditInvestigation, ct))
            throw new ForbiddenException("Chế độ điều tra (không thông báo) cần quyền Điều tra.");
        if (cmd.Hours is < 1 or > MaxHours) throw new AppException($"Thời hạn truy cập khẩn cấp từ 1 đến {MaxHours} giờ.");
        var reason = cmd.Reason?.Trim() ?? "";
        if (reason.Length is < 10 or > 1000) throw new AppException("Bắt buộc nhập lý do (10–1000 ký tự).");
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId && w.Status != SpaceStatus.Deleted, ct)
                 ?? throw new NotFoundException("Không tìm thấy Drive / Space.");
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddHours(cmd.Hours);
        // The root and every place below it with its own permissions (they do not inherit from the root).
        var root = await db.Securables.FirstOrDefaultAsync(s => s.Id == ws.Id, ct) ?? throw new NotFoundException("Không tìm thấy Drive / Space.");
        var scopes = new List<Guid> { root.Id };
        scopes.AddRange((await acl.UniqueScopesUnderAsync(root, ct)).Where(id => id != root.Id));
        var entries = new List<Guid>();
        foreach (var scope in scopes)
            entries.Add((await acl.GrantAsync(scope, PrincipalKind.User, uid, SystemLevels.ContentManager, expires, uid, ct, $"Truy cập khẩn cấp: {reason}", AclSource.Emergency)).Id);
        var record = new EmergencyAccess
        {
            AdminId = uid, WorkspaceId = ws.Id, Reason = reason, Investigation = cmd.Investigation, CreatedAt = now, ExpiresAt = expires, EntryIds = entries,
        };
        db.EmergencyAccesses.Add(record);
        await db.SaveChangesAsync(ct);
        var adminName = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
        var place = ws.Type == WorkspaceType.Personal ? $"Drive \"{ws.Name}\"" : $"Space \"{ws.Name}\"";
        await audit.LogAsync(new AuditEntry(AuditAction.EmergencyAccess, ResourceType.Workspace, ws.Id, ws.Name, ws.Id, new
        {
            severity = "high", emergencyAccess = record.Id, reason, hours = cmd.Hours, expiresAt = expires, investigation = cmd.Investigation, scopes = scopes.Count,
        }), ct);
        if (!cmd.Investigation)
        {
            var told = await db.WorkspaceMembers.Where(m => m.WorkspaceId == ws.Id && (m.MemberRole == WorkspaceMemberRole.Owner || m.MemberRole == WorkspaceMemberRole.Manager))
                .Select(m => m.UserId).ToListAsync(ct);
            told.Add(ws.OwnerId);
            foreach (var user in told.Distinct().Where(u => u != uid))
                await notifier.NotifyUserAsync(user, "emergency", "Quản trị truy cập khẩn cấp",
                    $"{adminName} được cấp quyền truy cập tạm thời vào {place} đến {expires.AddHours(7):HH:mm dd/MM/yyyy}. Lý do: {reason}", null, ct);
        }
        return record.Id;
    }

    public async Task<Unit> Handle(EndEmergencyAccessCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.EmergencyAdmin, ct)) throw new ForbiddenException("Bạn không có quyền truy cập khẩn cấp.");
        var record = await db.EmergencyAccesses.FirstOrDefaultAsync(e => e.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy lượt truy cập khẩn cấp.");
        if (record.EndedAt is not null) return Unit.Value;
        foreach (var id in record.EntryIds)
            if (await db.AclEntries.AnyAsync(e => e.Id == id, ct)) await acl.RevokeAsync(id, ct);
        record.EndedAt = clock.GetUtcNow().UtcDateTime;
        record.EndedBy = uid;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.EmergencyAccess, ResourceType.Workspace, record.WorkspaceId, null, record.WorkspaceId,
            new { emergencyAccess = record.Id, ended = true }), ct);
        return Unit.Value;
    }

    public async Task<IReadOnlyList<EmergencyAccessDto>> Handle(ListEmergencyAccessQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.EmergencyAdmin, ct) && !await perms.HasAsync(uid, Perm.AuditView, ct)) throw new ForbiddenException();
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await db.EmergencyAccesses.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(500).ToListAsync(ct);
        var wsIds = rows.Select(r => r.WorkspaceId).Distinct().ToList();
        var spaces = await db.Workspaces.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => new { w.Name, w.Type }, ct);
        var userIds = rows.Select(r => r.AdminId).Concat(rows.Where(r => r.EndedBy != null).Select(r => r.EndedBy!.Value)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return rows.Select(r => new EmergencyAccessDto(r.Id, r.WorkspaceId, spaces.GetValueOrDefault(r.WorkspaceId)?.Name ?? "(đã xóa)",
            spaces.GetValueOrDefault(r.WorkspaceId)?.Type == WorkspaceType.Personal, r.AdminId, users.GetValueOrDefault(r.AdminId, ""), r.Reason, r.Investigation,
            r.CreatedAt, r.ExpiresAt, r.EndedAt, r.EndedBy is { } e ? users.GetValueOrDefault(e) : null, r.EndedAt is null && r.ExpiresAt > now)).ToList();
    }
}
