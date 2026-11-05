using System.Reflection;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlueDrive.Infrastructure.Persistence.Seed;

/// <summary>Idempotent seed of permissions, system roles, catalogs, root organization and the default admin.</summary>
public sealed class ReferenceDataSeeder(
    AppDbContext db, IPasswordService passwords, ISettingsService settings, ISensitiveDataProtector pii,
    IPermissionService permissionService, IConfiguration config, ILogger<ReferenceDataSeeder> logger) : IDataSeeder
{
    public int Order => 0;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedPermissionsAsync(ct);
        await SeedRolesAsync(ct);
        await SeedLabelsAsync(ct);
        await SeedCatalogsAsync(ct);
        var root = await SeedRootOrganizationAsync(ct);
        await SeedAdminAsync(root, ct);
        await SeedChangeLogAsync(ct);
        permissionService.Invalidate();
    }

    private async Task SeedPermissionsAsync(CancellationToken ct)
    {
        var existing = await db.Permissions.ToDictionaryAsync(p => p.Code, ct);
        foreach (var def in Perm.All)
        {
            if (!existing.TryGetValue(def.Code, out var p)) db.Permissions.Add(p = new Permission { Code = def.Code });
            p.Module = def.Function;
            p.ModuleName = def.FunctionName;
            p.Action = def.Action;
            p.Name = def.Name;
            p.SortOrder = def.SortOrder;
        }
        var obsolete = existing.Keys.Except(Perm.All.Select(d => d.Code)).ToList();
        if (obsolete.Count > 0)
        {
            await db.RolePermissions.Where(rp => obsolete.Contains(rp.PermissionCode)).ExecuteDeleteAsync(ct);
            db.Permissions.RemoveRange(existing.Where(kv => obsolete.Contains(kv.Key)).Select(kv => kv.Value));
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Default sensitivity labels (spec 7.1). BlueDrive is not for state secrets: no "Mật" / "Tối mật" / "Tuyệt mật".</summary>
    private async Task SeedLabelsAsync(CancellationToken ct)
    {
        if (await db.SensitivityLabels.AnyAsync(ct)) return;
        const string note = " Không dùng cho thông tin bí mật nhà nước (hệ thống không lưu trữ thông tin mật).";
        db.SensitivityLabels.AddRange(
            new SensitivityLabel { Name = "Công khai", Color = "#52c41a", Priority = 0, Description = "Được phép công bố rộng rãi." + note },
            new SensitivityLabel { Name = "Nội bộ", Color = "#1677ff", Priority = 10, BlockAnyoneLinks = true, Description = "Dùng trong đơn vị; không tạo liên kết cho bất kỳ ai." + note },
            new SensitivityLabel
            {
                Name = "Hạn chế", Color = "#fa541c", Priority = 20, BlockExternal = true, BlockAnyoneLinks = true, ViewOnlyOutsideSpace = true, Watermark = true, BlockSync = true,
                Description = "Chỉ những người cần biết; không chia sẻ ra ngoài, người ngoài Space chỉ xem, có hình mờ, không đồng bộ xuống máy." + note,
            });
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        (string Code, string Name, string Desc)[] roles =
        [
            (Perm.Roles.Admin, "Quản trị hệ thống", "Toàn quyền quản trị hệ thống"),
            (Perm.Roles.Editor, "Biên tập/Tải lên", "Tải lên, biên tập, chia sẻ tư liệu"),
            (Perm.Roles.Approver, "Phê duyệt", "Phê duyệt tư liệu trước khi công bố"),
            (Perm.Roles.Viewer, "Chỉ xem", "Chỉ xem và tìm kiếm tư liệu đã được phê duyệt"),
            (Perm.Roles.Guest, "Khách bên ngoài", "Khách được mời qua email; chỉ làm việc trên tư liệu được chia sẻ trực tiếp"),
        ];
        foreach (var (code, name, desc) in roles)
        {
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code, ct);
            if (role is null)
            {
                role = new Role { Code = code, Name = name, Description = desc, IsSystem = true };
                db.Roles.Add(role);
                foreach (var p in Perm.DefaultRolePermissions[code]) role.Permissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = p });
            }
            else if (code == Perm.Roles.Admin)
            {
                // The administrator role always holds every permission (new permissions included).
                var missing = Perm.All.Select(p => p.Code).Except(role.Permissions.Select(p => p.PermissionCode));
                foreach (var p in missing) role.Permissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = p });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedCatalogsAsync(CancellationToken ct)
    {
        (string Code, string Name, string[] Items)[] catalogs =
        [
            ("loai_san_pham", "Loại sản phẩm truyền thông", ["Hình ảnh", "Video", "Âm thanh", "Tài liệu", "Infographic", "Ấn phẩm điện tử"]),
            ("chu_de", "Chủ đề", ["Dân số và phát triển", "Kế hoạch hóa gia đình", "Chăm sóc sức khỏe sinh sản", "Mất cân bằng giới tính khi sinh", "Già hóa dân số", "Nâng cao chất lượng dân số"]),
            ("chien_dich", "Chiến dịch truyền thông", ["Ngày Dân số Việt Nam 26/12", "Ngày Dân số Thế giới 11/7", "Tháng hành động quốc gia về dân số"]),
            ("doi_tuong", "Đối tượng truyền thông", ["Vị thành niên, thanh niên", "Cặp vợ chồng trong độ tuổi sinh đẻ", "Người cao tuổi", "Cán bộ dân số"]),
            ("nguon", "Nguồn tư liệu", ["Tự sản xuất", "Đơn vị phối hợp", "Sưu tầm"]),
            ("nam", "Năm phát hành", [.. Enumerable.Range(DateTime.UtcNow.Year - 5, 7).Select(y => y.ToString())]),
        ];
        var order = 0;
        foreach (var (code, name, items) in catalogs)
        {
            order++;
            if (await db.Catalogs.AnyAsync(c => c.Code == code, ct)) continue;
            var cat = new Catalog { Code = code, Name = name, IsSystem = code == "loai_san_pham", SortOrder = order };
            for (var i = 0; i < items.Length; i++)
                cat.Items.Add(new CatalogItem { CatalogId = cat.Id, Code = Application.Common.TextUtil.Unaccent(items[i]).Replace(' ', '_').Replace("/", "_").Replace(",", ""), Name = items[i], SortOrder = i + 1 });
            db.Catalogs.Add(cat);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<Organization> SeedRootOrganizationAsync(CancellationToken ct)
    {
        var root = await db.Organizations.FirstOrDefaultAsync(o => o.ParentId == null && o.Code == "TTTT", ct);
        if (root is not null) return root;
        root = new Organization
        {
            Code = "TTTT",
            Name = config["App:OrganizationName"] ?? "Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số",
            Type = OrganizationType.DonVi,
            SortOrder = 1,
        };
        root.Path = $"/{root.Id}/";
        db.Organizations.Add(root);
        (string Code, string Name)[] departments =
        [
            ("TTTT-HCTH", "Phòng Hành chính - Tổng hợp"),
            ("TTTT-TT", "Phòng Truyền thông"),
            ("TTTT-CDS", "Phòng Chuyển đổi số"),
            ("TTTT-DV", "Phòng Dịch vụ dân số"),
        ];
        for (var i = 0; i < departments.Length; i++)
        {
            var d = new Organization { Code = departments[i].Code, Name = departments[i].Name, Type = OrganizationType.PhongBan, ParentId = root.Id, SortOrder = i + 1 };
            d.Path = $"{root.Path}{d.Id}/";
            db.Organizations.Add(d);
        }
        await db.SaveChangesAsync(ct);
        return root;
    }

    private async Task SeedAdminAsync(Organization root, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Username == "admin", ct)) return;
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Thiếu Seed:AdminPassword (ADMIN_PASSWORD trong .env) để tạo tài khoản quản trị mặc định.");
        var email = config["Seed:AdminEmail"];
        var admin = new User
        {
            Username = "admin",
            FullName = "Quản trị hệ thống",
            OrganizationId = root.Id,
            Position = "Quản trị viên",
            Email = pii.Protect(email),
            EmailHash = pii.Hash(email),
        };
        await passwords.SetPasswordAsync(admin, password, mustChange: true, ct);
        db.Users.Add(admin);
        var adminRole = await db.Roles.FirstAsync(r => r.Code == Perm.Roles.Admin, ct);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        await WorkspaceProvisioning.EnsurePersonalWorkspaceAsync(db, settings, admin, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Đã tạo tài khoản quản trị mặc định 'admin' (bắt buộc đổi mật khẩu khi đăng nhập lần đầu)");
    }

    private async Task SeedChangeLogAsync(CancellationToken ct)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        if (await db.ChangeLogs.AnyAsync(c => c.Version == version && c.ChangeType == "deploy", ct)) return;
        db.ChangeLogs.Add(new ChangeLog
        {
            Version = version,
            ChangeType = "deploy",
            Description = (await db.ChangeLogs.AnyAsync(ct)) ? "Nâng cấp phiên bản hệ thống" : "Cài đặt lần đầu hệ thống BlueDrive",
            AppliedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
