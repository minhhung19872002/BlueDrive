using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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

// Spec 1.4: metadata columns of a library, the values of files, defaults by folder.

public sealed record LibraryColumnDto(Guid Id, Guid LibraryId, string Name, string Key, ColumnType Type, bool Required, bool Unique, JsonElement? DefaultValue,
    string? Validation, string? ValidationMessage, IReadOnlyList<string> Choices, Guid? CatalogId, int SortOrder);

public sealed record FileColumnDto(LibraryColumnDto Column, JsonElement? Value);

public static class ColumnValues
{
    public static readonly string[] SystemColumns = ["name", "type", "size", "createdBy", "createdAt", "updatedBy", "updatedAt", "version", "approval", "checkedOutBy"];

    public static LibraryColumnDto ToDto(LibraryColumn c) => new(c.Id, c.LibraryId, c.Name, c.Key, c.Type, c.Required, c.Unique, Parse(c.DefaultValue), c.Validation,
        c.ValidationMessage, c.Choices, c.CatalogId, c.SortOrder);

    public static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static bool Empty(JsonElement v) => v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                                  || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()))
                                                  || (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);

    /// <summary>Checks a value against its column; returns the value as stored (JSON and its typed copies) or throws a Vietnamese message.</summary>
    public static (string Json, string? Text, decimal? Number, DateTime? Date) Normalize(LibraryColumn c, JsonElement v)
    {
        if (Empty(v))
        {
            if (c.Required) throw new AppException($"Cột \"{c.Name}\" là bắt buộc.");
            return ("null", null, null, null);
        }
        string Fail(string why) => throw new AppException(c.ValidationMessage ?? $"Giá trị của cột \"{c.Name}\" {why}.");
        switch (c.Type)
        {
            case ColumnType.Text or ColumnType.MultilineText or ColumnType.Link:
            {
                if (v.ValueKind != JsonValueKind.String) Fail("phải là văn bản");
                var s = v.GetString()!.Trim();
                if (c.Type == ColumnType.Text && s.Contains('\n')) Fail("chỉ gồm một dòng");
                if (s.Length > 2000) Fail("dài quá 2000 ký tự");
                if (c.Type == ColumnType.Link && !(Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")) Fail("phải là địa chỉ http(s)");
                if (!string.IsNullOrWhiteSpace(c.Validation) && !Regex.IsMatch(s, c.Validation, RegexOptions.None, TimeSpan.FromMilliseconds(200))) Fail("không đúng định dạng");
                return (JsonSerializer.Serialize(s), s, null, null);
            }
            case ColumnType.Number:
            {
                decimal n = 0;
                if (!(v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out n))
                    && !(v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out n)))
                    Fail("phải là số");
                if (c.Validation is { } range && range.Split("..") is [var lo, var hi])
                {
                    if (decimal.TryParse(lo, NumberStyles.Number, CultureInfo.InvariantCulture, out var min) && n < min) Fail($"phải từ {min}");
                    if (decimal.TryParse(hi, NumberStyles.Number, CultureInfo.InvariantCulture, out var max) && n > max) Fail($"phải tới {max}");
                }
                return (n.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture), n, null);
            }
            case ColumnType.DateTime:
            {
                if (v.ValueKind != JsonValueKind.String || !DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    return (Fail("phải là ngày giờ"), null, null, null);
                d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
                return (JsonSerializer.Serialize(d), d.ToString("O"), null, d);
            }
            case ColumnType.Boolean:
            {
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("phải là Có/Không");
                var b = v.GetBoolean();
                return (b ? "true" : "false", b ? "1" : "0", b ? 1 : 0, null);
            }
            case ColumnType.Choice or ColumnType.Person or ColumnType.Catalog:
            {
                if (v.ValueKind != JsonValueKind.String) Fail("không hợp lệ");
                var s = v.GetString()!.Trim();
                if (c.Type == ColumnType.Choice && !c.Choices.Contains(s)) Fail("phải là một lựa chọn có sẵn");
                if (c.Type is ColumnType.Person or ColumnType.Catalog && !Guid.TryParse(s, out _)) Fail("không hợp lệ");
                return (JsonSerializer.Serialize(s), s, null, null);
            }
            case ColumnType.MultiChoice or ColumnType.Tags:
            {
                if (v.ValueKind != JsonValueKind.Array) Fail("phải là danh sách");
                var list = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                if (c.Type == ColumnType.MultiChoice && list.Any(x => !c.Choices.Contains(x))) Fail("phải là các lựa chọn có sẵn");
                return (JsonSerializer.Serialize(list), string.Join(", ", list), null, null);
            }
        }
        return (Fail("không hợp lệ"), null, null, null);
    }

    public static string KeyOf(string name)
    {
        var s = new string(TextUtil.Unaccent(name).ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Length == 0 ? "cot" : s.Length > 60 ? s[..60] : s;
    }
}

