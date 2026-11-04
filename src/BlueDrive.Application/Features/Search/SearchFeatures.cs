using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Search;

/// <summary>Advanced search criteria (#20, #32, #50, #51). All filters are optional and combined with AND.</summary>
public sealed record SearchCriteria
{
    public string? Q { get; init; }
    public IReadOnlyList<MediaType>? MediaTypes { get; init; }
    public IReadOnlyList<string>? Extensions { get; init; }
    public IReadOnlyList<Guid>? CatalogItemIds { get; init; }
    public Guid? OrganizationId { get; init; }
    public bool IncludeSubOrganizations { get; init; } = true;
    public Guid? CreatedBy { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    /// <summary>Restrict to one workspace (personal #20 or group #32), optionally to a folder subtree.</summary>
    public Guid? WorkspaceId { get; init; }
    public Guid? FolderId { get; init; }
    public string? Tag { get; init; }
    /// <summary>"relevance" (default with keyword), "-createdAt", "createdAt", "name", "-size".</summary>
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public sealed record SearchHit(DriveItemDto Item, string Location, string? HighlightedName, string? HighlightedTitle, string? Snippet, double Rank, string? UploaderOrganization);

/// <param name="TotalCapped">True when the matches were counted only up to <see cref="SearchPaging.CountLimit"/>: there are more than <c>Total</c>.</param>
public sealed record SearchResult(IReadOnlyList<SearchHit> Items, int Total, int Page, int PageSize, IReadOnlyList<string> Terms, long ElapsedMs, bool TotalCapped = false);

public static class SearchPaging
{
    /// <summary>
    /// Matches are counted up to this number (or one page past the current one). An exact count of a broad term ("dân số" in 80 000
    /// files) re-checks the access rules of every match and cost as much as the page itself (load test T1, docs/bao-cao-kiem-thu-tai.md).
    /// </summary>
    public const int CountLimit = 1000;
}

/// <summary>Accent-insensitive full-text search over files the user can access (implemented with PostgreSQL FTS).</summary>
public interface IFileSearchService
{
    Task<(IReadOnlyList<(Guid FileId, double Rank)> Page, int Total, bool TotalCapped)> SearchAsync(SearchCriteria criteria, PrincipalSet principals, CancellationToken ct);
}

public sealed record SearchFilesQuery(SearchCriteria Criteria) : IRequest<SearchResult>;

public sealed class SearchFilesValidator : AbstractValidator<SearchFilesQuery>
{
    public SearchFilesValidator()
    {
        RuleFor(x => x.Criteria.Q).MaximumLength(200).WithMessage("Từ khóa tối đa 200 ký tự.");
        RuleFor(x => x.Criteria).Must(c => c.From is null || c.To is null || c.From <= c.To).WithMessage("Khoảng thời gian không hợp lệ (từ ngày phải trước đến ngày).");
        RuleFor(x => x.Criteria).Must(c => c.MinSize is null || c.MaxSize is null || c.MinSize <= c.MaxSize).WithMessage("Khoảng dung lượng không hợp lệ.");
    }
}

public sealed class SearchFilesHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IPermissionService perms, IFileSearchService search,
    IContentTicketService tickets) : IRequestHandler<SearchFilesQuery, SearchResult>
{
    public async Task<SearchResult> Handle(SearchFilesQuery q, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.SearchView, ct) && !await perms.HasAsync(uid, Perm.DocView, ct))
            throw new ForbiddenException("Vai trò của bạn không có quyền tìm kiếm tư liệu.");
        var principals = await auth.GetPrincipalsAsync(uid, ct);
        var (page, total, capped) = await search.SearchAsync(q.Criteria, principals, ct);

        var ids = page.Select(p => p.FileId).ToList();
        var items = (await DriveProjection.Files(db, db.Files.AsNoTracking().Where(f => ids.Contains(f.Id)), uid).ToListAsync(ct)).ToDictionary(i => i.Id);
        var meta = await db.Files.AsNoTracking().Include(f => f.Workspace).Include(f => f.Folder).Where(f => ids.Contains(f.Id))
            .Select(f => new { f, Org = db.Organizations.Where(o => o.Id == f.UploaderOrganizationId).Select(o => o.Name).FirstOrDefault() }).ToDictionaryAsync(x => x.f.Id, ct);
        var terms = SearchText.Terms(q.Criteria.Q);
        var hits = new List<SearchHit>();
        foreach (var (fileId, rank) in page)
        {
            if (!items.TryGetValue(fileId, out var item)) continue;
            // Precise check (break-inheritance edge cases are filtered here).
            if (!await auth.CanAsync(uid, ResourceType.File, fileId, AccessLevel.View, ct)) { total--; continue; }
            var m = meta[fileId];
            var location = FileLocation.ToText(await FileLocation.BuildAsync(db, m.f.Workspace!, m.f.Folder, uid, ct));
            hits.Add(new SearchHit(item, location, SearchText.Highlight(m.f.Name, terms), SearchText.Highlight(m.f.Title, terms),
                SearchText.Snippet(m.f.Description, terms), rank, m.Org));
        }
        var withThumbs = ThumbnailUrls.Apply(hits.Select(h => h.Item), tickets, uid);
        hits = hits.Select((h, i) => h with { Item = withThumbs[i] }).ToList();
        return new SearchResult(hits, Math.Max(total, hits.Count), Math.Max(1, q.Criteria.Page), Math.Clamp(q.Criteria.PageSize, 1, 100), terms, sw.ElapsedMilliseconds, capped);
    }
}

