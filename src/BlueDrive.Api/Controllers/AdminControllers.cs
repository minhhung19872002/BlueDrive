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

    /// <summary>Người có quyền truy cập (kèm nguồn quyền).</summary>
    [HttpGet("permissions")]
    public Task<Application.Features.Access.ItemPermissionsDto> Permissions(Guid id) => Mediator.Send(new Application.Features.Access.GetItemPermissionsQuery(id));

    /// <summary>Cấp quyền cho người, nhóm hoặc đơn vị.</summary>
    [HttpPost("permissions")]
    public async Task<IActionResult> Grant(Guid id, Application.Features.Access.GrantItemPermissionCommand cmd) =>
        Ok(new { granted = await Mediator.Send(cmd with { ItemId = id }) });

    [HttpPatch("permissions/{entryId:guid}")]
    public async Task<IActionResult> UpdatePermission(Guid id, Guid entryId, Application.Features.Access.UpdateItemPermissionCommand cmd)
    {
        await Mediator.Send(cmd with { ItemId = id, EntryId = entryId });
        return NoContent();
    }

    [HttpDelete("permissions/{entryId:guid}")]
    public async Task<IActionResult> RevokePermission(Guid id, Guid entryId)
    {
        await Mediator.Send(new Application.Features.Access.RevokeItemPermissionCommand(id, entryId));
        return NoContent();
    }

    /// <summary>Ngắt kế thừa (copy = sao chép quyền hiện tại).</summary>
    [HttpPost("inheritance")]
    public async Task<IActionResult> BreakInheritance(Guid id, [FromQuery] bool copy = true)
    {
        await Mediator.Send(new Application.Features.Access.SetInheritanceCommand(id, Inherit: false, Copy: copy));
        return NoContent();
    }

    /// <summary>Khôi phục kế thừa (recursive = cả các mục con có quyền riêng).</summary>
    [HttpDelete("inheritance")]
    public async Task<IActionResult> RestoreInheritance(Guid id, [FromQuery] bool recursive = false)
    {
        await Mediator.Send(new Application.Features.Access.SetInheritanceCommand(id, Inherit: true, Recursive: recursive));
        return NoContent();
    }

    [HttpPut("general-access")]
    public async Task<IActionResult> GeneralAccess(Guid id, Application.Features.Access.SetGeneralAccessCommand cmd)
    {
        await Mediator.Send(cmd with { ItemId = id });
        return NoContent();
    }

    /// <summary>Chính sách chia sẻ hiệu lực của mục (tầng chặt nhất thắng).</summary>
    [HttpGet("sharing-policy")]
    public Task<Application.Features.Access.ItemSharingPolicyDto> SharingPolicy(Guid id) =>
        Mediator.Send(new Application.Features.Access.GetItemSharingPolicyQuery(id));

    [HttpPut("external-sharing")]
    public async Task<IActionResult> ExternalSharing(Guid id, Application.Features.Access.SetItemExternalSharingCommand cmd)
    {
        await Mediator.Send(cmd with { ItemId = id });
        return NoContent();
    }
}

/// <summary>Nhãn phân loại (không dùng cho thông tin bí mật nhà nước).</summary>
[Route("api/v1")]
public class LabelsController : ApiControllerBase
{
    [HttpGet("labels")]
    public Task<IReadOnlyList<Application.Features.Access.LabelDto>> List() => Mediator.Send(new Application.Features.Access.ListLabelsQuery());

    [HttpPost("admin/labels"), HasPermission(Perm.SettingsEdit)]
    public async Task<IActionResult> Create(Application.Features.Access.SaveLabelCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("admin/labels/{id:guid}"), HasPermission(Perm.SettingsEdit)]
    public async Task<IActionResult> Save(Guid id, Application.Features.Access.SaveLabelCommand cmd)
    {
        await Mediator.Send(cmd with { Id = id });
        return NoContent();
    }

    [HttpGet("items/{id:guid}/label")]
    public Task<Application.Features.Access.ItemLabelDto> ItemLabel(Guid id) => Mediator.Send(new Application.Features.Access.GetItemLabelQuery(id));

    /// <summary>Gán / đổi / gỡ nhãn của tệp, thư mục hoặc Space (mặc định); hạ nhãn cần lý do.</summary>
    [HttpPut("items/{id:guid}/label")]
    public async Task<IActionResult> SetLabel(Guid id, Application.Features.Access.SetLabelCommand cmd)
    {
        await Mediator.Send(cmd with { ItemId = id });
        return NoContent();
    }
}

