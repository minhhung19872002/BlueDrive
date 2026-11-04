using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Common;

/// <param name="TotalCapped">True when the rows were counted only up to <see cref="PagingExtensions.CountLimit"/>: there are more than <c>Total</c>.</param>
public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize, bool TotalCapped = false);

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

    /// <summary>
    /// Rows are counted up to this number (or one page past the current one) on lists that can grow without bound. An exact count
    /// of 2 000 000 audit rows or of a broad search term cost as much as the page itself or more (load tests T1 and T7,
    /// docs/bao-cao-kiem-thu-tai.md).
    /// </summary>
    public const int CountLimit = 1000;

    /// <summary>Like <see cref="ToPagedAsync{T}"/>, but the total is capped (<see cref="CappedTotalAsync{T}"/>).</summary>
    public static async Task<PagedResult<T>> ToCappedPagedAsync<T>(this IQueryable<T> query, PageQuery page, CancellationToken ct)
    {
        var items = await query.Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        var (total, capped) = await query.CappedTotalAsync(page.SafePage, page.SafePageSize, items.Count, ct);
        return new PagedResult<T>(items, total, page.SafePage, page.SafePageSize, capped);
    }

    /// <summary>
    /// A partly filled page is the last one, so the total follows from it without a second pass over the rows. Otherwise the rows
    /// are counted up to <see cref="CountLimit"/> (or one page past this one); beyond that the total is a lower bound and the client
    /// shows "more than".
    /// </summary>
    public static async Task<(int Total, bool Capped)> CappedTotalAsync<T>(this IQueryable<T> query, int page, int size, int rowsOnPage, CancellationToken ct)
    {
        var seen = (page - 1) * size + rowsOnPage;
        if (rowsOnPage < size && (rowsOnPage > 0 || page == 1)) return (seen, false);
        var limit = Math.Max(CountLimit, seen + size);
        var n = await query.Take(limit + 1).CountAsync(ct);
        return n > limit ? (limit, true) : (n, false);
    }
}