public static class SearchText
{
    /// <summary>Unaccented, lower-case alphanumeric tokens ("Dân số 2025" → dan, so, 2025).</summary>
    public static IReadOnlyList<string> Terms(string? q)
    {
        var u = TextUtil.Unaccent(q);
        var chars = u.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Take(10).ToList();
    }

    /// <summary>Wraps matches with &lt;mark&gt; on the ORIGINAL text, matching accent-insensitively (Vietnamese letters unaccent 1:1).</summary>
    public static string? Highlight(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0) return null;
        var original = text.Normalize(System.Text.NormalizationForm.FormC);
        var plain = TextUtil.Unaccent(original);
        if (plain.Length != original.Length) return null;
        var marks = new bool[original.Length];
        foreach (var t in terms)
            for (var i = plain.IndexOf(t, StringComparison.Ordinal); i >= 0; i = plain.IndexOf(t, i + 1, StringComparison.Ordinal))
                for (var k = i; k < i + t.Length; k++) marks[k] = true;
        if (!marks.Contains(true)) return null;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < original.Length; i++)
        {
            if (marks[i] && (i == 0 || !marks[i - 1])) sb.Append("<mark>");
            sb.Append(Encode(original[i]));
            if (marks[i] && (i == original.Length - 1 || !marks[i + 1])) sb.Append("</mark>");
        }
        return sb.ToString();
    }

    public static string? Snippet(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var plain = TextUtil.Unaccent(text);
        var pos = terms.Select(t => plain.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, pos - 60);
        var piece = text.Substring(start, Math.Min(200, text.Length - start));
        return (start > 0 ? "…" : "") + (Highlight(piece, terms) ?? string.Concat(piece.Select(Encode))) + (start + piece.Length < text.Length ? "…" : "");
    }

    /// <summary>Escapes only HTML-significant characters so Vietnamese letters stay readable.</summary>
    private static string Encode(char ch) => ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => ch.ToString() };
}

// ---------- Saved filters ----------
public sealed record SavedSearchDto(Guid Id, string Name, SearchCriteria Criteria, DateTime CreatedAt);

public sealed record ListSavedSearchesQuery : IRequest<IReadOnlyList<SavedSearchDto>>;

public sealed class ListSavedSearchesHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListSavedSearchesQuery, IReadOnlyList<SavedSearchDto>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SavedSearchDto>> Handle(ListSavedSearchesQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var rows = await db.SavedSearches.AsNoTracking().Where(s => s.UserId == uid).OrderBy(s => s.Name).ToListAsync(ct);
        return rows.Select(r => new SavedSearchDto(r.Id, r.Name, JsonSerializer.Deserialize<SearchCriteria>(r.QueryJson, Json) ?? new SearchCriteria(), r.CreatedAt)).ToList();
    }
}

public sealed record SaveSearchCommand(string Name, SearchCriteria Criteria) : IRequest<Guid>;

public sealed class SaveSearchValidator : AbstractValidator<SaveSearchCommand>
{
    public SaveSearchValidator() => RuleFor(x => x.Name).NotEmpty().WithMessage("Vui lòng đặt tên cho bộ lọc.").MaximumLength(100);
}

public sealed class SaveSearchHandler(IAppDbContext db, ICurrentUser me, TimeProvider clock) : IRequestHandler<SaveSearchCommand, Guid>
{
    public async Task<Guid> Handle(SaveSearchCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (await db.SavedSearches.CountAsync(s => s.UserId == uid, ct) >= 50) throw new AppException("Mỗi người dùng lưu tối đa 50 bộ lọc.");
        var name = cmd.Name.Trim();
        var row = await db.SavedSearches.FirstOrDefaultAsync(s => s.UserId == uid && s.Name == name, ct);
        if (row is null) db.SavedSearches.Add(row = new SavedSearch { UserId = uid, Name = name, CreatedAt = clock.GetUtcNow().UtcDateTime });
        row.QueryJson = JsonSerializer.Serialize(cmd.Criteria with { Page = 1 }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync(ct);
        return row.Id;
    }
}

public sealed record DeleteSavedSearchCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteSavedSearchHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<DeleteSavedSearchCommand, Unit>
{
    public async Task<Unit> Handle(DeleteSavedSearchCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (await db.SavedSearches.Where(s => s.Id == cmd.Id && s.UserId == uid).ExecuteDeleteAsync(ct) == 0) throw new NotFoundException("Không tìm thấy bộ lọc đã lưu.");
        return Unit.Value;
    }
}
