using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Reporting;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Organizations;

public sealed record OrganizationDto(Guid Id, Guid? ParentId, string? ParentName, string Code, string Name, OrganizationType Type, string? Address,
    string? Phone, string? Email, int SortOrder, bool IsActive, int UserCount, int ChildCount, DateTime CreatedAt);

public sealed record OrganizationTreeNode(Guid Id, Guid? ParentId, string Code, string Name, OrganizationType Type, bool IsActive, int SortOrder,
    int UserCount, List<OrganizationTreeNode> Children);

public static class OrgNames
{
    public static string Type(OrganizationType t) => t == OrganizationType.DonVi ? "Đơn vị" : "Phòng ban";
}

// ---------- Queries ----------
public sealed record ListOrganizationsQuery(Guid? ParentId, bool? IsActive, OrganizationType? Type) : PageQuery, IRequest<PagedResult<OrganizationDto>>;

public sealed class ListOrganizationsHandler(IAppDbContext db) : IRequestHandler<ListOrganizationsQuery, PagedResult<OrganizationDto>>
{
    public Task<PagedResult<OrganizationDto>> Handle(ListOrganizationsQuery q, CancellationToken ct)
        => OrganizationQueries.Filter(db, q).ToPagedAsync(q, ct);
}

public static class OrganizationQueries
{
    public static IQueryable<OrganizationDto> Filter(IAppDbContext db, ListOrganizationsQuery q)
    {
        var query = db.Organizations.AsNoTracking();
        if (q.ParentId.HasValue) query = query.Where(o => o.ParentId == q.ParentId);
        if (q.IsActive.HasValue) query = query.Where(o => o.IsActive == q.IsActive);
        if (q.Type.HasValue) query = query.Where(o => o.Type == q.Type);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(o => DbFn.Unaccent(o.Name).Contains(term) || DbFn.Unaccent(o.Code).Contains(term));
        }
        query = q.Sort switch
        {
            "name" => query.OrderBy(o => o.Name),
            "-name" => query.OrderByDescending(o => o.Name),
            "code" => query.OrderBy(o => o.Code),
            "-code" => query.OrderByDescending(o => o.Code),
            "-createdAt" => query.OrderByDescending(o => o.CreatedAt),
            _ => query.OrderBy(o => o.Path.Length).ThenBy(o => o.SortOrder).ThenBy(o => o.Name),
        };
        return query.Select(o => new OrganizationDto(o.Id, o.ParentId, o.Parent != null ? o.Parent.Name : null, o.Code, o.Name, o.Type,
            o.Address, o.Phone, o.Email, o.SortOrder, o.IsActive,
            db.Users.Count(u => u.OrganizationId == o.Id), o.Children.Count, o.CreatedAt));
    }
}

public sealed record GetOrganizationTreeQuery(bool IncludeInactive = true) : IRequest<IReadOnlyList<OrganizationTreeNode>>;

public sealed class GetOrganizationTreeHandler(IAppDbContext db) : IRequestHandler<GetOrganizationTreeQuery, IReadOnlyList<OrganizationTreeNode>>
{
    public async Task<IReadOnlyList<OrganizationTreeNode>> Handle(GetOrganizationTreeQuery q, CancellationToken ct)
    {
        var rows = await db.Organizations.AsNoTracking().Where(o => q.IncludeInactive || o.IsActive)
            .Select(o => new { o.Id, o.ParentId, o.Code, o.Name, o.Type, o.IsActive, o.SortOrder, Users = db.Users.Count(u => u.OrganizationId == o.Id) })
            .ToListAsync(ct);
        var nodes = rows.ToDictionary(r => r.Id, r => new OrganizationTreeNode(r.Id, r.ParentId, r.Code, r.Name, r.Type, r.IsActive, r.SortOrder, r.Users, []));
        var roots = new List<OrganizationTreeNode>();
        foreach (var n in nodes.Values)
            if (n.ParentId is { } p && nodes.TryGetValue(p, out var parent)) parent.Children.Add(n); else roots.Add(n);
        void Sort(List<OrganizationTreeNode> list)
        {
            list.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            foreach (var c in list) Sort(c.Children);
        }
        Sort(roots);
        return roots;
    }
}

public sealed record GetOrganizationQuery(Guid Id) : IRequest<OrganizationDto>;

public sealed class GetOrganizationHandler(IAppDbContext db) : IRequestHandler<GetOrganizationQuery, OrganizationDto>
{
    public async Task<OrganizationDto> Handle(GetOrganizationQuery q, CancellationToken ct)
        => await OrganizationQueries.Filter(db, new ListOrganizationsQuery(null, null, null)).FirstOrDefaultAsync(o => o.Id == q.Id, ct)
           ?? throw new NotFoundException("Không tìm thấy đơn vị/phòng ban.");
}