/// <summary>Quản trị cấp quyền (ma trận quyền × cấp quyền).</summary>
[Route("api/v1/admin/permission-levels")]
public class PermissionLevelAdminController : ApiControllerBase
{
    [HttpGet("rights"), HasPermission(Perm.RoleView)]
    public Task<IReadOnlyList<Application.Features.Access.AccessRightDto>> Rights() => Mediator.Send(new Application.Features.Access.ListAccessRightsQuery());

    [HttpPost, HasPermission(Perm.RoleEdit)]
    public async Task<IActionResult> Create(Application.Features.Access.SavePermissionLevelCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("{id:guid}"), HasPermission(Perm.RoleEdit)]
    public async Task<IActionResult> Save(Guid id, Application.Features.Access.SavePermissionLevelCommand cmd)
    {
        await Mediator.Send(cmd with { Id = id });
        return NoContent();
    }

    [HttpDelete("{id:guid}"), HasPermission(Perm.RoleEdit)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new Application.Features.Access.DeletePermissionLevelCommand(id));
        return NoContent();
    }

    [HttpPost("{id:guid}/reset"), HasPermission(Perm.RoleEdit)]
    public async Task<IActionResult> Reset(Guid id)
    {
        await Mediator.Send(new Application.Features.Access.ResetPermissionLevelCommand(id));
        return NoContent();
    }
}

/// <summary>Quản trị khách bên ngoài.</summary>
[Route("api/v1/admin/guests")]
public class GuestsController : ApiControllerBase
{
    [HttpGet, HasPermission(Perm.UserView)]
    public Task<IReadOnlyList<Application.Features.Access.GuestDto>> List([FromQuery] string? query) => Mediator.Send(new Application.Features.Access.ListGuestsQuery(query));

    [HttpPost("{id:guid}/active"), HasPermission(Perm.UserEdit)]
    public async Task<IActionResult> SetActive(Guid id, [FromQuery] bool value)
    {
        await Mediator.Send(new Application.Features.Access.SetGuestActiveCommand(id, value));
        return NoContent();
    }

    [HttpDelete("{id:guid}"), HasPermission(Perm.UserDelete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new Application.Features.Access.DeleteGuestCommand(id));
        return NoContent();
    }
}

/// <summary>Gia hạn quyền / liên kết sắp hết hạn (kind = permission | link).</summary>
[Route("api/v1/renewals/{kind:regex(^(permission|link)$)}/{id:guid}")]
public class RenewalsController : ApiControllerBase
{
    [HttpGet]
    public Task<Application.Features.Access.RenewalInfoDto> Get(string kind, Guid id) => Mediator.Send(new Application.Features.Access.GetRenewalQuery(kind, id));

    [HttpPost("extend")]
    public Task<Application.Features.Access.RenewalInfoDto> Extend(string kind, Guid id, Application.Features.Access.ExtendCommand cmd) =>
        Mediator.Send(cmd with { Kind = kind, Id = id });

    [HttpPost("request")]
    public async Task<IActionResult> RequestRenewal(string kind, Guid id)
    {
        if (kind != "permission") return NotFound();
        return Ok(new { id = await Mediator.Send(new Application.Features.Access.RequestRenewalCommand(id)) });
    }
}

/// <summary>Chính sách chia sẻ của Space.</summary>
[Route("api/v1/workspaces/{id:guid}/sharing-policy")]
public class SpaceSharingPolicyController : ApiControllerBase
{
    [HttpGet]
    public Task<Application.Features.Access.SpaceSharingPolicyDto> Get(Guid id) => Mediator.Send(new Application.Features.Access.GetSpaceSharingPolicyQuery(id));

    [HttpPut]
    public async Task<IActionResult> Set(Guid id, Application.Features.Access.SetSpaceSharingPolicyCommand cmd)
    {
        await Mediator.Send(cmd with { WorkspaceId = id });
        return NoContent();
    }
}

/// <summary>Yêu cầu quyền truy cập.</summary>
[Route("api/v1")]
public class AccessRequestsController : ApiControllerBase
{
    /// <summary>Người chưa có quyền mở địa chỉ một mục: có tồn tại không, ai quản lý (không có tên mục).</summary>
    [HttpGet("items/{id:guid}/access-info")]
    public Task<Application.Features.Access.AccessInfoDto> Info(Guid id) => Mediator.Send(new Application.Features.Access.GetAccessInfoQuery(id));

    [HttpPost("items/{id:guid}/access-requests")]
    public async Task<IActionResult> RequestAccess(Guid id, Application.Features.Access.RequestAccessCommand cmd) =>
        Ok(new { id = await Mediator.Send(cmd with { ItemId = id }) });

