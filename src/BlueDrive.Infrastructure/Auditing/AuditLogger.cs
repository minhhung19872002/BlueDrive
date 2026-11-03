using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using BlueDrive.Infrastructure.Persistence;
using Npgsql;

namespace BlueDrive.Infrastructure.Auditing;

/// <summary>
/// Append-only audit writer. Each row stores hash = SHA256(prev_hash | canonical(row)); rows are
/// serialized with a transaction-scoped advisory lock so the chain stays linear under concurrency.
/// </summary>
public sealed class AuditLogger(NpgsqlDataSource dataSource, ICurrentUser currentUser, TimeProvider clock, IAuditObserver? observer = null) : IAuditLogger
{
    private const long ChainLockKey = 0x426C7565_41756469; // "BlueAudi"
    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Operations from a BlueDrive Desktop session carry the device id and source = desktop_sync.</summary>
    private string? WithDevice(string? detail)
    {
        if (currentUser.DeviceId is not { } deviceId) return detail;
        var node = (detail is null ? null : System.Text.Json.Nodes.JsonNode.Parse(detail)) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        if (detail is not null && System.Text.Json.Nodes.JsonNode.Parse(detail) is not System.Text.Json.Nodes.JsonObject) node["value"] = System.Text.Json.Nodes.JsonNode.Parse(detail);
        node["deviceId"] ??= deviceId.ToString();
        node["source"] ??= "desktop_sync";
        return node.ToJsonString(JsonOpts);
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var log = new AuditLog
        {
            UserId = entry.UserId ?? currentUser.UserId,
            Username = entry.Username ?? currentUser.Username,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            ResourceName = entry.ResourceName,
            WorkspaceId = entry.WorkspaceId,
            Ip = currentUser.Ip,
            UserAgent = Truncate(currentUser.UserAgent, 500),
            Detail = WithDevice(entry.Detail is null ? null : JsonSerializer.Serialize(entry.Detail, JsonOpts)),
            // Truncate to microseconds so the hash recomputed from the DB value matches.
            OccurredAt = TruncateToMicros(clock.GetUtcNow().UtcDateTime),
        };

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", ChainLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        if (log.UserId.HasValue)
        {
            await using var orgCmd = new NpgsqlCommand("SELECT organization_id FROM users WHERE id = @u", conn, tx);
            orgCmd.Parameters.AddWithValue("u", log.UserId.Value);
            log.OrganizationId = await orgCmd.ExecuteScalarAsync(ct) as Guid?;
        }
        await using (var prevCmd = new NpgsqlCommand("SELECT hash FROM audit_logs ORDER BY id DESC LIMIT 1", conn, tx))
            log.PrevHash = await prevCmd.ExecuteScalarAsync(ct) as string;
        log.Hash = ComputeHash(log);

        await using (var ins = new NpgsqlCommand("""
            INSERT INTO audit_logs (user_id, username, organization_id, action, resource_type, resource_id, resource_name,
                workspace_id, ip, user_agent, detail, occurred_at, prev_hash, hash)
            VALUES (@user_id, @username, @org, @action, @rtype, @rid, @rname, @ws, @ip, @ua, @detail::jsonb, @at, @prev, @hash)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("user_id", (object?)log.UserId ?? DBNull.Value);
            ins.Parameters.AddWithValue("username", (object?)log.Username ?? DBNull.Value);
            ins.Parameters.AddWithValue("org", (object?)log.OrganizationId ?? DBNull.Value);
            ins.Parameters.AddWithValue("action", EnumNames.ToSnake(log.Action));
            ins.Parameters.AddWithValue("rtype", log.ResourceType is { } rt ? EnumNames.ToSnake(rt) : DBNull.Value);
            ins.Parameters.AddWithValue("rid", (object?)log.ResourceId ?? DBNull.Value);
            ins.Parameters.AddWithValue("rname", (object?)Truncate(log.ResourceName, 500) ?? DBNull.Value);
            ins.Parameters.AddWithValue("ws", (object?)log.WorkspaceId ?? DBNull.Value);
            ins.Parameters.AddWithValue("ip", (object?)log.Ip ?? DBNull.Value);
            ins.Parameters.AddWithValue("ua", (object?)log.UserAgent ?? DBNull.Value);
            ins.Parameters.AddWithValue("detail", (object?)log.Detail ?? DBNull.Value);
            ins.Parameters.AddWithValue("at", log.OccurredAt);
            ins.Parameters.AddWithValue("prev", (object?)log.PrevHash ?? DBNull.Value);
            ins.Parameters.AddWithValue("hash", log.Hash);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        if (observer is not null) await observer.OnLoggedAsync(log, ct);
    }

    public static string ComputeHash(AuditLog l)
    {
        var canonical = string.Join('|',
            l.PrevHash ?? "", l.UserId?.ToString() ?? "", l.Username ?? "", l.OrganizationId?.ToString() ?? "",
            EnumNames.ToSnake(l.Action), l.ResourceType is { } rt ? EnumNames.ToSnake(rt) : "", l.ResourceId?.ToString() ?? "",
            l.ResourceName ?? "", l.WorkspaceId?.ToString() ?? "", l.Ip ?? "", l.Detail is null ? "" : NormalizeJson(l.Detail),
            l.OccurredAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>jsonb re-orders keys and whitespace; normalize so DB round-trips hash identically.</summary>
    private static string NormalizeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Canonical(doc.RootElement);
        }
        catch (JsonException) { return json; }
    }

    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
        _ => e.GetRawText(),
    };

    private static DateTime TruncateToMicros(DateTime dt) => new(dt.Ticks - dt.Ticks % 10, DateTimeKind.Utc);
    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>Hook for anomaly detection (brute force, unusual downloads) after each audit write.</summary>
public interface IAuditObserver
{
    Task OnLoggedAsync(AuditLog log, CancellationToken ct);
}
