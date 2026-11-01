using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Reporting;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Catalogs;

public sealed record CatalogDto(Guid Id, string Code, string Name, string? Description, bool IsSystem, int SortOrder, int ItemCount);

public sealed record CatalogItemDto(Guid Id, Guid CatalogId, Guid? ParentId, string Code, string Name, int SortOrder, bool IsActive, int FileCount);

public sealed record CatalogWithItemsDto(Guid Id, string Code, string Name, IReadOnlyList<CatalogItemDto> Items);

public sealed record ListCatalogsQuery(string? Q) : IRequest<IReadOnlyList<CatalogDto>>;

public sealed class ListCatalogsHandler(IAppDbContext db) : IRequestHandler<ListCatalogsQuery, IReadOnlyList<CatalogDto>>
{
    public async Task<IReadOnlyList<CatalogDto>> Handle(ListCatalogsQuery q, CancellationToken ct)
    {
        var query = db.Catalogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(c => DbFn.Unaccent(c.Name).Contains(term) || c.Code.Contains(term));
        }
        return await query.OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CatalogDto(c.Id, c.Code, c.Name, c.Description, c.IsSystem, c.SortOrder, c.Items.Count)).ToListAsync(ct);
    }
}

/// <summary>All active catalogs with their items: used for metadata pickers and search filters.</summary>
public sealed record GetCatalogOptionsQuery(bool ActiveOnly = true) : IRequest<IReadOnlyList<CatalogWithItemsDto>>;

public sealed class GetCatalogOptionsHandler(IAppDbContext db) : IRequestHandler<GetCatalogOptionsQuery, IReadOnlyList<CatalogWithItemsDto>>
{
    public async Task<IReadOnlyList<CatalogWithItemsDto>> Handle(GetCatalogOptionsQuery q, CancellationToken ct)
    {
        var cats = await db.Catalogs.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync(ct);
        var items = await db.CatalogItems.AsNoTracking().Where(i => !q.ActiveOnly || i.IsActive).OrderBy(i => i.SortOrder).ThenBy(i => i.Name)
            .Select(i => new CatalogItemDto(i.Id, i.CatalogId, i.ParentId, i.Code, i.Name, i.SortOrder, i.IsActive, 0)).ToListAsync(ct);
        return cats.Select(c => new CatalogWithItemsDto(c.Id, c.Code, c.Name, items.Where(i => i.CatalogId == c.Id).ToList())).ToList();
    }
}

public sealed record ListCatalogItemsQuery(Guid CatalogId, string? Q, bool? IsActive) : IRequest<IReadOnlyList<CatalogItemDto>>;

public sealed class ListCatalogItemsHandler(IAppDbContext db) : IRequestHandler<ListCatalogItemsQuery, IReadOnlyList<CatalogItemDto>>
{
    public async Task<IReadOnlyList<CatalogItemDto>> Handle(ListCatalogItemsQuery q, CancellationToken ct)
    {
        var query = db.CatalogItems.AsNoTracking().Where(i => i.CatalogId == q.CatalogId);
        if (q.IsActive.HasValue) query = query.Where(i => i.IsActive == q.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            query = query.Where(i => DbFn.Unaccent(i.Name).Contains(term) || i.Code.Contains(term));
        }
        return await query.OrderBy(i => i.SortOrder).ThenBy(i => i.Name)
            .Select(i => new CatalogItemDto(i.Id, i.CatalogId, i.ParentId, i.Code, i.Name, i.SortOrder, i.IsActive, db.FileCatalogItems.Count(f => f.CatalogItemId == i.Id)))
            .ToListAsync(ct);
    }
}

public sealed record SaveCatalogCommand(Guid? Id, string Code, string Name, string? Description, int SortOrder) : IRequest<Guid>;

public sealed class SaveCatalogValidator : AbstractValidator<SaveCatalogCommand>
{
    public SaveCatalogValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Vui lòng nhập mã loại danh mục.").Matches("^[a-z0-9_]{2,50}$").WithMessage("Mã 2-50 ký tự: chữ thường không dấu, số, gạch dưới.");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên loại danh mục.").MaximumLength(255);
    }
}

