using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Libraries;

// Spec 1.4: views of a library (columns, sort, filter, group, flat; public or personal, a default one) and document templates.

public sealed record ViewFilter(string Column, string Op, JsonElement? Value);

public sealed record LibraryViewDto(Guid Id, Guid LibraryId, string Name, bool IsPublic, IReadOnlyList<string> Columns, string? SortBy, bool SortDescending,
    IReadOnlyList<ViewFilter> Filters, string? GroupBy, bool Flat, bool IsDefault, bool CanEdit);

public sealed record ListViewsQuery(Guid WorkspaceId, Guid LibraryId) : IRequest<IReadOnlyList<LibraryViewDto>>;

public sealed record SaveViewCommand(Guid WorkspaceId, Guid LibraryId, Guid? Id, string Name, bool IsPublic, IReadOnlyList<string> Columns, string? SortBy, bool SortDescending,
    IReadOnlyList<ViewFilter>? Filters, string? GroupBy, bool Flat, bool IsDefault) : IRequest<Guid>;

public sealed class SaveViewValidator : AbstractValidator<SaveViewCommand>
{
    private static readonly string[] Ops = ["eq", "ne", "contains", "gt", "lt", "empty", "notempty"];

    public SaveViewValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên dạng xem.").MaximumLength(255);
        RuleFor(x => x.Columns).NotEmpty().WithMessage("Dạng xem cần ít nhất một cột.");
        RuleForEach(x => x.Filters).Must(f => Ops.Contains(f.Op)).WithMessage("Điều kiện lọc không hợp lệ.");
    }
}

public sealed record DeleteViewCommand(Guid WorkspaceId, Guid LibraryId, Guid Id) : IRequest<Unit>;

/// <param name="FolderId">The folder shown (not for a flat view); null = the library's top.</param>
public sealed record QueryLibraryQuery(Guid WorkspaceId, Guid LibraryId, Guid? ViewId, Guid? FolderId) : PageQuery, IRequest<LibraryPageDto>;

public sealed record LibraryRowDto(DriveItemDto Item, IReadOnlyDictionary<string, JsonElement?> Values, string? Group);

public sealed record LibraryPageDto(IReadOnlyList<LibraryRowDto> Items, int Total, int Page, int PageSize, IReadOnlyList<LibraryColumnDto> Columns, LibraryViewDto View);

public sealed record LibraryTemplateDto(Guid Id, string Name, Guid FileId, string? Extension);

public sealed record ListTemplatesQuery(Guid WorkspaceId, Guid LibraryId) : IRequest<IReadOnlyList<LibraryTemplateDto>>;

public sealed record AddTemplateCommand(Guid WorkspaceId, Guid LibraryId, Guid FileId, string? Name) : IRequest<Guid>;

public sealed record DeleteTemplateCommand(Guid WorkspaceId, Guid LibraryId, Guid Id) : IRequest<Unit>;

/// <summary>"Mới" from a template: a copy of it in the folder, under the name given.</summary>
public sealed record CreateFromTemplateCommand(Guid WorkspaceId, Guid LibraryId, Guid TemplateId, Guid? FolderId, string Name) : IRequest<Guid>;

