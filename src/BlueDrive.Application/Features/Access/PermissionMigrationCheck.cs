using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

/// <summary>
/// Spec 12: after projecting the current model onto the new one, checks on random (user, item) pairs that the new rights are not below
/// the current ones (old ⊆ new), and lists every difference. Two differences are deliberate (docs/migration-permissions.md, section 3)
/// and reported apart: the document administrator no longer sees everyone's content, and links are not part of the comparison.
/// </summary>
public sealed record VerifyPermissionMigrationQuery(int Samples = 10_000, int? Seed = null) : IRequest<PermissionMigrationReport>;

public sealed record PermissionDifference(Guid UserId, string User, ResourceType Type, Guid ItemId, string Item, AccessLevel Old, AccessLevel New);

public sealed record PermissionMigrationReport(int Pairs, int Same, int Wider, int AdminOnly, IReadOnlyList<PermissionDifference> Narrower, IReadOnlyList<PermissionDifference> WiderSample);

public sealed class VerifyPermissionMigrationHandler(IAppDbContext db, LegacyResourceAuthorizationService old, ReferencePermissionEvaluator reference)
    : IRequestHandler<VerifyPermissionMigrationQuery, PermissionMigrationReport>
{
    public async Task<PermissionMigrationReport> Handle(VerifyPermissionMigrationQuery q, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.Username }).ToListAsync(ct);
        var items = await db.Securables.AsNoTracking()
            .Where(s => !s.IsDeleted && (s.Kind == SecurableKind.Folder || s.Kind == SecurableKind.File || s.Kind == SecurableKind.Space || s.Kind == SecurableKind.Drive))
            .Select(s => new { s.Id, s.Kind, s.Name }).ToListAsync(ct);
        if (users.Count == 0 || items.Count == 0) return new PermissionMigrationReport(0, 0, 0, 0, [], []);
        var random = q.Seed is { } seed ? new Random(seed) : Random.Shared;
        var pairs = Math.Clamp(q.Samples, 1, 100_000);
        var allPairs = (long)users.Count * items.Count;
        var picks = allPairs <= pairs
            ? users.SelectMany(u => items.Select(i => (u, i))).ToList()
            : Enumerable.Range(0, pairs).Select(_ => (users[random.Next(users.Count)], items[random.Next(items.Count)])).ToList();

        var admins = new Dictionary<Guid, bool>();
        var contexts = new Dictionary<Guid, ReferencePermissionEvaluator.UserContext>();
        int same = 0, wider = 0, adminOnly = 0;
        var narrower = new List<PermissionDifference>();
        var widerSample = new List<PermissionDifference>();
        foreach (var (u, i) in picks)
        {
            var type = i.Kind switch { SecurableKind.File => ResourceType.File, SecurableKind.Folder => ResourceType.Folder, _ => ResourceType.Workspace };
            var was = await old.GetAccessAsync(u.Id, type, i.Id, ct);
            // What the old model let them do: a membership level "Chỉnh sửa" under a role without doc.edit only read and downloaded.
            // A locked workspace allowed reading only (the old model reports its level and the lock apart).
            var before = (was.Level >= AccessLevel.Edit && was.WorkspaceLocked) || (was.Level == AccessLevel.Edit && !was.CanEdit) ? AccessLevel.Download : was.Level;
            if (!contexts.TryGetValue(u.Id, out var context)) contexts[u.Id] = context = await reference.LoadUserAsync(u.Id, ct);
            var after = ReferencePermissionEvaluator.ToLevel(await reference.EvaluateAsync(context, i.Id, ct));
            if (before == after) { same++; continue; }
            var diff = new PermissionDifference(u.Id, u.Username, type, i.Id, i.Name, before, after);
            if (after > before)
            {
                wider++;
                if (widerSample.Count < 50) widerSample.Add(diff);
                continue;
            }
            if (!admins.TryGetValue(u.Id, out var isAdmin))
                admins[u.Id] = isAdmin = context.Codes.Contains(Perm.DocAdmin);
            if (isAdmin) adminOnly++; // deliberate: no automatic access to other people's content (spec 3.3)
            else narrower.Add(diff);
        }
        return new PermissionMigrationReport(picks.Count, same, wider, adminOnly, narrower, widerSample);
    }
}

/// <summary>Rebuilds the new model from the current one (Perm-Phase 1; also scheduled).</summary>
public sealed record RebuildPermissionModelCommand : IRequest<PermissionProjection.Result>;

public sealed class RebuildPermissionModelHandler(PermissionProjection projection) : IRequestHandler<RebuildPermissionModelCommand, PermissionProjection.Result>
{
    public Task<PermissionProjection.Result> Handle(RebuildPermissionModelCommand cmd, CancellationToken ct) => projection.RebuildAsync(ct);
}
