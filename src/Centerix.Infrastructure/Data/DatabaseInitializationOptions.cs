namespace Centerix.Infrastructure.Data;

/// <summary>
/// Configuration contract for production database initialization (NEW-2).
/// Section: "DatabaseInitialization".
/// All flags are explicit so production startup behavior is auditable:
/// ApplyMigrations runs pending EF Core migrations, Seed writes required
/// deterministic/idempotent seed data, ValidateSchema fails startup when
/// pending migrations remain or the database is unreachable.
/// SeedDevelopmentData gates development-only/bootstrap data (e.g. bootstrap
/// admin users with temporary passwords) and MUST stay false in production
/// unless explicitly configured.
/// </summary>
public sealed class DatabaseInitializationOptions
{
    public const string SectionName = "DatabaseInitialization";

    /// <summary>Apply pending EF Core migrations at startup. Default true.</summary>
    public bool ApplyMigrations { get; set; } = true;

    /// <summary>Write required seed data (permissions, roles, root tenant row, subscription policy). Default true.</summary>
    public bool Seed { get; set; } = true;

    /// <summary>Fail startup when pending migrations remain or the DB is unreachable. Default true.</summary>
    public bool ValidateSchema { get; set; } = true;

    /// <summary>
    /// Gate for development-only/bootstrap data. Default false: production never
    /// seeds bootstrap credentials unless an operator explicitly opts in.
    /// </summary>
    public bool SeedDevelopmentData { get; set; } = false;

    public void Validate()
    {
        // No cross-field invariant to enforce: every combination is meaningful
        // (e.g. ValidateSchema=true + ApplyMigrations=false = verify-only mode).
    }
}