    [HttpGet("items/{id:guid}/access-requests")]
    public Task<IReadOnlyList<Application.Features.Access.AccessRequestDto>> OfItem(Guid id) =>
        Mediator.Send(new Application.Features.Access.ListAccessRequestsQuery(id));

    /// <summary>Yêu cầu đang chờ trên các mục tôi quản lý (mine = yêu cầu của tôi).</summary>
    [HttpGet("access-requests")]
    public Task<IReadOnlyList<Application.Features.Access.AccessRequestDto>> Pending([FromQuery] bool mine = false) =>
        Mediator.Send(new Application.Features.Access.ListAccessRequestsQuery(null, mine));

    [HttpPost("access-requests/{id:guid}/decide")]
    public async Task<IActionResult> Decide(Guid id, Application.Features.Access.DecideAccessRequestCommand cmd)
    {
        await Mediator.Send(cmd with { Id = id });
        return NoContent();
    }
}

/// <summary>Cấp quyền (Permission Level).</summary>
[Route("api/v1/permission-levels")]
public class PermissionLevelsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<Application.Features.Access.PermissionLevelDto>> List() => Mediator.Send(new Application.Features.Access.ListPermissionLevelsQuery());
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

/// <summary>Chính sách lưu giữ, kho lưu giữ, danh sách chờ duyệt xóa (đặc tả 7.2).</summary>
[Route("api/v1/admin/retention")]
public class RetentionController : ApiControllerBase
{
    [HttpGet("policies")]
    public Task<IReadOnlyList<Application.Features.Retention.RetentionPolicyDto>> Policies() => Mediator.Send(new Application.Features.Retention.ListRetentionPoliciesQuery());

    [HttpPost("policies")]
    public async Task<IActionResult> Create(Application.Features.Retention.SaveRetentionPolicyCommand cmd) => Ok(new { id = await Mediator.Send(cmd with { Id = null }) });

    [HttpPut("policies/{id:guid}")]
    public async Task<IActionResult> Save(Guid id, Application.Features.Retention.SaveRetentionPolicyCommand cmd)
    {
        await Mediator.Send(cmd with { Id = id });
        return NoContent();
    }

    [HttpDelete("policies/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Mediator.Send(new Application.Features.Retention.DeleteRetentionPolicyCommand(id));
        return NoContent();
    }

    [HttpGet("preserved")]
    public Task<PagedResult<Application.Features.Retention.PreservedItemDto>> Preserved([FromQuery] Application.Features.Retention.ListPreservedQuery query) => Mediator.Send(query);

    [HttpPost("preserved/release")]
    public async Task<IActionResult> Release(Application.Features.Retention.ReleasePreservedCommand cmd) => Ok(new { released = await Mediator.Send(cmd) });

    [HttpPost("preserved/purge")]
    public async Task<IActionResult> Purge(Application.Features.Retention.PurgePreservedCommand cmd) => Ok(new { purged = await Mediator.Send(cmd) });

    [HttpGet("review")]
    public Task<IReadOnlyList<Application.Features.Retention.ReviewItemDto>> Review() => Mediator.Send(new Application.Features.Retention.ListRetentionReviewQuery());

    [HttpPost("review/approve")]
    public async Task<IActionResult> Approve(Application.Features.Retention.ApproveRetentionDeleteCommand cmd) => Ok(new { deleted = await Mediator.Send(cmd) });
}

/// <summary>Giữ pháp lý (đặc tả 7.3): không gì trong phạm vi bị xóa vĩnh viễn, kể cả bởi quản trị, cho đến khi gỡ.</summary>
[Route("api/v1/admin/holds")]
public class LegalHoldsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<Application.Features.Retention.LegalHoldDto>> List([FromQuery] bool includeReleased = false) =>
        Mediator.Send(new Application.Features.Retention.ListLegalHoldsQuery(includeReleased));

    [HttpPost]
    public async Task<IActionResult> Create(Application.Features.Retention.CreateLegalHoldCommand cmd) => Ok(new { id = await Mediator.Send(cmd) });

    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, ReleaseHoldBody body)
    {
        await Mediator.Send(new Application.Features.Retention.ReleaseLegalHoldCommand(id, body.Reason));
        return NoContent();
    }

    [HttpGet("folders")]
    public Task<IReadOnlyList<Application.Features.Retention.HoldFolderDto>> Folders([FromQuery] Guid workspaceId) =>
        Mediator.Send(new Application.Features.Retention.ListHoldFoldersQuery(workspaceId));

    public sealed record ReleaseHoldBody(string Reason);
}