public sealed record ListColumnsQuery(Guid WorkspaceId, Guid LibraryId) : IRequest<IReadOnlyList<LibraryColumnDto>>;

public sealed record SaveColumnCommand(Guid WorkspaceId, Guid LibraryId, Guid? Id, string Name, ColumnType Type, bool Required, bool Unique, JsonElement? DefaultValue,
    string? Validation, string? ValidationMessage, IReadOnlyList<string>? Choices, Guid? CatalogId, int SortOrder = 0) : IRequest<Guid>;

public sealed class SaveColumnValidator : AbstractValidator<SaveColumnCommand>
{
    public SaveColumnValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng nhập tên cột.").MaximumLength(255);
        RuleFor(x => x.Choices).NotEmpty().When(x => x.Type is ColumnType.Choice or ColumnType.MultiChoice).WithMessage("Cột lựa chọn cần ít nhất một lựa chọn.");
        RuleFor(x => x.CatalogId).NotNull().When(x => x.Type == ColumnType.Catalog).WithMessage("Vui lòng chọn danh mục.");
        RuleFor(x => x.Validation).Must(r =>
        {
            if (string.IsNullOrWhiteSpace(r) || r.Contains("..")) return true;
            try { _ = new Regex(r, RegexOptions.None, TimeSpan.FromMilliseconds(200)); return true; }
            catch (ArgumentException) { return false; }
        }).WithMessage("Biểu thức kiểm tra không hợp lệ.");
    }
}

public sealed record DeleteColumnCommand(Guid WorkspaceId, Guid LibraryId, Guid Id) : IRequest<Unit>;

public sealed record GetFileColumnsQuery(Guid FileId) : IRequest<IReadOnlyList<FileColumnDto>>;

/// <summary>Sets column values (key → JSON) on one or more files of the same library — the details panel and the quick-edit grid.</summary>
public sealed record SetFileColumnsCommand(IReadOnlyList<Guid> FileIds, IReadOnlyDictionary<string, JsonElement> Values) : IRequest<int>;

public sealed record SetFolderDefaultsCommand(Guid FolderId, IReadOnlyDictionary<string, JsonElement> Values) : IRequest<Unit>;

public sealed record GetFolderDefaultsQuery(Guid FolderId) : IRequest<IReadOnlyDictionary<string, JsonElement?>>;

