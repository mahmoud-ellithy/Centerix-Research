namespace Centerix.Infrastructure.Platform;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves the tenant's effective subscription state with LAZY expiration AND
/// FINANCIAL RECONCILIATION: the persisted status may lag reality, so access decisions
/// compare EffectiveEndsAtUtc with the current instant. When expiration or financial
/// state drift is detected on a row, the transition is written through (best-effort)
/// so reporting converges without requiring a background job.
/// </summary>
public class SubscriptionStateService(
    IAppDbContext dbContext,
    ISubscriptionReconciliationService reconciliationService,
    TimeProvider timeProvider,
    ILogger<SubscriptionStateService> logger) : ISubscriptionStateService
{
    public async Task<SubscriptionStateInfo> GetCurrentAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new SubscriptionStateInfo(null, null, null, false);

        // Trigger reconciliation for lazy convergence of financial state (PastDue/Suspended)
        // and natural expiration (Active/PastDue/Suspended → Expired).
        // This is idempotent — running it multiple times produces the same result.
        await reconciliationService.ReconcileAsync(tenantId, cancellationToken);

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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var isActiveNow =
            subscription.Status == SubscriptionStatus.Active && now < subscription.EffectiveEndsAtUtc;

        return new SubscriptionStateInfo(
            subscription.Id,
            subscription.Status,
            subscription.EffectiveEndsAtUtc,
            isActiveNow);
    }
}
