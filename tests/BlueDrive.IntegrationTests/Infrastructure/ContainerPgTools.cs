using BlueDrive.Infrastructure.Operations;
using Testcontainers.PostgreSql;

namespace BlueDrive.IntegrationTests.Infrastructure;

/// <summary>
/// pg_dump / pg_restore run inside the Testcontainers PostgreSQL (the test host has no PostgreSQL client tools);
/// the dump file is copied out of / into the container, so the backup and restore code paths stay the production ones.
/// </summary>
public sealed class ContainerPgTools(PostgreSqlContainer pg) : IPgTools
{
    private const string Database = "bluedrive_test", User = "bluedrive";

    /// <summary>stderr of the last pg_restore (harmless "does not exist" notices are expected with --clean).</summary>
    public string LastRestoreOutput { get; private set; } = "";

    public async Task DumpAsync(string path, CancellationToken ct)
    {
        var r = await pg.ExecAsync(["pg_dump", "-U", User, "-d", Database, "-Fc", "--no-owner", "-f", "/tmp/bd-backup.dump"], ct);
        if (r.ExitCode != 0) throw new IOException($"pg_dump lỗi: {r.Stderr}");
        await File.WriteAllBytesAsync(path, await pg.ReadFileAsync("/tmp/bd-backup.dump", ct), ct);
    }

    public async Task RestoreAsync(string path, CancellationToken ct)
    {
        await pg.CopyAsync(await File.ReadAllBytesAsync(path, ct), "/tmp/bd-restore.dump", ct: ct);
        var r = await pg.ExecAsync(["pg_restore", "-U", User, "-d", Database, "--clean", "--if-exists", "--no-owner", "/tmp/bd-restore.dump"], ct);
        LastRestoreOutput = r.Stderr;
    }
}
