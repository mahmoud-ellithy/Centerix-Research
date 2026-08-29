namespace Centerix.Infrastructure.Platform;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Feature entitlement resolution: a tenant "owns" a feature iff it has an ACTIVE (unexpired,
/// not suspended) subscription whose ENTITLEMENT SNAPSHOT (TenantPlanFeatures, copied from the
/// plan at grant time) contains the code. Live Plan rows are never consulted here.
/// </summary>
public class FeatureAccessService(
    IAppDbContext dbContext,
    ISubscriptionStateService subscriptionState) : IFeatureAccessService
{
    public async Task<bool> HasFeatureAsync(string tenantId, string featureCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(featureCode))
            return false;

        var state = await subscriptionState.GetCurrentAsync(tenantId, cancellationToken);
        if (!state.IsActiveAsOfNow || state.SubscriptionId is null)
            return false;

        return await dbContext.TenantPlanFeatures
            .AsNoTracking()
            .AnyAsync(
                f => f.TenantPlanId == state.SubscriptionId.Value
                  && f.FeatureCode == featureCode.Trim(),
                cancellationToken);
    }
}
