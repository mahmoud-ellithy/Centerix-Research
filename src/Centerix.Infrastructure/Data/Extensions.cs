using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Centerix.Infrastructure.Data;

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
}