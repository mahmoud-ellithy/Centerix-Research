using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Centerix.Infrastructure.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> commands can construct <see cref="AppDbContext"/>
/// without booting the application host (which requires runtime-only services such as
/// <c>IMediator</c> and <c>ICurrentTenant</c>). Runtime behavior is unaffected.
/// The connection string is resolved from (in order): the CENTERIX_DB_CONNECTION environment
/// variable, the API project's appsettings.json, or the default local development connection.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CENTERIX_DB_CONNECTION")
            ?? ReadConnectionStringFromApiAppSettings()
            ?? "Server=.;Database=CenterixDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True;Encrypt=False";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        // IMediator and ICurrentTenant are only used at runtime (domain event dispatch and the
        // tenant query filter). The model builder reads ICurrentTenant.TenantId lazily inside a
        // lambda, never during model creation, so null is safe for design-time tooling.
        return new AppDbContext(optionsBuilder.Options, null!, null!);
    }

    private static string? ReadConnectionStringFromApiAppSettings()
    {
        var appSettingsPath = FindFileUpward("Centerix.API", "appsettings.json");
        if (appSettingsPath is null)
        {
            return null;
        }

        return new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(appSettingsPath)!)
            .AddJsonFile(Path.GetFileName(appSettingsPath), optional: false)
            .Build()
            .GetConnectionString("DefaultConnection");
    }

    private static string? FindFileUpward(string projectFolder, string fileName)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", projectFolder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Also support running with the solution folder itself as CWD root variants.
            candidate = Path.Combine(dir.FullName, projectFolder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
