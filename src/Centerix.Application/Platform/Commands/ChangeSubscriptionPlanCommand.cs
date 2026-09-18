namespace Centerix.Application.Platform.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Promotions;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// PLATFORM-ONLY workflow: Changes a subscription to a different plan (upgrade or downgrade).
///
/// Creates a completely new Offer → Contract → Subscription → BillingCycle → Invoice chain
/// using the CURRENT commercial terms (current target plan pricing, current promotions, current benefits).
///
/// The old subscription is ended (cancelled) at the effective date.
/// The old subscription remains historically accurate — its commercial snapshot is NEVER modified.
/// Old promotions, discounts, or benefits are NOT inherited.
/// No automatic early-cancellation refund is triggered.
///
/// Flow:
///   1. Validate eligibility of old subscription (must be Active)
///   2. Validate target plan exists and is active
///   3. Prevent overlapping active subscriptions (SERIALIZABLE transaction + unique index)
///   4. Calculate a fresh Offer for the target plan using current promotions
///   5. Persist the Offer as an immutable snapshot
///   6. Create a new Contract from the Offer (new commercial snapshot)
///   7. Create a new Subscription (TenantPlan) from the Contract/Offer snapshot
///   8. Create a new BillingCycle for the subscription period
///   9. Create a new Invoice from the BillingCycle
///  10. End (cancel) the old subscription at the effective date
///  11. Link Contract to previous subscription for traceability
///  12. Audit
/// </summary>
public record ChangeSubscriptionPlanCommand(
    Guid SubscriptionId,
    int NewPlanId) : IRequest<Result<Guid>>;

public class ChangeSubscriptionPlanValidator : AbstractValidator<ChangeSubscriptionPlanCommand>
{
    public ChangeSubscriptionPlanValidator()
    {
        RuleFor(x => x.SubscriptionId).NotEmpty();
        RuleFor(x => x.NewPlanId).GreaterThan(0);
    }
}

public class ChangeSubscriptionPlanHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ISubscriptionFactory subscriptionFactory,
    IPromotionCalculationService promotionCalculationService,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<ChangeSubscriptionPlanCommand, Result<Guid>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<Guid>> Handle(ChangeSubscriptionPlanCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        for (var attempt = 0; attempt < MaxDeadlockRetries; attempt++)
        {
            try
            {
                return await ExecuteChangePlanCoreAsync(request, now, cancellationToken);
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

    private async Task<Result<Guid>> ExecuteChangePlanCoreAsync(
        ChangeSubscriptionPlanCommand request,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var oldSubscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(tp => tp.Id == request.SubscriptionId, cancellationToken);

        if (oldSubscription is null)
            return Error.NotFound("Subscription.NotFound",
                $"Subscription '{request.SubscriptionId}' was not found.");

        var eligibilityResult = ValidateChangePlanEligibility(oldSubscription);
        if (!eligibilityResult.IsSuccess)
            return eligibilityResult.Errors!;

        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            if (transaction is not null)
            {
                oldSubscription = await dbContext.TenantPlans
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(tp => tp.Id == request.SubscriptionId, cancellationToken);

                if (oldSubscription is null)
                    return Error.NotFound("Subscription.NotFound",
                        $"Subscription '{request.SubscriptionId}' was not found.");

                eligibilityResult = ValidateChangePlanEligibility(oldSubscription);
                if (!eligibilityResult.IsSuccess)
                    return eligibilityResult.Errors!;
            }

            var plan = await dbContext.Plans
                .Include(p => p.PricingTiers)
                .Include(p => p.PlanFeatures)
                .FirstOrDefaultAsync(p => p.Id == request.NewPlanId, cancellationToken);

            if (plan is null)
                return TenantPlanErrors.PlanNotFound;

            if (!plan.IsActive)
                return TenantPlanErrors.PlanInactive;

            var startsAt = now;
            var durationMonths = plan.DurationMonths;
            var newEffectiveEndsAt = TenantPlan.AddCalendarMonths(startsAt, durationMonths + plan.BonusMonths);

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

            var candidatePromotions = await dbContext.Promotions
                .Where(p =>
                    (p.PlanId == 0 || p.PlanId == request.NewPlanId) &&
                    (p.DurationMonths == 0 || p.DurationMonths == durationMonths))
                .ToListAsync(cancellationToken);

            var calculated = promotionCalculationService.Calculate(
                plan, durationMonths, now, candidatePromotions);

            if (!calculated.IsSuccess)
                return calculated.Errors!;

            var calc = calculated.Value;

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

            var effectiveAt = startsAt;
            var endsAt = startsAt.AddMonths(durationMonths);

            var contractResult = Contract.Create(
                id: Guid.NewGuid(),
                tenantId: oldSubscription.TenantId,
                contractNumber: GenerateContractNumber(now),
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
                chargedMonths: calc.ChargedMonths);

            if (!contractResult.IsSuccess)
                return contractResult.Errors!;

            var contract = contractResult.Value;

            contract.LinkToPreviousSubscription(oldSubscription.Id);

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

            var markConvertedResult = offer.MarkConverted(contract.Id, now);
            if (!markConvertedResult.IsSuccess)
                return markConvertedResult.Errors!;

            var subscriptionResult = await subscriptionFactory.CreateFromSnapshotAsync(
                oldSubscription.TenantId,
                plan.Id,
                snapshotPrice: calc.MonthlyListPrice,
                snapshotCurrency: calc.CurrencyCode,
                durationMonths: durationMonths,
                bonusMonths: plan.BonusMonths,
                startsAtUtc: startsAt,
                autoRenew: false,
                cancellationToken);

            if (!subscriptionResult.IsSuccess)
                return subscriptionResult.Errors!;

            var subscription = subscriptionResult.Value;
            subscription.LinkToContract(contract.Id);

            var billingCycleResult = BillingCycle.Create(
                id: Guid.NewGuid(),
                tenantId: oldSubscription.TenantId,
                subscriptionId: subscription.Id,
                periodStart: startsAt,
                periodEnd: subscription.EffectiveEndsAtUtc);

            if (!billingCycleResult.IsSuccess)
                return billingCycleResult.Errors!;

            var billingCycle = billingCycleResult.Value;

            // ── Invoice amounts MUST derive from the authoritative Offer/Contract ──
            // The Offer engine (PromotionCalculationService) is the single source of truth:
            //   - BaseAmount: tier price when PricingTier applies, else MonthlyPrice × Duration
            //   - DiscountAmount: promotion discount applied to BaseAmount
            //   - FinalAmount: BaseAmount - DiscountAmount
            // The Contract.ContractedAmount is set to FinalAmount above.
            // Invoice.TotalAmount MUST equal Contract.ContractedAmount (no independent reconstruction).
            var subtotal = calc.BaseAmount;
            var discountAmount = calc.DiscountAmount;
            var taxAmount = 0m;
            var totalAmount = calc.FinalAmount;

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

            var cancelResult = oldSubscription.Cancel(now);
            if (!cancelResult.IsSuccess)
                return cancelResult.Errors!;

            dbContext.Offers.Add(offer);
            dbContext.Contracts.Add(contract);
            dbContext.TenantPlans.Add(subscription);
            dbContext.BillingCycles.Add(billingCycle);
            dbContext.Invoices.Add(invoice);
            dbContext.StampAddedTenantIds(oldSubscription.TenantId);

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

            await auditWriter.WriteAsync(
                action: "Subscription.ChangePlan",
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
                    NewPlanId = plan.Id,
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

    private static Result<Updated> ValidateChangePlanEligibility(TenantPlan oldSubscription)
    {
        return oldSubscription.Status switch
        {
            SubscriptionStatus.Active => Result.Updated,
            _ => TenantPlanErrors.InvalidStateTransition(oldSubscription.Status, "change plan")
        };
    }

    private static string GenerateContractNumber(DateTime now)
    {
        return $"CTR-CHANGE-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
    }
}
