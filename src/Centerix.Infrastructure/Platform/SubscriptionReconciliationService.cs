namespace Centerix.Infrastructure.Platform;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Deterministic, idempotent reconciliation of Subscription state from financial obligations.
/// PastDue and Suspended are system-derived — no administrator can set them directly.
/// </summary>
public class SubscriptionReconciliationService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<SubscriptionReconciliationService> logger) : ISubscriptionReconciliationService
{
    public async Task ReconcileAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var subscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
            return;

        // Terminal states: no reconciliation needed
        if (subscription.Status is SubscriptionStatus.Expired or SubscriptionStatus.Cancelled)
            return;

        // Pending: no financial reconciliation needed
        if (subscription.Status == SubscriptionStatus.Pending)
            return;

        // Lazy expiration: Active subscription past its effective end date
        if (subscription.Status == SubscriptionStatus.Active && now >= subscription.EffectiveEndsAtUtc)
        {
            try
            {
                subscription.MarkExpired(now);
                await dbContext.SaveChangesAsync(cancellationToken);
                DetachEntity(subscription);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to mark subscription {SubscriptionId} as expired", subscription.Id);
            }
            return;
        }

        // No contract: no installments to check for financial reconciliation
        if (subscription.ContractId is null)
            return;

        // Load installments for this subscription's contract
        var installments = await dbContext.Installments
            .Where(i => i.ContractId == subscription.ContractId.Value)
            .ToListAsync(cancellationToken);

        var hasOverdue = installments.Any(i => i.IsOverdue(now));

        if (!hasOverdue)
        {
            // No overdue obligations: recover to Active if currently PastDue or Suspended
            if (subscription.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended)
            {
                var recoveryResult = subscription.ReactivateFromFinancialRecovery(now);
                if (recoveryResult.IsSuccess)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    DetachEntity(subscription);
                    logger.LogInformation(
                        "Subscription {SubscriptionId} recovered to Active from {PreviousStatus} after settlement",
                        subscription.Id, subscription.Status);
                }
            }
            return;
        }

        // There are overdue installments
        var oldestOverdue = installments
            .Where(i => i.IsOverdue(now))
            .OrderBy(i => i.DueDateUtc)
            .First();

        var overdueDuration = now - oldestOverdue.DueDateUtc;
        var gracePeriodDays = await GetGracePeriodDaysAsync(cancellationToken);
        var gracePeriod = TimeSpan.FromDays(gracePeriodDays);

        if (overdueDuration <= gracePeriod)
        {
            // Within grace period: PastDue (only from Active)
            if (subscription.Status == SubscriptionStatus.Active)
            {
                var pastDueResult = subscription.MarkPastDue();
                if (pastDueResult.IsSuccess)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    DetachEntity(subscription);
                    logger.LogInformation(
                        "Subscription {SubscriptionId} transitioned to PastDue (overdue {OverdueDays} days, grace period {GraceDays} days)",
                        subscription.Id, (int)overdueDuration.TotalDays, gracePeriodDays);
                }
            }
        }
        else
        {
            // Past grace period: Suspended (from Active or PastDue)
            if (subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue)
            {
                var suspendResult = subscription.SuspendFromObligation();
                if (suspendResult.IsSuccess)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    DetachEntity(subscription);
                    logger.LogInformation(
                        "Subscription {SubscriptionId} transitioned to Suspended (overdue {OverdueDays} days exceeds grace period {GraceDays} days)",
                        subscription.Id, (int)overdueDuration.TotalDays, gracePeriodDays);
                }
            }
        }
    }

    private async Task<int> GetGracePeriodDaysAsync(CancellationToken cancellationToken)
    {
        var policy = await dbContext.SubscriptionPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        // Default to 7 days if no policy configured
        return policy?.GracePeriodDays ?? 7;
    }

    private void DetachEntity(object entity)
    {
        if (dbContext is Microsoft.EntityFrameworkCore.DbContext concrete)
            concrete.Entry(entity).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }
}
