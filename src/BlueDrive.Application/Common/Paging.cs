using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Common;

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public record PageQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Sort { get; init; }
    public string? Q { get; init; }

    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => PageSize is < 1 ? 20 : Math.Min(PageSize, 500);
}

public static class PagingExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageQuery page, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<T>(items, total, page.SafePage, page.SafePageSize);
    }

    public static async Task<PagedResult<TOut>> ToPagedAsync<TIn, TOut>(this IQueryable<TIn> query, PageQuery page, Func<TIn, TOut> map, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<TOut>(items.Select(map).ToList(), total, page.SafePage, page.SafePageSize);
    }
}
