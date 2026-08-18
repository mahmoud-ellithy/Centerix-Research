using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Centerix.Infrastructure.Data.Interceptors;

/// <summary>
/// Stamps the AUTHORIZED tenant id (from <see cref="ICurrentTenant"/>) onto newly added
/// <see cref="IHasTenantId"/> entities. It deliberately reads the verified tenant context, not the
/// client-resolved Finbuckle tenant, so rows are never written under an unverified tenant.
/// </summary>
public class TenantInterceptor(ICurrentTenant currentTenant) : SaveChangesInterceptor
{
    private readonly ICurrentTenant _currentTenant = currentTenant;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        SetTenantId(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        SetTenantId(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void SetTenantId(DbContext? context)
    {
        if (context == null)
        {
            return;
        }

        // Verified tenant context. Empty until authorized => nothing is stamped (fail-closed).
        var tenantId = _currentTenant.TenantId;

        if (string.IsNullOrEmpty(tenantId))
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<IHasTenantId>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(IHasTenantId.TenantId)).CurrentValue = tenantId;
            }
        }
    }
}
