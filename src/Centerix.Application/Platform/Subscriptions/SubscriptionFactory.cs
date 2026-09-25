namespace Centerix.Application.Platform.Subscriptions;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Builds a fully-snapshotted TenantPlan from Contract snapshots.
/// 
/// The ONLY allowed creation path for commercial Contract→Subscription workflows is via
/// SubscriptionSnapshot — all commercial terms, limits, and features come from the snapshot,
/// NOT from the Plan catalog.
/// 
/// Key invariant (prompt section #12):
///   SubscriptionFactory → NO dbContext.Plans
///   SubscriptionFactory → NO dbContext.PlanFeatures
///   SubscriptionFactory → NO dbContext.Features
/// 
/// The PlanId is retained as historical/reference identity only.
/// </summary>
public interface ISubscriptionFactory
{
    /// <summary>
    /// Creates a subscription exclusively from a Contract snapshot.
    /// No Plan queries — all commercial terms, limits, and features come from the snapshot.
    /// The PlanId is stored for reference, but no catalog data is read.
    /// Used by: CreateSubscriptionFromContractCommand, RenewSubscriptionOfferCommand, ChangeSubscriptionPlanCommand.
    /// </summary>
    Task<Result<TenantPlan>> CreateFromSnapshotAsync(
        string tenantId,
        int planId,
        SubscriptionSnapshot snapshot,
        DateTime startsAtUtc,
        bool autoRenew,
        bool activate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates an activated subscription from the current Plan catalog (initial assignment path).
    /// Commercial terms are derived from the Plan — this is the only path that reads Plan catalog.
    /// Used by: AssignPlanCommand, ApproveTenantCommand (platform-only initial assignment).
    /// </summary>
    Task<Result<TenantPlan>> CreateActivatedAsync(
        string tenantId,
        int planId,
        DateTime startsAtUtc,
        bool autoRenew,
        CancellationToken cancellationToken);
}

public class SubscriptionFactory(IAppDbContext dbContext) : ISubscriptionFactory
{
    /// <summary>
    /// Creates a subscription exclusively from the Contract snapshot.
    /// No Plan queries for commercial terms, limits, or features (per prompt section #12).
    /// </summary>
    public async Task<Result<TenantPlan>> CreateFromSnapshotAsync(
        string tenantId,
        int planId,
        SubscriptionSnapshot snapshot,
        DateTime startsAtUtc,
        bool autoRenew,
        bool activate,
        CancellationToken cancellationToken)
    {
        var createResult = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId,
            planId,
            snapshot.MonthlyListPrice,
            snapshot.MonthlyCharge,
            snapshot.CurrencyCode,
            snapshot.DurationMonths,
            snapshot.BonusMonths,
            startsAtUtc,
            autoRenew,
            SubscriptionStatus.Pending,
            snapshot.MaxStudents,
            snapshot.MaxUsers,
            snapshot.MaxBranches,
            snapshot.MaxTeachers,
            snapshot.StorageGb,
            snapshot.SmsQuota);

        if (!createResult.IsSuccess)
            return createResult.Errors!;

        var subscription = createResult.Value;

        // Feature codes come from the snapshot, not the Plan catalog (per prompt section #12)
        foreach (var featureCode in snapshot.FeatureCodes)
        {
            var grant = subscription.GrantFeature(featureCode);
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

    /// <summary>
    /// Creates an activated subscription from the current Plan catalog.
    /// This is the ONLY path that reads Plan catalog data.
    /// Reserved for initial plan assignment (AssignPlanCommand, ApproveTenantCommand).
    /// </summary>
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

        // For direct Plan assignments (no Contract), SnapshotMonthlyCharge = plan.MonthlyPrice (no discount applied)
        var createResult = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId,
            plan.Id,
            plan.MonthlyPrice,
            plan.MonthlyPrice, // SnapshotMonthlyCharge = plan.MonthlyPrice for direct assignments
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
}
