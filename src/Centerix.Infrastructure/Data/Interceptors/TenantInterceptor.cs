using Centerix.Domain.Common;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Centerix.Infrastructure.Data.Interceptors;

public class TenantInterceptor(IMultiTenantContextAccessor<CenterixTenantInfo> multiTenantContextAccessor) : SaveChangesInterceptor
{
    private readonly IMultiTenantContextAccessor<CenterixTenantInfo> _multiTenantContextAccessor = multiTenantContextAccessor;

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

        var tenantId = _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.Id;

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