/// <summary>Người dùng nghỉ việc / chuyển đơn vị (đặc tả 8.3).</summary>
[Route("api/v1/admin/offboarding")]
public class OffboardingController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<Application.Features.Users.OffboardingReportDto>> List() => Mediator.Send(new Application.Features.Users.ListOffboardingsQuery());

    [HttpGet("{userId:guid}/preview")]
    public Task<Application.Features.Users.OffboardingPreviewDto> Preview(Guid userId) => Mediator.Send(new Application.Features.Users.GetOffboardingPreviewQuery(userId));

    [HttpPost("{userId:guid}")]
    public Task<Application.Features.Users.OffboardingReportDto> Run(Guid userId, Application.Features.Users.OffboardUserCommand cmd) => Mediator.Send(cmd with { UserId = userId });

    /// <summary>Biên bản bàn giao (Excel/PDF).</summary>
    [HttpGet("records/{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, [FromQuery] string format = "xlsx")
    {
        var file = await Mediator.Send(new Application.Features.Users.ExportOffboardingQuery(id, format));
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Chuyển đơn vị: kèm danh sách quyền thay đổi theo.</summary>
    [HttpPost("{userId:guid}/unit")]
    public Task<Application.Features.Users.UnitChangeReportDto> ChangeUnit(Guid userId, Application.Features.Users.ChangeUserUnitCommand cmd) => Mediator.Send(cmd with { UserId = userId });
}

/// <summary>Truy cập khẩn cấp của quản trị (đặc tả 8.5).</summary>
[Route("api/v1/admin/emergency-access")]
public class EmergencyAccessController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<Application.Features.Access.EmergencyAccessDto>> List() => Mediator.Send(new Application.Features.Access.ListEmergencyAccessQuery());

    [HttpPost]
    public async Task<IActionResult> Grant(Application.Features.Access.GrantEmergencyAccessCommand cmd) => Ok(new { id = await Mediator.Send(cmd) });

    [HttpPost("{id:guid}/end")]
    public async Task<IActionResult> End(Guid id)
    {
        await Mediator.Send(new Application.Features.Access.EndEmergencyAccessCommand(id));
        return NoContent();
    }
}

/// <summary>Báo cáo quyền truy cập (đặc tả 8.4): quản trị toàn hệ thống, Quản lý Space trong Space của mình.</summary>
[Route("api/v1/admin/reports")]
public class AccessReportsController : ApiControllerBase
{
    [HttpGet("items")]
    public Task<IReadOnlyList<Application.Features.Access.ReportItemDto>> Items([FromQuery] Guid? workspaceId, [FromQuery] string? query) =>
        Mediator.Send(new Application.Features.Access.SearchReportItemsQuery(workspaceId, query));

    [HttpGet("{kind}")]
    public Task<Application.Features.Access.AccessReportDto> Run(string kind, [FromQuery] Guid? workspaceId, [FromQuery] Guid? userId, [FromQuery] Guid? groupId,
        [FromQuery] Guid? itemId, [FromQuery] int days = 30) =>
        Mediator.Send(new Application.Features.Access.RunAccessReportQuery(kind, workspaceId, userId, groupId, itemId, days));

    [HttpGet("{kind}/export")]
    public async Task<IActionResult> Export(string kind, [FromQuery] Guid? workspaceId, [FromQuery] Guid? userId, [FromQuery] Guid? groupId, [FromQuery] Guid? itemId,
        [FromQuery] int days = 30, [FromQuery] string format = "xlsx")
    {
        var file = await Mediator.Send(new Application.Features.Access.ExportAccessReportQuery(
            new Application.Features.Access.RunAccessReportQuery(kind, workspaceId, userId, groupId, itemId, days), format));
        return File(file.Content, file.ContentType, file.FileName);
    }
}

/// <summary>Thao tác hàng loạt trên quyền (đặc tả 8.4).</summary>
[Route("api/v1/admin/bulk")]
public class BulkPermissionsController : ApiControllerBase
{
    [HttpPost("revoke-anyone-links")]
    public Task<Application.Features.Access.BulkResultDto> RevokeAnyoneLinks(Application.Features.Access.BulkRevokeAnyoneLinksCommand cmd) => Mediator.Send(cmd);

    [HttpPost("remove-user-permissions")]
    public Task<Application.Features.Access.BulkResultDto> RemoveUser(Application.Features.Access.BulkRemovePrincipalCommand cmd) => Mediator.Send(cmd);

    [HttpPost("delete-inactive-guests")]
    public Task<Application.Features.Access.BulkResultDto> DeleteGuests(Application.Features.Access.BulkDeleteInactiveGuestsCommand cmd) => Mediator.Send(cmd);
}