// ---------- Export (#3) ----------
public sealed record ExportOrganizationsQuery(ListOrganizationsQuery Filter, string Format, string ExportedBy) : IRequest<ExportedFile>;

public sealed class ExportOrganizationsHandler(IAppDbContext db, ITableExporter exporter, IAuditLogger audit) : IRequestHandler<ExportOrganizationsQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(ExportOrganizationsQuery q, CancellationToken ct)
    {
        var rows = await OrganizationQueries.Filter(db, q.Filter).Take(50_000).ToListAsync(ct);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.Filter.Q)) filters.Add($"Từ khóa \"{q.Filter.Q}\"");
        if (q.Filter.IsActive.HasValue) filters.Add(q.Filter.IsActive.Value ? "Đang hoạt động" : "Ngừng hoạt động");
        if (q.Filter.Type.HasValue) filters.Add(OrgNames.Type(q.Filter.Type.Value));
        if (q.Filter.ParentId.HasValue) filters.Add($"Thuộc: {rows.FirstOrDefault()?.ParentName ?? q.Filter.ParentId.ToString()}");
        var file = await exporter.ExportAsync(new TableExport("Danh sách đơn vị, phòng ban",
            [new("Mã", 1), new("Tên đơn vị/phòng ban", 3), new("Loại", 1), new("Trực thuộc", 2.5f), new("Điện thoại", 1.2f), new("Email", 2),
             new("Địa chỉ", 2.5f), new("Số người dùng", 1), new("Trạng thái", 1.2f)],
            rows.Select(r => (IReadOnlyList<string?>)[r.Code, r.Name, OrgNames.Type(r.Type), r.ParentName, r.Phone, r.Email, r.Address,
                r.UserCount.ToString(), r.IsActive ? "Hoạt động" : "Ngừng hoạt động"]).ToList(),
            filters.Count > 0 ? string.Join("; ", filters) : null, q.ExportedBy), q.Format, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: file.FileName, Detail: new { report = "organizations", rows = rows.Count }), ct);
        return file;
    }
}

// ---------- Commands (#1) ----------
public sealed record SaveOrganizationCommand(Guid? Id, Guid? ParentId, string Code, string Name, OrganizationType Type, string? Address,
    string? Phone, string? Email, int SortOrder, bool IsActive = true) : IRequest<Guid>;

public sealed class SaveOrganizationValidator : AbstractValidator<SaveOrganizationCommand>
{
    public SaveOrganizationValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Vui lòng nhập mã đơn vị.").MaximumLength(50).WithMessage("Mã đơn vị tối đa 50 ký tự.")
            .Matches("^[A-Za-z0-9_.-]+$").WithMessage("Mã đơn vị chỉ gồm chữ không dấu, số và các ký tự . _ -");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên đơn vị.").MaximumLength(255).WithMessage("Tên đơn vị tối đa 255 ký tự.");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("Email không đúng định dạng.");
        RuleFor(x => x.Phone).Matches(@"^[0-9+()\s.-]{6,20}$").When(x => !string.IsNullOrWhiteSpace(x.Phone)).WithMessage("Số điện thoại không hợp lệ.");
        RuleFor(x => x).Must(x => x.Id is null || x.ParentId != x.Id).WithMessage("Đơn vị không thể trực thuộc chính nó.");
    }
}

public sealed class SaveOrganizationHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<SaveOrganizationCommand, Guid>
{
    public async Task<Guid> Handle(SaveOrganizationCommand cmd, CancellationToken ct)
    {
        var code = cmd.Code.Trim().ToUpperInvariant();
        if (await db.Organizations.AnyAsync(o => o.Code == code && o.Id != cmd.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["code"] = [$"Mã đơn vị '{code}' đã tồn tại."] });

        Organization? parent = null;
        if (cmd.ParentId.HasValue)
            parent = await db.Organizations.FirstOrDefaultAsync(o => o.Id == cmd.ParentId, ct) ?? throw new NotFoundException("Không tìm thấy đơn vị cấp trên.");

        var isNew = cmd.Id is null;
        var org = isNew ? new Organization() : await db.Organizations.FirstOrDefaultAsync(o => o.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy đơn vị/phòng ban.");
        if (!isNew && parent is not null && parent.Path.Contains($"/{org.Id}/"))
            throw new AppException("Không thể chuyển đơn vị vào đơn vị con của chính nó.");

        var before = isNew ? null : new { org.Code, org.Name, org.ParentId, org.IsActive };
        org.Code = code;
        org.Name = cmd.Name.Trim();
        org.Type = cmd.Type;
        org.Address = cmd.Address?.Trim();
        org.Phone = cmd.Phone?.Trim();
        org.Email = cmd.Email?.Trim();
        org.SortOrder = cmd.SortOrder;
        org.IsActive = cmd.IsActive;
        var oldPath = org.Path;
        org.ParentId = cmd.ParentId;
        org.Path = $"{parent?.Path ?? "/"}{org.Id}/";
        if (isNew) db.Organizations.Add(org);
        await db.SaveChangesAsync(ct);
        if (!isNew && oldPath != org.Path) await OrganizationTree.RebasePathsAsync(db, oldPath, org.Path, ct);

        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceName: $"Đơn vị {org.Name}",
            Detail: new { entity = "organization", id = org.Id, before, after = new { org.Code, org.Name, org.ParentId, org.IsActive } }), ct);
        return org.Id;
    }
}

