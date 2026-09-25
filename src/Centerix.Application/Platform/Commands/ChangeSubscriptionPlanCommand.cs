namespace Centerix.Application.Platform.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Promotions;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
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
            // Task 19 — hygiene: clear tracked entities on each retry so a rolled-back
            // attempt's Added/Modified entities do not leak into the next attempt's save.
            // Without this, a DbUpdateConcurrencyException-then-retry could re-insert
            // tracked Added offers/contracts/subscriptions and produce duplicate-key
            // errors that mask the real conflict. Mirrors the hardened pattern used in
            // AllocatePaymentCommand / ExecuteRefundCommand / ApplyCreditToInvoiceHandler.
            if (dbContext is Microsoft.EntityFrameworkCore.DbContext dbc)
            {
                dbc.ChangeTracker.Clear();
            }

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
        {
            // Idempotent replay: a prior successful change for this subscription always leaves
            // a SubscriptionChange credit keyed by the old subscription id (same SaveChanges as
            // the new contract). A retry must observe the existing financial result instead of
            // failing while leaving callers to retry blindly.
            var replayResult = await TryResolveReplayResultAsync(oldSubscription, cancellationToken);
            if (replayResult is not null)
                return replayResult;

            return eligibilityResult.Errors!;
        }

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
                {
                    // Same idempotent-replay rule inside the transaction: a concurrent
                    // winner may have cancelled this subscription after our first read.
                    var replayResult = await TryResolveReplayResultAsync(oldSubscription, cancellationToken);
                    if (replayResult is not null)
                        return replayResult;

                    return eligibilityResult.Errors!;
                }
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
            var newEffectiveEndsAt = TenantPlan.ComputeEffectiveEndsAtUtc(startsAt, durationMonths, plan.BonusMonths);

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
                expiresAtUtc: now.AddHours(24),
                bonusMonths: plan.BonusMonths,
                maxStudents: plan.MaxStudents,
                maxUsers: plan.MaxUsers,
                maxBranches: plan.MaxBranches,
                maxTeachers: plan.MaxTeachers,
                storageGb: plan.StorageGB,
                smsQuota: plan.SMSQuota,
                entitlementSnapshotVersion: Offer.CompleteEntitlementSnapshotVersion);

            if (!offerResult.IsSuccess)
                return offerResult.Errors!;

            var offer = offerResult.Value;

            // Capture historical pricing tiers into the Offer snapshot
            var offerSeenDurations = new HashSet<int>();
            foreach (var planTier in plan.PricingTiers.OrderBy(t => t.DisplayOrder))
            {
                if (!offerSeenDurations.Add(planTier.DurationMonths))
                    continue;

                var offerTierResult = OfferPricingTier.Create(
                    Guid.NewGuid(), offer.Id, planTier.DurationMonths, planTier.TierPrice, planTier.DisplayOrder);
                if (!offerTierResult.IsSuccess)
                    return offerTierResult.Errors!;

                offer.AddPricingTier(offerTierResult.Value);
            }

            var acceptResult = offer.Accept(now);
            if (!acceptResult.IsSuccess)
                return acceptResult.Errors!;

            var effectiveAt = startsAt;
            // Contract period uses the Offer snapshot: DurationMonths + BonusMonths
            var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(startsAt, offer.DurationMonths, offer.BonusMonths);

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
                grossAmount: calc.BaseAmount,
                contractedAmount: calc.FinalAmount,
                entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
                discountAmount: calc.DiscountAmount,
                promotionReference: calc.PromotionName,
                promotionId: calc.PromotionId,
                promotionType: calc.PromotionType,
                chargedMonths: calc.ChargedMonths,
                bonusMonths: offer.BonusMonths,
                maxStudents: offer.MaxStudents,
                maxUsers: offer.MaxUsers,
                maxBranches: offer.MaxBranches,
                maxTeachers: offer.MaxTeachers,
                storageGb: offer.StorageGB,
                smsQuota: offer.SMSQuota);

            if (!contractResult.IsSuccess)
                return contractResult.Errors!;

            var contract = contractResult.Value;

            // Validate that the entitlement snapshot is complete per prompt section #5
            var snapshotValidation = contract.ValidateSnapshotCompleteness();
            if (!snapshotValidation.IsSuccess)
                return snapshotValidation.Errors!;

            contract.LinkToPreviousSubscription(oldSubscription.Id);

            var seenDurations = new HashSet<int>();
            foreach (var offerTier in offer.PricingTiers.OrderBy(t => t.DisplayOrder))
            {
                if (!seenDurations.Add(offerTier.DurationMonths))
                    continue;

                var tierResult = ContractPricingTier.Create(
                    id: Guid.NewGuid(),
                    contractId: contract.Id,
                    durationMonths: offerTier.DurationMonths,
                    tierPrice: offerTier.TierPrice,
                    currencyCode: offer.CurrencyCode,
                    monthlyListPrice: offer.MonthlyListPrice,
                    displayOrder: offerTier.DisplayOrder);

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
                    var featureResult = ContractFeature.Create(contract.Id, feature);
                    if (!featureResult.IsSuccess)
                        return featureResult.Errors!;

                    var addFeatureResult = contract.AddContractFeature(featureResult.Value);
                    if (!addFeatureResult.IsSuccess)
                        return addFeatureResult.Errors!;
                }
            }

            var markConvertedResult = offer.MarkConverted(contract.Id, now);
            if (!markConvertedResult.IsSuccess)
                return markConvertedResult.Errors!;

            var snapshot = contract.GetSubscriptionSnapshot();

            var subscriptionResult = await subscriptionFactory.CreateFromSnapshotAsync(
                oldSubscription.TenantId,
                plan.Id,
                snapshot,
                startsAtUtc: startsAt,
                autoRenew: false,
                activate: true,
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

            // ── D-02: Calculate unused paid value and create Customer Credit ──
            // Before cancelling the old subscription, calculate the eligible unused PAID value.
            // The unused value becomes Customer Credit (NOT a Refund) applied to the new invoice.
            TenantCredit? unusedCredit = null;
            decimal creditAppliedToInvoice = 0m;

            if (oldSubscription.ContractId.HasValue)
            {
                var oldContract = await dbContext.Contracts
                    .IgnoreQueryFilters()
                    .Include(c => c.PricingTiers)
                    .FirstOrDefaultAsync(c => c.Id == oldSubscription.ContractId.Value, cancellationToken);

                if (oldContract is not null)
                {
                    var elapsedMonths = oldContract.GetElapsedMonths(now);
                    var consumedValue = oldContract.CalculateValueForElapsedMonths(elapsedMonths);
                    var totalContractValue = oldContract.ContractedAmount;
                    var unusedValue = totalContractValue - consumedValue;

                    if (unusedValue > 0)
                    {
                        // ── Eligible PAID SETTLEMENT of the old contract ──
                        // Settlement = active PaymentAllocations on the old contract's invoices
                        //            + eligible CreditApplications on the old contract's invoices
                        //            − executed Refunds associated with the old contract.
                        //
                        // 1) CreditApplication IS monetary settlement: Invoice.GetRemainingAmount()
                        //    = Total - Paid - AppliedCredit (Task 10/12 financial model), so credit
                        //    consumed an invoice it settled part of it.
                        // 2) ExecuteRefund does NOT reverse PaymentAllocations (they stay Active),
                        //    so refunded money must be subtracted explicitly via Refund.ContractId
                        //    (Refund carries an exact contract link — no pro-rating is invented).
                        //
                        // Task 18.4.2 — credit-source eligibility (approved business rule):
                        //    Only credits with real customer economic value count as paid settlement:
                        //      • Overpayment        — real cash already received from the customer
                        //      • SubscriptionChange — value already converted from a previous
                        //                             paid contract (originally customer cash)
                        //    Granted/free/discretionary sources (ReferralReward, Promotional,
                        //    Compensation, Manual) are NOT customer-paid value and must never be
                        //    re-recognised as a new SubscriptionChange credit.
                        //
                        // Task 18.5 — the eligible settlement is split by economic origin so the
                        // new credit can carry immutable lineage metadata:
                        //      • Overpayment applications        → direct customer-paid origin
                        //      • SubscriptionChange applications  → transferred customer-paid origin
                        //    Currency scoping mirrors the payment/refund queries (Task 18.5 §20.13:
                        //    a credit in one currency must never contribute to another currency's
                        //    settlement).
                        var paymentAllocated = await dbContext.Payments
                            .Where(p => p.TenantId == oldSubscription.TenantId
                                     && p.Status == PaymentStatus.Completed
                                     && p.CurrencyCode == oldContract.CurrencyCode)
                            .SelectMany(p => p.Allocations.Where(a =>
                                a.Status == PaymentAllocationStatus.Active
                                && a.Invoice.ContractId == oldContract.Id))
                            .SumAsync(a => a.AllocatedAmount, cancellationToken);

                        var overpaymentCreditApplied = await dbContext.CreditApplications
                            .Where(ca => ca.TenantId == oldSubscription.TenantId
                                     && dbContext.TenantCredits.Any(tc =>
                                         tc.Id == ca.CreditId
                                         && tc.TenantId == oldSubscription.TenantId
                                         && tc.SourceType == CreditSourceType.Overpayment
                                         && tc.CurrencyCode == oldContract.CurrencyCode)
                                     && dbContext.Invoices.Any(i =>
                                         i.TenantId == oldSubscription.TenantId
                                         && i.Id == ca.InvoiceId
                                         && i.ContractId == oldContract.Id))
                            .SumAsync(ca => ca.Amount, cancellationToken);

                        // Task 18.5.1 — lineage propagation: fetch individual CreditApplications for
                        // SubscriptionChange credits to compute proportional transferred-origin
                        // contribution from each consumed credit's TransferredPaidAmount.
                        // The total subscriptionChangeCreditApplied (legacy variable name) is needed
                        // for paidAmount calculation (total economic settlement), but transferred
                        // lineage must be derived proportionally per credit consumed.
                        var subscriptionChangeApplications = await dbContext.CreditApplications
                            .Where(ca => ca.TenantId == oldSubscription.TenantId
                                     && dbContext.TenantCredits.Any(tc =>
                                         tc.Id == ca.CreditId
                                         && tc.TenantId == oldSubscription.TenantId
                                         && tc.SourceType == CreditSourceType.SubscriptionChange
                                         && tc.CurrencyCode == oldContract.CurrencyCode)
                                     && dbContext.Invoices.Any(i =>
                                         i.TenantId == oldSubscription.TenantId
                                         && i.Id == ca.InvoiceId
                                         && i.ContractId == oldContract.Id))
                            .Select(ca => new { ca.CreditId, ca.Amount })
                            .ToListAsync(cancellationToken);

                        var subscriptionChangeCreditApplied = subscriptionChangeApplications.Sum(ca => ca.Amount);

                        var refunded = await dbContext.Refunds
                            .Where(r => r.TenantId == oldSubscription.TenantId
                                     && r.ContractId == oldContract.Id
                                     && r.Status == RefundStatus.Completed
                                     && r.CurrencyCode == oldContract.CurrencyCode)
                            .SumAsync(r => r.Amount, cancellationToken);

                        var paidAmount = paymentAllocated + overpaymentCreditApplied + subscriptionChangeCreditApplied - refunded;
                        if (paidAmount < 0) paidAmount = 0;

                        var creditAmount = Math.Min(unusedValue, paidAmount);

                        // Task 18.5.1 — proportional transferred-origin lineage:
                        // For each consumed SubscriptionChange credit, calculate its proportional
                        // contribution to transferred lineage: (Application.Amount / Credit.Amount) *
                        // Credit.TransferredPaidAmount. This ensures the new credit never attributes
                        // more transferred origin than the predecessor credits actually contained.
                        decimal transferredPaidAmount = 0m;
                        if (creditAmount > 0 && subscriptionChangeApplications.Count > 0)
                        {
                            var consumedCreditIds = subscriptionChangeApplications.Select(ca => ca.CreditId).Distinct().ToList();
                            var consumedCredits = await dbContext.TenantCredits
                                .Where(tc => consumedCreditIds.Contains(tc.Id))
                                .Select(tc => new { tc.Id, tc.Amount, tc.TransferredPaidAmount })
                                .ToDictionaryAsync(tc => tc.Id, cancellationToken);

                            foreach (var application in subscriptionChangeApplications)
                            {
                                if (consumedCredits.TryGetValue(application.CreditId, out var credit))
                                {
                                    // Proportional transferred contribution from this credit
                                    var proportion = credit.Amount > 0
                                        ? application.Amount / credit.Amount
                                        : 0m;
                                    transferredPaidAmount += proportion * credit.TransferredPaidAmount;
                                }
                            }
                        }

                        // Task 18.5 — economic-origin lineage invariant:
                        // transferredPaidAmount cannot exceed the total SubscriptionChange settlement
                        // (cap at subscriptionChangeCreditApplied if creditAmount > it, which
                        // happens when unusedValue > paidAmount — an edge case).
                        if (transferredPaidAmount > subscriptionChangeCreditApplied)
                            transferredPaidAmount = subscriptionChangeCreditApplied;

                        if (creditAmount > 0)
                        {
                            // Check for existing credit from this subscription change (idempotency)
                            var existingCredit = await dbContext.TenantCredits
                                .Where(tc =>
                                    tc.TenantId == oldSubscription.TenantId &&
                                    tc.SourceType == CreditSourceType.SubscriptionChange &&
                                    tc.SourceId == oldSubscription.Id)
                                .FirstOrDefaultAsync(cancellationToken);

                            if (existingCredit is null)
                            {
                                var creditResult = TenantCredit.CreateSubscriptionChange(
                                    Guid.NewGuid(),
                                    creditAmount,
                                    oldSubscription.Id,
                                    transferredPaidAmount,
                                    oldContract.CurrencyCode,
                                    idempotencyKey: $"sub-change-{oldSubscription.Id:N}");

                                if (!creditResult.IsSuccess)
                                    return creditResult.Errors!;

                                unusedCredit = creditResult.Value;

                                // Create ledger entry for credit creation — fail-fast per section #17
                                var previousBalance = await dbContext.CustomerLedgerEntries
                                    .Where(e => e.TenantId == oldSubscription.TenantId)
                                    .OrderByDescending(e => e.RecordedAtUtc)
                                    .Select(e => e.RunningBalance)
                                    .FirstOrDefaultAsync(cancellationToken);

                                var ledgerEntry = CustomerLedgerEntry.CreateCreditCreation(
                                    Guid.NewGuid(),
                                    unusedCredit.Id,
                                    creditAmount,
                                    oldContract.CurrencyCode,
                                    previousBalance,
                                    now,
                                    $"Unused paid value from subscription change: {creditAmount} {oldContract.CurrencyCode}");

                                if (!ledgerEntry.IsSuccess)
                                    return ledgerEntry.Errors!;

                                dbContext.CustomerLedgerEntries.Add(ledgerEntry.Value);

                                // Task 18.5 §20.13 — currency isolation: the credit carries the
                                // OLD contract's currency and must never settle an invoice whose
                                // contract is denominated in a different currency. On a cross-
                                // currency plan change the credit stays Available for invoices in
                                // its own currency instead of contaminating the new settlement.
                                var newInvoiceCurrencyMatches = string.Equals(
                                    oldContract.CurrencyCode,
                                    contract.CurrencyCode,
                                    StringComparison.OrdinalIgnoreCase);

                                if (newInvoiceCurrencyMatches)
                                {
                                    // Apply credit to the new invoice — cap at invoice remaining amount
                                    var invoiceRemaining = invoice.GetRemainingAmount();
                                    var applicationAmount = Math.Min(creditAmount, invoiceRemaining);

                                    var creditApplicationResult = CreditApplication.Create(
                                        Guid.NewGuid(),
                                        unusedCredit.Id,
                                        invoice.Id,
                                        applicationAmount,
                                        now,
                                        $"subscription-change-{oldSubscription.Id:N}");

                                    if (!creditApplicationResult.IsSuccess)
                                        return creditApplicationResult.Errors!;

                                    dbContext.CreditApplications.Add(creditApplicationResult.Value);

                                    var consumeResult = unusedCredit.ConsumeAmount(applicationAmount);
                                    if (!consumeResult.IsSuccess)
                                        return consumeResult.Errors!;

                                    creditAppliedToInvoice = applicationAmount;

                                    // Create CreditUsage ledger entry — fail-fast per section #17
                                    var balanceAfterCreditCreation = ledgerEntry.Value.RunningBalance;

                                    var usageLedgerEntry = CustomerLedgerEntry.CreateCreditUsage(
                                        Guid.NewGuid(),
                                        unusedCredit.Id,
                                        creditApplicationResult.Value.Id,
                                        invoice.Id,
                                        applicationAmount,
                                        oldContract.CurrencyCode,
                                        balanceAfterCreditCreation,
                                        now,
                                        $"Credit applied to invoice from subscription change: {applicationAmount} {oldContract.CurrencyCode}");

                                    if (!usageLedgerEntry.IsSuccess)
                                        return usageLedgerEntry.Errors!;

                                    dbContext.CustomerLedgerEntries.Add(usageLedgerEntry.Value);
                                }

                                dbContext.TenantCredits.Add(unusedCredit);
                            }
                            else
                            {
                                // Idempotent: credit already exists — look up the actual applied amount
                                var existingApplication = await dbContext.CreditApplications
                                    .Where(ca => ca.CreditId == existingCredit.Id && ca.InvoiceId == invoice.Id)
                                    .FirstOrDefaultAsync(cancellationToken);

                                creditAppliedToInvoice = existingApplication?.Amount ?? 0m;
                            }
                        }
                    }
                }
            }

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
                    invoice.TotalAmount,
                    UnusedCreditId = unusedCredit?.Id,
                    UnusedCreditAmount = unusedCredit?.Amount ?? 0m,
                    UnusedCreditTransferredPaidAmount = unusedCredit?.TransferredPaidAmount ?? 0m,
                    CreditAppliedToInvoice = creditAppliedToInvoice
                }),
                cancellationToken: cancellationToken);

            return contract.Id;
        }
        catch (DbUpdateException ex) when (transaction is not null && IsDuplicateKeyException(ex))
        {
            // Concurrent duplicate lost the race at the database unique constraint
            // (UX_TenantCredits_TenantId_SourceType_SourceId or the non-terminal
            // subscription index): the winner has committed the credit + contract.
            // Detach the failed unit of work and observe/reuse the existing result
            // instead of surfacing a constraint violation.
            if (dbContext is DbContext efContext)
            {
                foreach (var entry in efContext.ChangeTracker.Entries()
                    .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    .ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }

            var loserOld = await dbContext.TenantPlans
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(tp => tp.Id == request.SubscriptionId, cancellationToken);

            if (loserOld is not null)
            {
                var replayResult = await TryResolveReplayResultAsync(loserOld, cancellationToken);
                if (replayResult is not null)
                    return replayResult;
            }

            return TenantPlanErrors.ConcurrentRenewalConflict;
        }
        catch (Exception ex) when (transaction is not null && IsDeadlockException(ex))
        {
            return TenantPlanErrors.ConcurrentRenewalConflict;
        }
    }

    private async Task<Result<Guid>?> TryResolveReplayResultAsync(
        TenantPlan oldSubscription,
        CancellationToken cancellationToken)
    {
        // Only a subscription cancelled by a previous plan change can have a replay result:
        // SubscriptionChange credits are created exclusively by this handler.
        if (oldSubscription.Status != SubscriptionStatus.Cancelled)
            return null;

        var priorCreditExists = await dbContext.TenantCredits
            .AnyAsync(tc =>
                tc.TenantId == oldSubscription.TenantId &&
                tc.SourceType == CreditSourceType.SubscriptionChange &&
                tc.SourceId == oldSubscription.Id,
                cancellationToken);

        if (!priorCreditExists)
            return null;

        var priorContract = await dbContext.Contracts
            .IgnoreQueryFilters()
            .Where(c =>
                c.TenantId == oldSubscription.TenantId &&
                c.PreviousSubscriptionId == oldSubscription.Id)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return priorContract is not null ? priorContract.Id : null;
    }

    private static bool IsDeadlockException(Exception ex)
    {
        // SQL Server error 1205 = deadlock victim. EF Core (and its execution strategy) wrap
        // the SqlException in DbUpdateException/InvalidOperationException while saving inside
        // the SERIALIZABLE transaction, so the whole inner-exception chain must be inspected —
        // mirroring IsDuplicateKeyException's unwrapping (Task 18.5 §20.14: a concurrent
        // deadlock loser must resolve through the existing conflict/idempotency behavior,
        // never escape as an unhandled exception).
        var current = ex;
        while (current is not null)
        {
            if (current is Microsoft.Data.SqlClient.SqlException sqlEx && sqlEx.Number is 1205)
                return true;

            current = current.InnerException;
        }

        return false;
    }

    /// <summary>
    /// SQL Server error 2601 = duplicate key in unique index.
    /// SQL Server error 2627 = UNIQUE KEY constraint violation.
    /// </summary>
    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        var sqlEx = ex.InnerException as Microsoft.Data.SqlClient.SqlException;
        while (sqlEx is null && ex.InnerException is DbUpdateException innerDbEx)
        {
            ex = innerDbEx;
            sqlEx = ex.InnerException as Microsoft.Data.SqlClient.SqlException;
        }

        return sqlEx is not null && (sqlEx.Number == 2601 || sqlEx.Number == 2627);
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
