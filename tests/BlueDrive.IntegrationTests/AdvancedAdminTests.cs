using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BlueDrive.IntegrationTests;

/// <summary>Perm-Phase 7: advanced administration (custom permission levels, labels, retention and holds, trash, quotas, offboarding, emergency access).</summary>
[Collection(ApiCollection.Name)]
public class AdvancedAdminTests(ApiFactory factory)
{
    [Fact]
    public async Task Custom_levels_and_changed_system_levels_apply_at_once_and_stay()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (user, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        var rights = (await (await admin.GetAsync("/api/v1/admin/permission-levels/rights")).ReadJsonAsync()).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        rights.Should().Contain(["ViewItems", "EditItems", "DeleteItems", "ManagePermissions"]);
        (await owner.PostAsJsonAsync("/api/v1/admin/permission-levels", new { name = "X", rights = new[] { "ViewItems" } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // "Biên tập không xóa": sees, opens, downloads, edits, renames — never deletes.
        var level = (await (await admin.PostAsJsonAsync("/api/v1/admin/permission-levels", new
        {
            name = "Biên tập không xóa", description = "Sửa nhưng không xóa", rights = new[] { "ViewItems", "OpenItems", "Download", "EditItems", "RenameMove" },
        })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, ws, null, "cap-tuy-bien.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = userId } }, levelId = level, notify = false })).ReadJsonAsync();
        (await user.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi-ten.png" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeFalse();

        // Changing the level changes it for everyone who holds it, now.
        (await admin.PutAsJsonAsync($"/api/v1/admin/permission-levels/{level}", new { name = "Biên tập không xóa", rights = new[] { "ViewItems", "OpenItems" } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "lai-doi.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/v1/admin/permission-levels/{level}")).StatusCode.Should().Be(HttpStatusCode.Conflict, "still in use");

        // A system level changed by the administrator stays so after the model is rebuilt; "Chủ sở hữu" cannot be changed; reset brings it back.
        var levels = (await (await admin.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l);
        var commenter = levels["commenter"].GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/admin/permission-levels/{levels["owner"].GetProperty("id").GetGuid()}", new { name = "X", rights = new[] { "ViewItems" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/v1/admin/permission-levels/{commenter}", new { name = "Người góp ý", rights = new[] { "ViewItems", "OpenItems", "Comment" } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).IsSuccessStatusCode.Should().BeTrue();
        var after = (await (await admin.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == commenter);
        after.GetProperty("name").GetString().Should().Be("Người góp ý");
        after.GetProperty("isCustomized").GetBoolean().Should().BeTrue();
        after.GetProperty("rightNames").EnumerateArray().Select(r => r.GetString()).Should().NotContain("Download");
        (await admin.PostAsync($"/api/v1/admin/permission-levels/{commenter}/reset", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await admin.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == commenter)
            .GetProperty("name").GetString().Should().Be("Bình luận");
    }

    [Fact]
    public async Task A_label_restricts_sharing_outsiders_and_lowering_it_needs_a_reason()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (outsider, outsiderId, _) = await factory.CreateUserClientAsync("bien_tap");
        var labels = (await (await owner.GetAsync("/api/v1/labels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("id").GetGuid());
        labels.Keys.Should().Contain(["Công khai", "Nội bộ", "Hạn chế"]);
        (await admin.PostAsJsonAsync("/api/v1/admin/labels", new { name = "Tối mật", color = "#ff0000", priority = 99 })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "not for state secrets");

        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space nhãn", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Hồ sơ" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, folder, "nhay-cam.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = outsiderId } }, levelId = levels["editor"], notify = false })).ReadJsonAsync();
        (await outsider.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);

        // "Hạn chế" on the folder: the outsider only views (no download, a watermark), members keep working; no outside sharing.
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Hạn chế"] })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var label = await (await member.GetAsync($"/api/v1/items/{file}/label")).ReadJsonAsync();
        label.GetProperty("label").GetProperty("name").GetString().Should().Be("Hạn chế");
        label.GetProperty("source").GetString().Should().Be("Thư mục \"Hồ sơ\"");
        (await outsider.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.PutAsJsonAsync($"/api/v1/files/{file}/name", new { name = "doi.png" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var detail = await (await outsider.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync();
        detail.GetProperty("watermark").GetBoolean().Should().BeTrue();
        detail.GetProperty("labelName").GetString().Should().Be("Hạn chế");
        (await member.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        var policy = await (await owner.GetAsync($"/api/v1/items/{file}/sharing-policy")).ReadJsonAsync();
        policy.GetProperty("external").GetString().Should().Be("off");
        policy.GetProperty("externalSource").GetString().Should().Be("Nhãn \"Hạn chế\"");
        (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["viewer"], allowDownload = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Lowering: who manages the item, with a reason; raising needs neither.
        (await member.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Công khai"], reason = "Đã công bố" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Công khai"] })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync($"/api/v1/items/{folder}/label", new { labelId = labels["Công khai"], reason = "Đã công bố" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await outsider.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.PutAsJsonAsync($"/api/v1/items/{file}/label", new { labelId = labels["Nội bộ"] })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var audit = await (await admin.PostAsJsonAsync("/api/v1/audit-logs/search?pageSize=50", new { actions = new[] { "label_changed" }, workspaceId = ws })).ReadJsonAsync();
        audit.GetProperty("items").EnumerateArray().Should().Contain(a => a.GetProperty("resourceId").GetGuid() == folder);
    }

    [Fact]
    public async Task A_hold_blocks_every_purge_and_retention_preserves_what_users_purge()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        (await owner.GetAsync("/api/v1/admin/holds")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var labels = (await (await owner.GetAsync("/api/v1/labels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("id").GetGuid());
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space lưu giữ", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Hồ sơ vụ việc" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var inFolder = (await StorageTests.UploadAsync(owner, ws, folder, "giu.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var kept = (await StorageTests.UploadAsync(owner, ws, null, "luu.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var free = (await StorageTests.UploadAsync(owner, ws, null, "tu-do.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/items/{kept}/label", new { labelId = labels["Hạn chế"] })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var hold = (await (await admin.PostAsJsonAsync("/api/v1/admin/holds", new { name = "Vụ việc A", reason = "Thanh tra", scopeKind = "folder", scopeId = folder }))
            .ReadJsonAsync()).GetProperty("id").GetGuid();
        var policy = (await (await admin.PostAsJsonAsync("/api/v1/admin/retention/policies", new
        {
            name = "Hồ sơ hạn chế 10 năm", scopeKind = "label", scopeId = labels["Hạn chế"], years = 10, basis = "created", onExpiry = "none",
        })).ReadJsonAsync()).GetProperty("id").GetGuid();
        (await owner.DeleteAsync($"/api/v1/workspaces/{ws}")).StatusCode.Should().Be(HttpStatusCode.Conflict, "a hold covers a folder of the Space");

        (await owner.PostAsJsonAsync("/api/v1/items/delete", new { items = new object[] { new { type = "folder", id = folder }, new { type = "file", id = kept }, new { type = "file", id = free } } }))
            .IsSuccessStatusCode.Should().BeTrue();
        // Out of the trash first (to the second stage), then for good from there.
        (await owner.PostAsJsonAsync("/api/v1/trash/purge", new object[] { new { type = "folder", id = folder }, new { type = "file", id = kept }, new { type = "file", id = free } }))
            .IsSuccessStatusCode.Should().BeTrue();
        // Held: refused, whoever asks.
        (await owner.PostAsJsonAsync("/api/v1/trash/purge", new[] { new { type = "folder", id = folder } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Retained: the purge works for the user, the file goes to the preservation store; the other is purged for real.
        (await owner.PostAsJsonAsync("/api/v1/trash/purge", new[] { new { type = "file", id = kept }, new { type = "file", id = free } })).IsSuccessStatusCode.Should().BeTrue();
        var trash = await (await owner.GetAsync($"/api/v1/workspaces/{ws}/trash?stage=2")).ReadJsonAsync();
        trash.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().BeEquivalentTo([folder]);
        var preserved = await (await admin.GetAsync("/api/v1/admin/retention/preserved?pageSize=100")).ReadJsonAsync();
        var row = preserved.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == kept);
        row.GetProperty("policyName").GetString().Should().Be("Hồ sơ hạn chế 10 năm");
        preserved.GetProperty("items").EnumerateArray().Should().NotContain(i => i.GetProperty("id").GetGuid() == free);
        (await admin.PostAsJsonAsync("/api/v1/admin/retention/preserved/purge", new { items = new[] { new { type = "file", id = kept } } }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "still retained");

        // Released: the folder can go; the preserved file goes back to the trash.
        (await admin.PostAsJsonAsync($"/api/v1/admin/holds/{hold}/release", new { reason = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/v1/admin/holds/{hold}/release", new { reason = "Kết thúc thanh tra" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsJsonAsync("/api/v1/trash/purge", new[] { new { type = "folder", id = folder } })).IsSuccessStatusCode.Should().BeTrue();
        (await owner.GetAsync($"/api/v1/files/{inFolder}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync("/api/v1/admin/retention/preserved/release", new { items = new[] { new { type = "file", id = kept } } })).IsSuccessStatusCode.Should().BeTrue();
        trash = await (await owner.GetAsync($"/api/v1/workspaces/{ws}/trash")).ReadJsonAsync();
        trash.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().BeEquivalentTo([kept]);
        (await admin.DeleteAsync($"/api/v1/admin/retention/policies/{policy}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var audit = await (await admin.PostAsJsonAsync("/api/v1/audit-logs/search?pageSize=50", new { actions = new[] { "hold_applied", "hold_removed" } })).ReadJsonAsync();
        audit.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()).Should().Contain(["hold_applied", "hold_removed"]);
    }

    [Fact]
    public async Task The_second_stage_trash_is_for_managers_and_a_restore_brings_back_own_permissions_and_parents()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (member, memberId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (reader, readerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space thùng rác", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members", new { userIds = new[] { memberId }, role = "editor" })).ReadJsonAsync();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Báo cáo" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(member, ws, folder, "quy-1.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = readerId } }, levelId = levels["viewer"], notify = false })).ReadJsonAsync();
        (await reader.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The member deletes the file then the folder; "xóa vĩnh viễn" of the file sends it to the second stage, out of their sight.
        (await member.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeTrue();
        (await owner.PostAsJsonAsync("/api/v1/items/delete", new { items = new[] { new { type = "folder", id = folder } } })).IsSuccessStatusCode.Should().BeTrue();
        (await member.PostAsJsonAsync("/api/v1/trash/purge", new[] { new { type = "file", id = file } })).IsSuccessStatusCode.Should().BeTrue();
        (await (await member.GetAsync($"/api/v1/workspaces/{ws}/trash")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().NotContain(i => i.GetProperty("id").GetGuid() == file);
        (await member.GetAsync($"/api/v1/workspaces/{ws}/trash?stage=2")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/v1/trash/restore", new { items = new[] { new { type = "file", id = file } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var second = await (await owner.GetAsync($"/api/v1/workspaces/{ws}/trash?stage=2")).ReadJsonAsync();
        var row = second.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == file);
        row.GetProperty("stage").GetInt32().Should().Be(2);
        row.GetProperty("purgeAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(93), TimeSpan.FromHours(1));

        // The model is rebuilt meanwhile: the file's own permissions stay with it.
        (await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null)).IsSuccessStatusCode.Should().BeTrue();
        (await owner.PostAsJsonAsync("/api/v1/trash/restore", new { items = new[] { new { type = "file", id = file } } })).IsSuccessStatusCode.Should().BeTrue();
        var restored = await (await owner.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync();
        var parent = restored.GetProperty("folderId").GetGuid();
        parent.Should().NotBe(folder, "the deleted folder stays in the trash; one of the same name is made");
        (await (await owner.GetAsync($"/api/v1/workspaces/{ws}/folder-info?folderId={parent}")).ReadJsonAsync()).GetProperty("name").GetString().Should().Be("Báo cáo");
        (await reader.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK, "the grant came back with the file");
        (await reader.GetAsync($"/api/v1/files/{file}/download-url")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Quotas_default_by_unit_warn_the_owner_block_when_full_and_old_versions_can_be_cleared()
    {
        var admin = await factory.AdminClientAsync();
        const double Gb = 1024d * 1024 * 1024;
        // A unit's default for its new users.
        var org = (await (await admin.PostAsJsonAsync("/api/v1/organizations", new { code = "Q" + Guid.NewGuid().ToString("N")[..8], name = "Phòng thử dung lượng", type = "phong_ban", sortOrder = 0 }))
            .ReadJsonAsync()).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync("/api/v1/quotas/personal", new { organizationId = org, includeSubOrganizations = true, quotaGb = 3, setAsDefault = true })).EnsureSuccessStatusCode();
        var username = "q" + Guid.NewGuid().ToString("N")[..10];
        var roleIds = (await (await admin.GetAsync("/api/v1/roles")).ReadJsonAsync()).EnumerateArray().Where(r => r.GetProperty("code").GetString() == "bien_tap")
            .Select(r => r.GetProperty("id").GetGuid()).ToArray();
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username, fullName = "Người dùng " + username, email = $"{username}@example.gov.vn", isActive = true, organizationId = org, password = "Init@Pass123", roleIds,
        })).ReadJsonAsync();
        (await (await admin.GetAsync($"/api/v1/users/{created.GetProperty("id").GetGuid()}")).ReadJsonAsync()).GetProperty("quotaBytes").GetInt64().Should().Be((long)(3 * Gb));

        // A Drive the size of 4 versions: ~50 % and ~75 % tell nothing, 100 % tells the owner; uploads and new versions stop.
        var (c, userId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(c);
        var png = StorageTests.Png;
        (await admin.PutAsJsonAsync("/api/v1/quotas/personal", new { userIds = new[] { userId }, quotaGb = (png.Length * 4 + 6.5) / Gb })).EnsureSuccessStatusCode();
        var file = (await StorageTests.UploadAsync(c, ws, null, "dung-luong.png", png)).GetProperty("fileId").GetGuid();
        await StorageTests.UploadAsync(c, ws, null, "dung-luong.png", png.Concat(new byte[] { 1 }).ToArray(), target: file);
        (await (await c.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().NotContain(n => n.GetProperty("type").GetString() == "quota");
        await StorageTests.UploadAsync(c, ws, null, "dung-luong.png", png.Concat(new byte[] { 2 }).ToArray(), target: file);
        await StorageTests.UploadAsync(c, ws, null, "dung-luong.png", png.Concat(new byte[] { 3 }).ToArray(), target: file);
        (await (await c.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().Contain(n => n.GetProperty("type").GetString() == "quota");
        var form = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "them.png" }, { new StringContent(ws.ToString()), "workspaceId" } };
        (await c.PostAsync("/api/v1/files/upload", form)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        // "Bộ nhớ" shows what old versions take; clearing them gives the space back.
        var row = (await (await c.GetAsync("/api/v1/storage/usage")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("item").GetProperty("id").GetGuid() == file);
        row.GetProperty("versions").GetInt32().Should().Be(4);
        row.GetProperty("oldVersionBytes").GetInt64().Should().BeGreaterThan(0);
        var pruned = await (await c.PostAsync($"/api/v1/files/{file}/versions/prune", null)).ReadJsonAsync();
        pruned.GetProperty("deleted").GetInt32().Should().Be(3);
        var current = (await (await c.GetAsync($"/api/v1/files/{file}")).ReadJsonAsync()).GetProperty("sizeBytes").GetInt64();
        (await (await c.GetAsync("/api/v1/auth/me")).ReadJsonAsync()).GetProperty("usedBytes").GetInt64().Should().Be(current, "only the current version is left");
        (await StorageTests.UploadAsync(c, ws, null, "them.png", png)).GetProperty("fileId").GetGuid().Should().NotBeEmpty();

        // Someone else's Drive is not theirs to look at.
        var (other, _, _) = await factory.CreateUserClientAsync("bien_tap");
        (await other.GetAsync($"/api/v1/storage/usage?workspaceId={ws}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Offboarding_hands_the_drive_over_keeping_shares_and_reports_it()
    {
        var admin = await factory.AdminClientAsync();
        var (leaver, leaverId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (_, recipientId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (reader, readerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var drive = await StorageTests.PersonalWorkspaceAsync(leaver);
        var folder = (await (await leaver.PostAsJsonAsync("/api/v1/folders", new { workspaceId = drive, name = "Kế hoạch" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(leaver, drive, folder, "ke-hoach.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var loose = (await StorageTests.UploadAsync(leaver, drive, null, "rieng.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await leaver.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await leaver.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = readerId } }, levelId = levels["viewer"], notify = false })).ReadJsonAsync();
        var space = (await (await leaver.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space của người nghỉ", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        (await leaver.GetAsync("/api/v1/admin/offboarding")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var preview = await (await admin.GetAsync($"/api/v1/admin/offboarding/{leaverId}/preview")).ReadJsonAsync();
        preview.GetProperty("files").GetInt32().Should().Be(2);
        preview.GetProperty("spaces").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == space).GetProperty("soleOwner").GetBoolean().Should().BeTrue();

        var report = await (await admin.PostAsJsonAsync($"/api/v1/admin/offboarding/{leaverId}", new { recipientId, revokeLinks = true, deactivate = true })).ReadJsonAsync();
        report.GetProperty("lines").EnumerateArray().Should().NotBeEmpty();
        // In the recipient's Drive, under "Dữ liệu từ …", with the share kept.
        var moved = await (await admin.GetAsync($"/api/v1/users/{recipientId}")).ReadJsonAsync();
        moved.GetProperty("usedBytes").GetInt64().Should().Be(StorageTests.Png.Length * 2);
        (await reader.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK, "the share stays");
        (await reader.GetAsync($"/api/v1/files/{loose}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await admin.GetAsync($"/api/v1/users/{leaverId}")).ReadJsonAsync()).GetProperty("isActive").GetBoolean().Should().BeFalse();
        var spaces = await (await admin.GetAsync($"/api/v1/workspaces/all?q=Space%20c%E1%BB%A7a%20ng%C6%B0%E1%BB%9Di%20ngh%E1%BB%89&pageSize=50")).ReadJsonAsync();
        spaces.GetProperty("items").EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == space).GetProperty("ownerId").GetGuid().Should().Be(recipientId);
        var id = report.GetProperty("id").GetGuid();
        var xlsx = await admin.GetAsync($"/api/v1/admin/offboarding/records/{id}/export?format=xlsx");
        xlsx.StatusCode.Should().Be(HttpStatusCode.OK);
        (await xlsx.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task Emergency_access_is_temporary_audited_and_told_to_the_owner()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var drive = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await StorageTests.UploadAsync(owner, drive, null, "noi-bo.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        (await admin.GetAsync($"/api/v1/files/{file}")).IsSuccessStatusCode.Should().BeFalse("administrators do not see others' content by default");
        (await admin.PostAsJsonAsync("/api/v1/admin/emergency-access", new { workspaceId = drive, hours = 25, reason = "Kiểm tra sự cố an toàn thông tin" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/admin/emergency-access", new { workspaceId = drive, hours = 2, reason = "ngắn" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/v1/admin/emergency-access", new { workspaceId = drive, hours = 2, reason = "Kiểm tra sự cố an toàn thông tin" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var id = (await (await admin.PostAsJsonAsync("/api/v1/admin/emergency-access", new { workspaceId = drive, hours = 2, reason = "Kiểm tra sự cố an toàn thông tin" }))
            .ReadJsonAsync()).GetProperty("id").GetGuid();
        (await admin.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await owner.GetAsync("/api/v1/notifications")).ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Should().Contain(n => n.GetProperty("type").GetString() == "emergency");
        var row = (await (await admin.GetAsync("/api/v1/admin/emergency-access")).ReadJsonAsync()).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);
        row.GetProperty("active").GetBoolean().Should().BeTrue();
        row.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(5));
        var audit = await (await admin.PostAsJsonAsync("/api/v1/audit-logs/search?pageSize=50", new { actions = new[] { "emergency_access" }, workspaceId = drive })).ReadJsonAsync();
        audit.GetProperty("items").EnumerateArray().Should().NotBeEmpty();

        (await admin.PostAsync($"/api/v1/admin/emergency-access/{id}/end", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/v1/files/{file}")).IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task Access_reports_explain_list_and_export_and_bulk_operations_clean_up()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, ownerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (reader, readerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = (await (await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "Space báo cáo", type = "group" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var folder = (await (await owner.PostAsJsonAsync("/api/v1/folders", new { workspaceId = ws, name = "Báo cáo riêng" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var file = (await StorageTests.UploadAsync(owner, ws, folder, "so-lieu.png", StorageTests.Png)).GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{folder}/permissions", new
        {
            targets = new[] { new { kind = "user", id = readerId } }, levelId = levels["viewer"], notify = false, expiresAt = DateTime.UtcNow.AddDays(3),
        })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync("/api/v1/share-links", new { resourceType = "file", resourceId = file, scope = "anyone", levelId = levels["viewer"], allowDownload = true, expiresAt = DateTime.UtcNow.AddDays(5) }))
            .ReadJsonAsync();

        // Who may read them: administrators, and the Space's managers for their Space only.
        (await reader.GetAsync($"/api/v1/admin/reports/links")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.GetAsync($"/api/v1/admin/reports/links")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.GetAsync($"/api/v1/admin/reports/links?workspaceId={ws}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var check = await (await admin.GetAsync($"/api/v1/admin/reports/check?userId={readerId}&itemId={file}")).ReadJsonAsync();
        check.GetProperty("summary").GetString().Should().Contain("Báo cáo riêng");
        check.GetProperty("rows").GetArrayLength().Should().BeGreaterThan(0);
        var access = await (await admin.GetAsync($"/api/v1/admin/reports/user-access?userId={readerId}")).ReadJsonAsync();
        access.GetProperty("rows").EnumerateArray().Should().Contain(r => r.GetProperty("itemId").GetGuid() == folder);
        var external = await (await owner.GetAsync($"/api/v1/admin/reports/external?workspaceId={ws}")).ReadJsonAsync();
        external.GetProperty("rows").EnumerateArray().Should().Contain(r => r.GetProperty("itemId").GetGuid() == file);
        var unique = await (await owner.GetAsync($"/api/v1/admin/reports/unique?workspaceId={ws}")).ReadJsonAsync();
        unique.GetProperty("rows").EnumerateArray().Should().Contain(r => r.GetProperty("itemId").GetGuid() == folder);
        var expiring = await (await owner.GetAsync($"/api/v1/admin/reports/expiring?workspaceId={ws}&days=7")).ReadJsonAsync();
        expiring.GetProperty("rows").GetArrayLength().Should().Be(2, "the grant and the link end within a week");
        var pdf = await owner.GetAsync($"/api/v1/admin/reports/links/export?workspaceId={ws}&format=pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await (await admin.GetAsync($"/api/v1/admin/reports/items?workspaceId={ws}&query=so-lieu")).ReadJsonAsync()).EnumerateArray()
            .Should().Contain(i => i.GetProperty("id").GetGuid() == file);

        // Bulk: the Space's "Bất kỳ ai" links go; the reader loses every grant.
        (await owner.PostAsJsonAsync("/api/v1/admin/bulk/remove-user-permissions", new { userId = readerId })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var revoked = await (await admin.PostAsJsonAsync("/api/v1/admin/bulk/revoke-anyone-links", new { workspaceId = ws })).ReadJsonAsync();
        revoked.GetProperty("count").GetInt32().Should().Be(1);
        (await (await owner.GetAsync($"/api/v1/admin/reports/external?workspaceId={ws}")).ReadJsonAsync()).GetProperty("rows").GetArrayLength().Should().Be(0);
        (await reader.GetAsync($"/api/v1/files/{file}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var removed = await (await admin.PostAsJsonAsync("/api/v1/admin/bulk/remove-user-permissions", new { userId = readerId })).ReadJsonAsync();
        removed.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        (await reader.GetAsync($"/api/v1/files/{file}")).IsSuccessStatusCode.Should().BeFalse();
        (await admin.PostAsJsonAsync("/api/v1/admin/bulk/delete-inactive-guests", new { days = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Office_opens_to_edit_comment_or_view_with_a_watermark_by_rights()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (commenter, commenterId, _) = await factory.CreateUserClientAsync("bien_tap");
        var (viewer, viewerId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var create = await (await owner.PostAsJsonAsync("/api/v1/office/new", new { workspaceId = ws, folderId = (Guid?)null, kind = "docx", name = "Biên bản" })).ReadJsonAsync();
        var file = create.GetProperty("fileId").GetGuid();
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = commenterId } }, levelId = levels["commenter"], notify = false })).ReadJsonAsync();
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = viewerId } }, levelId = levels["view_only"], notify = false })).ReadJsonAsync();

        var mine = await (await owner.GetAsync($"/api/v1/office/config/{file}")).ReadJsonAsync();
        mine.GetProperty("mode").GetString().Should().Be("edit");
        mine.GetProperty("watermark").GetBoolean().Should().BeFalse();
        var comments = await (await commenter.GetAsync($"/api/v1/office/config/{file}")).ReadJsonAsync();
        comments.GetProperty("mode").GetString().Should().Be("comment");
        var perms = comments.GetProperty("config").GetProperty("document").GetProperty("permissions");
        perms.GetProperty("edit").GetBoolean().Should().BeFalse();
        perms.GetProperty("comment").GetBoolean().Should().BeTrue();
        var views = await (await viewer.GetAsync($"/api/v1/office/config/{file}")).ReadJsonAsync();
        views.GetProperty("mode").GetString().Should().Be("view");
        views.GetProperty("watermark").GetBoolean().Should().BeTrue();
        var vp = views.GetProperty("config").GetProperty("document").GetProperty("permissions");
        vp.GetProperty("download").GetBoolean().Should().BeFalse();
        vp.GetProperty("print").GetBoolean().Should().BeFalse();
        vp.GetProperty("copy").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task An_older_version_opens_read_only_in_the_online_editor()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync("bien_tap");
        var (viewOnly, viewOnlyId, _) = await factory.CreateUserClientAsync("bien_tap");
        var ws = await StorageTests.PersonalWorkspaceAsync(owner);
        var file = (await (await owner.PostAsJsonAsync("/api/v1/office/new", new { workspaceId = ws, folderId = (Guid?)null, kind = "docx", name = "Nhiều bản" })).ReadJsonAsync())
            .GetProperty("fileId").GetGuid();
        var first = (await (await owner.GetAsync($"/api/v1/files/{file}/versions")).ReadJsonAsync()).EnumerateArray().Single().GetProperty("id").GetGuid();
        await StorageTests.UploadAsync(owner, ws, null, "Nhiều bản.docx", "PK phiên bản 2"u8.ToArray(), target: file);

        var current = await (await owner.GetAsync($"/api/v1/office/config/{file}")).ReadJsonAsync();
        current.GetProperty("mode").GetString().Should().Be("edit");
        var old = await (await owner.GetAsync($"/api/v1/office/config/{file}?version={first}")).ReadJsonAsync();
        old.GetProperty("mode").GetString().Should().Be("view", "an older version is only read");
        old.GetProperty("config").GetProperty("document").GetProperty("key").GetString().Should().EndWith(first.ToString("N"));
        (await owner.GetAsync($"/api/v1/office/config/{file}?version={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // "Chỉ xem (không tải)" may not see versions.
        var levels = (await (await owner.GetAsync("/api/v1/permission-levels")).ReadJsonAsync()).EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());
        await (await owner.PostAsJsonAsync($"/api/v1/items/{file}/permissions", new { targets = new[] { new { kind = "user", id = viewOnlyId } }, levelId = levels["view_only"], notify = false })).ReadJsonAsync();
        (await viewOnly.GetAsync($"/api/v1/office/config/{file}?version={first}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
