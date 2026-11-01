using Npgsql;
using Serilog.Core;
using Serilog.Events;

namespace BlueDrive.Infrastructure.Logging;

/// <summary>Batched Serilog sink writing into the system_logs table (Warning and above by default).</summary>
public sealed class PostgresLogSink(string connectionString) : IBatchedLogEventSink
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var writer = await conn.BeginBinaryImportAsync(
                "COPY system_logs (timestamp, level, message, exception, source, properties) FROM STDIN (FORMAT BINARY)");
            foreach (var e in batch)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(e.Timestamp.UtcDateTime, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(e.Level.ToString());
                await writer.WriteAsync(Truncate(e.RenderMessage(), 4000));
                await writer.WriteAsync(e.Exception?.ToString(), NpgsqlTypes.NpgsqlDbType.Text);
                await writer.WriteAsync(e.Properties.TryGetValue("SourceContext", out var src) ? src.ToString().Trim('"') : null, NpgsqlTypes.NpgsqlDbType.Text);
                await writer.WriteAsync(PropertiesToJson(e), NpgsqlTypes.NpgsqlDbType.Jsonb);
            }
            await writer.CompleteAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table not created yet (before first migration) - drop the batch silently.
        }
    }

    public Task OnEmptyBatchAsync() => Task.CompletedTask;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string PropertiesToJson(LogEvent e)
    {
        var dict = e.Properties.Where(p => p.Key != "SourceContext")
            .ToDictionary(p => p.Key, p => p.Value.ToString().Trim('"'));
        return System.Text.Json.JsonSerializer.Serialize(dict);
    }
}
