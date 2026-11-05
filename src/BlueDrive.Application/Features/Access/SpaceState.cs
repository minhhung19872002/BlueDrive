using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// What a Space's status and settings take away from everyone's rights inside it (spec 6), the same for the engine and the reference evaluator:
/// locked or archived = read only; deleted (in the system trash) = nothing; downloads off = no download / print / copy, and the Activity tab off =
/// no activity, except for who manages the Space.
/// </summary>
public sealed record SpaceState(bool Locked, bool Deleted, bool NoDownload, bool ActivityManagersOnly)
{
    public static readonly SpaceState None = new(false, false, false, false);

    public static async Task<SpaceState> LoadAsync(IAppDbContext db, Guid? root, CancellationToken ct) =>
        root is not { } id ? None
        : await db.Workspaces.AsNoTracking().Where(w => w.Id == id)
              .Select(w => new SpaceState(w.IsLocked || w.Status == SpaceStatus.Archived, w.Status == SpaceStatus.Deleted, !w.AllowDownload, !w.ActivityForMembers))
              .FirstOrDefaultAsync(ct) ?? None;

    /// <summary>Applied after <see cref="Restrictions.Apply"/> (write removal for "locked" stays with the caller, which reports it apart).</summary>
    public AccessRight Apply(AccessRight rights)
    {
        if (Deleted) return AccessRight.None;
        if (rights.Has(AccessRight.ManageSpace)) return rights;
        if (NoDownload) rights &= ~(AccessRight.Download | AccessRight.Print | AccessRight.CopyContent);
        if (ActivityManagersOnly) rights &= ~AccessRight.ViewActivity;
        return rights;
    }
}
