using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Roles;

public sealed record RoleDto(Guid Id, string Code, string Name, string? Description, bool IsSystem, int UserCount, int PermissionCount);

public sealed record ListRolesQuery(string? Q) : IRequest<IReadOnlyList<RoleDto>>;

public sealed class ListRolesHandler(IAppDbContext db) : IRequestHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public async Task<IReadOnlyList<RoleDto>> Handle(ListRolesQuery q, CancellationToken ct)
    {
        var query = db.Roles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(r => DbFn.Unaccent(r.Name).Contains(term) || r.Code.Contains(term));
        }
        return await query.OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name)
            .Select(r => new RoleDto(r.Id, r.Code, r.Name, r.Description, r.IsSystem, db.UserRoles.Count(u => u.RoleId == r.Id), r.Permissions.Count))
            .ToListAsync(ct);
    }
}

public sealed record SaveRoleCommand(Guid? Id, string Code, string Name, string? Description, Guid? CopyFromRoleId) : IRequest<Guid>;

public sealed class SaveRoleValidator : AbstractValidator<SaveRoleCommand>
{
    public SaveRoleValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Vui lòng nhập mã vai trò.").Matches("^[a-z0-9_]{2,50}$").WithMessage("Mã vai trò 2-50 ký tự: chữ thường không dấu, số, dấu gạch dưới.");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên vai trò.").MaximumLength(255);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public sealed class SaveRoleHandler(IAppDbContext db, IPermissionService perms, IAuditLogger audit) : IRequestHandler<SaveRoleCommand, Guid>
{
    public async Task<Guid> Handle(SaveRoleCommand cmd, CancellationToken ct)
    {
        var code = cmd.Code.Trim().ToLowerInvariant();
        if (await db.Roles.AnyAsync(r => r.Code == code && r.Id != cmd.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["code"] = [$"Mã vai trò '{code}' đã tồn tại."] });
        var isNew = cmd.Id is null;
        var role = isNew ? new Role() : await db.Roles.FirstOrDefaultAsync(r => r.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy vai trò.");
        if (role.IsSystem && role.Code != code) throw new AppException("Không được đổi mã của vai trò hệ thống.");
        role.Code = code;
        role.Name = cmd.Name.Trim();
        role.Description = cmd.Description?.Trim();
        if (isNew)
        {
            db.Roles.Add(role);
            if (cmd.CopyFromRoleId.HasValue)
                foreach (var p in await db.RolePermissions.Where(x => x.RoleId == cmd.CopyFromRoleId).Select(x => x.PermissionCode).ToListAsync(ct))
                    role.Permissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = p });
        }
        await db.SaveChangesAsync(ct);
        perms.Invalidate();
        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceName: $"Vai trò {role.Name}", Detail: new { entity = "role", id = role.Id, role.Code }), ct);
        return role.Id;
    }
}

public sealed record DeleteRoleCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteRoleHandler(IAppDbContext db, IPermissionService perms, IAuditLogger audit) : IRequestHandler<DeleteRoleCommand, Unit>
{
    public async Task<Unit> Handle(DeleteRoleCommand cmd, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy vai trò.");
        if (role.IsSystem) throw new AppException("Không được xóa vai trò hệ thống.");
        var users = await db.UserRoles.CountAsync(u => u.RoleId == role.Id, ct);
        if (users > 0) throw new ConflictException($"Không thể xóa vai trò \"{role.Name}\" vì đang được gán cho {users} người dùng.");
        await db.RolePermissions.Where(p => p.RoleId == role.Id).ExecuteDeleteAsync(ct);
        await db.ResourcePermissions.Where(p => p.PrincipalType == PrincipalType.Role && p.PrincipalId == role.Id).ExecuteDeleteAsync(ct);
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        perms.Invalidate();
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: $"Vai trò {role.Name}", Detail: new { entity = "role", id = role.Id }), ct);
        return Unit.Value;
    }
}

// ---------- #8 permission matrix of one role ----------
public sealed record MatrixFunction(string Function, string FunctionName, string Module, string ModuleName, IReadOnlyList<string> AvailableActions);

public sealed record ActionDto(string Code, string Name);

public sealed record RolePermissionMatrixDto(Guid RoleId, string RoleName, bool IsSystem, IReadOnlyList<ActionDto> Actions,
    IReadOnlyList<MatrixFunction> Functions, IReadOnlyList<string> Granted);

public sealed record GetRolePermissionsQuery(Guid RoleId) : IRequest<RolePermissionMatrixDto>;

public static class PermissionMatrix
{
    public static IReadOnlyList<MatrixFunction> Functions() => Perm.All
        .GroupBy(p => p.Function)
        .Select(g => new MatrixFunction(g.Key, g.First().FunctionName, g.First().Module, g.First().ModuleName, g.Select(p => p.Action).ToList()))
        .ToList();
}

public sealed class GetRolePermissionsHandler(IAppDbContext db) : IRequestHandler<GetRolePermissionsQuery, RolePermissionMatrixDto>
{
    public async Task<RolePermissionMatrixDto> Handle(GetRolePermissionsQuery q, CancellationToken ct)
    {
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == q.RoleId, ct) ?? throw new NotFoundException("Không tìm thấy vai trò.");
        return new RolePermissionMatrixDto(role.Id, role.Name, role.IsSystem, Perm.Actions.All.Select(a => new ActionDto(a.Code, a.Name)).ToList(), PermissionMatrix.Functions(),
            role.Permissions.Select(p => p.PermissionCode).OrderBy(c => c).ToList());
    }
}

