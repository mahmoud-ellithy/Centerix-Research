namespace Centerix.Application.Platform.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// PLATFORM-ONLY workflow: Renews a subscription as a NEW commercial transaction.
///
/// Unlike the legacy RenewSubscriptionCommand (which appends months to the existing subscription),
/// this command creates a completely new Offer → Contract → Subscription → BillingCycle → Invoice
/// chain using the CURRENT commercial terms (current plan pricing, current promotions, current benefits).
///
/// The old subscription remains immutable. No old promotions, discounts, or benefits are inherited.
///
/// Flow:
///   1. Validate renewal eligibility of old subscription
///   2. Prevent overlapping active subscriptions (deterministic concurrency guard)
///   3. Calculate a fresh Offer using current plan pricing + current promotions
///   4. Accept the Offer
///   5. Create a new Contract from the Offer (new commercial snapshot)
///   6. Create a new Subscription (TenantPlan) from the Contract/Offer snapshot
///   7. Create a new BillingCycle for the subscription period
///   8. Create a new Invoice from the BillingCycle
///   9. Link Contract to previous subscription for traceability
///   10. Audit
/// </summary>
public record RenewSubscriptionOfferCommand(
    Guid SubscriptionId,
    int? PlanId = null,
    int? DurationMonths = null) : IRequest<Result<Guid>>;

public class RenewSubscriptionOfferValidator : AbstractValidator<RenewSubscriptionOfferCommand>
{
    public RenewSubscriptionOfferValidator()
    {
        RuleFor(x => x.SubscriptionId).NotEmpty();
        RuleFor(x => x.PlanId).GreaterThan(0).When(x => x.PlanId.HasValue);
        RuleFor(x => x.DurationMonths).GreaterThan(0).When(x => x.DurationMonths.HasValue);
    }
}

public class RenewSubscriptionOfferHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ISubscriptionFactory subscriptionFactory,
    IPromotionCalculationService promotionCalculationService,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<RenewSubscriptionOfferCommand, Result<Guid>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<Guid>> Handle(RenewSubscriptionOfferCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        for (var attempt = 0; attempt < MaxDeadlockRetries; attempt++)
        {
            try
            {
                return await ExecuteRenewalCoreAsync(request, now, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                if (attempt == MaxDeadlockRetries - 1)
                    throw;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 1205)
            {
                if (attempt == MaxDeadlockRetries - 1)
                    throw;
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), cancellationToken);
            }
        }

        return TenantPlanErrors.ConcurrentRenewalConflict;
    }

    private async Task<Result<Guid>> ExecuteRenewalCoreAsync(
        RenewSubscriptionOfferCommand request,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // ── Step 1: Load the existing subscription ──
        var oldSubscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(tp => tp.Id == request.SubscriptionId, cancellationToken);

        if (oldSubscription is null)
            return Error.NotFound("Subscription.NotFound",
                $"Subscription '{request.SubscriptionId}' was not found.");

        // ── Step 2: Validate renewal eligibility ──
        var eligibilityResult = ValidateRenewalEligibility(oldSubscription);
        if (!eligibilityResult.IsSuccess)
            return eligibilityResult.Errors!;

        // ── Step 3: Deterministic concurrency guard ──
        // On relational providers, wrap the overlap check + entity creation in a
        // SERIALIZABLE transaction so two concurrent renewals for the same subscription
        // are serialized. The single-non-terminal unique index provides the final guard.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            // Re-check eligibility inside the transaction (fresh read under lock)
            if (transaction is not null)
            {
                oldSubscription = await dbContext.TenantPlans
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(tp => tp.Id == request.SubscriptionId, cancellationToken);

                if (oldSubscription is null)
                    return Error.NotFound("Subscription.NotFound",
                        $"Subscription '{request.SubscriptionId}' was not found.");

                eligibilityResult = ValidateRenewalEligibility(oldSubscription);
                if (!eligibilityResult.IsSuccess)
                    return eligibilityResult.Errors!;
            }

            // ── Step 4: Resolve plan and duration (before overlap guard) ──
            var planId = request.PlanId ?? oldSubscription.PlanId;
            var plan = await dbContext.Plans
                .Include(p => p.PricingTiers)
                .Include(p => p.PlanFeatures)
                .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

            if (plan is null)
                return TenantPlanErrors.PlanNotFound;

            if (!plan.IsActive)
                return TenantPlanErrors.PlanInactive;

            var durationMonths = request.DurationMonths ?? plan.DurationMonths;

            // ── Step 5: Compute renewal start date (before overlap guard) ──
            // Active old sub with future end → new starts at old's EffectiveEndsAtUtc
            // Expired old sub → new starts from now
            var startsAt = oldSubscription.Status == SubscriptionStatus.Active &&
                           oldSubscription.EffectiveEndsAtUtc > now
                ? oldSubscription.EffectiveEndsAtUtc
                : now;

            var newEffectiveEndsAt = TenantPlan.AddCalendarMonths(startsAt, durationMonths + plan.BonusMonths);

            // ── Step 6: Temporal overlap guard ──
            // Reject if any non-terminal subscription for this tenant has a service period
            // that overlaps with the proposed new subscription's period.
            // The old subscription is excluded — its EffectiveEndsAtUtc == startsAt means
            // it does NOT overlap (ends exactly when new begins).
            var hasOverlap = await dbContext.TenantPlans
                .IgnoreQueryFilters()
                .AnyAsync(tp =>
                    tp.TenantId == oldSubscription.TenantId &&
                    tp.Id != oldSubscription.Id &&
                    tp.Status != SubscriptionStatus.Expired &&
                    tp.Status != SubscriptionStatus.Cancelled &&
                    tp.StartsAtUtc < newEffectiveEndsAt &&
                    tp.EffectiveEndsAtUtc > startsAt,
                    cancellationToken);

            if (hasOverlap)
                return TenantPlanErrors.OverlappingActiveSubscription;

            // ── Step 7: Calculate fresh Offer using current promotions ──
            var candidatePromotions = await dbContext.Promotions
                .Where(p =>
                    (p.PlanId == 0 || p.PlanId == planId) &&
                    (p.DurationMonths == 0 || p.DurationMonths == durationMonths))
                .ToListAsync(cancellationToken);

            var calculated = promotionCalculationService.Calculate(
                plan, durationMonths, now, candidatePromotions);

            if (!calculated.IsSuccess)
                return calculated.Errors!;

            var calc = calculated.Value;

            // ── Step 8: Persist the Offer as an immutable snapshot ──
            var offerResult = Offer.Create(
                id: Guid.NewGuid(),
                tenantId: oldSubscription.TenantId,
                planId: calc.PlanId,
                durationMonths: calc.DurationMonths,
                baseAmount: calc.BaseAmount,
                discountAmount: calc.DiscountAmount,
                finalAmount: calc.FinalAmount,
                monthlyListPrice: calc.MonthlyListPrice,
                currencyCode: calc.CurrencyCode,
                promotionId: calc.PromotionId,
                promotionName: calc.PromotionName,
                promotionCode: calc.PromotionCode,
                promotionType: calc.PromotionType,
                discountPercentage: calc.DiscountPercentage,
                chargedMonths: calc.ChargedMonths,
                calculatedAtUtc: now,
                expiresAtUtc: now.AddHours(24));

            if (!offerResult.IsSuccess)
                return offerResult.Errors!;

            var offer = offerResult.Value;

            var acceptResult = offer.Accept(now);
            if (!acceptResult.IsSuccess)
                return acceptResult.Errors!;

            // ── Step 9: Create new Contract from the Offer ──
            // The Contract must be temporally aligned with the Subscription:
            //   Contract.EffectiveAtUtc == Subscription.StartsAtUtc == startsAt
            // For scheduled renewals, startsAt == oldSubscription.EffectiveEndsAtUtc,
            // so the new Contract does not start during the old service period.
            var effectiveAt = startsAt;
            var endsAt = startsAt.AddMonths(durationMonths);

            var contractResult = Contract.Create(
                id: Guid.NewGuid(),
                tenantId: oldSubscription.TenantId,
                contractNumber: GenerateContractNumber(),
                planId: plan.Id,
                effectiveAtUtc: effectiveAt,
                endsAtUtc: endsAt,
                durationMonths: durationMonths,
                monthlyListPrice: calc.MonthlyListPrice,
                contractualMonthlyValue: calc.MonthlyListPrice,
                currencyCode: calc.CurrencyCode,
                contractedAmount: calc.FinalAmount,
                discountAmount: calc.DiscountAmount,
                promotionReference: calc.PromotionName,
                promotionId: calc.PromotionId,
                promotionType: calc.PromotionType,
                chargedMonths: calc.ChargedMonths,
                bonusMonths: plan.BonusMonths,
                maxStudents: plan.MaxStudents,
                maxUsers: plan.MaxUsers,
                maxBranches: plan.MaxBranches,
                maxTeachers: plan.MaxTeachers,
                storageGb: plan.StorageGB,
                smsQuota: plan.SMSQuota);

            if (!contractResult.IsSuccess)
                return contractResult.Errors!;

            var contract = contractResult.Value;

            contract.LinkToPreviousSubscription(oldSubscription.Id);

            // Snapshot pricing tiers from the Plan catalog into the Contract
            var seenDurations = new HashSet<int>();
            foreach (var planTier in plan.PricingTiers.OrderBy(t => t.DisplayOrder))
            {
                if (!seenDurations.Add(planTier.DurationMonths))
                    continue;

                var tierResult = ContractPricingTier.Create(
                    id: Guid.NewGuid(),
                    contractId: contract.Id,
                    durationMonths: planTier.DurationMonths,
                    tierPrice: planTier.TierPrice,
                    currencyCode: calc.CurrencyCode,
                    monthlyListPrice: calc.MonthlyListPrice,
                    displayOrder: planTier.DisplayOrder);

                if (!tierResult.IsSuccess)
                    return tierResult.Errors!;

                contract.AddPricingTier(tierResult.Value);
            }

            // Snapshot feature entitlements from the Plan catalog into the Contract
            foreach (var pf in plan.PlanFeatures.Where(f => f.IsEnabled))
            {
                var feature = await dbContext.Features
                    .AsNoTracking()
                    .Where(f => f.Id == pf.FeatureId)
                    .Select(f => f.Code)
                    .FirstOrDefaultAsync(cancellationToken);

                if (feature is not null)
                {
                    contract.AddContractFeature(
                        ContractFeature.Create(contract.Id, feature));
                }
            }

            var markConvertedResult = offer.MarkConverted(contract.Id, now);
            if (!markConvertedResult.IsSuccess)
                return markConvertedResult.Errors!;

            // ── Step 10: Create new Subscription ──
            // Old subscription is NEVER modified. It remains Active with its original dates.
            // When startsAt > now, the new subscription is created as Pending so the
            // non-terminal unique index (Status IN 1,4,5) allows coexistence with the
            // old Active subscription.
            var activateNew = startsAt <= now;

            var snapshot = contract.GetSubscriptionSnapshot();

            var subscriptionResult = await subscriptionFactory.CreateFromSnapshotAsync(
                oldSubscription.TenantId,
                plan.Id,
                snapshot,
                startsAtUtc: startsAt,
                autoRenew: false,
                activate: activateNew,
                cancellationToken);

            if (!subscriptionResult.IsSuccess)
                return subscriptionResult.Errors!;

            var subscription = subscriptionResult.Value;
            subscription.LinkToContract(contract.Id);

            // ── Step 11: Create BillingCycle ──
            var billingCycleResult = BillingCycle.Create(
                id: Guid.NewGuid(),
                tenantId: oldSubscription.TenantId,
                subscriptionId: subscription.Id,
                periodStart: startsAt,
                periodEnd: subscription.EffectiveEndsAtUtc);

            if (!billingCycleResult.IsSuccess)
                return billingCycleResult.Errors!;

            var billingCycle = billingCycleResult.Value;

            // ── Step 12: Create Invoice from the BillingCycle ──
            var cycleDurationMonths = durationMonths;
            if (cycleDurationMonths <= 0)
                cycleDurationMonths = 1;

            var subtotal = calc.MonthlyListPrice * cycleDurationMonths;
            var discountAmount = calc.DiscountAmount;
            var taxAmount = 0m;
            var totalAmount = subtotal - discountAmount + taxAmount;

            var invoiceNumber = $"INV-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

            var invoiceResult = Invoice.Create(
                id: Guid.NewGuid(),
                invoiceNumber: invoiceNumber,
                periodStart: DateOnly.FromDateTime(startsAt),
                periodEnd: DateOnly.FromDateTime(subscription.EffectiveEndsAtUtc),
                subtotal: subtotal,
                discountAmount: discountAmount,
                taxAmount: taxAmount,
                totalAmount: totalAmount,
                contractId: contract.Id,
                subscriptionId: subscription.Id,
                billingCycleId: billingCycle.Id);

            if (!invoiceResult.IsSuccess)
                return invoiceResult.Errors!;

            var invoice = invoiceResult.Value;

            var markInvoicedResult = billingCycle.MarkInvoiced();
            if (!markInvoicedResult.IsSuccess)
                return markInvoicedResult.Errors!;

            // ── Step 13: Persist all new entities in one batch ──
            dbContext.Offers.Add(offer);
            dbContext.Contracts.Add(contract);
            dbContext.TenantPlans.Add(subscription);
            dbContext.BillingCycles.Add(billingCycle);
            dbContext.Invoices.Add(invoice);
            dbContext.StampAddedTenantIds(oldSubscription.TenantId);

            // Keep the tenant's ValidUpTo consistent
            var tenant = await dbContext.Tenants
                .FirstOrDefaultAsync(t => t.Id == Guid.Parse(oldSubscription.TenantId), cancellationToken);

            if (tenant is not null)
            {
                tenant.SetValidUpTo(subscription.EffectiveEndsAtUtc);
                await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            // ── Step 14: Audit ──
            await auditWriter.WriteAsync(
                action: "Subscription.RenewAsNewTransaction",
                entityType: nameof(TenantPlan),
                entityId: subscription.Id.ToString(),
                oldValue: AuditPayload.Serialize(new
                {
                    OldSubscriptionId = oldSubscription.Id,
                    oldSubscription.PlanId,
                    oldSubscription.SnapshotPrice,
                    oldSubscription.DurationMonths,
                    oldSubscription.BonusMonths,
                    oldSubscription.EffectiveEndsAtUtc,
                    OldStatus = oldSubscription.Status.ToString()
                }),
                newValue: AuditPayload.Serialize(new
                {
                    NewSubscriptionId = subscription.Id,
                    NewContractId = contract.Id,
                    NewOfferId = offer.Id,
                    PlanId = plan.Id,
                    subscription.SnapshotPrice,
                    subscription.DurationMonths,
                    subscription.BonusMonths,
                    subscription.EffectiveEndsAtUtc,
                    NewStatus = subscription.Status.ToString(),
                    offer.PromotionId,
                    offer.PromotionType,
                    offer.DiscountAmount,
                    offer.FinalAmount,
                    PreviousSubscriptionId = oldSubscription.Id,
                    NewInvoiceId = invoice.Id,
                    invoice.TotalAmount
                }),
                cancellationToken: cancellationToken);

            return contract.Id;
        }
        catch (Exception ex) when (transaction is not null && IsDeadlockException(ex))
        {
            return TenantPlanErrors.ConcurrentRenewalConflict;
        }
    }

    private static bool IsDeadlockException(Exception ex)
    {
        return ex is Microsoft.Data.SqlClient.SqlException sqlEx && sqlEx.Number is 1205;
    }

    /// <summary>
    /// Validates whether the old subscription is eligible for renewal.
    /// Renewal creates a NEW commercial transaction — the old subscription must be
    /// in a state where starting a new transaction makes commercial sense.
    /// </summary>
    private static Result<Updated> ValidateRenewalEligibility(TenantPlan oldSubscription)
    {
        return oldSubscription.Status switch
        {
            SubscriptionStatus.Active => Result.Updated,
            SubscriptionStatus.Expired => Result.Updated,
            SubscriptionStatus.PastDue => TenantPlanErrors.CannotRenewSuspended,
            SubscriptionStatus.Suspended => TenantPlanErrors.CannotRenewSuspended,
            SubscriptionStatus.Pending => TenantPlanErrors.CannotRenewPending,
            SubscriptionStatus.Cancelled => TenantPlanErrors.CannotRenewCancelled,
            _ => TenantPlanErrors.InvalidStateTransition(oldSubscription.Status, "renew")
        };
    }

    private static string GenerateContractNumber()
    {
        return $"CTR-RENEW-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
    }
}
