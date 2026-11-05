using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

// Spec 7.1: sensitivity labels. NOT for state secrets: BlueDrive does not store information classified as state secret.

public sealed record LabelDto(Guid Id, string Name, string Color, string Description, int Priority, bool BlockExternal, bool BlockAnyoneLinks, bool ViewOnlyOutsideSpace,
    bool Watermark, bool BlockSync, bool IsActive);

/// <summary>The label that applies to an item: its own, the nearest folder's above it, or the Space's default.</summary>
public sealed record EffectiveLabel(SensitivityLabel Label, string Source);

public sealed class Labels(IAppDbContext db)
{
    private readonly Dictionary<Guid, EffectiveLabel?> _cache = [];
    private List<SensitivityLabel>? _all;

    private async Task<SensitivityLabel?> ByIdAsync(Guid? id, CancellationToken ct)
    {
        if (id is null) return null;
        _all ??= await db.SensitivityLabels.AsNoTracking().ToListAsync(ct);
        return _all.FirstOrDefault(l => l.Id == id && l.IsActive);
    }

    public async Task<EffectiveLabel?> ForAsync(Guid itemId, CancellationToken ct)
    {
        if (_cache.TryGetValue(itemId, out var known)) return known;
        var node = await db.Securables.AsNoTracking().Where(s => s.Id == itemId).Select(s => new { s.Kind, s.Path, Root = s.SpaceId ?? s.PersonalDriveId }).FirstOrDefaultAsync(ct);
        EffectiveLabel? result = null;
        if (node is not null)
        {
            if (node.Kind == SecurableKind.File && await ByIdAsync(await db.Files.Where(f => f.Id == itemId).Select(f => f.LabelId).FirstOrDefaultAsync(ct), ct) is { } own)
                result = new(own, "Tệp");
            if (result is null)
            {
                var ids = node.Path.Split('.').Select(p => Guid.TryParseExact(p, "N", out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
                var labelled = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id) && f.LabelId != null).Select(f => new { f.Id, f.Name, f.LabelId }).ToListAsync(ct);
                foreach (var id in Enumerable.Reverse(ids))
                    if (labelled.FirstOrDefault(f => f.Id == id) is { } f && await ByIdAsync(f.LabelId, ct) is { } l)
                    {
                        result = new(l, $"Thư mục \"{f.Name}\"");
                        break;
                    }
            }
            if (result is null && node.Root is { } root
                && await ByIdAsync(await db.Workspaces.Where(w => w.Id == root).Select(w => w.DefaultLabelId).FirstOrDefaultAsync(ct), ct) is { } d)
                result = new(d, "Mặc định của Space");
        }
        return _cache[itemId] = result;
    }

    public static LabelDto ToDto(SensitivityLabel l) => new(l.Id, l.Name, l.Color, l.Description, l.Priority, l.BlockExternal, l.BlockAnyoneLinks, l.ViewOnlyOutsideSpace,
        l.Watermark, l.BlockSync, l.IsActive);

    /// <summary>
    /// What a label takes from someone's rights (spec 7.1 "bắt buộc Chỉ xem với người ngoài Space"): who has no access as a member of the Space
    /// (only a direct grant, a link, general access) only views.
    /// </summary>
    public static AccessRight Apply(SensitivityLabel? label, bool inSpace, AccessRight membership, AccessRight rights) =>
        label is { ViewOnlyOutsideSpace: true } && inSpace && membership == AccessRight.None
            ? rights & (AccessRight.ViewItems | AccessRight.OpenItems)
            : rights;
}

public sealed record ListLabelsQuery : IRequest<IReadOnlyList<LabelDto>>;

public sealed record SaveLabelCommand(Guid? Id, string Name, string Color, string? Description, int Priority, bool BlockExternal, bool BlockAnyoneLinks,
    bool ViewOnlyOutsideSpace, bool Watermark, bool BlockSync, bool IsActive = true) : IRequest<Guid>;

public sealed class SaveLabelValidator : AbstractValidator<SaveLabelCommand>
{
    private static readonly string[] Forbidden = ["mật", "tối mật", "tuyệt mật"];

    public SaveLabelValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên nhãn.").MaximumLength(100)
            .Must(n => !Forbidden.Contains(n.Trim().ToLowerInvariant()))
            .WithMessage("BlueDrive không dùng để lưu thông tin bí mật nhà nước: không đặt nhãn \"Mật\", \"Tối mật\", \"Tuyệt mật\".");
        RuleFor(x => x.Color).Matches("^#[0-9a-fA-F]{6}$").WithMessage("Màu dạng #RRGGBB.");
        RuleFor(x => x.Priority).InclusiveBetween(0, 1000).WithMessage("Mức độ từ 0 đến 1000.");
    }
}

/// <summary>Labels a file, a folder (what is below follows) or a Space (its default). Lowering one needs a reason and who manages the item.</summary>
public sealed record SetLabelCommand(Guid ItemId, Guid? LabelId, string? Reason) : IRequest<Unit>;

public sealed record GetItemLabelQuery(Guid ItemId) : IRequest<ItemLabelDto>;

public sealed record ItemLabelDto(LabelDto? Label, string? Source, LabelDto? Own, bool CanChange);

