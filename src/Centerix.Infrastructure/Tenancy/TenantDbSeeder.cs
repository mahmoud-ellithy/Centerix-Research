using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Centerix.Infrastructure.Tenancy;

public class TenantDbSeeder(
    TenantDbContext tenantDbContext,
    IServiceProvider serviceProvider,
    IOptions<Data.DatabaseInitializationOptions> dbInitOptions) : ITenantDbSeeder
{
    private readonly TenantDbContext _tenantDbContext = tenantDbContext;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly Data.DatabaseInitializationOptions _options = dbInitOptions.Value;

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        // NEW-2: honor DatabaseInitialization:ApplyMigrations. The orchestrator
        // (Extensions.RunDatabaseInitializationAsync) already migrated the registry
        // first; this guard keeps verify-only mode (ApplyMigrations=false) honest.
        if (_options.ApplyMigrations)
            await _tenantDbContext.Database.MigrateAsync(cancellationToken);

        await InitializeRootTenantAsync(cancellationToken);

        // Required per-tenant seed runs only when DatabaseInitialization:Seed is set.
        if (!_options.Seed)
            return;

        foreach (var tenant in await _tenantDbContext.TenantInfo.ToListAsync(cancellationToken))
        {
            await InitializeApplicationDbForTenantAsync(tenant, cancellationToken);
        }
    }

    private async Task InitializeRootTenantAsync(CancellationToken cancellationToken)
    {
        var rootRegistryId = TenancyConstants.Root.GuidId.ToString();
        var existingRegistry = await _tenantDbContext.TenantInfo.FindAsync([rootRegistryId], cancellationToken);

        if (existingRegistry is null)
        {
            var rootTenant = new CenterixTenantInfo
            {
                Id = rootRegistryId,
                Identifier = rootRegistryId,
                Name = TenancyConstants.Root.Name,
                Email = TenancyConstants.Root.Email,
                FirstName = TenancyConstants.FirstName,
                LastName = TenancyConstants.LastName,
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(2),
                CreatedAt = DateTime.UtcNow
            };

            await _tenantDbContext.TenantInfo.AddAsync(rootTenant, cancellationToken);
            await _tenantDbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task InitializeApplicationDbForTenantAsync(CenterixTenantInfo currentTenant, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<CenterixTenantInfo>
            {
                TenantInfo = currentTenant
            };

        var initialiser = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitialiser>();
        if (_options.ApplyMigrations)
            await initialiser.InitialiseAsync();
        if (_options.Seed)
            await initialiser.SeedAsync();
    }
}
