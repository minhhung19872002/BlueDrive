using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using BlueDrive.Infrastructure.Persistence;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace BlueDrive.UnitTests.Security;

/// <summary>
/// Spec 13.2: on random worlds (tree, Spaces and drives, principals, memberships, entries with expiry, unique scopes) and random changes
/// (grant, revoke, break / restore inheritance, membership change, lock), the cached, scope-based engine always equals the reference
/// evaluator for every (user, node) — also right after each change, which checks that caches go stale. PERM_PROPERTY_RUNS sets the
/// number of worlds (CI: 15; release check: 10 000) and PERM_PROPERTY_NODES the tree size (CI: 40; up to 5 000).
/// </summary>
public class PermissionEngineProperties
{
    private static int Runs => int.TryParse(Environment.GetEnvironmentVariable("PERM_PROPERTY_RUNS"), out var n) ? n : 15;
    private static int Nodes => int.TryParse(Environment.GetEnvironmentVariable("PERM_PROPERTY_NODES"), out var n) ? n : 40;

    [Fact]
    public void Cached_engine_equals_the_reference_evaluator_through_random_changes()
    {
        Prop.ForAll(Gen.Choose(0, int.MaxValue).ToArbitrary(), seed =>
        {
            try { new World(seed).RunAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { throw new InvalidOperationException($"Hạt giống (seed) {seed}: {ex}", ex); }
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(Runs));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class World(int seed)
    {
        private readonly Random _r = new(seed);
        private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero));
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly Dictionary<Guid, IReadOnlySet<string>> _codes = [];
        private AppDbContext _db = null!;
        private readonly List<Guid> _users = [];
        private readonly List<Guid> _pool = []; // groups, units, Space groups: anything a user can count as
        private readonly List<Securable> _nodes = [];

        private T Pick<T>(IReadOnlyList<T> list) => list[_r.Next(list.Count)];

        public async Task RunAsync()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await _db.Database.EnsureCreatedAsync();
            await BuildAsync();
            await CompareAsync("ban đầu");
            for (var step = 0; step < 12; step++) await CompareAsync(await ChangeAsync());
        }

        private async Task BuildAsync()
        {
            foreach (var l in SystemLevels.All)
                _db.PermissionLevels.Add(new PermissionLevel { Id = l.Id, Code = l.Code, Name = l.Name, Description = l.Description, Permissions = l.Permissions, IsSystem = true });
            string[][] roleCodes = [[Perm.DocView, Perm.DocEdit], [Perm.DocView, Perm.DocEdit], [Perm.DocView], [Perm.DocAdmin], []];
            for (var i = 0; i < 6; i++)
            {
                var u = new User { Username = $"u{i}", FullName = $"Người {i}" };
                _db.Users.Add(u);
                _users.Add(u.Id);
                _codes[u.Id] = Pick(roleCodes).ToHashSet();
                _db.MembershipVersions.Add(new MembershipVersion { UserId = u.Id, Version = 1 });
            }
            for (var i = 0; i < 8; i++) _pool.Add(Guid.NewGuid());
            foreach (var u in _users)
            {
                _db.PrincipalMemberships.Add(new PrincipalMembership { UserId = u, PrincipalId = u });
                foreach (var p in _pool.Where(_ => _r.Next(3) == 0)) _db.PrincipalMemberships.Add(new PrincipalMembership { UserId = u, PrincipalId = p });
            }

            for (var w = 0; w < 2; w++)
            {
                var personal = w == 0;
                var owner = Pick(_users);
                var ws = new Workspace { Name = $"KG{w}", OwnerId = owner, Type = personal ? WorkspaceType.Personal : WorkspaceType.Group, IsLocked = _r.Next(5) == 0 };
                _db.Workspaces.Add(ws);
                var root = new Securable
                {
                    Id = ws.Id, Kind = personal ? SecurableKind.Drive : SecurableKind.Space, Path = ws.Id.ToString("N"), Name = ws.Name, HasUniqueAcl = true,
                    AclScopeId = ws.Id, SpaceId = personal ? null : ws.Id, PersonalDriveId = personal ? ws.Id : null, OwnerUserId = personal ? owner : null,
                };
                Add(root);
                for (var i = 0; i < Nodes / 2; i++)
                {
                    var parent = Pick(_nodes.Where(n => (n.SpaceId ?? n.PersonalDriveId) == ws.Id && n.Kind != SecurableKind.File).ToList());
                    var file = _r.Next(2) == 0;
                    var unique = _r.Next(5) == 0;
                    var node = new Securable
                    {
                        Kind = file ? SecurableKind.File : SecurableKind.Folder, ParentId = parent.Id, Name = $"n{i}", HasUniqueAcl = unique,
                        SpaceId = root.SpaceId, PersonalDriveId = root.PersonalDriveId, OwnerUserId = root.OwnerUserId,
                    };
                    node.Id = Guid.CreateVersion7();
                    node.Path = $"{parent.Path}.{node.Id:N}";
                    node.AclScopeId = unique ? node.Id : parent.AclScopeId;
                    Add(node);
                    if (!file)
                        _db.Folders.Add(new Folder { Id = node.Id, WorkspaceId = ws.Id, ParentId = parent.Kind == SecurableKind.Folder ? parent.Id : null, Name = node.Name, Path = "/" });
                    if (file)
                        _db.Files.Add(new StoredFile
                        {
                            Id = node.Id, WorkspaceId = ws.Id, Name = node.Name, Extension = "txt", CreatedBy = Pick(_users),
                            ApprovalStatus = _r.Next(4) == 0 ? ApprovalStatus.Pending : ApprovalStatus.Approved,
                        });
                }
            }
            foreach (var scope in _nodes.Where(n => n.HasUniqueAcl))
            {
                _db.AclScopes.Add(new AclScope { ScopeId = scope.Id, AclVersion = 1 });
                for (var e = _r.Next(4); e > 0; e--) _db.AclEntries.Add(RandomEntry(scope.Id));
            }
            await _db.SaveChangesAsync();
        }

        private void Add(Securable s)
        {
            _nodes.Add(s);
            _db.Securables.Add(s);
        }

        private AclEntry RandomEntry(Guid scope) => new()
        {
            ScopeId = scope, PrincipalKind = PrincipalKind.Group, PrincipalId = _r.Next(3) == 0 ? Pick(_users) : Pick(_pool),
            PermissionLevelId = Pick(SystemLevels.All).Id, Source = _r.Next(2) == 0 ? AclSource.SpaceDefault : AclSource.Direct,
            ExpiresAt = _r.Next(4) switch { 0 => _clock.Now.UtcDateTime.AddMinutes(-5), 1 => _clock.Now.UtcDateTime.AddMinutes(30), _ => null },
            GrantedAt = _clock.Now.UtcDateTime,
        };

        /// <summary>One random change; returns its description (for the failure message).</summary>
        private async Task<string> ChangeAsync()
        {
            var editor = new AclEditor(_db, _clock);
            var nodes = await _db.Securables.AsNoTracking().ToListAsync();
            switch (_r.Next(8))
            {
                case 0:
                {
                    var n = Pick(nodes);
                    var e = RandomEntry(n.Id);
                    await editor.GrantAsync(n.Id, e.PrincipalKind, e.PrincipalId, e.PermissionLevelId, e.ExpiresAt, null, default);
                    return $"cấp quyền trên {n.Name}";
                }
                case 1:
                {
                    var entries = await _db.AclEntries.AsNoTracking().ToListAsync();
                    if (entries.Count == 0) return "không có gì để thu hồi";
                    await editor.RevokeAsync(Pick(entries).Id, default);
                    return "thu hồi một quyền";
                }
                case 2:
                {
                    var n = Pick(nodes);
                    var copy = _r.Next(2) == 0;
                    await editor.BreakInheritanceAsync(n.Id, copy, null, default);
                    return $"ngắt kế thừa {n.Name} (sao chép: {copy})";
                }
                case 3:
                {
                    var unique = nodes.Where(n => n.HasUniqueAcl && n.ParentId is not null).ToList();
                    if (unique.Count == 0) return "không có gì để khôi phục";
                    var n = Pick(unique);
                    var recursive = _r.Next(2) == 0;
                    await editor.RestoreInheritanceAsync(n.Id, recursive, default);
                    return $"khôi phục kế thừa {n.Name} (cả cây: {recursive})";
                }
                case 4:
                {
                    var u = Pick(_users);
                    var p = Pick(_pool);
                    var row = await _db.PrincipalMemberships.FirstOrDefaultAsync(m => m.UserId == u && m.PrincipalId == p);
                    if (row is null) _db.PrincipalMemberships.Add(new PrincipalMembership { UserId = u, PrincipalId = p });
                    else _db.PrincipalMemberships.Remove(row);
                    (await _db.MembershipVersions.FirstAsync(v => v.UserId == u)).Version++;
                    await _db.SaveChangesAsync();
                    return "đổi thành viên";
                }
                case 5:
                {
                    var ws = Pick(await _db.Workspaces.ToListAsync());
                    ws.IsLocked = !ws.IsLocked;
                    await _db.SaveChangesAsync();
                    return "khóa / mở khóa không gian";
                }
                case 6:
                    _clock.Now = _clock.Now.AddMinutes(20); // entries run out
                    return "thời gian trôi 20 phút";
                default:
                    return "không đổi";
            }
        }

        private async Task CompareAsync(string after)
        {
            _db.ChangeTracker.Clear();
            var rbac = Substitute.For<IPermissionService>();
            rbac.GetPermissionsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(c => _codes[c.Arg<Guid>()]);
            var engine = new ItemPermissionService(_db, rbac, _cache, _clock); // a new request, the same memory cache
            var reference = new ReferencePermissionEvaluator(_db, rbac, _clock);
            var nodes = await _db.Securables.AsNoTracking().Select(s => new { s.Id, s.Name }).ToListAsync();
            foreach (var u in _users)
            {
                var ctx = await reference.LoadUserAsync(u, default);
                foreach (var n in nodes)
                {
                    var expected = await reference.EvaluateAsync(ctx, n.Id, default);
                    var actual = await engine.GetEffectiveAsync(u, n.Id);
                    actual.Should().Be(expected, $"người dùng {u}, nút {n.Name}, sau khi {after}");
                }
            }
        }
    }
}
