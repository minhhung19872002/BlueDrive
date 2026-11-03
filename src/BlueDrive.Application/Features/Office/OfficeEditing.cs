using System.Collections.Concurrent;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Office;

/// <summary>
/// Who is co-editing which document right now (spec 13A "ai đang mở/sửa file"), from the Document Server callbacks: status 1 carries the
/// current editors (sent whenever someone joins or leaves), 2/4 mean the document was closed. In memory: after a restart of the API the
/// next callback restores it; an entry not refreshed for 12 hours is dropped.
/// </summary>
public sealed class OfficeEditingTracker(TimeProvider clock)
{
    private static readonly TimeSpan Stale = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<Guid, (IReadOnlyList<Guid> Users, DateTime At)> _editing = new();

    public void OnCallback(Guid fileId, int status, IReadOnlyList<string> users)
    {
        if (status is 2 or 3 or 4 || (status == 1 && users.Count == 0)) _editing.TryRemove(fileId, out _);
        else if (status == 1) _editing[fileId] = (users.Select(u => Guid.TryParse(u, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList(),
            clock.GetUtcNow().UtcDateTime);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Current()
    {
        var limit = clock.GetUtcNow().UtcDateTime - Stale;
        foreach (var (id, v) in _editing.Where(e => e.Value.At < limit).ToList()) _editing.TryRemove(id, out _);
        return _editing.ToDictionary(e => e.Key, e => e.Value.Users);
    }
}

public sealed record EditingFileDto(Guid FileId, string Name, IReadOnlyList<string> Editors);

/// <summary>Documents being co-edited now that the user can see (BlueDrive Desktop: flyout, Explorer status, soft-lock warning).</summary>
public sealed record ListEditingFilesQuery : IRequest<IReadOnlyList<EditingFileDto>>;

public sealed class ListEditingFilesHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, OfficeEditingTracker tracker)
    : IRequestHandler<ListEditingFilesQuery, IReadOnlyList<EditingFileDto>>
{
    public async Task<IReadOnlyList<EditingFileDto>> Handle(ListEditingFilesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var editing = tracker.Current();
        if (editing.Count == 0) return [];
        var ids = editing.Keys.ToList();
        var files = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id) && !f.IsDeleted).Select(f => new { f.Id, f.Name }).ToListAsync(ct);
        var userIds = editing.Values.SelectMany(v => v).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var result = new List<EditingFileDto>();
        foreach (var f in files)
            if (await auth.CanAsync(uid, ResourceType.File, f.Id, AccessLevel.View, ct))
                result.Add(new EditingFileDto(f.Id, f.Name, editing[f.Id].Select(u => names.GetValueOrDefault(u, "Người dùng")).ToList()));
        return result;
    }
}
