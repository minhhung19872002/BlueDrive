using System.Security.Claims;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Sync;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Api.Realtime;

/// <summary>
/// SignalR hub at /hubs/sync for BlueDrive Desktop. A connection joins "ws:{id}" for each workspace it syncs (its roots and the
/// workspaces holding items shared with the user) and "device:{id}". Events: changes_available { workspaceId, latestSeq } (debounced
/// 1 s by <see cref="SyncChangeBroadcaster"/>) and device_status { deviceId, status }. Call Resubscribe() after a permission change.
/// </summary>
[Authorize]
public sealed class SyncHub(IAppDbContext db, SyncAccessContext access) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Resubscribe();
        // A workspace or share the user gets later is not one of the "ws:" groups yet: its permission change is also sent here.
        if (Guid.TryParse(Context.User?.FindFirstValue("sub"), out var uid)) await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{uid}");
        if (Guid.TryParse(Context.User?.FindFirstValue("did"), out var deviceId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");
        await base.OnConnectedAsync();
    }

    public async Task Resubscribe()
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue("sub"), out var uid)) throw new HubException("Chưa xác thực.");
        var ct = Context.ConnectionAborted;
        var roots = await (await WorkspaceQueries.AccessibleAsync(db, uid, ct)).Select(w => w.Id).ToListAsync(ct);
        await access.InitAsync(uid, ct);
        foreach (var ws in roots.Concat(await access.SharedWorkspacesAsync(roots, ct)).Distinct())
            await Groups.AddToGroupAsync(Context.ConnectionId, $"ws:{ws}", ct);
    }
}

public sealed class HubSyncNotifier(IHubContext<SyncHub> hub) : ISyncNotifier
{
    public Task DeviceStatusChangedAsync(Guid deviceId, string status, CancellationToken ct = default) =>
        hub.Clients.Group($"device:{deviceId}").SendAsync("device_status", new { deviceId, status }, ct);
}

/// <summary>
/// Polls the change journal once per second (rows written by the API and by the Worker alike) and sends one
/// changes_available per touched workspace — the debounce asked by the sync protocol.
/// </summary>
public sealed class SyncChangeBroadcaster(IServiceScopeFactory scopes, IHubContext<SyncHub> hub, ILogger<SyncChangeBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long last = -1;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                if (last < 0)
                {
                    last = await db.ChangeJournal.MaxAsync(c => (long?)c.Seq, stoppingToken) ?? 0;
                    continue;
                }
                var touched = await db.ChangeJournal.AsNoTracking().Where(c => c.Seq > last)
                    .GroupBy(c => c.WorkspaceId).Select(g => new { WorkspaceId = g.Key, Latest = g.Max(c => c.Seq) }).ToListAsync(stoppingToken);
                // Someone added to a workspace or given a share is not subscribed to that workspace yet: tell the user directly.
                var upTo = touched.Count == 0 ? last : touched.Max(t => t.Latest);
                var granted = await db.ChangeJournal.AsNoTracking().Where(c => c.Seq > last && c.Seq <= upTo && c.PrincipalId != null)
                    .Select(c => new { c.WorkspaceId, User = c.PrincipalId!.Value, c.Seq }).ToListAsync(stoppingToken);
                foreach (var t in touched)
                {
                    await hub.Clients.Group($"ws:{t.WorkspaceId}").SendAsync("changes_available", new { workspaceId = t.WorkspaceId, latestSeq = t.Latest }, stoppingToken);
                    last = Math.Max(last, t.Latest);
                }
                foreach (var g in granted.GroupBy(x => (x.User, x.WorkspaceId)))
                    await hub.Clients.Group($"user:{g.Key.User}").SendAsync("changes_available", new { workspaceId = g.Key.WorkspaceId, latestSeq = g.Max(x => x.Seq) }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Không gửi được thông báo thay đổi đồng bộ");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
