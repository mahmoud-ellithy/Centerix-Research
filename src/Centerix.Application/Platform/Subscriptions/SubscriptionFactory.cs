namespace Centerix.Application.Platform.Subscriptions;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Builds a fully-snapshotted, ACTIVATED TenantPlan from an active Plan:
/// commercial terms + limits + feature entitlement are copied at creation time so later
/// plan changes never alter existing grants. Shared by the ApproveTenant and AssignPlan
/// workflows; callers own persistence, tenant lifecycle and audit.
/// </summary>
public interface ISubscriptionFactory
{
    /// <summary>
    /// Creates an activated subscription from the current Plan catalog (legacy/initial assignment path).
    /// Commercial terms are derived from the Plan.
    /// </summary>
    Task<Result<TenantPlan>> CreateActivatedAsync(
        string tenantId,
        int planId,
        DateTime startsAtUtc,
        bool autoRenew,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates an activated subscription from an explicit commercial snapshot (renewal path).
    /// All commercial values come from the accepted Offer/Contract — NOT from the Plan catalog.
    /// Features are still resolved from the current Plan catalog.
    /// When <paramref name="activate"/> is false, the subscription remains Pending (used for
    /// future-starting subscriptions that coexist with an Active old subscription).
    /// </summary>
    Task<Result<TenantPlan>> CreateFromSnapshotAsync(
        string tenantId,
        int planId,
        decimal snapshotPrice,
        string snapshotCurrency,
        int durationMonths,
        int bonusMonths,
        DateTime startsAtUtc,
        bool autoRenew,
        bool activate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates an activated subscription from an explicit commercial snapshot (renewal path).
    /// All commercial values come from the accepted Offer/Contract — NOT from the Plan catalog.
    /// Features are still resolved from the current Plan catalog.
    /// </summary>
    Task<Result<TenantPlan>> CreateFromSnapshotAsync(
        string tenantId,
        int planId,
        decimal snapshotPrice,
        string snapshotCurrency,
        int durationMonths,
        int bonusMonths,
        DateTime startsAtUtc,
        bool autoRenew,
        CancellationToken cancellationToken);
}

public class SubscriptionFactory(IAppDbContext dbContext) : ISubscriptionFactory
{
    public async Task<Result<TenantPlan>> CreateActivatedAsync(
        string tenantId,
        int planId,
        DateTime startsAtUtc,
        bool autoRenew,
        CancellationToken cancellationToken)
    {
        // Global catalog — no tenant filter applies to Plans.
        var plan = await dbContext.Plans
            .Include(p => p.PlanFeatures)
            .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

        if (plan is null)
            return Error.NotFound("Subscription.PlanNotFound", $"Plan '{planId}' was not found.");

        if (!plan.IsActive)
            return Error.Conflict("Subscription.PlanNotActive", "The selected plan is not active and cannot be assigned.");

        var createResult = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId,
            plan.Id,
            plan.MonthlyPrice,
            plan.CurrencyCode,
            plan.DurationMonths,
            plan.BonusMonths,
            startsAtUtc,
            autoRenew,
            SubscriptionStatus.Pending,
            plan.MaxStudents,
            plan.MaxUsers,
            plan.MaxBranches,
            plan.MaxTeachers,
            plan.StorageGB,
            plan.SMSQuota);

        if (!createResult.IsSuccess)
            return createResult.Errors!;

        var subscription = createResult.Value;

        // Snapshot the entitlement: every ENABLED plan feature code is copied onto the grant.
        foreach (var pf in plan.PlanFeatures.Where(f => f.IsEnabled))
        {
            var feature = await dbContext.Features
                .AsNoTracking()
                .Where(f => f.Id == pf.FeatureId)
                .Select(f => f.Code)
                .FirstOrDefaultAsync(cancellationToken);

            if (feature is null)
                continue; // Catalog row vanished; entitlement simply not granted.

            var grant = subscription.GrantFeature(feature);
            if (!grant.IsSuccess)
                return grant.Errors!;
        }

        var activation = subscription.Activate(startsAtUtc);
        if (!activation.IsSuccess)
            return activation.Errors!;

        return subscription;
    }

    public async Task<Result<TenantPlan>> CreateFromSnapshotAsync(
        string tenantId,
        int planId,
        decimal snapshotPrice,
        string snapshotCurrency,
        int durationMonths,
        int bonusMonths,
        DateTime startsAtUtc,
        bool autoRenew,
        CancellationToken cancellationToken)
        => await CreateFromSnapshotAsync(tenantId, planId, snapshotPrice, snapshotCurrency,
            durationMonths, bonusMonths, startsAtUtc, autoRenew, activate: true, cancellationToken);

    public async Task<Result<TenantPlan>> CreateFromSnapshotAsync(
        string tenantId,
        int planId,
        decimal snapshotPrice,
        string snapshotCurrency,
        int durationMonths,
        int bonusMonths,
        DateTime startsAtUtc,
        bool autoRenew,
        bool activate,
        CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans
            .Include(p => p.PlanFeatures)
            .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

        if (plan is null)
            return Error.NotFound("Subscription.PlanNotFound", $"Plan '{planId}' was not found.");

        // NOTE: CreateFromSnapshotAsync intentionally does NOT require the Plan to be Active.
        // When creating a subscription from an accepted Contract/Offer, the commercial terms
        // are authoritative regardless of the Plan catalog's current status. A Plan may be
        // deactivated after the Contract was signed; the Contract's terms must still be honored.

        var createResult = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId,
            plan.Id,
            snapshotPrice,
            snapshotCurrency,
            durationMonths,
            bonusMonths,
            startsAtUtc,
            autoRenew,
            SubscriptionStatus.Pending,
            plan.MaxStudents,
            plan.MaxUsers,
            plan.MaxBranches,
            plan.MaxTeachers,
            plan.StorageGB,
            plan.SMSQuota);

        if (!createResult.IsSuccess)
            return createResult.Errors!;

        var subscription = createResult.Value;

        foreach (var pf in plan.PlanFeatures.Where(f => f.IsEnabled))
        {
            var feature = await dbContext.Features
                .AsNoTracking()
                .Where(f => f.Id == pf.FeatureId)
                .Select(f => f.Code)
                .FirstOrDefaultAsync(cancellationToken);

            if (feature is null)
                continue;

            var grant = subscription.GrantFeature(feature);
            if (!grant.IsSuccess)
                return grant.Errors!;
        }

        if (activate)
        {
            var activation = subscription.Activate(startsAtUtc);
            if (!activation.IsSuccess)
                return activation.Errors!;
        }

        return subscription;
    }
}
