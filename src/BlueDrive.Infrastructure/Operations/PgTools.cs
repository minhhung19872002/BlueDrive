using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BlueDrive.Infrastructure.Operations;

/// <summary>Database dump / restore used by backups (pg_dump custom format, pg_restore --clean).</summary>
public interface IPgTools
{
    Task DumpAsync(string path, CancellationToken ct);
    Task RestoreAsync(string path, CancellationToken ct);
}

/// <summary>Runs the pg_dump / pg_restore binaries installed next to the worker (deploy/worker image).</summary>
public sealed class ProcessPgTools(IOptions<BackupOptions> options, IConfiguration config, ILogger<ProcessPgTools> logger) : IPgTools
{
    public async Task DumpAsync(string path, CancellationToken ct)
    {
        var (code, err) = await RunAsync(options.Value.PgDump, ["-Fc", "--no-owner", "-f", path], ct);
        if (code != 0) throw new IOException($"pg_dump lỗi: {err}");
    }

    public async Task RestoreAsync(string path, CancellationToken ct)
    {
        // pg_restore --clean reports harmless errors (objects that did not exist yet) with a non-zero exit code.
        var (code, err) = await RunAsync(options.Value.PgRestore, ["--clean", "--if-exists", "--no-owner", path], ct);
        if (code != 0) logger.LogWarning("pg_restore cảnh báo: {Err}", err);
    }

    private async Task<(int Code, string Error)> RunAsync(string exe, string[] extra, CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Default"));
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-h", cs.Host ?? "localhost", "-p", cs.Port.ToString(), "-U", cs.Username ?? "", "-d", cs.Database ?? "" }.Concat(extra))
            psi.ArgumentList.Add(a);
        psi.Environment["PGPASSWORD"] = cs.Password ?? "";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Không chạy được {exe}.");
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, err.Trim());
    }
}
