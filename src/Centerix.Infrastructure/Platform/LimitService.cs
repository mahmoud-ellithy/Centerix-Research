namespace Centerix.Infrastructure.Platform;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Plan-limit enforcement with override precedence:
///   TenantLimitOverride (platform-granted; REPLACES the snapshot limit)
///     → TenantPlan limit SNAPSHOT → fail-closed when undefined.
///
/// Concurrency: capacity reservation is an ATOMIC conditional UPDATE against the tenant's usage
/// counter row inside the caller's ambient transaction — exactly one concurrent caller can claim
/// the last free unit. When no counter row exists the check FAILS CLOSED (usage tracking must be
/// provisioned first); we never guess usage.
/// </summary>
public class LimitService(
    IAppDbContext dbContext,
    ISubscriptionStateService subscriptionState,
    ILogger<LimitService> logger) : ILimitService
{
    public async Task<int?> GetEffectiveMaxAsync(string tenantId, string limitType, CancellationToken cancellationToken = default)
    {
        var state = await subscriptionState.GetCurrentAsync(tenantId, cancellationToken);
        if (!state.IsActiveAsOfNow || state.SubscriptionId is null)
            return null;

        var overrideValue = await dbContext.TenantLimitOverrides
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.LimitType == limitType)
            .Select(o => (int?)o.OverrideValue)
            .FirstOrDefaultAsync(cancellationToken);

        if (overrideValue is not null)
            return overrideValue;

        var subscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(tp => tp.Id == state.SubscriptionId.Value, cancellationToken);

        return subscription.GetSnapshotLimit(limitType);
    }

    public async Task<Result<Updated>> ReserveAsync(string tenantId, string limitType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(limitType))
            return Error.Validation("Limit.Invalid", "Tenant and limit type are required.");

        var state = await subscriptionState.GetCurrentAsync(tenantId, cancellationToken);
        if (!state.IsActiveAsOfNow)
            return Error.Conflict("Subscription.NotActive",
                "The tenant does not have an active subscription.");

        var max = await GetEffectiveMaxAsync(tenantId, limitType, cancellationToken);
        if (max is null)
            return Error.Conflict("Limit.NotDefined",
                $"No '{limitType}' limit is defined for this tenant's subscription.");

        // Atomic reservation (relational): conditional update claims the last free unit or
        // affects no rows. On the EF InMemory test provider ExecuteUpdate is unsupported, so a
        // read-only quota check is used there; true multi-writer behavior is proven against
        // SQL Server by the integration suite.
        if (!dbContext.IsRelational)
        {
            var snapshotRow = await dbContext.TenantUsageCounters
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == Guid.Parse(tenantId), cancellationToken);

            if (snapshotRow is null)
                return Error.Conflict("Limit.TrackingNotProvisioned",
                    "Usage tracking is not provisioned for this tenant.");

            var current = limitType switch
            {
                LimitTypeCodes.Students => snapshotRow.StudentsCount,
                LimitTypeCodes.Users => snapshotRow.UsersCount,
                LimitTypeCodes.Branches => snapshotRow.BranchesCount,
                LimitTypeCodes.Teachers => snapshotRow.TeachersCount,
                _ => -1
            };

            return current >= 0 && current < max.Value
                ? Result.Updated
                : Error.Conflict("Limit.Exceeded",
                    $"The tenant's '{limitType}' limit of {max} has been reached.");
        }

        int claimed;
        switch (limitType)
        {
            case LimitTypeCodes.Students:
                claimed = await dbContext.TenantUsageCounters
                    .Where(c => c.Id == Guid.Parse(tenantId) && c.StudentsCount < max.Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.StudentsCount, c => c.StudentsCount + 1), cancellationToken);
                break;
            case LimitTypeCodes.Users:
                claimed = await dbContext.TenantUsageCounters
                    .Where(c => c.Id == Guid.Parse(tenantId) && c.UsersCount < max.Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsersCount, c => c.UsersCount + 1), cancellationToken);
                break;
            case LimitTypeCodes.Branches:
                claimed = await dbContext.TenantUsageCounters
                    .Where(c => c.Id == Guid.Parse(tenantId) && c.BranchesCount < max.Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.BranchesCount, c => c.BranchesCount + 1), cancellationToken);
                break;
            case LimitTypeCodes.Teachers:
                claimed = await dbContext.TenantUsageCounters
                    .Where(c => c.Id == Guid.Parse(tenantId) && c.TeachersCount < max.Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.TeachersCount, c => c.TeachersCount + 1), cancellationToken);
                break;
            default:
                logger.LogWarning("Limit type {LimitType} has no counter mapping; failing closed", limitType);
                return Error.Conflict("Limit.TrackingNotProvisioned",
                    $"Usage tracking for '{limitType}' is not provisioned.");
        }

        if (claimed == 0)
        {
            // Distinguish "quota full" from "no tracking row provisioned" (fail-closed either way).
            var rowExists = await dbContext.TenantUsageCounters
                .AsNoTracking()
                .AnyAsync(c => c.Id == Guid.Parse(tenantId), cancellationToken);

            return rowExists
                ? Error.Conflict("Limit.Exceeded",
                    $"The tenant's '{limitType}' limit of {max} has been reached.")
                : Error.Conflict("Limit.TrackingNotProvisioned",
                    "Usage tracking is not provisioned for this tenant.");
        }

        return Result.Updated;
    }

    public async Task ReleaseAsync(string tenantId, string limitType, CancellationToken cancellationToken = default)
    {
        if (!dbContext.IsRelational)
            return; // Read-only check mode on the InMemory test provider.

        try
        {
            if (!Guid.TryParse(tenantId, out var tenantGuid))
                return;

            switch (limitType)
            {
                case LimitTypeCodes.Students:
                    await dbContext.TenantUsageCounters
                        .Where(c => c.Id == tenantGuid && c.StudentsCount > 0)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.StudentsCount, c => c.StudentsCount - 1), cancellationToken);
                    break;
                case LimitTypeCodes.Users:
                    await dbContext.TenantUsageCounters
                        .Where(c => c.Id == tenantGuid && c.UsersCount > 0)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsersCount, c => c.UsersCount - 1), cancellationToken);
                    break;
                case LimitTypeCodes.Branches:
                    await dbContext.TenantUsageCounters
                        .Where(c => c.Id == tenantGuid && c.BranchesCount > 0)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.BranchesCount, c => c.BranchesCount - 1), cancellationToken);
                    break;
                case LimitTypeCodes.Teachers:
                    await dbContext.TenantUsageCounters
                        .Where(c => c.Id == tenantGuid && c.TeachersCount > 0)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.TeachersCount, c => c.TeachersCount - 1), cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release limit {LimitType} for tenant {TenantId}", limitType, tenantId);
        }
    }
}
