using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Turns PostgreSQL JIT off for the application database. Searches over a common term (e.g. "dân số" in 40 % of the files)
    /// get a cost estimate above jit_above_cost, and compiling the plan took ~350 ms of a ~550 ms query on every execution;
    /// without JIT the same query takes ~140 ms (load test T1, docs/bao-cao-kiem-thu-tai.md). Applies to new connections.
    /// </summary>
    public partial class DisableJit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN EXECUTE format('ALTER DATABASE %I SET jit = off', current_database()); END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN EXECUTE format('ALTER DATABASE %I RESET jit', current_database()); END $$;");
        }
    }
}
