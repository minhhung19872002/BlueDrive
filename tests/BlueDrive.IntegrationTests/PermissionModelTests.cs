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
    /// <summary>The model kept up to date change by change equals the model rebuilt from scratch.</summary>
    private async Task<(List<string> Nodes, List<string> Entries, List<string> Memberships)> SnapshotAsync(IEnumerable<Guid> workspaces, IEnumerable<Guid> users)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ws = workspaces.ToList();
        var us = users.ToList();
        var nodes = (await db.Securables.AsNoTracking().Where(s => ws.Contains(s.SpaceId ?? s.PersonalDriveId ?? Guid.Empty)).ToListAsync())
            .Select(s => $"{s.Id}|{s.Kind}|{s.ParentId}|{s.Path}|{s.Name}|{s.HasUniqueAcl}|{s.AclScopeId}|{s.IsDeleted}").ToList();
        var ids = await db.Securables.AsNoTracking().Where(s => ws.Contains(s.SpaceId ?? s.PersonalDriveId ?? Guid.Empty)).Select(s => s.Id).ToListAsync();
        var entries = (await db.AclEntries.AsNoTracking().Where(e => ids.Contains(e.ScopeId)).ToListAsync())
            .Select(e => $"{e.ScopeId}|{e.PrincipalId}|{e.PermissionLevelId}|{e.Source}|{e.ExpiresAt}").ToList();
        var memberships = (await db.PrincipalMemberships.AsNoTracking().Where(m => us.Contains(m.UserId)).ToListAsync()).Select(m => $"{m.UserId}|{m.PrincipalId}").ToList();
        return ([.. nodes.Order()], [.. entries.Order()], [.. memberships.Order()]);
    }

    [Fact]
    public async Task Every_change_keeps_the_new_model_equal_to_a_full_rebuild()
    {
        var admin = await factory.AdminClientAsync();
        await (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).ReadJsonAsync();
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (_, friendId, _) = await factory.CreateUserClientAsync("chi_xem");
        var personal = await StorageTests.PersonalWorkspaceAsync(owner);
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm đồng bộ quyền", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var a = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "A" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var b = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, parentId = a, name = "B" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var f1 = (await StorageTests.UploadAsync(owner, ws, b, "mot.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await StorageTests.UploadAsync(owner, ws, null, "anh/con/hai.png", StorageTests.Png, "anh/con/hai.png"); // folders created with the file
        await StorageTests.UploadAsync(owner, personal, null, "rieng.png", StorageTests.Png);
        (await owner.PutAsJsonAsync($"/api/v1/access/folder/{b}", new
        {
            entries = new[] { new { principalType = "user", principalId = friendId, accessLevel = "download", inherit = true } }, breakInheritance = true,
        })).EnsureSuccessStatusCode();
        await (await owner.PostAsJsonAsync("/api/v1/shares", new { resourceType = "file", resourceId = f1, targets = new[] { new { type = "user", id = memberId } }, accessLevel = 3 })).ReadJsonAsync();
        (await owner.PutAsJsonAsync($"/api/v1/files/{f1}/name", new { name = "một.png" })).IsSuccessStatusCode.Should().BeTrue();
        await (await owner.PostAsJsonAsync("/api/v1/items/move", new { items = new[] { new { type = "folder", id = b } }, targetFolderId = (Guid?)null, targetWorkspaceId = ws })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "folder", id = a } } })).ReadJsonAsync();
        await (await admin.PostAsJsonAsync("/api/v1/user-groups", new { name = "Nhóm đồng bộ " + Guid.NewGuid().ToString("N")[..6], memberIds = new[] { friendId } })).ReadJsonAsync();

        var live = await SnapshotAsync([ws, personal], [ownerId, memberId, friendId]);
        live.Nodes.Should().Contain(n => n.StartsWith(f1 + "|")).And.HaveCountGreaterThan(6);
        await (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).ReadJsonAsync();
        var rebuilt = await SnapshotAsync([ws, personal], [ownerId, memberId, friendId]);
        live.Nodes.Should().Equal(rebuilt.Nodes);
        live.Entries.Should().BeEquivalentTo(rebuilt.Entries);
        live.Memberships.Should().Equal(rebuilt.Memberships);
    }

    /// <summary>Spec 13.3: an item a member may not see leaks on no channel — not its name, nor a count or a size.</summary>
    [Fact]
    public async Task A_folder_that_stopped_inheriting_leaks_on_no_channel()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var marker = "Kin" + Random.Shared.Next(100000, 999999);
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nhóm có thư mục riêng " + marker, type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var open = (await StorageTests.UploadAsync(owner, ws, null, $"cong-khai-{marker}.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var secret = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = $"Riêng {marker}" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var hidden = (await StorageTests.UploadAsync(owner, ws, secret, $"bi-mat-{marker}.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/access/folder/{secret}", new { entries = Array.Empty<object>(), breakInheritance = true })).EnsureSuccessStatusCode();

        // Listing of the Space: the open file, not the folder.
        var top = (await (await member.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        top.Should().Contain(open).And.NotContain(secret);
        (await member.GetAsync($"/api/v1/workspaces/{ws}/items?folderId={secret}")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await member.GetAsync($"/api/v1/files/{hidden}")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound, "404, not 403: no sign it exists");
        // Search: nothing by name, nothing counted.
        var search = await (await member.PostAsJsonAsync("/api/v1/search", new { q = marker })).ReadJsonAsync();
        search.GetProperty("items").EnumerateArray().Select(h => h.GetProperty("item").GetProperty("id").GetGuid()).Should().Contain(open).And.NotContain(hidden);
        search.GetProperty("total").GetInt32().Should().Be(1);
        // Group dashboard: neither in recent documents nor in the totals by type.
        var dash = await (await member.GetAsync("/api/v1/dashboards/group")).ReadJsonAsync();
        dash.GetProperty("recent").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Should().NotContain(hidden);
        var slices = dash.GetProperty("typesByGroup").EnumerateArray().Single(g => g.GetProperty("workspaceId").GetGuid() == ws).GetProperty("slices");
        slices.EnumerateArray().Sum(s => s.GetProperty("files").GetInt32()).Should().Be(1, "the hidden file is not counted");

        // The owner sees everything.
        (await (await owner.PostAsJsonAsync("/api/v1/search", new { q = marker })).ReadJsonAsync()).GetProperty("total").GetInt32().Should().Be(2);
        (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/items")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).Should().Contain(secret);
    }

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
            nodes[c].AclScopeId.Should().Be(a, "C inherits from A; A's \"chỉ mục này\" grant is marked so it does not reach C");
            (await db.AclEntries.AsNoTracking().SingleAsync(e => e.ScopeId == a && e.PrincipalId == outsiderId)).AppliesToChildren.Should().BeFalse();
            nodes[inB].AclScopeId.Should().Be(b);
            nodes[inC].HasUniqueAcl.Should().BeTrue("shared on its own");

            var reference = scope.ServiceProvider.GetRequiredService<ReferencePermissionEvaluator>();
            async Task<AccessRight> R(Guid user, Guid item) => await reference.EvaluateAsync(user, item, CancellationToken.None);
            (await R(editorId, inC)).Has(AccessRight.EditItems).Should().BeTrue("Thành viên → Chỉnh sửa");
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

        // Perm-Phase 2: the engine on the API, and "Kiểm tra quyền" explains where a right comes from.
        var effective = await (await admin.GetAsync($"/api/v1/items/{inC}/effective-permissions?user={inGroupId}")).ReadJsonAsync();
        effective.GetProperty("rights").EnumerateArray().Select(r => r.GetString()).Should().Contain("Download").And.NotContain("EditItems");
        var why = await (await admin.GetAsync($"/api/v1/items/{inC}/explain?user={inGroupId}")).ReadJsonAsync();
        why.GetProperty("inherited").GetBoolean().Should().BeFalse("the file was shared on its own: it has its own ACL");
        why.GetProperty("sources").EnumerateArray().Should().Contain(x => x.GetProperty("kind").GetString() == "Qua nhóm"
            && x.GetProperty("level").GetString() == "Chỉ xem" && x.GetProperty("origin").GetString() == "inherited", "the group's grant was copied from A");
        var whyFolder = await (await admin.GetAsync($"/api/v1/items/{b}/explain?user={ownerId}")).ReadJsonAsync();
        whyFolder.GetProperty("sources").EnumerateArray().Should().Contain(x => x.GetProperty("kind").GetString() == "Thành viên Space");
        (await owner.GetAsync($"/api/v1/items/{inC}/explain?user={inGroupId}")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "the owner manages the item");
        var (stranger, _, _) = await factory.CreateUserClientAsync("bien_tap");
        (await stranger.GetAsync($"/api/v1/items/{inC}/explain?user={inGroupId}")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound, "no sign the item exists");
        (await stranger.GetAsync($"/api/v1/items/{inC}/effective-permissions")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "anyone may ask about themselves");

        // Rebuilding again keeps the derived ids.
        await (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).ReadJsonAsync();
        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Libraries.AnyAsync(l => l.Id == PermissionProjection.LibraryOf(ws))).Should().BeTrue();
    }
}
