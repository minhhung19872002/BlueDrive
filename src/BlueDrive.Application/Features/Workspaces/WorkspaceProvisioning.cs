using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Workspaces;

public static class WorkspaceProvisioning
{
    public const long Gb = 1024L * 1024 * 1024;

    /// <summary>Every user owns exactly one personal workspace ("Tài liệu của tôi"). Adds it to the context if missing.</summary>
    public static async Task<Workspace> EnsurePersonalWorkspaceAsync(IAppDbContext db, ISettingsService settings, User user, CancellationToken ct)
    {
        var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.OwnerId == user.Id && w.Type == WorkspaceType.Personal, ct)
                       ?? db.Workspaces.Local.FirstOrDefault(w => w.OwnerId == user.Id && w.Type == WorkspaceType.Personal);
        if (existing is not null) return existing;
        if (user.QuotaBytes <= 0)
            user.QuotaBytes = (long)((await DefaultQuotaGbAsync(db, user, ct) ?? await settings.GetAsync(SettingKeys.QuotaPersonalDefaultGb, 10, ct)) * Gb);
        var ws = new Workspace
        {
            Name = $"Tài liệu của {user.FullName}",
            OwnerId = user.Id,
            OrganizationId = user.OrganizationId,
            Type = WorkspaceType.Personal,
            QuotaBytes = user.QuotaBytes,
        };
        ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = user.Id, MemberRole = WorkspaceMemberRole.Owner, AddedAt = DateTime.UtcNow });
        db.Workspaces.Add(ws);
        return ws;
    }

    /// <summary>Spec 8.2: the largest default of the user's roles, otherwise that of their unit or the nearest unit above with one.</summary>
    public static async Task<double?> DefaultQuotaGbAsync(IAppDbContext db, User user, CancellationToken ct)
    {
        var roleIds = user.UserRoles.Select(r => r.RoleId).ToList();
        if (roleIds.Count > 0 && await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id) && r.DefaultQuotaGb != null).MaxAsync(r => r.DefaultQuotaGb, ct) is { } byRole)
            return byRole;
        if (user.OrganizationId is not { } orgId) return null;
        var path = await db.Organizations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Path).FirstOrDefaultAsync(ct);
        var chain = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).ToList();
        var defaults = await db.Organizations.AsNoTracking().Where(o => chain.Contains(o.Id) && o.DefaultQuotaGb != null).ToDictionaryAsync(o => o.Id, o => o.DefaultQuotaGb, ct);
        return Enumerable.Reverse(chain).Select(id => defaults.GetValueOrDefault(id)).FirstOrDefault(d => d is not null);
    }
}
