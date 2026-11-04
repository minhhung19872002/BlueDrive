using System.Text.Json;
using BlueDrive.Api.Infrastructure;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Catalogs;
using BlueDrive.Application.Features.Organizations;
using BlueDrive.Application.Features.Roles;
using BlueDrive.Application.Features.Settings;
using BlueDrive.Application.Features.Users;
using BlueDrive.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

/// <summary>Cơ cấu tổ chức (HSMT #1–3).</summary>
[Route("api/v1/organizations")]
public class OrganizationsController : ApiControllerBase
{
    /// <summary>Danh sách dạng bảng, lọc theo mã/tên (không dấu), trạng thái, đơn vị cha.</summary>
    [HttpGet, HasPermission(Perm.OrgView)]
    public Task<PagedResult<OrganizationDto>> List([FromQuery] ListOrganizationsQuery query) => Mediator.Send(query);

    /// <summary>Danh sách dạng cây. Mở cho mọi người dùng đã đăng nhập: là nguồn chọn đơn vị (chia sẻ, nhóm tài nguyên, bộ lọc tìm kiếm).</summary>
    [HttpGet("tree")]
    public Task<IReadOnlyList<OrganizationTreeNode>> Tree([FromQuery] bool includeInactive = true) => Mediator.Send(new GetOrganizationTreeQuery(includeInactive));

    [HttpGet("{id:guid}"), HasPermission(Perm.OrgView)]
    public Task<OrganizationDto> Get(Guid id) => Mediator.Send(new GetOrganizationQuery(id));

    /// <summary>Xuất Excel/PDF theo bộ lọc hiện tại.</summary>
    [HttpGet("export"), HasPermission(Perm.OrgView)]
    public async Task<IActionResult> Export([FromQuery] ListOrganizationsQuery query, [FromQuery] string format = "xlsx")
        => FileResult(await Mediator.Send(new ExportOrganizationsQuery(query, format, ExportedBy)));

    [HttpPost, HasPermission(Perm.OrgCreate)]
    public async Task<IActionResult> Create(SaveOrganizationCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("{id:guid}"), HasPermission(Perm.OrgEdit)]
    public async Task<IActionResult> Update(Guid id, SaveOrganizationCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = id }) });

    /// <summary>Kéo thả: đổi đơn vị cha và thứ tự.</summary>
    [HttpPost("{id:guid}/move"), HasPermission(Perm.OrgEdit)]
    public async Task<IActionResult> Move(Guid id, MoveOrganizationCommand cmd)
    {
        await Mediator.Send(cmd with { Id = id });
        return NoContent();
    }

    /// <summary>Xóa; nếu còn người dùng/đơn vị con thì chỉ cho phép "ngừng hoạt động" (deactivateIfInUse=true).</summary>
    [HttpDelete("{id:guid}"), HasPermission(Perm.OrgDelete)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool deactivateIfInUse = false)
        => Ok(new { result = await Mediator.Send(new DeleteOrganizationCommand(id, deactivateIfInUse)) });
}

/// <summary>Người dùng (HSMT #4–6).</summary>
[Route("api/v1/users")]
public class UsersController : ApiControllerBase
{
    [HttpGet, HasPermission(Perm.UserView)]
    public Task<PagedResult<UserDto>> List([FromQuery] ListUsersQuery query) => Mediator.Send(query);

    /// <summary>Tra cứu nhanh người dùng (chọn người chia sẻ, @nhắc tên).</summary>
    [HttpGet("lookup")]
    public Task<IReadOnlyList<UserLookupDto>> Lookup([FromQuery] string? q, [FromQuery] Guid? organizationId, [FromQuery] int take = 20)
        => Mediator.Send(new LookupUsersQuery(q, organizationId, take));

    [HttpGet("{id:guid}"), HasPermission(Perm.UserView)]
    public Task<UserDto> Get(Guid id) => Mediator.Send(new GetUserQuery(id));

    [HttpGet("export"), HasPermission(Perm.UserView)]
    public async Task<IActionResult> Export([FromQuery] ListUsersQuery query, [FromQuery] string format = "xlsx")
        => FileResult(await Mediator.Send(new ExportUsersQuery(query, format, ExportedBy)));

