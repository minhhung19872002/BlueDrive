using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Permissions;
using MediatR;

namespace BlueDrive.Application.Features.Access;

/// <summary>Rights of a user on an item, as atomic right names (spec 10: items/{id}/effective-permissions).</summary>
public sealed record EffectivePermissionsDto(Guid ItemId, Guid UserId, long Mask, IReadOnlyList<string> Rights, IReadOnlyList<string> Descriptions);

public sealed record GetEffectivePermissionsQuery(Guid ItemId, Guid? UserId) : IRequest<EffectivePermissionsDto>;

/// <summary>"Kiểm tra quyền" (spec 4.3 / 8.4): where each right of a user on an item comes from.</summary>
public sealed record ExplainPermissionsQuery(Guid ItemId, Guid? UserId) : IRequest<PermissionExplanation>;

public sealed class AccessQueryHandlers(IItemPermissionService engine, ICurrentUser me, IPermissionService rbac)
    : IRequestHandler<GetEffectivePermissionsQuery, EffectivePermissionsDto>, IRequestHandler<ExplainPermissionsQuery, PermissionExplanation>
{
    /// <summary>Oneself always; someone else only for who manages the item's permissions, or a settings administrator.</summary>
    private async Task<Guid> SubjectAsync(Guid itemId, Guid? userId, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (userId is not { } other || other == uid) return uid;
        if (await rbac.HasAsync(uid, Perm.SettingsEdit, ct)) return other;
        await engine.DemandAsync(uid, itemId, AccessRight.ManagePermissions, ct);
        return other;
    }

    public async Task<EffectivePermissionsDto> Handle(GetEffectivePermissionsQuery q, CancellationToken ct)
    {
        var subject = await SubjectAsync(q.ItemId, q.UserId, ct);
        var rights = await engine.GetEffectiveAsync(subject, q.ItemId, ct);
        var atoms = Enum.GetValues<AccessRight>().Where(r => r != AccessRight.None && rights.Has(r)).ToList();
        return new EffectivePermissionsDto(q.ItemId, subject, (long)rights, atoms.Select(a => a.ToString()).ToList(),
            atoms.Select(a => AccessRights.Descriptions.GetValueOrDefault(a, a.ToString())).ToList());
    }

    public async Task<PermissionExplanation> Handle(ExplainPermissionsQuery q, CancellationToken ct)
    {
        var subject = await SubjectAsync(q.ItemId, q.UserId, ct);
        return await engine.ExplainAsync(subject, q.ItemId, ct);
    }
}
