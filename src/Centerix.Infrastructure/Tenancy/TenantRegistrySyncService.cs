using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Centerix.Infrastructure.Tenancy;

/// <summary>
/// Synchronizes the canonical Platform.Tenants state to the derived
/// Finbuckle TenantRegistry projection. Each sync method atomically
/// saves both AppDbContext and TenantDbContext within a single database
/// transaction using UseTransactionAsync to share the connection.
/// </summary>
public class TenantRegistrySyncService(
    AppDbContext appDbContext,
    TenantDbContext tenantDbContext,
    ILogger<TenantRegistrySyncService> logger) : ITenantRegistrySync
{
    private readonly AppDbContext _appDbContext = appDbContext;
    private readonly TenantDbContext _tenantDbContext = tenantDbContext;
    private readonly ILogger<TenantRegistrySyncService> _logger = logger;

    public async Task SyncCreatedAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        var existing = await _tenantDbContext.TenantInfo.FindAsync(
            [tenant.Id.ToString()], cancellationToken);

        if (existing is not null)
        {
            _logger.LogWarning(
                "TenantRegistry already contains entry for Tenant {TenantId}. Skipping creation.",
                tenant.Id);
            return;
        }

        var tenantInfo = MapToTenantInfo(tenant);

        _tenantDbContext.TenantInfo.Add(tenantInfo);

        await SaveBothAtomicallyAsync(cancellationToken);
    }

    public async Task SyncLifecycleAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        var tenantInfo = await _tenantDbContext.TenantInfo.FindAsync(
            [tenant.Id.ToString()], cancellationToken);

        if (tenantInfo is null)
        {
            _logger.LogError(
                "TenantRegistry has no entry for Tenant {TenantId}. Cannot sync lifecycle.",
                tenant.Id);
            return;
        }

        ApplyLifecycleToTenantInfo(tenant, tenantInfo);

        await SaveBothAtomicallyAsync(cancellationToken);
    }

    public async Task SyncMetadataAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        var tenantInfo = await _tenantDbContext.TenantInfo.FindAsync(
            [tenant.Id.ToString()], cancellationToken);

        if (tenantInfo is null)
        {
            _logger.LogError(
                "TenantRegistry has no entry for Tenant {TenantId}. Cannot sync metadata.",
                tenant.Id);
            return;
        }

        tenantInfo.Name = tenant.DisplayName;
        tenantInfo.DisplayName = tenant.DisplayName;
        tenantInfo.LogoUrl = tenant.LogoUrl;
        tenantInfo.PrimaryColor = tenant.PrimaryColor;

        await SaveBothAtomicallyAsync(cancellationToken);
    }

    /// <summary>
    /// Saves both AppDbContext and TenantDbContext within a single database transaction.
    /// Uses BeginTransactionAsync on AppDbContext's connection, then enlists TenantDbContext
    /// via UseTransactionAsync so both contexts share the same DbConnection and DbTransaction.
    /// If either save fails, both are rolled back atomically.
    /// </summary>
    private async Task SaveBothAtomicallyAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await _appDbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await _tenantDbContext.Database.UseTransactionAsync(
                transaction.GetDbTransaction(), cancellationToken);

            await _appDbContext.SaveChangesAsync(cancellationToken);
            await _tenantDbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static CenterixTenantInfo MapToTenantInfo(Tenant tenant)
    {
        return new CenterixTenantInfo
        {
            Id = tenant.Id.ToString(),
            Identifier = tenant.Id.ToString(),
            Name = tenant.DisplayName,
            Slug = tenant.Slug,
            Subdomain = tenant.Subdomain,
            DisplayName = tenant.DisplayName,
            LogoUrl = tenant.LogoUrl,
            PrimaryColor = tenant.PrimaryColor,
            Country = tenant.Country,
            Currency = tenant.Currency,
            Timezone = tenant.Timezone,
            ConnectionString = tenant.ConnectionStringRef,
            Email = tenant.OwnerEmail,
            FirstName = tenant.OwnerFirstName,
            LastName = tenant.OwnerLastName,
            IsActive = tenant.IsActive,
            ValidUpTo = tenant.ValidUpTo ?? DateTime.UtcNow.AddMonths(1),
            Status = (byte)tenant.LifecycleStatus,
            TrialEndsAt = tenant.TrialEndsAt,
            CreatedAt = tenant.CreatedAtUtc.UtcDateTime
        };
    }

    private static void ApplyLifecycleToTenantInfo(Tenant tenant, CenterixTenantInfo tenantInfo)
    {
        tenantInfo.IsActive = tenant.IsActive;
        tenantInfo.Status = (byte)tenant.LifecycleStatus;
        tenantInfo.Name = tenant.DisplayName;
        tenantInfo.Slug = tenant.Slug;
        tenantInfo.Subdomain = tenant.Subdomain;
        tenantInfo.DisplayName = tenant.DisplayName;
        tenantInfo.LogoUrl = tenant.LogoUrl;
        tenantInfo.PrimaryColor = tenant.PrimaryColor;
        tenantInfo.Country = tenant.Country;
        tenantInfo.Currency = tenant.Currency;
        tenantInfo.Timezone = tenant.Timezone;
        tenantInfo.ValidUpTo = tenant.ValidUpTo ?? tenantInfo.ValidUpTo;
        tenantInfo.TrialEndsAt = tenant.TrialEndsAt;
    }
}
