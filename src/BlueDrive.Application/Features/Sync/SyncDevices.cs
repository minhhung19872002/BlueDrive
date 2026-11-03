using System.Security.Cryptography;
using System.Text;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Auth;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sync;

public sealed record DeviceDto(
    Guid Id, Guid UserId, string UserName, string UserFullName, string Name, string Os, string AppVersion, DeviceStatus Status,
    DateTime CreatedAt, DateTime LastSeenAt, string? LastIp, DateTime? RevokedAt, DateTime? WipeRequestedAt, DateTime? WipedAt, bool IsCurrent);

internal static class DeviceProjection
{
    public static IQueryable<DeviceDto> Project(IQueryable<SyncDevice> q, Guid? currentDeviceId) => q.Select(d => new DeviceDto(
        d.Id, d.UserId, d.User!.Username, d.User.FullName, d.Name, d.Os, d.AppVersion, d.Status, d.CreatedAt, d.LastSeenAt, d.LastIp,
        d.RevokedAt, d.WipeRequestedAt, d.WipedAt, d.Id == currentDeviceId));
}

// ---------------------------------------------------------------- register / heartbeat (desktop app)

/// <summary>POST /sync/devices: refreshes the registration of the calling device (name, OS, version) and returns its id.</summary>
public sealed record RegisterDeviceCommand(string Name, string? Os, string? AppVersion) : IRequest<DeviceDto>;

public sealed class RegisterDeviceHandler(IAppDbContext db, ICurrentUser me, SyncPolicyService policy, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<RegisterDeviceCommand, DeviceDto>
{
    public async Task<DeviceDto> Handle(RegisterDeviceCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        var deviceId = me.DeviceId ?? throw new ForbiddenException("Chỉ ứng dụng BlueDrive Desktop (đăng nhập qua trình duyệt) mới đăng ký được thiết bị.");
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && x.UserId == uid, ct) ?? throw new NotFoundException("Không tìm thấy thiết bị.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrWhiteSpace(cmd.Name)) d.Name = cmd.Name.Trim()[..Math.Min(cmd.Name.Trim().Length, 200)];
        if (cmd.Os is not null) d.Os = cmd.Os.Trim()[..Math.Min(cmd.Os.Trim().Length, 200)];
        if (cmd.AppVersion is not null) d.AppVersion = cmd.AppVersion.Trim()[..Math.Min(cmd.AppVersion.Trim().Length, 50)];
        d.LastSeenAt = now;
        d.LastIp = me.Ip;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, Detail: new { entity = "device", device = d.Name, d.Os, d.AppVersion }), ct);
        return await DeviceProjection.Project(db.Devices.AsNoTracking().Where(x => x.Id == d.Id), deviceId).FirstAsync(ct);
    }
}

/// <summary>
/// Device status polled by the client (and pushed on /hubs/sync). A pending remote wipe is returned as a command signed with the
/// device secret (HMAC-SHA256 over "wipe:{deviceId}:{requestedAt:O}") so a spoofed response cannot wipe the computer.
/// </summary>
public sealed record DeviceStatusDto(Guid DeviceId, DeviceStatus Status, bool UpdateRequired, string MinClientVersion, WipeCommandDto? Wipe);

public sealed record WipeCommandDto(DateTime RequestedAt, string Signature);

public sealed record GetDeviceStatusQuery(string? AppVersion) : IRequest<DeviceStatusDto>;

public sealed class GetDeviceStatusHandler(IAppDbContext db, ICurrentUser me, SyncPolicyService policy, ISensitiveDataProtector pii, TimeProvider clock)
    : IRequestHandler<GetDeviceStatusQuery, DeviceStatusDto>
{
    public async Task<DeviceStatusDto> Handle(GetDeviceStatusQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var deviceId = me.DeviceId ?? throw new ForbiddenException("Phiên đăng nhập này không thuộc thiết bị đồng bộ.");
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && x.UserId == uid, ct) ?? throw new NotFoundException("Không tìm thấy thiết bị.");
        d.LastSeenAt = clock.GetUtcNow().UtcDateTime;
        d.LastIp = me.Ip;
        if (q.AppVersion is { Length: > 0 } v) d.AppVersion = v.Trim()[..Math.Min(v.Trim().Length, 50)];
        await db.SaveChangesAsync(ct);
        var p = await policy.GetAsync(ct);
        WipeCommandDto? wipe = null;
        if (d.Status == DeviceStatus.WipePending && d.WipeRequestedAt is { } at && pii.Unprotect(d.SecretProtected) is { } secret)
            wipe = new WipeCommandDto(at, DeviceCommands.Sign(secret, d.Id, at));
        return new DeviceStatusDto(d.Id, d.Status, SyncPolicyService.IsOlder(d.AppVersion, p.MinClientVersion), p.MinClientVersion, wipe);
    }
}

public static class DeviceCommands
{
    public static string Sign(string secret, Guid deviceId, DateTime requestedAt) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"wipe:{deviceId}:{requestedAt:O}")));
}

/// <summary>The client reports that the remote wipe is done: the device ends (sessions revoked).</summary>
public sealed record ConfirmWipedCommand : IRequest<Unit>;

