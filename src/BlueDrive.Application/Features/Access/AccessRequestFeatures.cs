using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

// "Yêu cầu quyền truy cập" (spec 5.2).

/// <summary>What someone without access may know about an item opened by its address: that it exists and who owns / manages it, not its name.</summary>
public sealed record AccessInfoDto(Guid ItemId, bool HasAccess, string? OwnerName, bool RequestPending);

public sealed record GetAccessInfoQuery(Guid ItemId) : IRequest<AccessInfoDto>;

public sealed record AccessRequestDto(Guid Id, Guid ItemId, string ItemName, SecurableKind ItemKind, Guid RequesterId, string RequesterName, Guid RequestedLevelId,
    string RequestedLevelName, string? Message, AccessRequestStatus Status, DateTime CreatedAt, Guid? DecidedBy, Guid? DecidedLevelId, DateTime? DecidedAt);

public sealed record RequestAccessCommand(Guid ItemId, Guid LevelId, string? Message) : IRequest<Guid>;

public sealed class RequestAccessValidator : AbstractValidator<RequestAccessCommand>
{
    public RequestAccessValidator() => RuleFor(x => x.Message).MaximumLength(1000);
}

/// <summary>Pending requests: of one item (who manages it), or every item I manage, or mine.</summary>
public sealed record ListAccessRequestsQuery(Guid? ItemId, bool Mine = false) : IRequest<IReadOnlyList<AccessRequestDto>>;

/// <summary>Approve (possibly with another level) or refuse.</summary>
public sealed record DecideAccessRequestCommand(Guid Id, bool Approve, Guid? LevelId, string? Note) : IRequest<Unit>;

public static class AccessRequestRules
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Who decides: the owner (Drive của tôi) and whoever may manage the item's permissions — or, when the Space says so, only its owners
    /// (at most 50 people).
    /// </summary>
    public static async Task<List<Guid>> DecidersAsync(IAppDbContext db, Securable node, CancellationToken ct)
    {
        var right = await RightAsync(db, node, ct);
        var managing = (await db.PermissionLevels.AsNoTracking().ToListAsync(ct)).Where(l => l.Permissions.Has(right)).Select(l => l.Id).ToList();
        var scope = right == AccessRight.ManageSpace ? node.SpaceId!.Value : node.AclScopeId;
        var principals = db.AclEntries.Where(e => e.ScopeId == scope && managing.Contains(e.PermissionLevelId)).Select(e => e.PrincipalId);
        var users = await db.PrincipalMemberships.Where(m => principals.Contains(m.PrincipalId)).Select(m => m.UserId).Distinct().Take(50).ToListAsync(ct);
        if (node.OwnerUserId is { } owner && !users.Contains(owner)) users.Insert(0, owner);
        return users;
    }

    /// <summary>The right that decides requests on the item: managing its permissions, or managing the Space when only its owners decide.</summary>
    public static async Task<AccessRight> RightAsync(IAppDbContext db, Securable node, CancellationToken ct) =>
        node.SpaceId is { } space && await db.Workspaces.AnyAsync(w => w.Id == space && w.AccessRequestDeciders == AccessRequestDeciders.SpaceOwners, ct)
            ? AccessRight.ManageSpace : AccessRight.ManagePermissions;

    public static async Task<bool> CanDecideAsync(IAppDbContext db, IItemPermissionService engine, Guid uid, Guid itemId, CancellationToken ct)
    {
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == itemId, ct);
        if (node is null) return false;
        var right = await RightAsync(db, node, ct);
        return await engine.HasAsync(uid, right == AccessRight.ManageSpace ? node.SpaceId!.Value : itemId, right, ct);
    }
}