public sealed class SaveCatalogHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<SaveCatalogCommand, Guid>
{
    public async Task<Guid> Handle(SaveCatalogCommand cmd, CancellationToken ct)
    {
        var code = cmd.Code.Trim().ToLowerInvariant();
        if (await db.Catalogs.AnyAsync(c => c.Code == code && c.Id != cmd.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["code"] = [$"Mã loại danh mục '{code}' đã tồn tại."] });
        var isNew = cmd.Id is null;
        var cat = isNew ? new Catalog() : await db.Catalogs.FirstOrDefaultAsync(c => c.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy loại danh mục.");
        if (cat.IsSystem && cat.Code != code) throw new AppException("Không được đổi mã của danh mục hệ thống.");
        cat.Code = code;
        cat.Name = cmd.Name.Trim();
        cat.Description = cmd.Description?.Trim();
        cat.SortOrder = cmd.SortOrder;
        if (isNew) db.Catalogs.Add(cat);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceName: $"Loại danh mục {cat.Name}", Detail: new { entity = "catalog", id = cat.Id }), ct);
        return cat.Id;
    }
}

public sealed record DeleteCatalogCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteCatalogHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<DeleteCatalogCommand, Unit>
{
    public async Task<Unit> Handle(DeleteCatalogCommand cmd, CancellationToken ct)
    {
        var cat = await db.Catalogs.FirstOrDefaultAsync(c => c.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy loại danh mục.");
        if (cat.IsSystem) throw new AppException("Không được xóa danh mục hệ thống.");
        var used = await db.FileCatalogItems.CountAsync(f => f.CatalogItem!.CatalogId == cat.Id, ct);
        if (used > 0) throw new ConflictException($"Không thể xóa vì {used} tư liệu đang gắn mục thuộc danh mục này. Hãy ngừng sử dụng các mục thay vì xóa.");
        db.Catalogs.Remove(cat);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: $"Loại danh mục {cat.Name}", Detail: new { entity = "catalog", id = cat.Id }), ct);
        return Unit.Value;
    }
}

public sealed record SaveCatalogItemCommand(Guid? Id, Guid CatalogId, Guid? ParentId, string Code, string Name, int SortOrder, bool IsActive) : IRequest<Guid>;

public sealed class SaveCatalogItemValidator : AbstractValidator<SaveCatalogItemCommand>
{
    public SaveCatalogItemValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Vui lòng nhập mã mục danh mục.").MaximumLength(100);
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên mục danh mục.").MaximumLength(255);
        RuleFor(x => x).Must(x => x.Id is null || x.ParentId != x.Id).WithMessage("Mục danh mục không thể là cha của chính nó.");
    }
}

public sealed class SaveCatalogItemHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<SaveCatalogItemCommand, Guid>
{
    public async Task<Guid> Handle(SaveCatalogItemCommand cmd, CancellationToken ct)
    {
        if (!await db.Catalogs.AnyAsync(c => c.Id == cmd.CatalogId, ct)) throw new NotFoundException("Không tìm thấy loại danh mục.");
        var code = cmd.Code.Trim();
        if (await db.CatalogItems.AnyAsync(i => i.CatalogId == cmd.CatalogId && i.Code == code && i.Id != cmd.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["code"] = [$"Mã '{code}' đã tồn tại trong danh mục."] });
        if (cmd.ParentId.HasValue && !await db.CatalogItems.AnyAsync(i => i.Id == cmd.ParentId && i.CatalogId == cmd.CatalogId, ct))
            throw new AppException("Mục cha không thuộc cùng loại danh mục.");
        var isNew = cmd.Id is null;
        var item = isNew ? new CatalogItem() : await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy mục danh mục.");
        item.CatalogId = cmd.CatalogId;
        item.ParentId = cmd.ParentId;
        item.Code = code;
        item.Name = cmd.Name.Trim();
        item.SortOrder = cmd.SortOrder;
        item.IsActive = cmd.IsActive;
        if (isNew) db.CatalogItems.Add(item);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(isNew ? AuditAction.Create : AuditAction.Edit, ResourceName: $"Mục danh mục {item.Name}", Detail: new { entity = "catalog_item", id = item.Id, item.CatalogId }), ct);
        return item.Id;
    }
}

public sealed record DeleteCatalogItemCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteCatalogItemHandler(IAppDbContext db, IAuditLogger audit) : IRequestHandler<DeleteCatalogItemCommand, Unit>
{
    public async Task<Unit> Handle(DeleteCatalogItemCommand cmd, CancellationToken ct)
    {
        var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy mục danh mục.");
        var used = await db.FileCatalogItems.CountAsync(f => f.CatalogItemId == item.Id, ct);
        if (used > 0) throw new ConflictException($"Không thể xóa vì {used} tư liệu đang gắn mục này. Hãy chuyển sang \"Ngừng sử dụng\".");
        if (await db.CatalogItems.AnyAsync(i => i.ParentId == item.Id, ct)) throw new ConflictException("Không thể xóa mục đang có mục con.");
        db.CatalogItems.Remove(item);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: $"Mục danh mục {item.Name}", Detail: new { entity = "catalog_item", id = item.Id }), ct);
        return Unit.Value;
    }
}

