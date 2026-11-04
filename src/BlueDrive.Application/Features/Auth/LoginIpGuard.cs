using System.Collections.Concurrent;

namespace BlueDrive.Application.Features.Auth;

/// <summary>
/// Counts failed logins per client address. The per-account lockout stops guessing one account's password; this stops one
/// address guessing across many accounts (password spraying) without throttling an office whose staff all log in from
/// one NAT address at the start of the day (successful logins are not counted).
/// </summary>
public sealed class LoginIpGuard(TimeProvider clock)
{
    private sealed class Entry
    {
        public DateTime WindowStart;
        public int Failures;
        public DateTime? BlockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private long nextSweepTicks;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Remaining block time of the address, or null when it may try to log in.</summary>
    public TimeSpan? BlockedFor(string? ip)
    {
        if (ip is null || !entries.TryGetValue(ip, out var e)) return null;
        var now = Now;
        lock (e) return e.BlockedUntil > now ? e.BlockedUntil.Value - now : null;
    }

    /// <summary>
    /// Records one failed login. When the address reaches <paramref name="maxFailures"/> within <paramref name="window"/>
    /// it is blocked for <paramref name="window"/> and the call returns true (once, so the caller raises a single alert).
    /// </summary>
    public bool RecordFailure(string? ip, int maxFailures, TimeSpan window)
    {
        if (ip is null || maxFailures <= 0 || window <= TimeSpan.Zero) return false;
        var now = Now;
        Sweep(now, window);
        var e = entries.GetOrAdd(ip, _ => new Entry { WindowStart = now });
        lock (e)
        {
            if (e.BlockedUntil > now) return false;
            if (now - e.WindowStart >= window)
            {
                e.WindowStart = now;
                e.Failures = 0;
            }
            if (++e.Failures < maxFailures) return false;
            e.BlockedUntil = now + window;
            e.WindowStart = now;
            e.Failures = 0;
            return true;
        }
    }

    /// <summary>Drops addresses whose window and block are over, at most once a minute, so the map cannot grow without bound.</summary>
    private void Sweep(DateTime now, TimeSpan window)
    {
        var next = Interlocked.Read(ref nextSweepTicks);
        if (now.Ticks < next || Interlocked.CompareExchange(ref nextSweepTicks, now.AddMinutes(1).Ticks, next) != next) return;
        foreach (var (ip, e) in entries)
        {
            lock (e)
            {
                if (e.BlockedUntil > now || now - e.WindowStart < window) continue;
            }
            entries.TryRemove(ip, out _);
        }
    }
}