public sealed class AccessRequestHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, AclEditor editor, IUserNotifier notify, IAuditLogger audit,
    TimeProvider clock)
    : IRequestHandler<GetAccessInfoQuery, AccessInfoDto>, IRequestHandler<RequestAccessCommand, Guid>,
      IRequestHandler<ListAccessRequestsQuery, IReadOnlyList<AccessRequestDto>>, IRequestHandler<DecideAccessRequestCommand, Unit>
{
    private async Task<Securable> NodeAsync(Guid id, CancellationToken ct) =>
        await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tư liệu.");

    public async Task<AccessInfoDto> Handle(GetAccessInfoQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var node = await NodeAsync(q.ItemId, ct);
        var has = await engine.HasAsync(uid, node.Id, AccessRight.ViewItems, ct);
        var deciders = await AccessRequestRules.DecidersAsync(db, node, ct);
        var owner = deciders.Count == 0 ? null : await db.Users.AsNoTracking().Where(u => u.Id == deciders[0]).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        var pending = await db.AccessRequests.AnyAsync(r => r.SecurableId == node.Id && r.RequesterId == uid && r.Status == AccessRequestStatus.Pending, ct);
        return new AccessInfoDto(node.Id, has, owner, pending);
    }

    public async Task<Guid> Handle(RequestAccessCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var node = await NodeAsync(cmd.ItemId, ct);
        var level = await db.PermissionLevels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == cmd.LevelId, ct) ?? throw new NotFoundException("Không tìm thấy cấp quyền.");
        var now = clock.GetUtcNow().UtcDateTime;
        var request = await db.AccessRequests.FirstOrDefaultAsync(r => r.SecurableId == node.Id && r.RequesterId == uid && r.Status == AccessRequestStatus.Pending, ct);
        if (request is null) db.AccessRequests.Add(request = new AccessRequest { SecurableId = node.Id, RequesterId = uid, CreatedAt = now });
        request.RequestedLevelId = level.Id;
        request.Message = cmd.Message?.Trim();
        request.ExpiresAt = now + AccessRequestRules.Lifetime;
        await db.SaveChangesAsync(ct);
        var who = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
        foreach (var d in await AccessRequestRules.DecidersAsync(db, node, ct))
            await notify.NotifyUserAsync(d, "access_request", $"{who} yêu cầu quyền \"{level.Name}\" trên \"{node.Name}\"", request.Message, $"/access-requests?item={node.Id}", ct);
        await audit.LogAsync(new AuditEntry(AuditAction.AccessRequested, GrantItemPermissionHandler.Kind(node.Kind), node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId,
            new { request = request.Id, level = level.Code }), ct);
        return request.Id;
    }

    public async Task<IReadOnlyList<AccessRequestDto>> Handle(ListAccessRequestsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        var query = db.AccessRequests.AsNoTracking().Where(r => r.Status == AccessRequestStatus.Pending && r.ExpiresAt > now);
        if (q.Mine) query = db.AccessRequests.AsNoTracking().Where(r => r.RequesterId == uid);
        else if (q.ItemId is { } item)
        {
            if (!await AccessRequestRules.CanDecideAsync(db, engine, uid, item, ct)) throw new NotFoundException("Không tìm thấy tư liệu.");
            query = query.Where(r => r.SecurableId == item);
        }
        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        if (!q.Mine && q.ItemId is null)
        {
            // Every item I manage: checked one by one (the list is short and recent).
            var mine = new List<AccessRequest>();
            foreach (var r in rows)
                if (await AccessRequestRules.CanDecideAsync(db, engine, uid, r.SecurableId, ct)) mine.Add(r);
            rows = mine;
        }
        var ids = rows.Select(r => r.SecurableId).Distinct().ToList();
        var nodes = await db.Securables.AsNoTracking().Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var users = rows.Select(r => r.RequesterId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => users.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var levels = await db.PermissionLevels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return rows.Where(r => nodes.ContainsKey(r.SecurableId)).Select(r => new AccessRequestDto(r.Id, r.SecurableId,
            // Someone waiting for access sees their own request without the item's name.
            q.Mine && !nodes[r.SecurableId].IsDeleted && r.Status != AccessRequestStatus.Approved ? "(tư liệu chưa được cấp quyền)" : nodes[r.SecurableId].Name,
            nodes[r.SecurableId].Kind, r.RequesterId, names.GetValueOrDefault(r.RequesterId, ""), r.RequestedLevelId, levels.GetValueOrDefault(r.RequestedLevelId, ""), r.Message,
            r.Status == AccessRequestStatus.Pending && r.ExpiresAt <= now ? AccessRequestStatus.Expired : r.Status, r.CreatedAt, r.DecidedBy, r.DecidedLevelId, r.DecidedAt)).ToList();
    }

    public async Task<Unit> Handle(DecideAccessRequestCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var request = await db.AccessRequests.FirstOrDefaultAsync(r => r.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy yêu cầu.");
        if (!await AccessRequestRules.CanDecideAsync(db, engine, uid, request.SecurableId, ct))
            throw new ForbiddenException("Bạn không phải người duyệt yêu cầu truy cập của mục này.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (request.Status != AccessRequestStatus.Pending || request.ExpiresAt <= now) throw new ConflictException("Yêu cầu đã được xử lý hoặc đã hết hạn.");
        var node = await NodeAsync(request.SecurableId, ct);
        var levelId = cmd.LevelId ?? request.RequestedLevelId;
        var level = await db.PermissionLevels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == levelId, ct) ?? throw new NotFoundException("Không tìm thấy cấp quyền.");
        request.Status = cmd.Approve ? AccessRequestStatus.Approved : AccessRequestStatus.Rejected;
        request.DecidedBy = uid;
        request.DecidedAt = now;
        request.DecidedLevelId = cmd.Approve ? level.Id : null;
        request.DecisionNote = cmd.Note?.Trim();
        await db.SaveChangesAsync(ct);
        if (cmd.Approve) await editor.GrantAsync(node.Id, PrincipalKind.User, request.RequesterId, level.Id, null, uid, ct);
        var link = node.Kind == SecurableKind.File ? $"/files/{node.Id}" : "/shared";
        await notify.NotifyUserAsync(request.RequesterId, "access_request",
            cmd.Approve ? $"Yêu cầu quyền truy cập \"{node.Name}\" đã được duyệt ({level.Name})" : "Yêu cầu quyền truy cập của bạn đã bị từ chối",
            request.DecisionNote, cmd.Approve ? link : null, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.AccessRequestDecided, GrantItemPermissionHandler.Kind(node.Kind), node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId,
            new { request = request.Id, approved = cmd.Approve, level = level.Code }), ct);
        return Unit.Value;
    }
}
