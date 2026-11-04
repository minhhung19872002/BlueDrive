using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

// Spec 1.1 "Chuyển quyền sở hữu" (Drive của tôi, like Google): the owner offers an item to someone in the organization; once they accept it is
// theirs (it goes to their Drive của tôi, keeping what was given to people directly) and the former owner keeps "Biên tập".

public sealed record OwnershipTransferDto(Guid Id, Guid ItemId, string ItemType, string ItemName, Guid FromUserId, string FromName, Guid ToUserId, string ToName,
    OwnershipTransferStatus Status, DateTime CreatedAt, DateTime? DecidedAt);

public sealed record OfferOwnershipCommand(Guid ItemId, Guid ToUserId) : IRequest<Guid>;

public sealed record ListOwnershipTransfersQuery(bool Incoming = true) : IRequest<IReadOnlyList<OwnershipTransferDto>>;

public sealed record GetOwnershipTransferQuery(Guid Id) : IRequest<OwnershipTransferDto>;

/// <summary>accept / decline (the recipient), cancel (who offered it).</summary>
public sealed record DecideOwnershipTransferCommand(Guid Id, string Action) : IRequest<Unit>;

public sealed class OwnershipHandlers(IAppDbContext db, ICurrentUser me, IUserNotifier notify, IAuditLogger audit, AclEditor acl, MoveItemsHandler mover, TimeProvider clock,
    IItemPermissionService engine)
    : IRequestHandler<OfferOwnershipCommand, Guid>, IRequestHandler<ListOwnershipTransfersQuery, IReadOnlyList<OwnershipTransferDto>>,
      IRequestHandler<GetOwnershipTransferQuery, OwnershipTransferDto>, IRequestHandler<DecideOwnershipTransferCommand, Unit>
{
    private async Task<(Securable Node, Workspace Drive)> OwnedAsync(Guid itemId, Guid uid, CancellationToken ct)
    {
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == itemId && !s.IsDeleted && (s.Kind == SecurableKind.File || s.Kind == SecurableKind.Folder), ct)
                   ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        if (node.PersonalDriveId is not { } driveId) throw new AppException("Chỉ chuyển quyền sở hữu được tư liệu trong Drive của tôi; tư liệu trong Space thuộc về Space.");
        var drive = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == driveId, ct);
        if (drive.OwnerId != uid) throw new ForbiddenException("Chỉ chủ sở hữu được chuyển quyền sở hữu.");
        return (node, drive);
    }

    public async Task<Guid> Handle(OfferOwnershipCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        // Something one may not see is not found (its existence is not told).
        await engine.DemandAsync(uid, cmd.ItemId, AccessRight.ViewItems, ct);
        var (node, _) = await OwnedAsync(cmd.ItemId, uid, ct);
        if (cmd.ToUserId == uid) throw new AppException("Bạn đang là chủ sở hữu.");
        var to = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == cmd.ToUserId && u.IsActive && !u.IsGuest, ct)
                 ?? throw new NotFoundException("Không tìm thấy người nhận (phải là người dùng trong đơn vị).");
        var now = clock.GetUtcNow().UtcDateTime;
        // One offer at a time per item: a new one replaces the pending one.
        await db.OwnershipTransfers.Where(t => t.ItemId == node.Id && t.Status == OwnershipTransferStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, OwnershipTransferStatus.Cancelled).SetProperty(t => t.DecidedAt, now), ct);
        var transfer = new OwnershipTransfer
        {
            ItemId = node.Id, ItemType = node.Kind == SecurableKind.File ? "file" : "folder", ItemName = node.Name, FromUserId = uid, ToUserId = to.Id, CreatedAt = now,
        };
        db.OwnershipTransfers.Add(transfer);
        await db.SaveChangesAsync(ct);
        var from = await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstAsync(ct);
        await notify.NotifyUserAsync(to.Id, "ownership", $"{from} muốn chuyển quyền sở hữu \"{node.Name}\" cho bạn", "Bấm để chấp nhận hoặc từ chối.",
            $"/ownership/{transfer.Id}", ct);
        return transfer.Id;
    }

    public async Task<IReadOnlyList<OwnershipTransferDto>> Handle(ListOwnershipTransfersQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var rows = await db.OwnershipTransfers.AsNoTracking().Where(t => q.Incoming ? t.ToUserId == uid : t.FromUserId == uid)
            .OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<OwnershipTransferDto> Handle(GetOwnershipTransferQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var t = await db.OwnershipTransfers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id && (x.ToUserId == uid || x.FromUserId == uid), ct)
                ?? throw new NotFoundException("Không tìm thấy yêu cầu chuyển quyền sở hữu.");
        return (await MapAsync([t], ct))[0];
    }

    public async Task<Unit> Handle(DecideOwnershipTransferCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var t = await db.OwnershipTransfers.FirstOrDefaultAsync(x => x.Id == cmd.Id && (x.ToUserId == uid || x.FromUserId == uid), ct)
                ?? throw new NotFoundException("Không tìm thấy yêu cầu chuyển quyền sở hữu.");
        if (t.Status != OwnershipTransferStatus.Pending) throw new ConflictException("Yêu cầu đã được xử lý.");
        var now = clock.GetUtcNow().UtcDateTime;
        switch (cmd.Action)
        {
            case "cancel" when t.FromUserId == uid:
                t.Status = OwnershipTransferStatus.Cancelled;
                break;
            case "decline" when t.ToUserId == uid:
                t.Status = OwnershipTransferStatus.Declined;
                await notify.NotifyUserAsync(t.FromUserId, "ownership", $"Yêu cầu chuyển quyền sở hữu \"{t.ItemName}\" đã bị từ chối", null, null, ct);
                break;
            case "accept" when t.ToUserId == uid:
                // Still the former owner's (it may have been moved or deleted since the offer).
                var (node, drive) = await OwnedAsync(t.ItemId, t.FromUserId, ct);
                var target = await db.Workspaces.Where(w => w.OwnerId == uid && w.Type == WorkspaceType.Personal).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct)
                             ?? throw new AppException("Tài khoản của bạn chưa có Drive của tôi.");
                // The former owner keeps "Biên tập" (a direct grant, which goes with the item).
                await acl.GrantAsync(node.Id, PrincipalKind.User, t.FromUserId, SystemLevels.Editor, null, uid, ct, "Chủ sở hữu trước");
                await mover.MoveAsync(uid, new MoveItemsCommand([new ItemRef(t.ItemType, t.ItemId)], target, null), trusted: true, ct);
                t.Status = OwnershipTransferStatus.Accepted;
                await notify.NotifyUserAsync(t.FromUserId, "ownership", $"Đã chuyển quyền sở hữu \"{t.ItemName}\"; bạn vẫn có quyền Biên tập", null,
                    t.ItemType == "file" ? $"/files/{t.ItemId}" : "/shared", ct);
                await audit.LogAsync(new AuditEntry(AuditAction.OwnershipTransferred, t.ItemType == "file" ? ResourceType.File : ResourceType.Folder, t.ItemId, t.ItemName,
                    drive.Id, new { from = t.FromUserId, to = uid }), ct);
                break;
            default:
                throw new ForbiddenException("Bạn không thực hiện được thao tác này.");
        }
        t.DecidedAt = now;
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }

    private async Task<List<OwnershipTransferDto>> MapAsync(List<OwnershipTransfer> rows, CancellationToken ct)
    {
        var ids = rows.SelectMany(r => new[] { r.FromUserId, r.ToUserId }).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return rows.Select(r => new OwnershipTransferDto(r.Id, r.ItemId, r.ItemType, r.ItemName, r.FromUserId, names.GetValueOrDefault(r.FromUserId, ""), r.ToUserId,
            names.GetValueOrDefault(r.ToUserId, ""), r.Status, r.CreatedAt, r.DecidedAt)).ToList();
    }
}
