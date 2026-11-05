using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

// Spec 5.4: sharing policies of an item and of a Space.

/// <summary>What the sharing screens need: how far sharing may go here and why.</summary>
public sealed record ItemSharingPolicyDto(ExternalSharing External, string ExternalLabel, string ExternalSource, int AnyoneLinkMaxDays, bool AnyoneLinksAllowed,
    bool MembersShareOutside, bool AllowItemSharing, bool InSpace, ExternalSharing? ItemExternal, bool CanChangeItem);

public sealed record GetItemSharingPolicyQuery(Guid ItemId) : IRequest<ItemSharingPolicyDto>;

/// <summary>The item's own tier (null = as above); a folder's applies to everything under it.</summary>
public sealed record SetItemExternalSharingCommand(Guid ItemId, ExternalSharing? External) : IRequest<Unit>;

public sealed record SpaceSharingPolicyDto(Guid WorkspaceId, ExternalSharing? External, ExternalSharing SystemExternal, bool MembersShareOutside, bool AllowItemSharing,
    AccessRequestDeciders AccessRequestDeciders, bool CanEdit);

public sealed record GetSpaceSharingPolicyQuery(Guid WorkspaceId) : IRequest<SpaceSharingPolicyDto>;

public sealed record SetSpaceSharingPolicyCommand(Guid WorkspaceId, ExternalSharing? External, bool MembersShareOutside, bool AllowItemSharing,
    AccessRequestDeciders AccessRequestDeciders) : IRequest<Unit>;

public sealed class SharingPolicyHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, SharingPolicy policy, ISettingsService settings, IAuditLogger audit)
    : IRequestHandler<GetItemSharingPolicyQuery, ItemSharingPolicyDto>, IRequestHandler<SetItemExternalSharingCommand, Unit>,
      IRequestHandler<GetSpaceSharingPolicyQuery, SpaceSharingPolicyDto>, IRequestHandler<SetSpaceSharingPolicyCommand, Unit>
{
    public async Task<ItemSharingPolicyDto> Handle(GetItemSharingPolicyQuery q, CancellationToken ct)
    {
        var rights = await engine.DemandAsync(me.RequireUserId(), q.ItemId, AccessRight.ViewItems, ct);
        var p = await policy.EffectiveAsync(q.ItemId, ct);
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == q.ItemId, ct);
        return new ItemSharingPolicyDto(p.External, SharingPolicy.Label(p.External), p.ExternalSource, p.AnyoneLinkMaxDays, p.External == ExternalSharing.Anyone,
            p.MembersShareOutside, p.AllowItemSharing, p.InSpace, p.ItemExternal,
            rights.Has(AccessRight.ManagePermissions) && node.Kind is SecurableKind.Folder or SecurableKind.File);
    }

    public async Task<Unit> Handle(SetItemExternalSharingCommand cmd, CancellationToken ct)
    {
        await engine.DemandAsync(me.RequireUserId(), cmd.ItemId, AccessRight.ManagePermissions, ct);
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == cmd.ItemId, ct);
        if (node.Kind == SecurableKind.Folder)
            await db.Folders.Where(f => f.Id == cmd.ItemId).ExecuteUpdateAsync(u => u.SetProperty(f => f.ExternalSharing, cmd.External), ct);
        else if (node.Kind == SecurableKind.File)
            await db.Files.Where(f => f.Id == cmd.ItemId).ExecuteUpdateAsync(u => u.SetProperty(f => f.ExternalSharing, cmd.External), ct);
        else throw new AppException("Chính sách của Space đặt ở phần cài đặt Space.");
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, GrantItemPermissionHandler.Kind(node.Kind), node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId,
            new { externalSharing = cmd.External?.ToString() ?? "inherit" }), ct);
        return Unit.Value;
    }

    public async Task<SpaceSharingPolicyDto> Handle(GetSpaceSharingPolicyQuery q, CancellationToken ct)
    {
        var rights = await engine.DemandAsync(me.RequireUserId(), q.WorkspaceId, AccessRight.ViewItems, ct);
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == q.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy không gian.");
        var system = SharingPolicy.Parse(await settings.GetAsync(BlueDrive.Application.Settings.SettingKeys.ShareExternal, "anyone", ct));
        return new SpaceSharingPolicyDto(ws.Id, ws.ExternalSharing, system, ws.MembersShareOutside, ws.AllowItemSharing, ws.AccessRequestDeciders,
            rights.Has(AccessRight.ManageSpace));
    }

    public async Task<Unit> Handle(SetSpaceSharingPolicyCommand cmd, CancellationToken ct)
    {
        await engine.DemandAsync(me.RequireUserId(), cmd.WorkspaceId, AccessRight.ManageSpace, ct);
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == cmd.WorkspaceId, ct) ?? throw new NotFoundException("Không tìm thấy không gian.");
        var before = new { ws.ExternalSharing, ws.MembersShareOutside, ws.AllowItemSharing, ws.AccessRequestDeciders };
        ws.ExternalSharing = cmd.External;
        ws.MembersShareOutside = cmd.MembersShareOutside;
        ws.AllowItemSharing = cmd.AllowItemSharing;
        ws.AccessRequestDeciders = cmd.AccessRequestDeciders;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Workspace, ws.Id, ws.Name, ws.Id,
            new { before, after = new { cmd.External, cmd.MembersShareOutside, cmd.AllowItemSharing, cmd.AccessRequestDeciders } }), ct);
        return Unit.Value;
    }
}