    [HttpPost, HasPermission(Perm.UserCreate)]
    public Task<SaveUserResult> Create(SaveUserCommand cmd) => Mediator.Send(cmd with { Id = null });

    [HttpPut("{id:guid}"), HasPermission(Perm.UserEdit)]
    public Task<SaveUserResult> Update(Guid id, SaveUserCommand cmd) => Mediator.Send(cmd with { Id = id, Password = null });

    [HttpDelete("{id:guid}"), HasPermission(Perm.UserDelete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new DeleteUserCommand(id));
        return NoContent();
    }

    [HttpPost("{id:guid}/lock"), HasPermission(Perm.UserAdmin)]
    public async Task<IActionResult> Lock(Guid id)
    {
        await Mediator.Send(new SetUserLockCommand(id, true));
        return NoContent();
    }

    [HttpPost("{id:guid}/unlock"), HasPermission(Perm.UserAdmin)]
    public async Task<IActionResult> Unlock(Guid id)
    {
        await Mediator.Send(new SetUserLockCommand(id, false));
        return NoContent();
    }

    /// <summary>Quản trị đặt lại mật khẩu (sinh mật khẩu tạm nếu không nhập).</summary>
    [HttpPost("{id:guid}/reset-password"), HasPermission(Perm.UserAdmin)]
    public Task<SaveUserResult> ResetPassword(Guid id, [FromBody] ResetPasswordBody? body) => Mediator.Send(new AdminResetPasswordCommand(id, body?.NewPassword));

    /// <summary>Gán đơn vị/vai trò hàng loạt.</summary>
    [HttpPost("bulk-assign"), HasPermission(Perm.UserEdit)]
    public async Task<IActionResult> BulkAssign(BulkAssignUsersCommand cmd) => Ok(new { updated = await Mediator.Send(cmd) });

    /// <summary>Tải tệp mẫu Excel nhập người dùng.</summary>
    [HttpGet("import-template"), HasPermission(Perm.UserCreate)]
    public async Task<IActionResult> ImportTemplate() => FileResult(await Mediator.Send(new GetUserImportTemplateQuery()));

    /// <summary>Nhập người dùng hàng loạt từ Excel (dryRun=true chỉ kiểm tra).</summary>
    [HttpPost("import"), HasPermission(Perm.UserCreate), RequestSizeLimit(20_000_000)]
    public async Task<ImportUsersResult> Import(IFormFile file, [FromQuery] bool dryRun = false)
    {
        await using var s = file.OpenReadStream();
        return await Mediator.Send(new ImportUsersCommand(s, dryRun));
    }

    public sealed record ResetPasswordBody(string? NewPassword);
}

/// <summary>Nhóm người dùng (HSMT #4).</summary>
[Route("api/v1/user-groups")]
public class UserGroupsController : ApiControllerBase
{
    /// <summary>Tra cứu nhóm người dùng. Mở cho mọi người dùng đã đăng nhập vì hộp chọn đối tượng chia sẻ dùng danh sách này.</summary>
    [HttpGet]
    public Task<PagedResult<UserGroupDto>> List([FromQuery] ListUserGroupsQuery query) => Mediator.Send(query);

    [HttpGet("{id:guid}"), HasPermission(Perm.GroupView)]
    public Task<UserGroupDetailDto> Get(Guid id) => Mediator.Send(new GetUserGroupQuery(id));

    [HttpPost, HasPermission(Perm.GroupCreate)]
    public async Task<IActionResult> Create(SaveUserGroupCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("{id:guid}"), HasPermission(Perm.GroupEdit)]
    public async Task<IActionResult> Update(Guid id, SaveUserGroupCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = id }) });

    [HttpDelete("{id:guid}"), HasPermission(Perm.GroupDelete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new DeleteUserGroupCommand(id));
        return NoContent();
    }
}

/// <summary>Vai trò và phân quyền chức năng (HSMT #7–9, #54).</summary>
[Route("api/v1/roles")]
public class RolesController : ApiControllerBase
{
    /// <summary>Tra cứu vai trò. Mở cho mọi người dùng đã đăng nhập vì hộp chọn đối tượng chia sẻ dùng danh sách này.</summary>
    [HttpGet]
    public Task<IReadOnlyList<RoleDto>> List([FromQuery] string? q) => Mediator.Send(new ListRolesQuery(q));

