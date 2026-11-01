using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.IntegrationTests;

/// <summary>HSMT #1–10: tổ chức, người dùng, nhóm, vai trò, ma trận quyền, bảng đối chiếu, danh mục.</summary>
[Collection(ApiCollection.Name)]
public class AdminTests(ApiFactory factory)
{
    [Fact]
    public async Task Organization_crud_tree_search_unaccented_and_delete_rules()
    {
        var admin = await factory.AdminClientAsync();
        var code = "PB" + Random.Shared.Next(10000, 99999);
        var root = (await (await admin.GetAsync("/api/v1/organizations/tree")).ReadJsonAsync())[0].GetProperty("id").GetGuid();
        var created = await (await admin.PostAsJsonAsync("/api/v1/organizations", new { parentId = root, code, name = "Phòng Kiểm thử Dân số", type = "phong_ban", sortOrder = 9 })).ReadJsonAsync();
        var id = created.GetProperty("id").GetGuid();

        var dup = await admin.PostAsJsonAsync("/api/v1/organizations", new { parentId = root, code, name = "Trùng mã", type = "phong_ban" });
        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await dup.Content.ReadAsStringAsync()).Should().Contain("đã tồn tại");

        var search = await (await admin.GetAsync("/api/v1/organizations?q=kiem%20thu%20dan%20so")).ReadJsonAsync();
        search.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("id").GetGuid() == id);

        var child = await (await admin.PostAsJsonAsync("/api/v1/organizations", new { parentId = id, code = code + "-1", name = "Tổ con", type = "phong_ban" })).ReadJsonAsync();
        var conflict = await admin.DeleteAsync($"/api/v1/organizations/{id}");
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var deactivated = await (await admin.DeleteAsync($"/api/v1/organizations/{id}?deactivateIfInUse=true")).ReadJsonAsync();
        deactivated.GetProperty("result").GetString().Should().Be("deactivated");

        // Drag & drop: move the child to root level.
        (await admin.PostAsJsonAsync($"/api/v1/organizations/{child.GetProperty("id").GetGuid()}/move", new { newParentId = root, newIndex = 0 })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/v1/organizations/{child.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var fmt in new[] { "xlsx", "pdf" })
        {
            var file = await admin.GetAsync($"/api/v1/organizations/export?format={fmt}&q=phong");
            file.StatusCode.Should().Be(HttpStatusCode.OK);
            (await file.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(1000);
        }
    }

    [Fact]
    public async Task User_crud_lock_reset_bulk_assign_and_export()
    {
        var admin = await factory.AdminClientAsync();
        var roles = await (await admin.GetAsync("/api/v1/roles")).ReadJsonAsync();
        var editorRole = roles.EnumerateArray().First(r => r.GetProperty("code").GetString() == "bien_tap").GetProperty("id").GetGuid();
        var username = "crud" + Guid.NewGuid().ToString("N")[..8];
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new { username, fullName = "Trần Thị Bình", phone = "0912345678", isActive = true, roleIds = new[] { editorRole } })).ReadJsonAsync();
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("temporaryPassword").GetString().Should().NotBeNullOrEmpty();

        var got = await (await admin.GetAsync($"/api/v1/users/{id}")).ReadJsonAsync();
        got.GetProperty("phone").GetString().Should().Be("0912345678");
        using (var scope = factory.Services.CreateScope())
        {
            var raw = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == id).Select(u => u.Phone).FirstAsync();
            raw.Should().StartWith("dp:", "phone numbers are encrypted at rest");
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Workspaces.AnyAsync(w => w.OwnerId == id)).Should().BeTrue("a personal workspace is provisioned");
        }

        var list = await (await admin.GetAsync($"/api/v1/users?q=tran%20thi%20binh&roleId={editorRole}")).ReadJsonAsync();
        list.GetProperty("items").EnumerateArray().Should().Contain(u => u.GetProperty("id").GetGuid() == id);

        (await admin.PostAsync($"/api/v1/users/{id}/lock", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await admin.GetAsync($"/api/v1/users/{id}")).ReadJsonAsync()).GetProperty("isLocked").GetBoolean().Should().BeTrue();
        (await admin.PostAsync($"/api/v1/users/{id}/unlock", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var reset = await (await admin.PostAsJsonAsync($"/api/v1/users/{id}/reset-password", new { })).ReadJsonAsync();
        await TestHelpers.LoginAsync(factory.CreateClient(), username, reset.GetProperty("temporaryPassword").GetString()!);

        var org = (await (await admin.GetAsync("/api/v1/organizations/tree")).ReadJsonAsync())[0].GetProperty("children")[0].GetProperty("id").GetGuid();
        var bulk = await (await admin.PostAsJsonAsync("/api/v1/users/bulk-assign", new { userIds = new[] { id }, organizationId = org, roleIds = new[] { editorRole }, replaceRoles = true })).ReadJsonAsync();
        bulk.GetProperty("updated").GetInt32().Should().Be(1);

        var xlsx = await admin.GetAsync($"/api/v1/users/export?format=xlsx&organizationId={org}");
        using var wb = new XLWorkbook(await xlsx.Content.ReadAsStreamAsync());
        wb.Worksheet(1).RangeUsed()!.RowsUsed().Should().Contain(r => r.Cells().Any(c => c.GetString() == username));

        (await admin.DeleteAsync($"/api/v1/users/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Import_users_from_excel_reports_errors_per_row()
    {
        var admin = await factory.AdminClientAsync();
        var template = await admin.GetAsync("/api/v1/users/import-template");
        template.StatusCode.Should().Be(HttpStatusCode.OK);

        var u1 = "imp" + Guid.NewGuid().ToString("N")[..8];
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Người dùng");
        string[] headers = ["Tên đăng nhập", "Họ và tên", "Email", "Điện thoại", "Mã đơn vị", "Chức vụ", "Mã vai trò", "Nhóm người dùng", "Dung lượng (GB)", "Mật khẩu"];
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Cell(2, 1).Value = u1; ws.Cell(2, 2).Value = "Lê Văn Nhập"; ws.Cell(2, 5).Value = "TTTT"; ws.Cell(2, 7).Value = "chi_xem";
        ws.Cell(3, 1).Value = "x"; ws.Cell(3, 2).Value = ""; ws.Cell(3, 5).Value = "KHONG_CO";
        var ms = new MemoryStream();
        wb.SaveAs(ms);

        async Task<System.Text.Json.JsonElement> Upload(byte[] bytes)
        {
            var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            form.Add(content, "file", "users.xlsx");
            return await (await admin.PostAsync("/api/v1/users/import", form)).ReadJsonAsync();
        }

        var bad = await Upload(ms.ToArray());
        bad.GetProperty("imported").GetInt32().Should().Be(0);
        bad.GetProperty("errors").EnumerateArray().Should().OnlyContain(e => e.GetProperty("row").GetInt32() == 3);

        ws.Row(3).Delete();
        ms = new MemoryStream();
        wb.SaveAs(ms);
        var ok = await Upload(ms.ToArray());
        ok.GetProperty("imported").GetInt32().Should().Be(1);
        ok.GetProperty("users")[0].GetProperty("temporaryPassword").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Role_permission_matrix_changes_are_audited_and_enforced()
    {
        var admin = await factory.AdminClientAsync();
        var created = await (await admin.PostAsJsonAsync("/api/v1/roles", new { code = "kt_" + Random.Shared.Next(1000, 9999), name = "Vai trò kiểm thử" })).ReadJsonAsync();
        var roleId = created.GetProperty("id").GetGuid();
        var matrix = await (await admin.GetAsync($"/api/v1/roles/{roleId}/permissions")).ReadJsonAsync();
        matrix.GetProperty("actions").GetArrayLength().Should().Be(7);
        (await admin.PutAsJsonAsync($"/api/v1/roles/{roleId}/permissions", new[] { "org.view", "catalog.view" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (client, userId, _) = await factory.CreateUserClientAsync();
        (await client.GetAsync("/api/v1/organizations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}", new { username = (await (await admin.GetAsync($"/api/v1/users/{userId}")).ReadJsonAsync()).GetProperty("username").GetString(), fullName = "Có vai trò", isActive = true, roleIds = new[] { roleId } })).ReadJsonAsync();
        var orgRes = await client.GetAsync("/api/v1/organizations");
        orgRes.StatusCode.Should().Be(HttpStatusCode.OK, await orgRes.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.AuditLogs.AnyAsync(a => a.Action == Domain.Enums.AuditAction.PermissionChange && a.ResourceName!.Contains("Vai trò kiểm thử"))).Should().BeTrue();

        var system = (await (await admin.GetAsync("/api/v1/roles")).ReadJsonAsync()).EnumerateArray().First(r => r.GetProperty("isSystem").GetBoolean()).GetProperty("id").GetGuid();
        (await admin.DeleteAsync($"/api/v1/roles/{system}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var comparison = await (await admin.GetAsync("/api/v1/permissions/comparison")).ReadJsonAsync();
        comparison.GetProperty("roles").GetArrayLength().Should().BeGreaterThanOrEqualTo(5);
        (await admin.GetAsync("/api/v1/permissions/comparison/export?format=pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Catalogs_seeded_and_items_crud_with_import_export()
    {
        var admin = await factory.AdminClientAsync();
        var catalogs = await (await admin.GetAsync("/api/v1/catalogs")).ReadJsonAsync();
        var productTypes = catalogs.EnumerateArray().First(c => c.GetProperty("code").GetString() == "loai_san_pham");
        productTypes.GetProperty("itemCount").GetInt32().Should().Be(6);

        var cat = await (await admin.PostAsJsonAsync("/api/v1/catalogs", new { code = "kt" + Random.Shared.Next(1000, 9999), name = "Danh mục kiểm thử", sortOrder = 99 })).ReadJsonAsync();
        var catId = cat.GetProperty("id").GetGuid();
        var item = await (await admin.PostAsJsonAsync($"/api/v1/catalogs/{catId}/items", new { code = "A", name = "Mục A", sortOrder = 1, isActive = true })).ReadJsonAsync();
        (await admin.PostAsJsonAsync($"/api/v1/catalogs/{catId}/items", new { code = "A", name = "Trùng", sortOrder = 2, isActive = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var export = await admin.GetAsync($"/api/v1/catalogs/{catId}/items/export?format=xlsx");
        var bytes = await export.Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);
        // Re-import a modified sheet (headers start after the title rows written by the exporter).
        using var imp = new XLWorkbook();
        var s = imp.AddWorksheet("x");
        s.Cell(1, 1).Value = "Mã"; s.Cell(1, 2).Value = "Tên"; s.Cell(1, 3).Value = "Mã mục cha"; s.Cell(1, 4).Value = "Thứ tự"; s.Cell(1, 5).Value = "Trạng thái";
        s.Cell(2, 1).Value = "B"; s.Cell(2, 2).Value = "Mục B"; s.Cell(2, 3).Value = "A"; s.Cell(2, 4).Value = "2"; s.Cell(2, 5).Value = "Đang sử dụng";
        var ms = new MemoryStream();
        imp.SaveAs(ms);
        var form = new MultipartFormDataContent { { new ByteArrayContent(ms.ToArray()), "file", "items.xlsx" } };
        var res = await (await admin.PostAsync($"/api/v1/catalogs/{catId}/items/import", form)).ReadJsonAsync();
        res.GetProperty("created").GetInt32().Should().Be(1);
        var items = await (await admin.GetAsync($"/api/v1/catalogs/{catId}/items")).ReadJsonAsync();
        items.EnumerateArray().First(i => i.GetProperty("code").GetString() == "B").GetProperty("parentId").GetGuid().Should().Be(item.GetProperty("id").GetGuid());
        ws.Should().NotBeNull();
    }

    [Fact]
    public async Task Settings_update_writes_change_log_and_audit()
    {
        var admin = await factory.AdminClientAsync();
        var list = await (await admin.GetAsync("/api/v1/settings")).ReadJsonAsync();
        list.GetArrayLength().Should().BeGreaterThan(10);
        var bad = await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["backup.cron"] = "không hợp lệ" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var ok = await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["trash.retention_days"] = 45 })).ReadJsonAsync();
        ok.GetProperty("changed").GetInt32().Should().Be(1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.ChangeLogs.AnyAsync(c => c.ChangeType == "config")).Should().BeTrue();
        await (await admin.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, object> { ["trash.retention_days"] = 30 })).ReadJsonAsync();
    }

    [Fact]
    public async Task Viewer_role_cannot_access_admin_functions()
    {
        var (viewer, _, _) = await factory.CreateUserClientAsync("chi_xem");
        (await viewer.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/v1/roles", new { code = "hack", name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync("/api/v1/sessions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync("/api/v1/users/lookup?q=admin")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