public static class OrganizationTree
{
    public static Task RebasePathsAsync(IAppDbContext db, string oldPath, string newPath, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE organizations SET path = {newPath} || substr(path, {oldPath.Length + 1}) WHERE path LIKE {oldPath + "%"} AND path <> {newPath}", ct);
}

/// <summary>Drag-and-drop in the tree: change parent and/or order.</summary>
public sealed record MoveOrganizationCommand(Guid Id, Guid? NewParentId, int NewIndex) : IRequest<Unit>;

public sealed class MoveOrganizationHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<MoveOrganizationCommand, Unit>
{
    public async Task<Unit> Handle(MoveOrganizationCommand cmd, CancellationToken ct)
    {
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy đơn vị/phòng ban.");
        Organization? parent = null;
        if (cmd.NewParentId.HasValue)
        {
            parent = await db.Organizations.FirstOrDefaultAsync(o => o.Id == cmd.NewParentId, ct) ?? throw new NotFoundException("Không tìm thấy đơn vị cấp trên.");
            if (parent.Id == org.Id || parent.Path.Contains($"/{org.Id}/")) throw new AppException("Không thể chuyển đơn vị vào đơn vị con của chính nó.");
        }
        var oldParent = org.ParentId;
        var oldPath = org.Path;
        org.ParentId = cmd.NewParentId;
        org.Path = $"{parent?.Path ?? "/"}{org.Id}/";

        var siblings = await db.Organizations.Where(o => o.ParentId == cmd.NewParentId && o.Id != org.Id).OrderBy(o => o.SortOrder).ThenBy(o => o.Name).ToListAsync(ct);
        siblings.Insert(Math.Clamp(cmd.NewIndex, 0, siblings.Count), org);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i + 1;
        await db.SaveChangesAsync(ct);
        if (oldPath != org.Path) await OrganizationTree.RebasePathsAsync(db, oldPath, org.Path, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Move, ResourceName: $"Đơn vị {org.Name}",
            Detail: new { entity = "organization", id = org.Id, fromParent = oldParent, toParent = org.ParentId, index = cmd.NewIndex }), ct);
        return Unit.Value;
    }
}

/// <summary>Hard delete is refused while users or child units exist; callers may deactivate instead.</summary>
public sealed record DeleteOrganizationCommand(Guid Id, bool DeactivateIfInUse) : IRequest<string>;

public sealed class DeleteOrganizationHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<DeleteOrganizationCommand, string>
{
    public async Task<string> Handle(DeleteOrganizationCommand cmd, CancellationToken ct)
    {
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy đơn vị/phòng ban.");
        var users = await db.Users.CountAsync(u => u.OrganizationId == org.Id, ct);
        var children = await db.Organizations.CountAsync(o => o.ParentId == org.Id, ct);
        var workspaces = await db.Workspaces.CountAsync(w => w.OrganizationId == org.Id && w.Type != WorkspaceType.Personal, ct);
        if (users > 0 || children > 0 || workspaces > 0)
        {
            if (!cmd.DeactivateIfInUse)
                throw new ConflictException($"Không thể xóa \"{org.Name}\" vì còn {users} người dùng, {children} đơn vị con và {workspaces} nhóm tài nguyên trực thuộc. Bạn có thể chuyển sang trạng thái \"Ngừng hoạt động\".");
            org.IsActive = false;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceName: $"Đơn vị {org.Name}", Detail: new { entity = "organization", id = org.Id, action = "deactivate" }), ct);
            return "deactivated";
        }
        db.Organizations.Remove(org);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: $"Đơn vị {org.Name}", Detail: new { entity = "organization", id = org.Id, org.Code }), ct);
        return "deleted";
    }
}