public sealed class LibraryViewHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, AccessFilter filter, IContentTicketService tickets, ISender mediator,
    Libraries libraries, TimeProvider clock)
    : IRequestHandler<ListViewsQuery, IReadOnlyList<LibraryViewDto>>, IRequestHandler<SaveViewCommand, Guid>, IRequestHandler<DeleteViewCommand, Unit>,
      IRequestHandler<QueryLibraryQuery, LibraryPageDto>, IRequestHandler<ListTemplatesQuery, IReadOnlyList<LibraryTemplateDto>>,
      IRequestHandler<AddTemplateCommand, Guid>, IRequestHandler<DeleteTemplateCommand, Unit>, IRequestHandler<CreateFromTemplateCommand, Guid>
{
    private static readonly string[] DefaultColumns = ["name", "updatedAt", "updatedBy", "size"];

    private async Task<(AccessRight Rights, Folder? Root)> LibraryAsync(Guid workspaceId, Guid libraryId, CancellationToken ct)
    {
        Folder? root = null;
        if (libraryId != PermissionProjection.LibraryOf(workspaceId))
            root = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == libraryId && f.WorkspaceId == workspaceId && f.IsLibrary && !f.IsDeleted, ct)
                   ?? throw new NotFoundException("Không tìm thấy thư viện.");
        var rights = await engine.DemandAsync(me.RequireUserId(), ColumnHandlers.NodeOf(workspaceId, libraryId), AccessRight.ViewItems, ct);
        return (rights, root);
    }

    private static LibraryViewDto Dto(LibraryView v, Guid uid, bool manages) => new(v.Id, v.LibraryId, v.Name, v.OwnerUserId is null, v.Columns, v.SortBy, v.SortDescending,
        JsonSerializer.Deserialize<List<ViewFilter>>(v.Filters, Json) ?? [], v.GroupBy, v.Flat, v.IsDefault, v.OwnerUserId == uid || (v.OwnerUserId is null && manages));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<LibraryViewDto>> Handle(ListViewsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var (rights, _) = await LibraryAsync(q.WorkspaceId, q.LibraryId, ct);
        var views = await db.LibraryViews.AsNoTracking().Where(v => v.LibraryId == q.LibraryId && (v.OwnerUserId == null || v.OwnerUserId == uid))
            .OrderByDescending(v => v.IsDefault).ThenBy(v => v.OwnerUserId != null).ThenBy(v => v.Name).ToListAsync(ct);
        return views.Select(v => Dto(v, uid, rights.Has(AccessRight.ManageLibrarySettings))).ToList();
    }

    public async Task<Guid> Handle(SaveViewCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var (rights, _) = await LibraryAsync(cmd.WorkspaceId, cmd.LibraryId, ct);
        var manages = rights.Has(AccessRight.ManageLibrarySettings);
        // Public views (and the default one) belong to who manages the library; anyone makes personal views.
        if ((cmd.IsPublic || cmd.IsDefault) && !manages) throw new ForbiddenException("Chỉ người quản lý thư viện tạo được dạng xem công khai.");
        var keys = await db.LibraryColumns.Where(c => c.LibraryId == cmd.LibraryId).Select(c => c.Key).ToListAsync(ct);
        string[] known = [.. ColumnValues.SystemColumns, .. keys];
        var unknown = cmd.Columns.Concat(cmd.Filters?.Select(f => f.Column) ?? []).Concat(new[] { cmd.SortBy, cmd.GroupBy }.Where(x => x is not null)!)
            .Where(c => !known.Contains(c)).Distinct().ToList();
        if (unknown.Count > 0) throw new AppException($"Cột không có trong thư viện: {string.Join(", ", unknown)}.");
        var view = cmd.Id is { } id ? await db.LibraryViews.FirstOrDefaultAsync(v => v.Id == id && v.LibraryId == cmd.LibraryId, ct) ?? throw new NotFoundException("Không tìm thấy dạng xem.") : null;
        if (view is not null && !(view.OwnerUserId == uid || (view.OwnerUserId is null && manages))) throw new ForbiddenException("Bạn không sửa được dạng xem này.");
        if (view is null) db.LibraryViews.Add(view = new LibraryView { LibraryId = cmd.LibraryId, WorkspaceId = cmd.WorkspaceId, CreatedAt = clock.GetUtcNow().UtcDateTime });
        view.Name = cmd.Name.Trim();
        view.OwnerUserId = cmd.IsPublic ? null : uid;
        view.Columns = cmd.Columns.Distinct().ToList();
        view.SortBy = cmd.SortBy;
        view.SortDescending = cmd.SortDescending;
        view.Filters = JsonSerializer.Serialize(cmd.Filters ?? [], Json);
        view.GroupBy = cmd.GroupBy;
        view.Flat = cmd.Flat;
        view.IsDefault = cmd.IsDefault && cmd.IsPublic;
        if (view.IsDefault)
            await db.LibraryViews.Where(v => v.LibraryId == cmd.LibraryId && v.Id != view.Id && v.IsDefault).ExecuteUpdateAsync(u => u.SetProperty(v => v.IsDefault, false), ct);
        await db.SaveChangesAsync(ct);
        return view.Id;
    }

    public async Task<Unit> Handle(DeleteViewCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var (rights, _) = await LibraryAsync(cmd.WorkspaceId, cmd.LibraryId, ct);
        var view = await db.LibraryViews.FirstOrDefaultAsync(v => v.Id == cmd.Id && v.LibraryId == cmd.LibraryId, ct) ?? throw new NotFoundException("Không tìm thấy dạng xem.");
        if (!(view.OwnerUserId == uid || (view.OwnerUserId is null && rights.Has(AccessRight.ManageLibrarySettings)))) throw new ForbiddenException("Bạn không xóa được dạng xem này.");
        db.LibraryViews.Remove(view);
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }

    public async Task<LibraryPageDto> Handle(QueryLibraryQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var (rights, root) = await LibraryAsync(q.WorkspaceId, q.LibraryId, ct);
        var stored = q.ViewId is { } vid
            ? await db.LibraryViews.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid && v.LibraryId == q.LibraryId && (v.OwnerUserId == null || v.OwnerUserId == uid), ct)
              ?? throw new NotFoundException("Không tìm thấy dạng xem.")
            : await db.LibraryViews.AsNoTracking().FirstOrDefaultAsync(v => v.LibraryId == q.LibraryId && v.IsDefault, ct);
        var view = stored is null
            ? new LibraryViewDto(Guid.Empty, q.LibraryId, "Tất cả tư liệu", true, DefaultColumns, "name", false, [], null, false, false, false)
            : Dto(stored, uid, rights.Has(AccessRight.ManageLibrarySettings));
        var columns = await db.LibraryColumns.AsNoTracking().Where(c => c.LibraryId == q.LibraryId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var byKey = columns.ToDictionary(c => c.Key);

        // What belongs to the library: under its top folder, or (default library) what is not in a library of its own.
        var libraryFolders = db.Folders.Where(t => t.WorkspaceId == q.WorkspaceId && t.IsLibrary && !t.IsDeleted);
        IQueryable<Folder> inLibrary = root is not null
            ? db.Folders.Where(f => f.Path.StartsWith(root.Path) && !f.IsDeleted)
            : db.Folders.Where(f => f.WorkspaceId == q.WorkspaceId && !f.IsDeleted && !libraryFolders.Any(t => f.Path.StartsWith(t.Path)));
        var folderIds = inLibrary.Select(f => f.Id);
        var viewer = await filter.ForAsync(uid, ct);
        var files = filter.Files(db.Files.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && !f.IsDeleted
                                                                     && (f.FolderId == null ? root == null : folderIds.Contains(f.FolderId.Value))), viewer);
        var current = q.FolderId ?? root?.Id;
        IQueryable<Folder> folders = db.Folders.Where(_ => false);
        if (!view.Flat)
        {
            files = files.Where(f => f.FolderId == current);
            folders = filter.Folders(db.Folders.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && f.ParentId == current && !f.IsDeleted && (root != null || !f.IsLibrary)), viewer);
        }
        foreach (var f in view.Filters) files = ApplyFilter(files, f, byKey);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            files = files.Where(f => DbFn.Unaccent(f.Name).Contains(term) || (f.Title != null && DbFn.Unaccent(f.Title).Contains(term)));
            folders = folders.Where(f => DbFn.Unaccent(f.Name).Contains(term));
        }
        // Grouping sorts by the group first; then the view's sort.
        files = Order(files, view.GroupBy, false, byKey, then: false);
        files = Order(files, view.SortBy ?? "name", view.SortDescending, byKey, then: view.GroupBy is not null);

        var folderCount = await folders.CountAsync(ct);
        var fileCount = await files.CountAsync(ct);
        var skip = (q.SafePage - 1) * q.SafePageSize;
        var items = new List<DriveItemDto>();
        if (skip < folderCount) items.AddRange(await DriveProjection.Folders(db, folders.OrderBy(f => f.Name).Skip(skip).Take(q.SafePageSize), uid).ToListAsync(ct));
        var remaining = q.SafePageSize - items.Count;
        if (remaining > 0) items.AddRange(await DriveProjection.Files(db, files.Skip(Math.Max(0, skip - folderCount)).Take(remaining), uid).ToListAsync(ct));
        // Order inside the page follows the query (DriveProjection keeps it for files; folders first).
        var ids = items.Where(i => i.Type == "file").Select(i => i.Id).ToList();
        var colIds = columns.Select(c => c.Id).ToList();
        var values = await db.FileColumnValues.AsNoTracking().Where(v => ids.Contains(v.FileId) && colIds.Contains(v.ColumnId)).ToListAsync(ct);
        var keyOf = columns.ToDictionary(c => c.Id, c => c.Key);
        var rows = ThumbnailUrls.Apply(items, tickets, uid).Select(i =>
        {
            var vals = values.Where(v => v.FileId == i.Id).ToDictionary(v => keyOf[v.ColumnId], v => ColumnValues.Parse(v.Value));
            string? group = view.GroupBy is null || i.Type != "file" ? null
                : byKey.ContainsKey(view.GroupBy) ? (vals.GetValueOrDefault(view.GroupBy) is { } gv ? Text(gv) : null) : SystemText(i, view.GroupBy);
            return new LibraryRowDto(i, vals, group);
        }).ToList();
        return new LibraryPageDto(rows, folderCount + fileCount, q.SafePage, q.SafePageSize, columns.Select(ColumnValues.ToDto).ToList(), view);
    }

    private static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ToString())),
        JsonValueKind.True => "Có",
        JsonValueKind.False => "Không",
        _ => v.ToString(),
    };

    private static string? SystemText(DriveItemDto i, string column) => column switch
    {
        "type" => i.Extension,
        "createdBy" => i.CreatedByName,
        "approval" => i.ApprovalStatus?.ToString(),
        _ => null,
    };

    private IQueryable<StoredFile> ApplyFilter(IQueryable<StoredFile> q, ViewFilter f, Dictionary<string, LibraryColumn> byKey)
    {
        var text = f.Value is { ValueKind: JsonValueKind.String } s ? s.GetString() : f.Value?.ToString();
        if (byKey.TryGetValue(f.Column, out var c))
        {
            var cid = c.Id;
            var vals = db.FileColumnValues.Where(v => v.ColumnId == cid);
            decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n);
            DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d);
            d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            var number = c.Type is ColumnType.Number or ColumnType.Boolean;
            var date = c.Type == ColumnType.DateTime;
            return f.Op switch
            {
                "empty" => q.Where(x => !vals.Any(v => v.FileId == x.Id)),
                "notempty" => q.Where(x => vals.Any(v => v.FileId == x.Id)),
                "contains" => q.Where(x => vals.Any(v => v.FileId == x.Id && v.TextValue != null && v.TextValue.ToLower().Contains((text ?? "").ToLower()))),
                "ne" => q.Where(x => !vals.Any(v => v.FileId == x.Id && v.TextValue == text)),
                "gt" when number => q.Where(x => vals.Any(v => v.FileId == x.Id && v.NumberValue > n)),
                "lt" when number => q.Where(x => vals.Any(v => v.FileId == x.Id && v.NumberValue < n)),
                "gt" when date => q.Where(x => vals.Any(v => v.FileId == x.Id && v.DateValue > d)),
                "lt" when date => q.Where(x => vals.Any(v => v.FileId == x.Id && v.DateValue < d)),
                "eq" when number => q.Where(x => vals.Any(v => v.FileId == x.Id && v.NumberValue == n)),
                _ => q.Where(x => vals.Any(v => v.FileId == x.Id && (v.TextValue == text || (c.Type == ColumnType.MultiChoice || c.Type == ColumnType.Tags) && v.TextValue!.Contains(text!)))),
            };
        }
        var bytes = long.TryParse(text, out var parsed) ? parsed : (long?)null;
        return (f.Column, f.Op) switch
        {
            ("name", "contains") => q.Where(x => x.Name.ToLower().Contains((text ?? "").ToLower())),
            ("name", "eq") => q.Where(x => x.Name == text),
            ("type", "eq") => q.Where(x => x.Extension == text),
            ("type", "ne") => q.Where(x => x.Extension != text),
            ("approval", "eq") => q.Where(x => x.ApprovalStatus.ToString() == text),
            ("checkedOutBy", "notempty") => q.Where(x => x.CheckedOutBy != null),
            ("checkedOutBy", "empty") => q.Where(x => x.CheckedOutBy == null),
            ("size", "gt") => q.Where(x => x.SizeBytes > (bytes ?? 0)),
            ("size", "lt") => q.Where(x => x.SizeBytes < (bytes ?? long.MaxValue)),
            _ => throw new AppException($"Không lọc được cột \"{f.Column}\" theo điều kiện \"{f.Op}\"."),
        };
    }

    private IQueryable<StoredFile> Order(IQueryable<StoredFile> q, string? column, bool desc, Dictionary<string, LibraryColumn> byKey, bool then)
    {
        if (column is null) return q;
        IOrderedQueryable<StoredFile> By<T>(System.Linq.Expressions.Expression<Func<StoredFile, T>> key) =>
            then && q is IOrderedQueryable<StoredFile> o ? (desc ? o.ThenByDescending(key) : o.ThenBy(key)) : (desc ? q.OrderByDescending(key) : q.OrderBy(key));
        if (byKey.TryGetValue(column, out var c))
        {
            var cid = c.Id;
            return c.Type is ColumnType.Number or ColumnType.Boolean ? By(x => db.FileColumnValues.Where(v => v.FileId == x.Id && v.ColumnId == cid).Select(v => v.NumberValue).FirstOrDefault())
                : c.Type == ColumnType.DateTime ? By(x => db.FileColumnValues.Where(v => v.FileId == x.Id && v.ColumnId == cid).Select(v => v.DateValue).FirstOrDefault())
                : By(x => db.FileColumnValues.Where(v => v.FileId == x.Id && v.ColumnId == cid).Select(v => v.TextValue).FirstOrDefault());
        }
        return column switch
        {
            "size" => By(x => x.SizeBytes),
            "createdAt" => By(x => x.CreatedAt),
            "updatedAt" => By(x => x.UpdatedAt ?? x.CreatedAt),
            "type" => By(x => x.Extension),
            "createdBy" => By(x => x.CreatedBy),
            "updatedBy" => By(x => x.UpdatedBy),
            "approval" => By(x => x.ApprovalStatus),
            "checkedOutBy" => By(x => x.CheckedOutBy),
            _ => By(x => x.Name),
        };
    }

    public async Task<IReadOnlyList<LibraryTemplateDto>> Handle(ListTemplatesQuery q, CancellationToken ct)
    {
        await LibraryAsync(q.WorkspaceId, q.LibraryId, ct);
        return await db.LibraryTemplates.AsNoTracking().Where(t => t.LibraryId == q.LibraryId).OrderBy(t => t.Name)
            .Select(t => new LibraryTemplateDto(t.Id, t.Name, t.FileId, db.Files.Where(f => f.Id == t.FileId).Select(f => f.Extension).FirstOrDefault())).ToListAsync(ct);
    }

    public async Task<Guid> Handle(AddTemplateCommand cmd, CancellationToken ct)
    {
        var (rights, _) = await LibraryAsync(cmd.WorkspaceId, cmd.LibraryId, ct);
        if (!rights.Has(AccessRight.ManageLibrarySettings)) throw new ForbiddenException("Bạn không có quyền thay đổi cài đặt thư viện.");
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == cmd.FileId && f.WorkspaceId == cmd.WorkspaceId && !f.IsDeleted, ct)
                   ?? throw new NotFoundException("Mẫu phải là một tệp trong Space.");
        var t = new LibraryTemplate
        {
            LibraryId = cmd.LibraryId, WorkspaceId = cmd.WorkspaceId, FileId = file.Id, Name = string.IsNullOrWhiteSpace(cmd.Name) ? Path.GetFileNameWithoutExtension(file.Name) : cmd.Name.Trim(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.LibraryTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        return t.Id;
    }

    public async Task<Unit> Handle(DeleteTemplateCommand cmd, CancellationToken ct)
    {
        var (rights, _) = await LibraryAsync(cmd.WorkspaceId, cmd.LibraryId, ct);
        if (!rights.Has(AccessRight.ManageLibrarySettings)) throw new ForbiddenException("Bạn không có quyền thay đổi cài đặt thư viện.");
        await db.LibraryTemplates.Where(t => t.Id == cmd.Id && t.LibraryId == cmd.LibraryId).ExecuteDeleteAsync(ct);
        return Unit.Value;
    }

    public async Task<Guid> Handle(CreateFromTemplateCommand cmd, CancellationToken ct)
    {
        await LibraryAsync(cmd.WorkspaceId, cmd.LibraryId, ct);
        var t = await db.LibraryTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == cmd.TemplateId && x.LibraryId == cmd.LibraryId, ct) ?? throw new NotFoundException("Không tìm thấy mẫu.");
        var source = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == t.FileId && !f.IsDeleted, ct) ?? throw new NotFoundException("Tệp mẫu không còn.");
        // The copy is checked like any copy (who may add here); the template itself is read by the library's rights.
        var before = await db.Files.Where(f => f.WorkspaceId == cmd.WorkspaceId && f.FolderId == cmd.FolderId).Select(f => f.Id).ToListAsync(ct);
        await mediator.Send(new CopyItemsCommand([new ItemRef("file", source.Id)], cmd.WorkspaceId, cmd.FolderId), ct);
        var copy = await db.Files.Where(f => f.WorkspaceId == cmd.WorkspaceId && f.FolderId == cmd.FolderId && !before.Contains(f.Id)).OrderByDescending(f => f.CreatedAt).FirstAsync(ct);
        var name = cmd.Name.Trim();
        if (name.Length > 0)
        {
            if (!name.EndsWith("." + source.Extension, StringComparison.OrdinalIgnoreCase)) name += "." + source.Extension;
            copy.Name = await StorageNames.UniqueFileNameAsync(db, cmd.WorkspaceId, cmd.FolderId, FileNameSanitizer.Sanitize(name), copy.Id, ct);
            await db.SaveChangesAsync(ct);
        }
        await libraries.ApplyDefaultsAsync(copy, ct);
        return copy.Id;
    }
}
