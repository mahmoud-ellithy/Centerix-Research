using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Centerix.Infrastructure.Tenancy;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> commands can construct <see cref="TenantDbContext"/>
/// without booting the application host. The connection string is resolved the same way as
/// <see cref="Data.AppDbContextFactory"/> (CENTERIX_DB_CONNECTION environment variable, the API
/// project's appsettings.json, or the default local development connection). Runtime behavior
/// is unaffected.
/// </summary>
public class TenantDbContextFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CENTERIX_DB_CONNECTION")
            ?? ReadConnectionStringFromApiAppSettings()
            ?? "Server=.;Database=CenterixDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True;Encrypt=False";

        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();
        optionsBuilder.UseSqlServer(
            connectionString,
            sql => sql.MigrationsHistoryTable("__TenantMigrationsHistory"));

        return new TenantDbContext(optionsBuilder.Options);
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

            candidate = Path.Combine(dir.FullName, projectFolder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
