using BenchmarkDotNet.Attributes;
using BlueDrive.Application.Features.Access;
using BlueDrive.Domain.Permissions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.Benchmarks;

/// <summary>Shared between the runner and the in-process benchmarks.</summary>
public static class Perf
{
    public static PerfHost Host = null!;
    public static PerfData Data = null!;
}

/// <summary>Spec 4.5: <c>Has()</c> of the permission engine with its cache (target &lt; 1 ms) and without (target &lt; 20 ms).</summary>
[MemoryDiagnoser]
public class PermissionBenchmarks
{
    private IServiceScope _scope = null!;
    private IMemoryCache _cache = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cache = Perf.Host.Services.GetRequiredService<IMemoryCache>();
        _scope = Perf.Host.Services.CreateScope();
        // Warm once.
        _ = Has(_scope).GetAwaiter().GetResult();
    }

    private static Task<bool> Has(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IItemPermissionService>().HasAsync(Perf.Data.UserId, Perf.Data.FileInScope, AccessRight.OpenItems);

    /// <summary>A new request (its per-request memo empty), the shared cache warm.</summary>
    [Benchmark(Description = "Has() có cache")]
    public async Task<bool> HasCached()
    {
        using var scope = Perf.Host.Services.CreateScope();
        return await Has(scope);
    }

    /// <summary>A new request with the shared cache emptied: every row is read from PostgreSQL.</summary>
    [Benchmark(Description = "Has() không cache")]
    public async Task<bool> HasCold()
    {
        ((MemoryCache)_cache).Compact(1.0);
        using var scope = Perf.Host.Services.CreateScope();
        return await Has(scope);
    }

    [GlobalCleanup]
    public void Cleanup() => _scope.Dispose();
}
