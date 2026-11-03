using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Features.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BlueDrive.Api.Infrastructure;

/// <summary>
/// Optimistic concurrency for the desktop sync client on the regular drive endpoints: when the request carries
/// <c>If-Match: "&lt;revision&gt;"</c>, a stale ETag answers 412 with the item's current sync metadata, and a successful
/// change returns the new <c>ETag</c>. Requests without the header (web, mobile) are unaffected.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SyncIfMatchAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ifMatch = context.HttpContext.Request.Headers.IfMatch.ToString();
        Guid? id = context.ActionArguments.TryGetValue("id", out var raw) && raw is Guid g ? g : null;
        if (id is null)
        {
            var items = context.ActionArguments.Values.Select(v => v switch
            {
                MoveItemsCommand m => m.Items,
                DeleteItemsCommand d => d.Items,
                _ => null,
            }).FirstOrDefault(i => i is not null);
            if (!string.IsNullOrWhiteSpace(ifMatch) && items is { Count: > 1 })
                throw new AppException("If-Match chỉ áp dụng khi thao tác trên một mục.");
            id = items is { Count: 1 } ? items[0].Id : null;
        }

        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<IAppDbContext>();
        var ct = context.HttpContext.RequestAborted;
        if (id is { } itemId && !string.IsNullOrWhiteSpace(ifMatch))
        {
            var me = services.GetRequiredService<ICurrentUser>();
            await SyncPreconditions.CheckAsync(db, services.GetRequiredService<SyncAccessContext>(), me.RequireUserId(), itemId, ifMatch, ct);
        }

        var executed = await next();
        if (id is { } changed && executed.Exception is null && await SyncPreconditions.CurrentETagAsync(db, changed, ct) is { } etag)
            context.HttpContext.Response.Headers.ETag = etag;
    }
}