public sealed class LabelHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, IPermissionService perms, Labels labels, IAuditLogger audit)
    : IRequestHandler<ListLabelsQuery, IReadOnlyList<LabelDto>>, IRequestHandler<SaveLabelCommand, Guid>, IRequestHandler<SetLabelCommand, Unit>,
      IRequestHandler<GetItemLabelQuery, ItemLabelDto>
{
    public async Task<IReadOnlyList<LabelDto>> Handle(ListLabelsQuery q, CancellationToken ct) =>
        (await db.SensitivityLabels.AsNoTracking().OrderBy(l => l.Priority).ThenBy(l => l.Name).ToListAsync(ct)).Select(Labels.ToDto).ToList();

    public async Task<Guid> Handle(SaveLabelCommand cmd, CancellationToken ct)
    {
        if (!await perms.HasAsync(me.RequireUserId(), Perm.SettingsEdit, ct)) throw new ForbiddenException();
        var label = cmd.Id is { } id ? await db.SensitivityLabels.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy nhãn.") : null;
        if (label is null) db.SensitivityLabels.Add(label = new SensitivityLabel());
        label.Name = cmd.Name.Trim();
        label.Color = cmd.Color;
        label.Description = cmd.Description?.Trim() ?? "";
        label.Priority = cmd.Priority;
        label.BlockExternal = cmd.BlockExternal;
        label.BlockAnyoneLinks = cmd.BlockAnyoneLinks || cmd.BlockExternal;
        label.ViewOnlyOutsideSpace = cmd.ViewOnlyOutsideSpace;
        label.Watermark = cmd.Watermark;
        label.BlockSync = cmd.BlockSync;
        label.IsActive = cmd.IsActive;
        await db.SaveChangesAsync(ct);
        // Rights may change where the label is used: new versions of the scopes there.
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceName: label.Name, Detail: new { label = cmd }), ct);
        return label.Id;
    }

    public async Task<ItemLabelDto> Handle(GetItemLabelQuery q, CancellationToken ct)
    {
        var rights = await engine.DemandAsync(me.RequireUserId(), q.ItemId, AccessRight.ViewItems, ct);
        var effective = await labels.ForAsync(q.ItemId, ct);
        var node = await db.Securables.AsNoTracking().FirstAsync(s => s.Id == q.ItemId, ct);
        Guid? own = node.Kind switch
        {
            SecurableKind.File => await db.Files.Where(f => f.Id == node.Id).Select(f => f.LabelId).FirstOrDefaultAsync(ct),
            SecurableKind.Folder => await db.Folders.Where(f => f.Id == node.Id).Select(f => f.LabelId).FirstOrDefaultAsync(ct),
            _ => await db.Workspaces.Where(w => w.Id == node.Id).Select(w => w.DefaultLabelId).FirstOrDefaultAsync(ct),
        };
        var ownLabel = own is { } o ? await db.SensitivityLabels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == o, ct) : null;
        return new ItemLabelDto(effective is null ? null : Labels.ToDto(effective.Label), effective?.Source, ownLabel is null ? null : Labels.ToDto(ownLabel),
            rights.Has(node.Kind is SecurableKind.Space or SecurableKind.Drive ? AccessRight.ManageSpace : AccessRight.EditItems));
    }

    public async Task<Unit> Handle(SetLabelCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var node = await db.Securables.AsNoTracking().FirstOrDefaultAsync(s => s.Id == cmd.ItemId && !s.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tư liệu.");
        var rights = await engine.DemandAsync(uid, node.Id, AccessRight.ViewItems, ct);
        var root = node.Kind is SecurableKind.Space or SecurableKind.Drive;
        if (!rights.Has(root ? AccessRight.ManageSpace : AccessRight.EditItems)) throw new ForbiddenException("Bạn không có quyền đổi nhãn của mục này.");
        var next = cmd.LabelId is { } lid ? await db.SensitivityLabels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid && l.IsActive, ct) ?? throw new NotFoundException("Không tìm thấy nhãn.") : null;
        var before = await labels.ForAsync(node.Id, ct);
        // Lowering (or removing) a label: a reason, and who manages the item's permissions (spec 7.1).
        if (before is not null && (next?.Priority ?? -1) < before.Label.Priority)
        {
            if (!rights.Has(AccessRight.ManagePermissions)) throw new ForbiddenException("Hạ nhãn cần quyền quản lý mục này.");
            if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new AppException("Vui lòng nhập lý do hạ nhãn.");
        }
        switch (node.Kind)
        {
            case SecurableKind.File:
                await db.Files.Where(f => f.Id == node.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.LabelId, cmd.LabelId).SetProperty(f => f.Revision, f => f.Revision + 1), ct);
                break;
            case SecurableKind.Folder:
                await db.Folders.Where(f => f.Id == node.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.LabelId, cmd.LabelId), ct);
                break;
            default:
                await db.Workspaces.Where(w => w.Id == node.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.DefaultLabelId, cmd.LabelId), ct);
                break;
        }
        // Rights under it may change ("Chỉ xem với người ngoài Space"): the scope and those below get new versions.
        var scopes = root
            ? await db.Securables.Where(s => s.SpaceId == node.Id || s.PersonalDriveId == node.Id).Select(s => s.AclScopeId).Distinct().ToListAsync(ct)
            : db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? await db.Database.SqlQuery<Guid>($"SELECT DISTINCT acl_scope_id AS \"Value\" FROM securables WHERE path <@ {node.Path}::ltree").ToListAsync(ct)
                : await db.Securables.Where(s => s.Id == node.Id || s.Path.StartsWith(node.Path + ".")).Select(s => s.AclScopeId).Distinct().ToListAsync(ct);
        foreach (var s in scopes) await db.AclScopes.Where(a => a.ScopeId == s).ExecuteUpdateAsync(u => u.SetProperty(a => a.AclVersion, a => a.AclVersion + 1), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.LabelChanged, GrantItemPermissionHandler.Kind(node.Kind), node.Id, node.Name, node.SpaceId ?? node.PersonalDriveId,
            new { from = before?.Label.Name, to = next?.Name, reason = cmd.Reason }), ct);
        return Unit.Value;
    }
}
