using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using BlueDrive.Application.Features.Access;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.Benchmarks;

// Spec 4.5 benchmarks. Usage: dotnet run -c Release --project tests/BlueDrive.Benchmarks -- [--files 1000000 --users 5000 ...] [--out file.md]
// Needs Docker (PostgreSQL 16 in a container). Writes a Markdown report (default: artifacts/perf-permissions-results.md).
public static class BenchmarkEntry
{
    public static async Task Main(string[] args)
    {
        var options = PerfOptions.Parse(args);
        var outFile = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine("artifacts", "perf-permissions-results.md");

        await using var host = new PerfHost();
        Console.WriteLine("Khởi động PostgreSQL và API…");
        await host.StartAsync();
        Console.WriteLine($"Tạo dữ liệu: {options}");
        var data = await Seeder.SeedAsync(host, options);
        Console.WriteLine($"Đã tạo {data.Files:N0} tệp, {data.Folders:N0} thư mục, {data.Scopes:N0} scope, {data.Entries:N0} mục quyền trong {data.SeedTime}");
        Perf.Host = host;
        Perf.Data = data;

        // ---- Has(): BenchmarkDotNet, in process (the seeded host is shared)
        var config = ManualConfig.CreateMinimumViable()
            .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(3).WithIterationCount(15))
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);
        var summary = BenchmarkRunner.Run<PermissionBenchmarks>(config);

        // ---- listing, search and breaking inheritance: p95 of real API calls / the editor
        var user = await host.LoginAsync(data.Username, PerfHost.UserPassword);
        async Task<double[]> TimeAsync(int runs, Func<Task> action)
        {
            await action(); // warm-up
            var times = new double[runs];
            for (var i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                await action();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            return times;
        }
        static double P(double[] sorted, double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

        var list = await TimeAsync(30, async () =>
        {
            var json = await PerfHost.ReadAsync(await user.GetAsync($"/api/v1/workspaces/{data.Space0}/items?folderId={data.ListFolderId}&page=1&pageSize=500"));
            if (json.GetProperty("total").GetInt32() < options.ListFolder) throw new InvalidOperationException("Danh sách thiếu mục.");
        });
        var listAll = await TimeAsync(10, async () =>
        {
            for (var page = 1; page <= (options.ListFolder + 499) / 500; page++)
                await PerfHost.ReadAsync(await user.GetAsync($"/api/v1/workspaces/{data.Space0}/items?folderId={data.ListFolderId}&page={page}&pageSize=500"));
        });
        var search = await TimeAsync(30, async () => await PerfHost.ReadAsync(await user.GetAsync("/api/v1/search?q=t%C3%A0i%20li%E1%BB%87u%2012&pageSize=20")));

        double breakMs, restoreMs;
        using (var scope = host.Services.CreateScope())
        {
            var editor = scope.ServiceProvider.GetRequiredService<AclEditor>();
            var db = scope.ServiceProvider.GetRequiredService<BlueDrive.Application.Abstractions.IAppDbContext>();
            var sw = Stopwatch.StartNew();
            // As the API does it: one transaction for the whole subtree.
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await editor.BreakInheritanceAsync(data.BigFolderId, copy: true, null, CancellationToken.None);
                await tx.CommitAsync();
            }
            breakMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            await editor.RestoreInheritanceAsync(data.BigFolderId, recursive: false, CancellationToken.None);
            restoreMs = sw.Elapsed.TotalMilliseconds;
        }

        // ---- report
        string Ms(double v) => v.ToString(v < 10 ? "0.00" : "0", CultureInfo.InvariantCulture) + " ms";
        string Mean(string name)
        {
            var r = summary.Reports.FirstOrDefault(x => x.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo.Contains(name));
            return r?.ResultStatistics is { } s ? $"{Ms(s.Mean / 1e6)} (p95 {Ms(s.Percentiles.P95 / 1e6)})" : "—";
        }
        var md = new StringBuilder();
        md.AppendLine($"Đo lúc {DateTime.Now:dd/MM/yyyy HH:mm}, {Environment.ProcessorCount} luồng CPU, {Environment.OSVersion}, .NET {Environment.Version}, PostgreSQL 16 (container).");
        md.AppendLine();
        md.AppendLine($"Dữ liệu: {data.Files:N0} tệp, {data.Folders:N0} thư mục, {data.Scopes:N0} scope quyền riêng, {data.Entries:N0} mục quyền, {data.Users:N0} người dùng, {data.Groups:N0} nhóm; tạo trong {data.SeedTime:hh\\:mm\\:ss}.");
        md.AppendLine();
        md.AppendLine("| Phép đo | Mục tiêu | Kết quả | Đạt |");
        md.AppendLine("|---|---|---|---|");
        double MeanMs(string name) => summary.Reports.FirstOrDefault(x => x.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo.Contains(name))?.ResultStatistics?.Mean / 1e6 ?? double.NaN;
        string Ok(bool b) => b ? "Đạt" : "Chưa đạt";
        md.AppendLine($"| `Has()` có cache | < 1 ms | {Mean("có cache")} | {Ok(MeanMs("có cache") < 1)} |");
        md.AppendLine($"| `Has()` không cache | < 20 ms | {Mean("không cache")} | {Ok(MeanMs("không cache") < 20)} |");
        md.AppendLine($"| Liệt kê thư mục {options.ListFolder:N0} mục, lọc quyền (trang 500 mục, API) | p95 < 300 ms | p50 {Ms(P(list, .5))}, p95 {Ms(P(list, .95))} | {Ok(P(list, .95) < 300)} |");
        md.AppendLine($"| Liệt kê đủ {options.ListFolder:N0} mục (mọi trang) | — | p50 {Ms(P(listAll, .5))}, p95 {Ms(P(listAll, .95))} | — |");
        md.AppendLine($"| Tìm kiếm toàn hệ thống, lọc quyền (API) | p95 < 1 s | p50 {Ms(P(search, .5))}, p95 {Ms(P(search, .95))} | {Ok(P(search, .95) < 1000)} |");
        md.AppendLine($"| Ngắt kế thừa thư mục có {options.BigFolder:N0} mục con (một transaction) | < 10 s | {Ms(breakMs)} | {Ok(breakMs < 10_000)} |");
        md.AppendLine($"| Khôi phục kế thừa thư mục đó | — | {Ms(restoreMs)} | — |");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        await File.WriteAllTextAsync(outFile, md.ToString());
        Console.WriteLine(md.ToString());
        Console.WriteLine($"Đã ghi {Path.GetFullPath(outFile)}");
    }
}
