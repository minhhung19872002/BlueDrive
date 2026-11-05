using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Workspaces;

// Spec 6: the Space's home page and its "Quyền của Space" page (SharePoint "Advanced permissions").

public sealed record SpaceHomeMemberDto(string Name, WorkspaceMemberRole Role, bool IsGroup);

public sealed record SpaceLibraryDto(Guid Id, string Name, bool IsDefault);

public sealed record SpaceHomeDto(Guid Id, string Name, string? Description, string? CoverUrl, int MemberCount, IReadOnlyList<SpaceHomeMemberDto> Members,
    IReadOnlyList<DriveItemDto> RecentFiles, long UsedBytes, long QuotaBytes, IReadOnlyList<SpaceLibraryDto> Libraries, int UniqueScopes, bool CanManage);

public sealed record GetSpaceHomeQuery(Guid WorkspaceId) : IRequest<SpaceHomeDto>;

/// <summary>A folder or file of the Space with permissions of its own: what it has besides the Space's members.</summary>
public sealed record UniqueScopeDto(Guid Id, string Name, SecurableKind Kind, string Location, int Entries, IReadOnlyList<string> Direct, int Links);

public sealed record ListUniqueScopesQuery(Guid WorkspaceId) : IRequest<IReadOnlyList<UniqueScopeDto>>;

public sealed class SpaceHomeHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, AccessFilter filter, IContentTicketService tickets)
    : IRequestHandler<GetSpaceHomeQuery, SpaceHomeDto>, IRequestHandler<ListUniqueScopesQuery, IReadOnlyList<UniqueScopeDto>>
{
    public async Task<SpaceHomeDto> Handle(GetSpaceHomeQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var rights = await engine.DemandAsync(uid, q.WorkspaceId, AccessRight.ViewItems, ct);
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == q.WorkspaceId && w.Status != SpaceStatus.Deleted, ct)
                 ?? throw new NotFoundException("Không tìm thấy không gian.");
        // Members are shown to who manages the Space only.
        var showsMembers = rights.Has(AccessRight.ManagePermissions);
        var users = !showsMembers ? [] : await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == ws.Id).OrderBy(m => m.MemberRole).ThenBy(m => m.User!.FullName)
            .Select(m => new SpaceHomeMemberDto(m.User!.FullName, m.MemberRole, false)).ToListAsync(ct);
        var groupRows = !showsMembers ? [] : await db.WorkspaceGroupMembers.AsNoTracking().Where(m => m.WorkspaceId == ws.Id).ToListAsync(ct);
        var groupNames = await PrincipalDisplay.ResolveAsync(db, groupRows.Select(g => g.PrincipalId), ct);
        var members = groupRows.Select(g => new SpaceHomeMemberDto(groupNames.GetValueOrDefault(g.PrincipalId, ("", null)).Name, g.MemberRole, true)).Concat(users).ToList();
        // Recent documents the viewer may see (security trimming in SQL).
        var viewer = await filter.ForAsync(uid, ct);
        var recent = await DriveProjection.Files(db, filter.Files(db.Files.AsNoTracking().Where(f => f.WorkspaceId == ws.Id && !f.IsDeleted), viewer)
            .OrderByDescending(f => f.UpdatedAt ?? f.CreatedAt).Take(8), uid).ToListAsync(ct);
        string? cover = null;
        if (ws.CoverFileId is { } c && await engine.HasAsync(uid, c, AccessRight.ViewItems, ct)
            && await db.Files.AnyAsync(f => f.Id == c && !f.IsDeleted && f.ThumbnailKey != null, ct))
            cover = ThumbnailUrls.For(tickets, uid, c, large: true);
        var libraries = await db.Libraries.AsNoTracking().Where(l => l.SpaceId == ws.Id || l.PersonalDriveId == ws.Id).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name)
            .Select(l => new SpaceLibraryDto(l.Id, l.Name, l.IsDefault)).ToListAsync(ct);
        var unique = await db.Securables.CountAsync(s => s.SpaceId == ws.Id && s.HasUniqueAcl && s.ParentId != null && !s.IsDeleted, ct);
        return new SpaceHomeDto(ws.Id, ws.Name, ws.Description, cover, members.Count, members.Take(12).ToList(), ThumbnailUrls.Apply(recent, tickets, uid),
            ws.UsedBytes, ws.QuotaBytes, libraries, unique, rights.Has(AccessRight.ManagePermissions));
    }

    public async Task<IReadOnlyList<UniqueScopeDto>> Handle(ListUniqueScopesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await engine.DemandAsync(uid, q.WorkspaceId, AccessRight.ManagePermissions, ct);
        var nodes = await db.Securables.AsNoTracking().Where(s => s.SpaceId == q.WorkspaceId && s.HasUniqueAcl && s.ParentId != null && !s.IsDeleted
                                                                  && (s.Kind == SecurableKind.Folder || s.Kind == SecurableKind.File))
            .OrderBy(s => s.Path).Take(1000).ToListAsync(ct);
        var ids = nodes.Select(n => n.Id).ToList();
        var entries = await db.AclEntries.AsNoTracking().Where(e => ids.Contains(e.ScopeId)).ToListAsync(ct);
        var names = await PrincipalDisplay.ResolveAsync(db, entries.Where(e => e.Source == AclSource.Direct).Select(e => e.PrincipalId), ct);
        // Location: names of the folders on the way, from the Space (they are all the Space's, shown to who manages it).
        var folderIds = nodes.SelectMany(n => n.Path.Split('.').Select(p => Guid.TryParseExact(p, "N", out var g) ? g : Guid.Empty)).Where(g => g != Guid.Empty).Distinct().ToList();
        var folderNames = await db.Folders.AsNoTracking().Where(f => folderIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        string Location(Guid self, string path) => string.Join(" / ", path.Split('.').Select(p => Guid.TryParseExact(p, "N", out var g) ? g : Guid.Empty)
            .Where(g => g != self && folderNames.ContainsKey(g)).Select(g => folderNames[g]).DefaultIfEmpty("(gốc)"));
        return nodes.Select(n =>
        {
            var own = entries.Where(e => e.ScopeId == n.Id).ToList();
            return new UniqueScopeDto(n.Id, n.Name, n.Kind, Location(n.Id, n.Path), own.Count,
                own.Where(e => e.Source == AclSource.Direct).Select(e => names.GetValueOrDefault(e.PrincipalId, ("(không xác định)", null)).Name).Distinct().ToList(),
                own.Count(e => e.Source == AclSource.Link));
        }).ToList();
    }
}