    [HttpPost, HasPermission(Perm.RoleCreate)]
    public async Task<IActionResult> Create(SaveRoleCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("{id:guid}"), HasPermission(Perm.RoleEdit)]
    public async Task<IActionResult> Update(Guid id, SaveRoleCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = id }) });

    [HttpDelete("{id:guid}"), HasPermission(Perm.RoleDelete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new DeleteRoleCommand(id));
        return NoContent();
    }

    /// <summary>Ma trận quyền của một vai trò (chức năng × hành động).</summary>
    [HttpGet("{id:guid}/permissions"), HasPermission(Perm.RoleView)]
    public Task<RolePermissionMatrixDto> Permissions(Guid id) => Mediator.Send(new GetRolePermissionsQuery(id));

    /// <summary>Lưu ma trận quyền (ghi nhật ký permission_change).</summary>
    [HttpPut("{id:guid}/permissions"), HasPermission(Perm.RoleAdmin)]
    public async Task<IActionResult> SetPermissions(Guid id, [FromBody] IReadOnlyList<string> permissionCodes)
    {
        await Mediator.Send(new SetRolePermissionsCommand(id, permissionCodes));
        return NoContent();
    }
}

/// <summary>Danh mục quyền và bảng đối chiếu (HSMT #9).</summary>
[Route("api/v1/permissions")]
public class PermissionsController : ApiControllerBase
{
    [HttpGet, HasPermission(Perm.RoleView)]
    public Task<IReadOnlyList<Perm.Definition>> List() => Mediator.Send(new ListPermissionsQuery());

    /// <summary>Bảng đối chiếu toàn bộ chức năng × vai trò.</summary>
    [HttpGet("comparison"), HasPermission(Perm.RoleView)]
    public Task<PermissionComparisonDto> Comparison([FromQuery] string? module, [FromQuery] string? q) => Mediator.Send(new GetPermissionComparisonQuery(module, q));

    [HttpGet("comparison/export"), HasPermission(Perm.RoleView)]
    public async Task<IActionResult> ExportComparison([FromQuery] string? module, [FromQuery] string? q, [FromQuery] string format = "xlsx")
        => FileResult(await Mediator.Send(new ExportPermissionComparisonQuery(module, q, format, ExportedBy)));
}

/// <summary>Danh mục dùng chung (HSMT #10).</summary>
[Route("api/v1/catalogs")]
public class CatalogsController : ApiControllerBase
{
    [HttpGet, HasPermission(Perm.CatalogView)]
    public Task<IReadOnlyList<CatalogDto>> List([FromQuery] string? q) => Mediator.Send(new ListCatalogsQuery(q));

    /// <summary>Toàn bộ danh mục đang dùng kèm các mục (cho bộ lọc/nhập metadata).</summary>
    [HttpGet("options")]
    public Task<IReadOnlyList<CatalogWithItemsDto>> Options() => Mediator.Send(new GetCatalogOptionsQuery());