public sealed class ConfirmWipedHandler(IAppDbContext db, ICurrentUser me, SessionRevoker revoker, IAuditLogger audit, TimeProvider clock) : IRequestHandler<ConfirmWipedCommand, Unit>
{
    public async Task<Unit> Handle(ConfirmWipedCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var deviceId = me.DeviceId ?? throw new ForbiddenException("Phiên đăng nhập này không thuộc thiết bị đồng bộ.");
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && x.UserId == uid, ct) ?? throw new NotFoundException("Không tìm thấy thiết bị.");
        if (d.Status != DeviceStatus.WipePending) throw new ConflictException("Thiết bị không có lệnh xóa dữ liệu.");
        d.Status = DeviceStatus.Wiped;
        d.WipedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await revoker.RevokeAsync(await db.UserSessions.Where(s => s.DeviceId == d.Id).Select(s => s.Id).ToListAsync(ct), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.SessionRevoke, Detail: new { entity = "device", action = "wiped", deviceId = d.Id, device = d.Name, source = "desktop_sync" }), ct);
        return Unit.Value;
    }
}

// ---------------------------------------------------------------- management (web: own devices, administrators: all)

public sealed record ListMyDevicesQuery : IRequest<IReadOnlyList<DeviceDto>>;

public sealed class ListMyDevicesHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListMyDevicesQuery, IReadOnlyList<DeviceDto>>
{
    public async Task<IReadOnlyList<DeviceDto>> Handle(ListMyDevicesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        return await DeviceProjection.Project(db.Devices.AsNoTracking().Where(d => d.UserId == uid).OrderByDescending(d => d.LastSeenAt), me.DeviceId).ToListAsync(ct);
    }
}

public sealed record ListDevicesQuery(string? Q, DeviceStatus? Status, int Page = 1, int PageSize = 20) : IRequest<PagedResult<DeviceDto>>;

public sealed class ListDevicesHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListDevicesQuery, PagedResult<DeviceDto>>
{
    public async Task<PagedResult<DeviceDto>> Handle(ListDevicesQuery q, CancellationToken ct)
    {
        var query = db.Devices.AsNoTracking();
        if (q.Status.HasValue) query = query.Where(d => d.Status == q.Status);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(d => DbFn.Unaccent(d.Name).Contains(term) || DbFn.Unaccent(d.User!.FullName).Contains(term) || d.User.Username.Contains(term));
        }
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var total = await query.CountAsync(ct);
        var items = await DeviceProjection.Project(query.OrderByDescending(d => d.LastSeenAt).Skip((page - 1) * size).Take(size), me.DeviceId).ToListAsync(ct);
        return new PagedResult<DeviceDto>(items, total, page, size);
    }
}

/// <summary>Revoke (stop syncing, sign out) or request a remote wipe. Owners act on their devices, device.admin on any.</summary>
public sealed record DeviceActionCommand(Guid Id, bool Wipe) : IRequest<DeviceDto>;

public sealed class DeviceActionHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, SessionRevoker revoker, IUserNotifier notify,
    ISyncNotifier sync, IAuditLogger audit, TimeProvider clock) : IRequestHandler<DeviceActionCommand, DeviceDto>
{
    public async Task<DeviceDto> Handle(DeviceActionCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var d = await db.Devices.Include(x => x.User).FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy thiết bị.");
        if (d.UserId != uid && !await perms.HasAsync(uid, Perm.DeviceAdmin, ct)) throw new ForbiddenException();
        if (d.Status is DeviceStatus.Wiped || (d.Status == DeviceStatus.Revoked && !cmd.Wipe))
            throw new ConflictException("Thiết bị đã bị thu hồi.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (cmd.Wipe)
        {
            // Sessions stay alive until the client confirms, so it can receive the signed wipe command.
            d.Status = DeviceStatus.WipePending;
            d.WipeRequestedAt = now;
            d.WipeRequestedBy = uid;
            await db.SaveChangesAsync(ct);
            await sync.DeviceStatusChangedAsync(d.Id, "wipe_pending", ct);
        }
        else
        {
            d.Status = DeviceStatus.Revoked;
            d.RevokedAt = now;
            d.RevokedBy = uid;
            await db.SaveChangesAsync(ct);
            await revoker.RevokeAsync(await db.UserSessions.Where(s => s.DeviceId == d.Id).Select(s => s.Id).ToListAsync(ct), ct);
            await sync.DeviceStatusChangedAsync(d.Id, "revoked", ct);
        }
        if (d.UserId != uid)
            await notify.NotifyUserAsync(d.UserId, "device", cmd.Wipe ? $"Quản trị viên yêu cầu xóa dữ liệu BlueDrive trên máy \"{d.Name}\"" : $"Máy \"{d.Name}\" đã bị thu hồi quyền đồng bộ",
                null, "/devices", ct);
        await audit.LogAsync(new AuditEntry(AuditAction.SessionRevoke, Detail: new { entity = "device", action = cmd.Wipe ? "wipe" : "revoke", deviceId = d.Id, device = d.Name, owner = d.User!.Username }), ct);
        return await DeviceProjection.Project(db.Devices.AsNoTracking().Where(x => x.Id == d.Id), me.DeviceId).FirstAsync(ct);
    }
}
