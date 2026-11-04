using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

// Spec 3.2 / 11.8 "Cấp quyền": administrators change what the levels allow (the matrix of rights × levels) and add levels of their own.

public sealed record AccessRightDto(string Name, string Group, string Description);

public sealed record ListAccessRightsQuery : IRequest<IReadOnlyList<AccessRightDto>>;

/// <param name="Rights">Names of <see cref="AccessRight"/> flags.</param>
public sealed record SavePermissionLevelCommand(Guid? Id, string Name, string? Description, IReadOnlyList<string> Rights) : IRequest<Guid>;

public sealed class SavePermissionLevelValidator : AbstractValidator<SavePermissionLevelCommand>
{
    public SavePermissionLevelValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên cấp quyền.").MaximumLength(255);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Rights).Must(r => r.Contains(nameof(AccessRight.ViewItems))).WithMessage("Cấp quyền nào cũng phải có quyền \"Xem\".");
    }
}

public sealed record DeletePermissionLevelCommand(Guid Id) : IRequest<Unit>;

/// <summary>A system level back to what BlueDrive gives it.</summary>
public sealed record ResetPermissionLevelCommand(Guid Id) : IRequest<Unit>;

public sealed class PermissionLevelAdminHandlers(IAppDbContext db, AclEditor editor, IAuditLogger audit)
    : IRequestHandler<ListAccessRightsQuery, IReadOnlyList<AccessRightDto>>, IRequestHandler<SavePermissionLevelCommand, Guid>,
      IRequestHandler<DeletePermissionLevelCommand, Unit>, IRequestHandler<ResetPermissionLevelCommand, Unit>
{
    public static string GroupOf(AccessRight r) => (long)r switch
    {
        < 1L << 16 => "Xem",
        < 1L << 32 => "Sửa",
        _ => "Chia sẻ / Quản lý",
    };

    public Task<IReadOnlyList<AccessRightDto>> Handle(ListAccessRightsQuery q, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AccessRightDto>>(AccessRights.Descriptions.OrderBy(d => (long)d.Key)
            .Select(d => new AccessRightDto(d.Key.ToString(), GroupOf(d.Key), d.Value)).ToList());

    private static AccessRight Parse(IReadOnlyList<string> names)
    {
        var rights = AccessRight.None;
        foreach (var n in names.Distinct())
            rights |= Enum.TryParse<AccessRight>(n, out var r) && AccessRights.Descriptions.ContainsKey(r) ? r : throw new AppException($"Quyền \"{n}\" không tồn tại.");
        return rights;
    }

    /// <summary>Every scope with an entry of the level gets a new version and its grants rewritten (rights changed for all who hold it).</summary>
    private async Task RefreshAsync(Guid levelId, CancellationToken ct)
    {
        var scopes = await db.AclEntries.Where(e => e.PermissionLevelId == levelId).Select(e => e.ScopeId).Distinct().ToListAsync(ct);
        foreach (var s in scopes) await editor.TouchAsync(s, ct);
    }

    public async Task<Guid> Handle(SavePermissionLevelCommand cmd, CancellationToken ct)
    {
        var rights = Parse(cmd.Rights);
        PermissionLevel level;
        if (cmd.Id is { } id)
        {
            level = await db.PermissionLevels.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy cấp quyền.");
            if (level.Id == SystemLevels.Owner) throw new AppException("Cấp \"Chủ sở hữu / Quản lý\" luôn có toàn quyền, không sửa được.");
        }
        else
        {
            var code = "custom_" + new string(TextUtil.Unaccent(cmd.Name).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
            if (code.Length > 60) code = code[..60];
            var baseCode = code;
            for (var i = 2; await db.PermissionLevels.AnyAsync(l => l.Code == code, ct); i++) code = $"{baseCode}_{i}";
            var last = await db.PermissionLevels.MaxAsync(l => (int?)l.SortOrder, ct) ?? 0;
            db.PermissionLevels.Add(level = new PermissionLevel { Code = code, SortOrder = last + 10 });
        }
        var before = level.Permissions;
        level.Name = cmd.Name.Trim();
        level.Description = cmd.Description?.Trim() ?? "";
        level.Permissions = rights;
        if (level.IsSystem) level.IsCustomized = true;
        await db.SaveChangesAsync(ct);
        if (cmd.Id is not null && before != rights) await RefreshAsync(level.Id, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionLevelChanged, ResourceName: level.Name,
            Detail: new { level = level.Code, before = before.ToString(), after = rights.ToString(), created = cmd.Id is null }), ct);
        return level.Id;
    }

    public async Task<Unit> Handle(DeletePermissionLevelCommand cmd, CancellationToken ct)
    {
        var level = await db.PermissionLevels.FirstOrDefaultAsync(l => l.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy cấp quyền.");
        if (level.IsSystem) throw new AppException("Không xóa được cấp quyền của hệ thống.");
        if (await db.AclEntries.AnyAsync(e => e.PermissionLevelId == level.Id, ct) || await db.SpaceGroups.AnyAsync(g => g.PermissionLevelId == level.Id, ct)
            || await db.ShareLinks.AnyAsync(l => l.LevelId == level.Id && !l.IsRevoked, ct) || await db.AccessRequests.AnyAsync(r => r.RequestedLevelId == level.Id, ct))
            throw new ConflictException("Cấp quyền đang được dùng; hãy đổi các quyền đã cấp sang cấp khác trước.");
        db.PermissionLevels.Remove(level);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionLevelChanged, ResourceName: level.Name, Detail: new { level = level.Code, deleted = true }), ct);
        return Unit.Value;
    }

    public async Task<Unit> Handle(ResetPermissionLevelCommand cmd, CancellationToken ct)
    {
        var level = await db.PermissionLevels.FirstOrDefaultAsync(l => l.Id == cmd.Id && l.IsSystem, ct) ?? throw new NotFoundException("Không tìm thấy cấp quyền hệ thống.");
        var def = SystemLevels.All.First(l => l.Id == level.Id);
        var changed = level.Permissions != def.Permissions;
        level.Name = def.Name;
        level.Description = def.Description;
        level.Permissions = def.Permissions;
        level.IsCustomized = false;
        await db.SaveChangesAsync(ct);
        if (changed) await RefreshAsync(level.Id, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionLevelChanged, ResourceName: level.Name, Detail: new { level = level.Code, reset = true }), ct);
        return Unit.Value;
    }
}