public sealed record SetRolePermissionsCommand(Guid RoleId, IReadOnlyList<string> PermissionCodes) : IRequest<Unit>;

public sealed class SetRolePermissionsHandler(IAppDbContext db, IPermissionService perms, IAuditLogger audit) : IRequestHandler<SetRolePermissionsCommand, Unit>
{
    public async Task<Unit> Handle(SetRolePermissionsCommand cmd, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == cmd.RoleId, ct) ?? throw new NotFoundException("Không tìm thấy vai trò.");
        var valid = Perm.All.Select(p => p.Code).ToHashSet();
        var unknown = cmd.PermissionCodes.Where(c => !valid.Contains(c)).ToList();
        if (unknown.Count > 0) throw new AppException($"Quyền không hợp lệ: {string.Join(", ", unknown)}");
        var desired = cmd.PermissionCodes.ToHashSet();
        if (role.Code == Perm.Roles.Admin && !valid.SetEquals(desired))
            throw new AppException("Vai trò Quản trị hệ thống luôn có toàn bộ quyền và không thể thu hẹp.");
        var before = role.Permissions.Select(p => p.PermissionCode).ToHashSet();
        var added = desired.Except(before).OrderBy(x => x).ToList();
        var removed = before.Except(desired).OrderBy(x => x).ToList();
        role.Permissions.RemoveAll(p => removed.Contains(p.PermissionCode));
        foreach (var c in added) role.Permissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = c });
        await db.SaveChangesAsync(ct);
        perms.Invalidate();
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionChange, ResourceName: $"Vai trò {role.Name}",
            Detail: new { entity = "role", id = role.Id, added, removed }), ct);
        return Unit.Value;
    }
}

// ---------- #9 comparison table: all functions x all roles ----------
public sealed record ComparisonRow(string Code, string Module, string ModuleName, string Function, string FunctionName, string Action, string ActionName,
    IReadOnlyDictionary<Guid, bool> Roles);

public sealed record PermissionComparisonDto(IReadOnlyList<RoleDto> Roles, IReadOnlyList<ComparisonRow> Rows);

public sealed record GetPermissionComparisonQuery(string? Module, string? Q) : IRequest<PermissionComparisonDto>;

public sealed class GetPermissionComparisonHandler(IAppDbContext db, IMediator mediator) : IRequestHandler<GetPermissionComparisonQuery, PermissionComparisonDto>
{
    public async Task<PermissionComparisonDto> Handle(GetPermissionComparisonQuery q, CancellationToken ct)
    {
        var roles = await mediator.Send(new ListRolesQuery(null), ct);
        var grants = await db.RolePermissions.AsNoTracking().Select(rp => new { rp.RoleId, rp.PermissionCode }).ToListAsync(ct);
        var set = grants.Select(g => (g.RoleId, g.PermissionCode)).ToHashSet();
        var term = TextUtil.Unaccent(q.Q);
        var rows = Perm.All
            .Where(p => string.IsNullOrEmpty(q.Module) || p.Module == q.Module)
            .Where(p => term.Length == 0 || TextUtil.Unaccent(p.Name).Contains(term) || p.Code.Contains(term))
            .Select(p => new ComparisonRow(p.Code, p.Module, p.ModuleName, p.Function, p.FunctionName, p.Action,
                Perm.Actions.All.First(a => a.Code == p.Action).Name, roles.ToDictionary(r => r.Id, r => set.Contains((r.Id, p.Code)))))
            .ToList();
        return new PermissionComparisonDto(roles, rows);
    }
}

public sealed record ExportPermissionComparisonQuery(string? Module, string? Q, string Format, string ExportedBy) : IRequest<ExportedFile>;

public sealed class ExportPermissionComparisonHandler(IMediator mediator, ITableExporter exporter, IAuditLogger audit) : IRequestHandler<ExportPermissionComparisonQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(ExportPermissionComparisonQuery q, CancellationToken ct)
    {
        var data = await mediator.Send(new GetPermissionComparisonQuery(q.Module, q.Q), ct);
        var columns = new List<ExportColumn> { new("Phân hệ", 1.4f), new("Chức năng", 2), new("Hành động", 1.3f), new("Mã quyền", 1.6f) };
        columns.AddRange(data.Roles.Select(r => new ExportColumn(r.Name, 1)));
        var rows = data.Rows.Select(r =>
        {
            var cells = new List<string?> { r.ModuleName, r.FunctionName, r.ActionName, r.Code };
            cells.AddRange(data.Roles.Select(role => r.Roles[role.Id] ? "X" : ""));
            return (IReadOnlyList<string?>)cells;
        }).ToList();
        var file = await exporter.ExportAsync(new TableExport("Bảng đối chiếu chức năng và vai trò", columns, rows,
            string.IsNullOrEmpty(q.Q) && string.IsNullOrEmpty(q.Module) ? null : $"Phân hệ: {q.Module ?? "tất cả"}; từ khóa: {q.Q}", q.ExportedBy), q.Format, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: file.FileName, Detail: new { report = "permission_comparison" }), ct);
        return file;
    }
}

public sealed record ListPermissionsQuery : IRequest<IReadOnlyList<Perm.Definition>>;

public sealed class ListPermissionsHandler : IRequestHandler<ListPermissionsQuery, IReadOnlyList<Perm.Definition>>
{
    public Task<IReadOnlyList<Perm.Definition>> Handle(ListPermissionsQuery q, CancellationToken ct) => Task.FromResult(Perm.All);
}
