using System.Net.Http.Json;
using BlueDrive.Application.Features.Access;
using BlueDrive.Domain.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>Perm-Phase 1: the current model projected onto the new one (docs/migration-permissions.md) keeps every right (old ⊆ new).</summary>
[Collection(ApiCollection.Name)]
public class PermissionModelTests(ApiFactory factory)
{
    [Fact]
    public async Task The_current_model_is_projected_without_losing_any_right()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (_, editorId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (_, uploaderId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (_, viewerId, _) = await factory.CreateUserClientAsync("chi_xem");
        var (_, outsiderId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (_, inGroupId, _) = await factory.CreateUserClientAsync("chi_xem");
        var (_, sharedId, _) = await factory.CreateUserClientAsync("chi_xem");

        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm phân quyền mới", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        foreach (var (id, role) in new[] { (editorId, "editor"), (uploaderId, "uploader"), (viewerId, "viewer") })
            await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { id }, role })).ReadJsonAsync();
        var a = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Chiến dịch 2026" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var b = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = a, name = "Riêng" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var c = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = a, name = "Chung" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var inB = (await StorageTests.UploadAsync(owner, ws, b, "kin.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var inC = (await StorageTests.UploadAsync(owner, ws, c, "chung.png", StorageTests.Png)).GetProperty("fileId").GetGuid();

        var group = (await (await admin.PostAsJsonAsync("/api/v1/user-groups", new { name = "Nhóm đọc " + Guid.NewGuid().ToString("N")[..6], memberIds = new[] { inGroupId } })).ReadJsonAsync()).GetProperty("id").GetGuid();
        // A: the group may download (inherits); the outsider edits A only ("không kế thừa").
        (await owner.PutAsJsonAsync($"/api/v1/access/folder/{a}", new
        {
            entries = new object[]
            {
                new { principalType = "group", principalId = group, accessLevel = "download", inherit = true },
                new { principalType = "user", principalId = outsiderId, accessLevel = "edit", inherit = false },
            },
        })).EnsureSuccessStatusCode();
        // B breaks inheritance: only the owners keep access, plus the outsider.
        (await owner.PutAsJsonAsync($"/api/v1/access/folder/{b}", new
        {
            entries = new[] { new { principalType = "user", principalId = outsiderId, accessLevel = "view", inherit = true } }, breakInheritance = true,
        })).EnsureSuccessStatusCode();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = inC, targets = new[] { new { type = "user", id = sharedId } }, accessLevel = 2 })).ReadJsonAsync();

        var rebuilt = await (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).ReadJsonAsync();
        rebuilt.GetProperty("securables").GetInt32().Should().BeGreaterThan(5);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var nodes = await db.Securables.AsNoTracking().Where(s => s.SpaceId == ws).ToDictionaryAsync(s => s.Id);
            var library = PermissionProjection.LibraryOf(ws);
            nodes[ws].Kind.Should().Be(SecurableKind.Space);
            nodes[library].ParentId.Should().Be(ws);
            nodes[a].ParentId.Should().Be(library);
            nodes[inB].Path.Should().Be($"{ws:N}.{library:N}.{a:N}.{b:N}.{inB:N}");
            nodes[a].HasUniqueAcl.Should().BeTrue("direct grants on A");
            nodes[b].HasUniqueAcl.Should().BeTrue("inheritance broken");
            nodes[c].HasUniqueAcl.Should().BeTrue("A has a grant that does not reach its children");
            nodes[inB].AclScopeId.Should().Be(b);
            nodes[inC].HasUniqueAcl.Should().BeTrue("shared on its own");

            var reference = scope.ServiceProvider.GetRequiredService<ReferencePermissionEvaluator>();
            async Task<AccessRight> R(Guid user, Guid item) => await reference.EvaluateAsync(user, item, CancellationToken.None);
            (await R(editorId, inC)).Has(AccessRight.EditItems).Should().BeTrue("Thành viên → Biên tập");
            (await R(editorId, inB)).Should().Be(AccessRight.None, "below the break only the owners and the people granted there");
            (await R(ownerId, inB)).Has(AccessRight.ManagePermissions).Should().BeTrue();
            (await R(outsiderId, a)).Has(AccessRight.EditItems).Should().BeTrue();
            (await R(outsiderId, c)).Should().Be(AccessRight.None, "the grant on A was for A only");
            (await R(outsiderId, inB)).Has(AccessRight.ViewItems).Should().BeTrue();
            (await R(outsiderId, inB)).Has(AccessRight.Download).Should().BeFalse("old \"Xem\" → Chỉ xem (không tải)");
            (await R(inGroupId, inC)).Has(AccessRight.Download).Should().BeTrue("the group's grant on A reaches C");
            (await R(sharedId, inC)).Has(AccessRight.Download).Should().BeTrue();
            (await R(sharedId, c)).Should().Be(AccessRight.None, "sharing a file gives nothing on its folder");
            (await R(viewerId, inC)).Should().Be(AccessRight.ViewItems | AccessRight.OpenItems, "old viewer members could not download: Chỉ xem (không tải)");

            // scope_grants agree with the entries (used to trim queries in SQL from Perm-Phase 3 on).
            var groupGrant = await db.ScopeGrants.AsNoTracking().SingleAsync(g => g.ScopeId == a && g.PrincipalId == group);
            groupGrant.Permissions.Should().Be(SystemLevels.All.Single(l => l.Id == SystemLevels.Viewer).Permissions);
            (await db.PrincipalMemberships.AnyAsync(m => m.UserId == viewerId && m.PrincipalId == PermissionProjection.GroupOf(ws, SpaceGroupKind.Visitors))).Should().BeTrue();
            (await db.PrincipalMemberships.AnyAsync(m => m.UserId == viewerId && m.PrincipalId == StableGuid.Everyone)).Should().BeTrue();
        }

        // Spec 12: on random pairs from the whole database (every other test's data too), the new rights are never below the old ones.
        var report = await (await admin.GetAsync("/api/v1/admin/permission-model/verify?samples=3000&seed=7")).ReadJsonAsync();
        report.GetProperty("pairs").GetInt32().Should().BeGreaterThan(100);
        report.GetProperty("narrower").GetArrayLength().Should().Be(0, report.GetProperty("narrower").ToString());

        // Rebuilding again keeps the derived ids.
        await (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).ReadJsonAsync();
        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Libraries.AnyAsync(l => l.Id == PermissionProjection.LibraryOf(ws))).Should().BeTrue();
    }
}
