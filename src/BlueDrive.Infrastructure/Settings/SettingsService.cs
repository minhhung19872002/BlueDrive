using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BlueDrive.Infrastructure.Settings;

/// <summary>Key/value jsonb settings with a 60s in-memory cache.</summary>
public sealed class SettingsService(IAppDbContext db, IMemoryCache cache, ICurrentUser currentUser, TimeProvider clock) : ISettingsService
{
    private const string CacheKey = "settings:all";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        if (!all.TryGetValue(key, out var json)) return defaultValue;
        try { return JsonSerializer.Deserialize<T>(json, Json) ?? defaultValue; }
        catch (JsonException) { return defaultValue; }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value, Json);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.SystemSettings.Add(row = new SystemSetting { Key = key });
        row.ValueJson = json;
        row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        row.UpdatedBy = currentUser.UserId;
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
    }

    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out Dictionary<string, string>? cached) && cached is not null) return cached;
        var dict = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.ValueJson, ct);
        cache.Set(CacheKey, dict, TimeSpan.FromSeconds(60));
        return dict;
    }
}
