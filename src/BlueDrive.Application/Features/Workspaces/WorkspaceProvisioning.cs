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
            user.QuotaBytes = await settings.GetAsync(SettingKeys.QuotaPersonalDefaultGb, 10, ct) * Gb;
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
}
