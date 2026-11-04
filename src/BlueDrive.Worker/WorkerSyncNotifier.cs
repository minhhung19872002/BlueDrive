using BlueDrive.Application.Features.Sync;

namespace BlueDrive.Worker;

/// <summary>
/// The worker does not host /hubs/sync (the API does), so it cannot push to connected desktop clients.
/// Device actions only run in the API; this registration keeps the shared handler graph resolvable
/// (container validation in Development) and the client still sees the new status on its next poll.
/// </summary>
public sealed class WorkerSyncNotifier(ILogger<WorkerSyncNotifier> log) : ISyncNotifier
{
    public Task DeviceStatusChangedAsync(Guid deviceId, string status, CancellationToken ct = default)
    {
        log.LogInformation("Device {DeviceId} status changed to {Status}; the client picks it up on its next poll", deviceId, status);
        return Task.CompletedTask;
    }
}
