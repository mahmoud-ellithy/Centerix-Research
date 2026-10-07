using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Centerix.Infrastructure.Data;

/// <summary>
/// NEW-2 production database initialization.
/// Ordering is load-bearing: the tenant registry (TenantDbContext) migrates FIRST
/// because it is the source of the tenant list; the application database
/// (AppDbContext) migrates SECOND; per-tenant required seed runs LAST.
/// ValidateSchema fails startup when pending migrations remain on EITHER context
/// or when the database is unreachable. Failures are never swallowed: they are
/// logged and rethrown so the host does not start against an unmigrated schema.
/// </summary>
public static class Extensions
{
    public static async Task InitialiseDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        using var scope = app.Services.CreateScope();

        var initialiser = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitialiser>();

        await initialiser.InitialiseAsync(cancellationToken);
    }

    public static async Task InitialiseTenantDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        using var scope = app.Services.CreateScope();

        var tenantDbSeeder = scope.ServiceProvider.GetRequiredService<ITenantDbSeeder>();

        await tenantDbSeeder.InitializeDatabaseAsync(cancellationToken);
    }

    /// <summary>
    /// Runs the configured production initialization pipeline (NEW-2). Honors
    /// DatabaseInitialization: ApplyMigrations / Seed / ValidateSchema. Skipped
    /// entirely in the Testing environment (the test host manages its own stores).
    /// </summary>
    public static async Task RunDatabaseInitializationAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var env = app.Services.GetRequiredService<IHostEnvironment>();
        if (env.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
            return;

        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitialization");
        var options = sp.GetRequiredService<IOptions<DatabaseInitializationOptions>>().Value;

        try
        {
            // ORDER 1: tenant registry first (source of the tenant list).
            if (options.ApplyMigrations)
            {
                var tenantDb = sp.GetRequiredService<TenantDbContext>();
                await tenantDb.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Tenant registry migrations applied.");
            }

            // ORDER 2: application database second.
            if (options.ApplyMigrations)
            {
                var initialiser = sp.GetRequiredService<ApplicationDbContextInitialiser>();
                await initialiser.InitialiseAsync(cancellationToken);
                logger.LogInformation("Application database migrations applied.");
            }

            // ORDER 3: required seed last (also seeds the root registry row +
            // per-tenant required data via the tenant seeder).
            if (options.Seed)
            {
                var tenantDbSeeder = sp.GetRequiredService<ITenantDbSeeder>();
                await tenantDbSeeder.InitializeDatabaseAsync(cancellationToken);
                logger.LogInformation("Required database seed completed.");
            }

            // Validation: unreachable DB or pending migrations fail startup.
            if (options.ValidateSchema)
                await ValidateSchemaAsync(sp, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never silently swallow initialization failures: log loudly and fail startup.
            logger.LogCritical(ex, "Database initialization failed. Startup aborted.");
            throw;
        }
    }

    private static async Task ValidateSchemaAsync(IServiceProvider sp, ILogger logger, CancellationToken cancellationToken)
    {
        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        var appDb = sp.GetRequiredService<AppDbContext>();

        if (!await tenantDb.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException(
                "DatabaseInitialization: database is unreachable (TenantDbContext.CanConnectAsync returned false). " +
                "Check ConnectionStrings:DefaultConnection and SQL Server availability.");

        var tenantPending = await tenantDb.Database.GetPendingMigrationsAsync(cancellationToken);
        var appPending = await appDb.Database.GetPendingMigrationsAsync(cancellationToken);

        var pendingTenant = tenantPending.ToList();
        var pendingApp = appPending.ToList();

        if (pendingTenant.Count > 0 || pendingApp.Count > 0)
            throw new InvalidOperationException(
                "DatabaseInitialization: pending migrations remain after initialization. " +
                $"TenantDbContext pending: [{string.Join(", ", pendingTenant)}]. " +
                $"AppDbContext pending: [{string.Join(", ", pendingApp)}]. " +
                "Enable DatabaseInitialization:ApplyMigrations or apply migrations before starting.");

        logger.LogInformation("Database schema validation passed (no pending migrations on either context).");
    }
}
