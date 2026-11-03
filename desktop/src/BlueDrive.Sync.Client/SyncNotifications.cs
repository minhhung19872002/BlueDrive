using Microsoft.AspNetCore.SignalR.Client;

namespace BlueDrive.Sync.Client;

/// <summary>
/// Tells the app when to run a sync round: SignalR /hubs/sync ("changes_available", "device_status") with automatic reconnect, plus a
/// poll timer as fallback (every <see cref="PollInterval"/>, or <see cref="DisconnectedPollInterval"/> while the hub is down).
/// </summary>
public sealed class SyncNotifications : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly CancellationTokenSource _stop = new();
    private Task? _poller;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan DisconnectedPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised on a server push or a poll tick: the receiver should run a sync round (coalesce bursts).</summary>
    public event Action<string>? SyncRequested;

    /// <summary>"revoked" / "wipe_requested" / "update_required": the app checks /sync/devices/current and acts.</summary>
    public event Action<string>? DeviceStatusChanged;

    public HubConnectionState State => _hub.State;

    public SyncNotifications(Uri server, Func<Task<string?>> accessToken, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(BlueDriveHttp.Normalize(server), "hubs/sync"), o =>
            {
                o.AccessTokenProvider = accessToken;
                if (handlerFactory is not null)
                {
                    // Tests (TestServer): long polling through the in-memory handler.
                    o.HttpMessageHandlerFactory = _ => handlerFactory();
                    o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                }
            })
            .WithAutomaticReconnect(new BackoffRetry())
            .Build();
        _hub.On<ChangesAvailable>("changes_available", _ => SyncRequested?.Invoke("push"));
        _hub.On<DeviceStatus>("device_status", s => DeviceStatusChanged?.Invoke(s.Status));
        _hub.Reconnected += async _ =>
        {
            await _hub.InvokeAsync("Resubscribe");
            SyncRequested?.Invoke("reconnected"); // pushes may have been missed while offline
        };
    }

    private sealed record ChangesAvailable(Guid WorkspaceId, long LatestSeq);
    private sealed record DeviceStatus(Guid DeviceId, string Status);

    public async Task StartAsync(CancellationToken ct = default)
    {
        try { await _hub.StartAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException) { /* poll until the hub is reachable */ }
        _poller = PollAsync(_stop.Token);
    }

    /// <summary>Call after a new root was added or a share arrived: the hub re-joins the workspace groups.</summary>
    public Task ResubscribeAsync(CancellationToken ct = default) =>
        _hub.State == HubConnectionState.Connected ? _hub.InvokeAsync("Resubscribe", ct) : Task.CompletedTask;

    private async Task PollAsync(CancellationToken ct)
    {
        var last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
            var interval = _hub.State == HubConnectionState.Connected ? PollInterval : DisconnectedPollInterval;
            if (DateTime.UtcNow - last < interval) continue;
            last = DateTime.UtcNow;
            if (_hub.State == HubConnectionState.Disconnected)
            {
                try { await _hub.StartAsync(ct); }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException) { }
            }
            SyncRequested?.Invoke("poll");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_poller is not null) await _poller;
        await _hub.DisposeAsync();
        _stop.Dispose();
    }

    private sealed class BackoffRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) =>
            TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(ctx.PreviousRetryCount, 6))) + Random.Shared.NextDouble());
    }
}