// ---------- Excel import / export ----------
public sealed record ExportCatalogItemsQuery(Guid CatalogId, string Format, string ExportedBy) : IRequest<ExportedFile>;

public sealed class ExportCatalogItemsHandler(IAppDbContext db, IMediator mediator, ITableExporter exporter, IAuditLogger audit) : IRequestHandler<ExportCatalogItemsQuery, ExportedFile>
{
    public async Task<ExportedFile> Handle(ExportCatalogItemsQuery q, CancellationToken ct)
    {
        var cat = await db.Catalogs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == q.CatalogId, ct) ?? throw new NotFoundException("Không tìm thấy loại danh mục.");
        var items = await mediator.Send(new ListCatalogItemsQuery(q.CatalogId, null, null), ct);
        var codes = items.ToDictionary(i => i.Id, i => i.Code);
        var file = await exporter.ExportAsync(new TableExport($"Danh mục {cat.Name}",
            [new("Mã", 1.5f), new("Tên", 3), new("Mã mục cha", 1.5f), new("Thứ tự", 0.8f), new("Trạng thái", 1.2f), new("Số tư liệu", 1)],
            items.Select(i => (IReadOnlyList<string?>)[i.Code, i.Name, i.ParentId is { } p && codes.TryGetValue(p, out var pc) ? pc : "", i.SortOrder.ToString(),
                i.IsActive ? "Đang sử dụng" : "Ngừng sử dụng", i.FileCount.ToString()]).ToList(), null, q.ExportedBy), q.Format, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: file.FileName, Detail: new { report = "catalog", catalogId = cat.Id }), ct);
        return file;
    }
}

public sealed record ImportCatalogItemsCommand(Guid CatalogId, Stream File) : IRequest<CatalogImportResult>;

public sealed record CatalogImportResult(int Created, int Updated, IReadOnlyList<string> Errors);

public sealed class ImportCatalogItemsHandler(IAppDbContext db, IExcelReader excel, IAuditLogger audit) : IRequestHandler<ImportCatalogItemsCommand, CatalogImportResult>
{
    public async Task<CatalogImportResult> Handle(ImportCatalogItemsCommand cmd, CancellationToken ct)
    {
        if (!await db.Catalogs.AnyAsync(c => c.Id == cmd.CatalogId, ct)) throw new NotFoundException("Không tìm thấy loại danh mục.");
        var rows = excel.Read(cmd.File);
        var existing = await db.CatalogItems.Where(i => i.CatalogId == cmd.CatalogId).ToDictionaryAsync(i => i.Code, ct);
        var errors = new List<string>();
        var created = 0;
        var updated = 0;
        var parents = new List<(CatalogItem Item, string ParentCode, int Row)>();
        foreach (var r in rows)
        {
            var code = r.Get("Mã");
            var name = r.Get("Tên");
            if (code.Length == 0 || name.Length == 0) { errors.Add($"Dòng {r.RowNumber}: thiếu Mã hoặc Tên."); continue; }
            if (!existing.TryGetValue(code, out var item))
            {
                item = new CatalogItem { CatalogId = cmd.CatalogId, Code = code };
                db.CatalogItems.Add(item);
                existing[code] = item;
                created++;
            }
            else updated++;
            item.Name = name;
            item.SortOrder = int.TryParse(r.Get("Thứ tự"), out var so) ? so : item.SortOrder;
            var status = TextUtil.Unaccent(r.Get("Trạng thái"));
            item.IsActive = !status.StartsWith("ngung");
            var parent = r.Get("Mã mục cha");
            if (parent.Length > 0) parents.Add((item, parent, r.RowNumber));
        }
        foreach (var (item, parentCode, row) in parents)
            if (existing.TryGetValue(parentCode, out var p) && p != item) item.ParentId = p.Id; else errors.Add($"Dòng {row}: không tìm thấy mã mục cha '{parentCode}'.");
        if (errors.Count > 0) return new CatalogImportResult(0, 0, errors);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Import, ResourceName: "Nhập danh mục từ Excel", Detail: new { entity = "catalog", cmd.CatalogId, created, updated }), ct);
        return new CatalogImportResult(created, updated, errors);
    }
}
