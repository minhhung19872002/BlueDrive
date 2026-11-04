using System.Security.Claims;
using System.Text.Json;
using BlueDrive.Infrastructure.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace BlueDrive.Api.Realtime;

/// <summary>SignalR hub at /hubs/notifications. Each connection joins "user:{id}" and "all"; file pages join "file:{id}".</summary>
[Authorize]
public sealed class NotificationHub(Application.Abstractions.IResourceAccessChecker? access = null) : Hub
{
    /// <summary>Every signed-in connection (system announcements).</summary>
    public const string AllUsersGroup = Application.Features.Operations.RealtimeGroups.AllUsers;

    private Guid? UserId => Guid.TryParse(Context.User?.FindFirstValue("sub"), out var id) ? id : null;

    public override async Task OnConnectedAsync()
    {
        if (UserId is { } uid)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{uid}");
            await Groups.AddToGroupAsync(Context.ConnectionId, AllUsersGroup);
        }
        await base.OnConnectedAsync();
    }

    /// <summary>Subscribe to live comments of a file the caller may view.</summary>
    public async Task JoinFile(Guid fileId)
    {
        if (UserId is not { } uid) throw new HubException("Chưa xác thực.");
        if (access is not null && !await access.CanViewFileAsync(uid, fileId, Context.ConnectionAborted))
            throw new HubException("Bạn không có quyền xem tài liệu này.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"file:{fileId}");
    }

    public Task LeaveFile(Guid fileId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"file:{fileId}");
}

/// <summary>LISTENs on PostgreSQL and forwards envelopes to SignalR groups (works for messages raised by the Worker too).</summary>
public sealed class RealtimeRelay(NpgsqlDataSource dataSource, IHubContext<NotificationHub> hub, ILogger<RealtimeRelay> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, e) =>
                {
                    try
                    {
                        var env = JsonSerializer.Deserialize<RealtimeEnvelope>(e.Payload, Json);
                        if (env is not null) _ = hub.Clients.Group(env.Group).SendAsync(env.Method, env.Payload, stoppingToken);
                    }
                    catch (JsonException ex) { logger.LogWarning(ex, "Thông điệp realtime không hợp lệ"); }
                };
                await using (var cmd = new NpgsqlCommand($"LISTEN {DbNotificationPublisher.Channel}", conn))
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested) await conn.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mất kết nối kênh realtime, thử lại sau 5 giây");
                await Task.Delay(5000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }
}
