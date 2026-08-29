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
    /// Relational providers share ONE DbTransaction via UseTransactionAsync; the EF InMemory
    /// test provider has no relational transactions, so each context uses its own (no-op)
    /// transaction there while preserving the same all-or-nothing save sequence.
    /// If either save fails, everything rolls back atomically.
    /// </summary>
    private async Task SaveBothAtomicallyAsync(CancellationToken cancellationToken)
    {
        var useSharedTransaction = _appDbContext.Database.IsRelational();

        await using var appTransaction = await _appDbContext.Database.BeginTransactionAsync(cancellationToken);
        await using var tenantTransaction = useSharedTransaction
            ? null
            : await _tenantDbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (useSharedTransaction)
            {
                await _tenantDbContext.Database.UseTransactionAsync(
                    appTransaction.GetDbTransaction(), cancellationToken);
            }

            await _appDbContext.SaveChangesAsync(cancellationToken);
            await _tenantDbContext.SaveChangesAsync(cancellationToken);

            await appTransaction.CommitAsync(cancellationToken);
            if (tenantTransaction is not null)
                await tenantTransaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await appTransaction.RollbackAsync(cancellationToken);
            if (tenantTransaction is not null)
                await tenantTransaction.RollbackAsync(cancellationToken);
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
            // Domain rule: null ValidUpTo = no expiration. The registry stores the MinValue sentinel
            // (non-nullable column); CurrentTenant.ValidUpTo translates it back to null.
            ValidUpTo = tenant.ValidUpTo ?? DateTime.MinValue,
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
        // Domain rule: null ValidUpTo = no expiration → MinValue sentinel in the registry.
        tenantInfo.ValidUpTo = tenant.ValidUpTo ?? DateTime.MinValue;
        tenantInfo.TrialEndsAt = tenant.TrialEndsAt;
    }
}