public sealed class ColumnHandlers(IAppDbContext db, ICurrentUser me, IItemPermissionService engine, Libraries libraries, IAuditLogger audit)
    : IRequestHandler<ListColumnsQuery, IReadOnlyList<LibraryColumnDto>>, IRequestHandler<SaveColumnCommand, Guid>, IRequestHandler<DeleteColumnCommand, Unit>,
      IRequestHandler<GetFileColumnsQuery, IReadOnlyList<FileColumnDto>>, IRequestHandler<SetFileColumnsCommand, int>,
      IRequestHandler<SetFolderDefaultsCommand, Unit>, IRequestHandler<GetFolderDefaultsQuery, IReadOnlyDictionary<string, JsonElement?>>
{
    /// <summary>The node whose rights stand for the library: its top folder, or the Space for the default library.</summary>
    public static Guid NodeOf(Guid workspaceId, Guid libraryId) => libraryId == PermissionProjection.LibraryOf(workspaceId) ? workspaceId : libraryId;

    private async Task EnsureLibraryAsync(Guid workspaceId, Guid libraryId, AccessRight right, CancellationToken ct)
    {
        if (libraryId != PermissionProjection.LibraryOf(workspaceId) && !await db.Folders.AnyAsync(f => f.Id == libraryId && f.WorkspaceId == workspaceId && f.IsLibrary && !f.IsDeleted, ct))
            throw new NotFoundException("Không tìm thấy thư viện.");
        var rights = await engine.DemandAsync(me.RequireUserId(), NodeOf(workspaceId, libraryId), AccessRight.ViewItems, ct);
        if (!rights.Has(right)) throw new ForbiddenException("Bạn không có quyền thay đổi cài đặt thư viện.");
    }

    public async Task<IReadOnlyList<LibraryColumnDto>> Handle(ListColumnsQuery q, CancellationToken ct)
    {
        await EnsureLibraryAsync(q.WorkspaceId, q.LibraryId, AccessRight.ViewItems, ct);
        return (await db.LibraryColumns.AsNoTracking().Where(c => c.LibraryId == q.LibraryId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct))
            .Select(ColumnValues.ToDto).ToList();
    }

    public async Task<Guid> Handle(SaveColumnCommand cmd, CancellationToken ct)
    {
        await EnsureLibraryAsync(cmd.WorkspaceId, cmd.LibraryId, AccessRight.ManageLibrarySettings, ct);
        var column = cmd.Id is { } id
            ? await db.LibraryColumns.FirstOrDefaultAsync(c => c.Id == id && c.LibraryId == cmd.LibraryId, ct) ?? throw new NotFoundException("Không tìm thấy cột.")
            : null;
        if (column is null)
        {
            var key = ColumnValues.KeyOf(cmd.Name);
            if (ColumnValues.SystemColumns.Contains(key)) key = "cot_" + key;
            var taken = await db.LibraryColumns.Where(c => c.LibraryId == cmd.LibraryId).Select(c => c.Key).ToListAsync(ct);
            for (var i = 2; taken.Contains(key); i++) key = $"{ColumnValues.KeyOf(cmd.Name)}_{i}";
            var last = await db.LibraryColumns.Where(c => c.LibraryId == cmd.LibraryId).MaxAsync(c => (int?)c.SortOrder, ct) ?? 0;
            db.LibraryColumns.Add(column = new LibraryColumn { LibraryId = cmd.LibraryId, WorkspaceId = cmd.WorkspaceId, Key = key, Type = cmd.Type, SortOrder = last + 1 });
        }
        else if (column.Type != cmd.Type && await db.FileColumnValues.AnyAsync(v => v.ColumnId == column.Id, ct))
            throw new AppException("Cột đã có dữ liệu, không đổi kiểu được.");
        column.Name = cmd.Name.Trim();
        column.Type = cmd.Type;
        column.Required = cmd.Required;
        column.Unique = cmd.Unique;
        column.Validation = string.IsNullOrWhiteSpace(cmd.Validation) ? null : cmd.Validation.Trim();
        column.ValidationMessage = string.IsNullOrWhiteSpace(cmd.ValidationMessage) ? null : cmd.ValidationMessage.Trim();
        column.Choices = (cmd.Choices ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
        column.CatalogId = cmd.CatalogId;
        if (cmd.SortOrder > 0) column.SortOrder = cmd.SortOrder; // new columns go last
        // A default must itself be a valid value.
        column.DefaultValue = cmd.DefaultValue is { } d && d.ValueKind != JsonValueKind.Null
            ? ColumnValues.Normalize(new LibraryColumn { Name = column.Name, Type = column.Type, Choices = column.Choices, Validation = column.Validation }, d).Json
            : null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Folder, cmd.LibraryId, column.Name, cmd.WorkspaceId, new { column = column.Key, type = column.Type }), ct);
        return column.Id;
    }

    public async Task<Unit> Handle(DeleteColumnCommand cmd, CancellationToken ct)
    {
        await EnsureLibraryAsync(cmd.WorkspaceId, cmd.LibraryId, AccessRight.ManageLibrarySettings, ct);
        var column = await db.LibraryColumns.FirstOrDefaultAsync(c => c.Id == cmd.Id && c.LibraryId == cmd.LibraryId, ct) ?? throw new NotFoundException("Không tìm thấy cột.");
        await db.FileColumnValues.Where(v => v.ColumnId == column.Id).ExecuteDeleteAsync(ct);
        await db.FolderColumnDefaults.Where(v => v.ColumnId == column.Id).ExecuteDeleteAsync(ct);
        db.LibraryColumns.Remove(column);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceType.Folder, cmd.LibraryId, column.Name, cmd.WorkspaceId, new { deletedColumn = column.Key }), ct);
        return Unit.Value;
    }

    public async Task<IReadOnlyList<FileColumnDto>> Handle(GetFileColumnsQuery q, CancellationToken ct)
    {
        await engine.DemandAsync(me.RequireUserId(), q.FileId, AccessRight.ViewItems, ct);
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == q.FileId, ct);
        var library = await libraries.ForAsync(file.WorkspaceId, file.FolderId, ct);
        var columns = await db.LibraryColumns.AsNoTracking().Where(c => c.LibraryId == library.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var ids = columns.Select(c => c.Id).ToList();
        var values = await db.FileColumnValues.AsNoTracking().Where(v => v.FileId == file.Id && ids.Contains(v.ColumnId)).ToDictionaryAsync(v => v.ColumnId, v => v.Value, ct);
        return columns.Select(c => new FileColumnDto(ColumnValues.ToDto(c), ColumnValues.Parse(values.GetValueOrDefault(c.Id)))).ToList();
    }

    public async Task<int> Handle(SetFileColumnsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (cmd.FileIds.Count == 0 || cmd.FileIds.Count > 500) throw new AppException("Chọn từ 1 đến 500 tệp.");
        var count = 0;
        foreach (var fileId in cmd.FileIds.Distinct())
        {
            await engine.DemandAsync(uid, fileId, AccessRight.EditItems, ct);
            var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId && !f.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy tệp.");
            var library = await libraries.ForAsync(file.WorkspaceId, file.FolderId, ct);
            var columns = await db.LibraryColumns.AsNoTracking().Where(c => c.LibraryId == library.Id).ToDictionaryAsync(c => c.Key, ct);
            foreach (var (key, raw) in cmd.Values)
            {
                if (!columns.TryGetValue(key, out var column)) throw new AppException($"Thư viện \"{library.Name}\" không có cột \"{key}\".");
                var (json, text, number, date) = ColumnValues.Normalize(column, raw);
                if (column.Unique && text is not null && await db.FileColumnValues.AnyAsync(v => v.ColumnId == column.Id && v.FileId != file.Id && v.TextValue == text, ct))
                    throw new ConflictException($"Giá trị \"{text}\" đã có ở tệp khác trong cột \"{column.Name}\" (cột không cho trùng).");
                var row = await db.FileColumnValues.FirstOrDefaultAsync(v => v.FileId == file.Id && v.ColumnId == column.Id, ct);
                if (json == "null")
                {
                    if (row is not null) db.FileColumnValues.Remove(row);
                    continue;
                }
                if (row is null) db.FileColumnValues.Add(row = new FileColumnValue { FileId = file.Id, ColumnId = column.Id });
                row.Value = json;
                row.TextValue = text;
                row.NumberValue = number;
                row.DateValue = date;
            }
            file.Revision++;
            await db.SaveChangesAsync(ct);
            count++;
        }
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceType.File, cmd.FileIds[0], null, null, new { columns = cmd.Values.Keys, files = cmd.FileIds.Count }), ct);
        return count;
    }

    public async Task<IReadOnlyDictionary<string, JsonElement?>> Handle(GetFolderDefaultsQuery q, CancellationToken ct)
    {
        await engine.DemandAsync(me.RequireUserId(), q.FolderId, AccessRight.ViewItems, ct);
        return await db.FolderColumnDefaults.AsNoTracking().Where(d => d.FolderId == q.FolderId)
            .Join(db.LibraryColumns, d => d.ColumnId, c => c.Id, (d, c) => new { c.Key, d.Value }).ToDictionaryAsync(x => x.Key, x => ColumnValues.Parse(x.Value), ct);
    }

    public async Task<Unit> Handle(SetFolderDefaultsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!(await engine.DemandAsync(uid, cmd.FolderId, AccessRight.ViewItems, ct)).Has(AccessRight.ManageLibrarySettings))
            throw new ForbiddenException("Bạn không có quyền đặt giá trị mặc định cho thư mục.");
        var folder = await db.Folders.AsNoTracking().FirstAsync(f => f.Id == cmd.FolderId, ct);
        var library = await libraries.ForAsync(folder.WorkspaceId, folder.Id, ct);
        var columns = await db.LibraryColumns.AsNoTracking().Where(c => c.LibraryId == library.Id).ToDictionaryAsync(c => c.Key, ct);
        foreach (var (key, raw) in cmd.Values)
        {
            if (!columns.TryGetValue(key, out var column)) throw new AppException($"Thư viện không có cột \"{key}\".");
            var json = ColumnValues.Normalize(new LibraryColumn { Name = column.Name, Type = column.Type, Choices = column.Choices, Validation = column.Validation }, raw).Json;
            await db.FolderColumnDefaults.Where(d => d.FolderId == folder.Id && d.ColumnId == column.Id).ExecuteDeleteAsync(ct);
            if (json != "null") db.FolderColumnDefaults.Add(new FolderColumnDefault { FolderId = folder.Id, ColumnId = column.Id, Value = json });
        }
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
