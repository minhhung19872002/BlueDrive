namespace BlueDrive.Infrastructure.Persistence;

/// <summary>Seeds reference data at startup (idempotent).</summary>
public interface IDataSeeder
{
    int Order { get; }
    Task SeedAsync(CancellationToken ct = default);
}