    [HttpPost, HasPermission(Perm.CatalogCreate)]
    public async Task<IActionResult> Create(SaveCatalogCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("{id:guid}"), HasPermission(Perm.CatalogEdit)]
    public async Task<IActionResult> Update(Guid id, SaveCatalogCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = id }) });

    [HttpDelete("{id:guid}"), HasPermission(Perm.CatalogDelete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new DeleteCatalogCommand(id));
        return NoContent();
    }

    [HttpGet("{id:guid}/items"), HasPermission(Perm.CatalogView)]
    public Task<IReadOnlyList<CatalogItemDto>> Items(Guid id, [FromQuery] string? q, [FromQuery] bool? isActive) => Mediator.Send(new ListCatalogItemsQuery(id, q, isActive));

    [HttpPost("{id:guid}/items"), HasPermission(Perm.CatalogCreate)]
    public async Task<IActionResult> CreateItem(Guid id, SaveCatalogItemCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null, CatalogId = id }) });

    [HttpPut("{id:guid}/items/{itemId:guid}"), HasPermission(Perm.CatalogEdit)]
    public async Task<IActionResult> UpdateItem(Guid id, Guid itemId, SaveCatalogItemCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = itemId, CatalogId = id }) });

    [HttpDelete("{id:guid}/items/{itemId:guid}"), HasPermission(Perm.CatalogDelete)]
    public async Task<IActionResult> DeleteItem(Guid itemId)
    {
        await Mediator.Send(new DeleteCatalogItemCommand(itemId));
        return NoContent();
    }

    [HttpGet("{id:guid}/items/export"), HasPermission(Perm.CatalogView)]
    public async Task<IActionResult> ExportItems(Guid id, [FromQuery] string format = "xlsx") => FileResult(await Mediator.Send(new ExportCatalogItemsQuery(id, format, ExportedBy)));

    /// <summary>Nhập mục danh mục từ Excel (cột: Mã, Tên, Mã mục cha, Thứ tự, Trạng thái).</summary>
    [HttpPost("{id:guid}/items/import"), HasPermission(Perm.CatalogCreate), RequestSizeLimit(20_000_000)]
    public async Task<CatalogImportResult> ImportItems(Guid id, IFormFile file)
    {
        await using var s = file.OpenReadStream();
        return await Mediator.Send(new ImportCatalogItemsCommand(id, s));
    }
}

/// <summary>Cấu hình hệ thống.</summary>
[Route("api/v1/settings")]
public class SettingsController : ApiControllerBase
{
    [HttpGet, HasPermission(Perm.SettingsView)]
    public Task<IReadOnlyList<SettingDto>> List() => Mediator.Send(new ListSettingsQuery());

    /// <summary>Cập nhật nhiều cấu hình (tự ghi nhật ký thay đổi và audit config_change).</summary>
    [HttpPut, HasPermission(Perm.SettingsEdit)]
    public async Task<IActionResult> Update([FromBody] Dictionary<string, JsonElement> values) => Ok(new { changed = await Mediator.Send(new UpdateSettingsCommand(values)) });
}

/// <summary>Mô hình phân quyền mới (Perm-Phase 1): dựng lại từ mô hình hiện tại và đối chiếu quyền cũ ⊆ quyền mới.</summary>
[Route("api/v1/admin/permission-model")]
public class PermissionModelController : ApiControllerBase
{
    [HttpPost("rebuild"), HasPermission(Perm.SettingsEdit)]
    public Task<Application.Features.Access.PermissionProjection.Result> Rebuild() => Mediator.Send(new Application.Features.Access.RebuildPermissionModelCommand());

    [HttpGet("verify"), HasPermission(Perm.SettingsEdit)]
    public Task<Application.Features.Access.PermissionMigrationReport> Verify([FromQuery] int samples = 10_000, [FromQuery] int? seed = null) =>
        Mediator.Send(new Application.Features.Access.VerifyPermissionMigrationQuery(samples, seed));
}

/// <summary>Quyền hiệu lực và "Kiểm tra quyền" trên một mục (mô hình phân quyền mới).</summary>
[Route("api/v1/items/{id:guid}")]
public class ItemAccessController : ApiControllerBase
{
    [HttpGet("effective-permissions")]
    public Task<Application.Features.Access.EffectivePermissionsDto> Effective(Guid id, [FromQuery] Guid? user) =>
        Mediator.Send(new Application.Features.Access.GetEffectivePermissionsQuery(id, user));

    [HttpGet("explain")]
    public Task<Application.Features.Access.PermissionExplanation> Explain(Guid id, [FromQuery] Guid? user) =>
        Mediator.Send(new Application.Features.Access.ExplainPermissionsQuery(id, user));
}

/// <summary>Thông báo của tôi.</summary>
[Route("api/v1/notifications")]
public class NotificationsController : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<NotificationDto>> List([FromQuery] ListNotificationsQuery query) => Mediator.Send(query);

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount() => Ok(new { count = await Mediator.Send(new UnreadNotificationCountQuery()) });

    [HttpPost("mark-read")]
    public async Task<IActionResult> MarkRead([FromBody] IReadOnlyList<Guid>? ids) => Ok(new { updated = await Mediator.Send(new MarkNotificationsReadCommand(ids)) });
}
