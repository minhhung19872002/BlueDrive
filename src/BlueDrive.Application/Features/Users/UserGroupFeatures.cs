using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Users;

public sealed record UserGroupDto(Guid Id, string Name, string? Description, int MemberCount, DateTime CreatedAt);

public sealed record UserGroupDetailDto(Guid Id, string Name, string? Description, IReadOnlyList<UserLookupDto> Members);

public sealed record ListUserGroupsQuery : PageQuery, IRequest<PagedResult<UserGroupDto>>;

public sealed class ListUserGroupsHandler(IAppDbContext db) : IRequestHandler<ListUserGroupsQuery, PagedResult<UserGroupDto>>
{
    public Task<PagedResult<UserGroupDto>> Handle(ListUserGroupsQuery q, CancellationToken ct)
    {
        var query = db.UserGroups.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(g => DbFn.Unaccent(g.Name).Contains(term) || (g.Description != null && DbFn.Unaccent(g.Description).Contains(term)));
        }
        return query.OrderBy(g => g.Name).Select(g => new UserGroupDto(g.Id, g.Name, g.Description, g.Members.Count, g.CreatedAt)).ToPagedAsync(q, ct);
    }
}

public sealed record GetUserGroupQuery(Guid Id) : IRequest<UserGroupDetailDto>;

public sealed class GetUserGroupHandler(IAppDbContext db) : IRequestHandler<GetUserGroupQuery, UserGroupDetailDto>
{
    public async Task<UserGroupDetailDto> Handle(GetUserGroupQuery q, CancellationToken ct)
    {
        var g = await db.UserGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct) ?? throw new NotFoundException("Không tìm thấy nhóm người dùng.");
        var members = await db.UserGroupMembers.Where(m => m.GroupId == q.Id).OrderBy(m => m.User!.FullName)
            .Select(m => new UserLookupDto(m.UserId, m.User!.Username, m.User.FullName, m.User.Organization != null ? m.User.Organization.Name : null, m.User.AvatarFileId))
            .ToListAsync(ct);
        return new UserGroupDetailDto(g.Id, g.Name, g.Description, members);
    }
}

public sealed record SaveUserGroupCommand(Guid? Id, string Name, string? Description, IReadOnlyList<Guid>? MemberIds) : IRequest<Guid>;

public sealed class SaveUserGroupValidator : AbstractValidator<SaveUserGroupCommand>
{
    public SaveUserGroupValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên nhóm.").MaximumLength(255).WithMessage("Tên nhóm tối đa 255 ký tự.");
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class SaveUserGroupHandler(IAppDbContext db, IPermissionService perms, IAuditLogger audit) : IRequestHandler<SaveUserGroupCommand, Guid>
{
    public async Task<Guid> Handle(SaveUserGroupCommand cmd, CancellationToken ct)
    {
        var name = cmd.Name.Trim();
        if (await db.UserGroups.AnyAsync(g => g.Name == name && g.Id != cmd.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["name"] = [$"Nhóm '{name}' đã tồn tại."] });
        var isNew = cmd.Id is null;
        var g = isNew ? new UserGroup() : await db.UserGroups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy nhóm người dùng.");
        g.Name = name;
        g.Description = cmd.Description?.Trim();
        if (isNew) db.UserGroups.Add(g);
        if (cmd.MemberIds is not null)
        {
            var valid = await db.Users.Where(u => cmd.MemberIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
            g.Members.RemoveAll(m => !valid.Contains(m.UserId));
            foreach (var id in valid.Where(id => g.Members.All(m => m.UserId != id)))
                g.Members.Add(new UserGroupMember { GroupId = g.Id, UserId = id, AddedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync(ct);
        perms.Invalidate();
        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceName: $"Nhóm người dùng {g.Name}",
            Detail: new { entity = "user_group", id = g.Id, members = cmd.MemberIds }), ct);
        return g.Id;
    }
}

public sealed record DeleteUserGroupCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteUserGroupHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<DeleteUserGroupCommand, Unit>
{
    public async Task<Unit> Handle(DeleteUserGroupCommand cmd, CancellationToken ct)
    {
        var g = await db.UserGroups.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy nhóm người dùng.");
        // Group-based grants and shares disappear together with the group.
        await db.ResourcePermissions.Where(p => p.PrincipalType == PrincipalType.Group && p.PrincipalId == g.Id).ExecuteDeleteAsync(ct);
        await db.Shares.Where(s => s.SharedWithType == PrincipalType.Group && s.SharedWithId == g.Id).ExecuteDeleteAsync(ct);
        await db.UserGroupMembers.Where(m => m.GroupId == g.Id).ExecuteDeleteAsync(ct);
        db.UserGroups.Remove(g);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: $"Nhóm người dùng {g.Name}", Detail: new { entity = "user_group", id = g.Id }), ct);
        return Unit.Value;
    }
}
