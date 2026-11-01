using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueDrive.Infrastructure.Realtime;

/// <summary>Realtime message relayed from any process (API or Worker) to SignalR via PostgreSQL NOTIFY.</summary>
public sealed record RealtimeEnvelope(string Group, string Method, JsonElement Payload);

/// <summary>
/// Persists notifications and publishes them on the "bluedrive_realtime" channel; the API's RealtimeRelay
/// LISTENs and forwards to SignalR groups ("user:{id}", "file:{id}"...). Works without Redis.
/// </summary>
public sealed class DbNotificationPublisher(IAppDbContext db, NpgsqlDataSource dataSource, TimeProvider clock) : INotificationPublisher
{
    public const string Channel = "bluedrive_realtime";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task NotifyUserAsync(Guid userId, string type, string title, string? body, string? link, CancellationToken ct = default)
        => await NotifyManyAsync([userId], type, title, body, link, ct);

    public async Task NotifyPermissionAsync(string permission, string type, string title, string? body, string? link, CancellationToken ct = default)
    {
        var users = await (from ur in db.UserRoles
                           join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                           join u in db.Users on ur.UserId equals u.Id
                           where rp.PermissionCode == permission && u.IsActive
                           select u.Id).Distinct().ToListAsync(ct);
        await NotifyManyAsync(users, type, title, body, link, ct);
    }

    public async Task BroadcastToGroupAsync(string group, string method, object payload, CancellationToken ct = default)
        => await SendAsync(new RealtimeEnvelope(group, method, JsonSerializer.SerializeToElement(payload, Json)), ct);

    private async Task NotifyManyAsync(IReadOnlyCollection<Guid> userIds, string type, string title, string? body, string? link, CancellationToken ct)
    {
        if (userIds.Count == 0) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = userIds.Distinct().Select(u => new Notification { UserId = u, Type = type, Title = title, Body = body, Link = link, CreatedAt = now }).ToList();
        db.Notifications.AddRange(rows);
        await db.SaveChangesAsync(ct);
        foreach (var n in rows)
            await SendAsync(new RealtimeEnvelope($"user:{n.UserId}", "notification",
                JsonSerializer.SerializeToElement(new { n.Id, n.Type, n.Title, n.Body, n.Link, n.IsRead, n.CreatedAt }, Json)), ct);
    }

    private async Task SendAsync(RealtimeEnvelope env, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(env, Json);
        if (payload.Length > 7900) payload = JsonSerializer.Serialize(env with { Payload = JsonSerializer.SerializeToElement(new { truncated = true }, Json) }, Json);
        await using var cmd = dataSource.CreateCommand("SELECT pg_notify(@c, @p)");
        cmd.Parameters.AddWithValue("c", Channel);
        cmd.Parameters.AddWithValue("p", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
