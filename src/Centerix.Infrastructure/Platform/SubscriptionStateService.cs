namespace Centerix.Infrastructure.Platform;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Resolves the tenant's effective subscription state with LAZY expiration: the persisted status
/// may lag reality, so access decisions compare EffectiveEndsAtUtc with the current instant.
/// When expiration is detected on an Active row, the transition is written through (best-effort)
/// so reporting converges without requiring a background job.
/// </summary>
public class SubscriptionStateService(IAppDbContext dbContext) : ISubscriptionStateService
{
    public async Task<SubscriptionStateInfo> GetCurrentAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new SubscriptionStateInfo(null, null, null, false);

        // Explicit filter bypass: callers pass an explicit tenant id (platform staff or the
        // already-authorized tenant context); the global query filter would fail-closed for
        // platform-scoped requests that legitimately operate cross-tenant.
        var subscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
            return new SubscriptionStateInfo(null, null, null, false);

        var now = DateTime.UtcNow;
        var isActiveNow =
            subscription.Status == SubscriptionStatus.Active && now < subscription.EffectiveEndsAtUtc;

        if (subscription.Status == SubscriptionStatus.Active && !isActiveNow)
        {
            try
            {
                subscription.MarkExpired(now);
                await dbContext.SaveChangesAsync(cancellationToken);
                // Detach the expired entity so subsequent SaveChanges calls in the same scope
                // (e.g. the handler persisting its own aggregate) don't attempt a redundant
                // update against the already-persisted row on the InMemory test provider.
                if (dbContext is Microsoft.EntityFrameworkCore.DbContext concrete)
                    concrete.Entry(subscription).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
            catch
            {
                // Write-through convergence only; denial already decided by the date comparison.
            }
        }

        return new SubscriptionStateInfo(
            subscription.Id,
            subscription.Status,
            subscription.EffectiveEndsAtUtc,
            isActiveNow);
    }
}
