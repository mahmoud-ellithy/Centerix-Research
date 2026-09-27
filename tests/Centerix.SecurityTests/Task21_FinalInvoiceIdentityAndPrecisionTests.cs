namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// TASK 21 (final closure) — full-term invoice IDENTITY and monetary PRECISION.
///
/// Why these tests exist (the two defects they close):
///
/// 1. IDENTITY. A cycle may only take Contract amounts verbatim when its period IS the paid
///    term of the subscription — [StartsAtUtc, BaseEndsAtUtc]. Comparing MONTH COUNTS is not
///    an identity test: a renewal cycle, a cycle that starts before the subscription, or any
///    other cycle can span exactly the same number of months without being the paid term,
///    and would then silently bill the contract price of a different period.
///    Case A proves the positive identity; Cases B/C prove the negative (same duration,
///    different period) — the case a duration-equality check gets wrong.
///
/// 2. PRECISION. Invoice money moved from decimal(10,2) to decimal(18,2) so that
///    Invoice.TotalAmount == Contract.ContractedAmount holds for EVERY value a Contract can
///    store, not just the ones below 100,000,000.00. The rounding POLICY is unchanged:
///    round once, at invoice derivation, to 2 decimals, AwayFromZero.
///
/// Every fixture is built through the real production chain:
/// PromotionCalculationService → CalculatedOffer → Contract.Create → GetSubscriptionSnapshot
/// → SubscriptionFactory → BillingCycle → CreateInvoiceFromBillingCycleHandler → Invoice.
/// No amount is hand-written into an invoice anywhere in this file.
/// </summary>
public class Task21_FinalInvoiceIdentityAndPrecisionTests
{
    private const string TenantA = "7d2f3a4b-2c3d-4e5f-9a01-234567890abc";

    private static readonly DateTime Start2026 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now2026 = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        Plan Plan,
        CalculatedOffer Offer,
        Contract Contract,
        TenantPlan Subscription);

    // ------------------------------------------------------------------
    // Infrastructure helpers
    // ------------------------------------------------------------------

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TASK21F_{Guid.NewGuid():N}")
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(TenantA);
        currentTenant.IsAuthorized.Returns(true);

        var db = new AppDbContext(options, mediator, currentTenant);
        db.StampAddedTenantIds(TenantA);
        return db;
    }

    private static TimeProvider FrozenClock(DateTime utcNow)
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(new DateTimeOffset(utcNow));
        return timeProvider;
    }

    private static ICurrentTenant TenantService()
    {
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(TenantA);
        currentTenant.IsAuthorized.Returns(true);
        return currentTenant;
    }

    private static string? Describe(IReadOnlyCollection<Error>? errors)
        => errors is null || errors.Count == 0
            ? null
            : string.Join("; ", errors.Select(e => $"{e.Code}: {e.Description}"));

    private static Plan BuildPlan(int planId, decimal monthlyPrice, int durationMonths, int bonusMonths)
    {
        var result = Plan.Create(
            id: planId,
            code: $"P{planId}",
            displayName: $"Plan {planId}",
            monthlyPrice: monthlyPrice,
            maxStudents: 100,
            maxUsers: 50,
            maxBranches: 10,
            maxTeachers: 20,
            storageGB: 100,
            smsQuota: 1000,
            isActive: true,
            description: "TASK 21 final closure fixture",
            currencyCode: "EGP",
            durationMonths: durationMonths,
            bonusMonths: bonusMonths);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        return result.Value;
    }

    /// <summary>
    /// Plan → Offer → Contract → Subscription snapshot → Subscription, with an optional
    /// promotion. This is the authoritative commercial chain: the Contract's commercial
    /// facts ARE the Offer's, and the Subscription carries only the Contract snapshot.
    /// </summary>
    private static async Task<Fixture> BuildChainAsync(
        AppDbContext db,
        int planId,
        decimal monthlyPrice,
        int durationMonths = 12,
        int bonusMonths = 0,
        Promotion? promotion = null)
    {
        var plan = BuildPlan(planId, monthlyPrice, durationMonths, bonusMonths);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var promotions = promotion is null
            ? Array.Empty<Promotion>()
            : new[] { promotion };

        var calculation = new PromotionCalculationService()
            .Calculate(plan, durationMonths, Start2026, promotions);

        Assert.True(calculation.IsSuccess, Describe(calculation.Errors));
        var offer = calculation.Value;

        // Offer invariant, asserted at the source so every downstream assertion inherits it.
        Assert.Equal(offer.BaseAmount - offer.DiscountAmount, offer.FinalAmount);

        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(Start2026, durationMonths, bonusMonths);

        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantA,
            contractNumber: $"CTR-21F-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            planId: plan.Id,
            effectiveAtUtc: Start2026,
            endsAtUtc: endsAt,
            durationMonths: durationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            grossAmount: offer.BaseAmount,
            contractedAmount: offer.FinalAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            discountAmount: offer.DiscountAmount,
            promotionReference: offer.PromotionName,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths,
            bonusMonths: bonusMonths,
            maxStudents: plan.MaxStudents,
            maxUsers: plan.MaxUsers,
            maxBranches: plan.MaxBranches,
            maxTeachers: plan.MaxTeachers,
            storageGb: plan.StorageGB,
            smsQuota: plan.SMSQuota);

        Assert.True(contractResult.IsSuccess, Describe(contractResult.Errors));
        var contract = contractResult.Value;

        // Contract invariant: ContractedAmount = GrossAmount − DiscountAmount, and the
        // snapshot must be complete before a subscription may be granted from it.
        Assert.True(contract.ValidateSnapshotCompleteness().IsSuccess);
        Assert.Equal(contract.GrossAmount - contract.DiscountAmount, contract.ContractedAmount);

        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.True(contract.Activate(Start2026).IsSuccess);
        db.Contracts.Add(contract);

        var snapshot = contract.GetSubscriptionSnapshot();

        var subscriptionResult = await new SubscriptionFactory(db).CreateFromSnapshotAsync(
            TenantA, plan.Id, snapshot, Start2026, autoRenew: false, activate: true, CancellationToken.None);

        Assert.True(subscriptionResult.IsSuccess, Describe(subscriptionResult.Errors));
        var subscription = subscriptionResult.Value;
        Assert.True(subscription.LinkToContract(contract.Id).IsSuccess);
        db.TenantPlans.Add(subscription);

        await db.SaveChangesAsync();
        return new Fixture(plan, offer, contract, subscription);
    }

    private static Promotion Percentage(int durationMonths, decimal percentage)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"{percentage}% off {durationMonths} months",
            type: PromotionType.PercentageDiscount,
            planId: 0,
            durationMonths: durationMonths,
            startsAtUtc: Start2026,
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PCT10",
            percentage: percentage);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static Promotion PayForX(int durationMonths, int chargedMonths)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"Pay {chargedMonths} get {durationMonths}",
            type: PromotionType.PayForXMonths,
            planId: 0,
            durationMonths: durationMonths,
            startsAtUtc: Start2026,
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PAY10X12",
            chargedMonths: chargedMonths);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static Promotion PromotionalPrice(int durationMonths, decimal price)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"Special {price} for {durationMonths}",
            type: PromotionType.PromotionalPrice,
            planId: 0,
            durationMonths: durationMonths,
            startsAtUtc: Start2026,
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PRC9000",
            promotionalPrice: price);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static async Task<BillingCycle> AddCycleAsync(
        AppDbContext db, TenantPlan subscription, DateTime periodStart, DateTime periodEnd)
    {
        var cycleResult = BillingCycle.Create(
            Guid.NewGuid(), TenantA, subscription.Id, periodStart, periodEnd);

        Assert.True(cycleResult.IsSuccess, Describe(cycleResult.Errors));
        db.BillingCycles.Add(cycleResult.Value);
        await db.SaveChangesAsync();
        return cycleResult.Value;
    }

    private static async Task<Invoice> InvoiceFromCycleAsync(AppDbContext db, Guid billingCycleId)
    {
        var handler = new CreateInvoiceFromBillingCycleHandler(db, TenantService(), FrozenClock(Now2026));
        var result = await handler.Handle(
            new CreateInvoiceFromBillingCycleCommand(billingCycleId), CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        return await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.BillingCycleId == billingCycleId);
    }

    /// <summary>
    /// Arithmetic drift of a stored invoice: |Total − (Subtotal − Discount + Tax)|.
    /// Must be exactly 0m — not merely inside a tolerance — because the derivation rounds each
    /// component once and then DERIVES the total from the already-rounded components.
    /// </summary>
    private static void AssertReconcilesExactly(Invoice invoice)
    {
        var drift = Math.Abs(invoice.TotalAmount
            - (invoice.Subtotal - invoice.DiscountAmount + invoice.TaxAmount));

        Assert.True(
            drift == 0m,
            $"Invoice {invoice.InvoiceNumber} does not reconcile exactly: drift={drift}, "
            + $"Subtotal={invoice.Subtotal}, Discount={invoice.DiscountAmount}, "
            + $"Tax={invoice.TaxAmount}, Total={invoice.TotalAmount}");
    }

    /// <summary>Every stored money field must fit the 2-decimal monetary policy.</summary>
    private static void AssertTwoDecimalScale(Invoice invoice)
    {
        var fields = new (string Name, decimal Value)[]
        {
            ("Subtotal", invoice.Subtotal),
            ("DiscountAmount", invoice.DiscountAmount),
            ("TaxAmount", invoice.TaxAmount),
            ("TotalAmount", invoice.TotalAmount),
        };

        foreach (var (name, value) in fields)
        {
            Assert.True(
                Math.Round(value, 2, MidpointRounding.AwayFromZero) == value,
                $"{name} = {value} carries more than 2 decimal places.");
        }
    }

    private static DateTime Utc(int year, int month, int day = 1)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    // ==================================================================
    // 1. FULL-TERM IDENTITY — Case A: the period IS the paid term.
    //    Contract amounts are taken verbatim.
    // ==================================================================
    [Fact]
    public async Task FullTermIdentity_CaseA_PeriodEqualsPaidTerm_TakesContractAmountsVerbatim()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(db, planId: 21301, monthlyPrice: 1000m, promotion: Percentage(12, 10m));

        // 12 × 1000 = 12 000 gross, 10 % → 1 200 discount, 10 800 contracted.
        Assert.Equal(12000m, f.Offer.BaseAmount);
        Assert.Equal(1200m, f.Offer.DiscountAmount);
        Assert.Equal(10800m, f.Contract.ContractedAmount);

        // No bonus months: paid term == entitlement term == [2026-01-01, 2027-01-01].
        Assert.Equal(Start2026, f.Subscription.StartsAtUtc);
        Assert.Equal(Utc(2027, 1), f.Subscription.BaseEndsAtUtc);

        var cycle = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.BaseEndsAtUtc);
        Assert.True(cycle.IsFullTermFor(f.Subscription));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // Contract amounts verbatim — never recomputed from a monthly rate.
        Assert.Equal(f.Contract.GrossAmount, invoice.Subtotal);
        Assert.Equal(f.Contract.DiscountAmount, invoice.DiscountAmount);
        Assert.Equal(f.Contract.ContractedAmount, invoice.TotalAmount);
        Assert.Equal(10800m, invoice.TotalAmount);
        Assert.Equal(0m, invoice.TaxAmount);
        AssertReconcilesExactly(invoice);
        AssertTwoDecimalScale(invoice);

        // The invoice is bound to all three documents by identity, not by inference.
        Assert.Equal(cycle.Id, invoice.BillingCycleId);
        Assert.Equal(f.Subscription.Id, invoice.SubscriptionId);
        Assert.Equal(f.Contract.Id, invoice.ContractId);
    }

    // ==================================================================
    // 2. FULL-TERM IDENTITY — Case B: SAME month count, EARLIER period.
    //    This is the case a duration-equality check gets wrong.
    // ==================================================================
    [Fact]
    public async Task FullTermIdentity_CaseB_SameMonthCountEarlierPeriod_IsNotFullTerm_AndBillsOnlyBillableMonths()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(db, planId: 21302, monthlyPrice: 1000m, promotion: Percentage(12, 10m));

        // A 12-month span shifted one month EARLIER than the paid term.
        var cycle = await AddCycleAsync(db, f.Subscription, Utc(2025, 12), Utc(2026, 12));

        // A duration check would have called this full-term: 12 months == the contract duration.
        var span = BillingCycle.ComputeBillableMonths(cycle.PeriodStart, cycle.PeriodEnd);
        Assert.True(span.IsSuccess, Describe(span.Errors));
        Assert.Equal(f.Contract.DurationMonths, span.Value);

        // Period identity correctly refuses it.
        Assert.False(cycle.IsFullTermFor(f.Subscription));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // Only the 11 months inside the paid term [2026-01-01, 2027-01-01] are billable:
        // 2025-12 falls before StartsAtUtc and was never purchased, so it is not billed.
        Assert.Equal(11000m, invoice.Subtotal);       // 1 000 × 11
        Assert.Equal(1100m, invoice.DiscountAmount);  // (1 000 − 900) × 11
        Assert.Equal(9900m, invoice.TotalAmount);
        Assert.NotEqual(f.Contract.ContractedAmount, invoice.TotalAmount);
        AssertReconcilesExactly(invoice);
        AssertTwoDecimalScale(invoice);

        // The invoice keeps the cycle's own period — it is not silently rewritten to the term.
        Assert.Equal(new DateOnly(2025, 12, 1), invoice.PeriodStart);
        Assert.Equal(new DateOnly(2026, 12, 1), invoice.PeriodEnd);
    }

    // ==================================================================
    // 3. FULL-TERM IDENTITY — Case C: renewal period of identical length.
    //    A second 12-month cycle must not masquerade as the contract's term.
    // ==================================================================
    [Fact]
    public async Task FullTermIdentity_CaseC_RenewalPeriodOfSameLength_IsNotFullTerm_AndBillsItsOwnPeriod()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(db, planId: 21303, monthlyPrice: 1000m, promotion: Percentage(12, 10m));

        var term = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.BaseEndsAtUtc);
        var renewal = await AddCycleAsync(db, f.Subscription, Utc(2027, 1), Utc(2028, 1));

        Assert.True(term.IsFullTermFor(f.Subscription));
        Assert.False(renewal.IsFullTermFor(f.Subscription));

        var termInvoice = await InvoiceFromCycleAsync(db, term.Id);
        var renewalInvoice = await InvoiceFromCycleAsync(db, renewal.Id);

        // The renewal cycle lies entirely past EffectiveEndsAtUtc, so rule 2 of
        // GetBillableMonthsFor bills it on its OWN period at the snapshot rate — it never
        // reaches the Contract branch, which is reserved for the paid term.
        Assert.Equal(12000m, renewalInvoice.Subtotal);
        Assert.Equal(1200m, renewalInvoice.DiscountAmount);
        Assert.Equal(10800m, renewalInvoice.TotalAmount);

        // NOTE (deliberate, and the reason amounts alone are NOT an identity proof): when the
        // discount divides evenly by the duration, a same-length renewal cycle computes to the
        // same numbers as the Contract. Amounts cannot tell the two apart — only the period can.
        // That is exactly why the identity assertions below are the ones that matter.
        Assert.Equal(new DateOnly(2026, 1, 1), termInvoice.PeriodStart);
        Assert.Equal(new DateOnly(2027, 1, 1), termInvoice.PeriodEnd);
        Assert.Equal(new DateOnly(2027, 1, 1), renewalInvoice.PeriodStart);
        Assert.Equal(new DateOnly(2028, 1, 1), renewalInvoice.PeriodEnd);
        Assert.NotEqual(termInvoice.Id, renewalInvoice.Id);
        Assert.NotEqual(termInvoice.BillingCycleId, renewalInvoice.BillingCycleId);

        AssertReconcilesExactly(termInvoice);
        AssertReconcilesExactly(renewalInvoice);
        AssertTwoDecimalScale(renewalInvoice);

        // One invoice per cycle; both are bound to the same contract and subscription.
        Assert.Equal(2, await db.Invoices.IgnoreQueryFilters().CountAsync());
        var invoices = await db.Invoices.IgnoreQueryFilters().ToListAsync();
        Assert.All(invoices, i =>
        {
            Assert.Equal(f.Contract.Id, i.ContractId);
            Assert.Equal(f.Subscription.Id, i.SubscriptionId);
        });
    }

    // ==================================================================
    // 4. FULL-TERM IDENTITY — bonus entitlement is NOT part of the paid term.
    //    A period ending at EffectiveEndsAtUtc is not full-term, and the bonus
    //    months inside it are billed as zero.
    // ==================================================================
    [Fact]
    public async Task FullTermIdentity_PeriodEndingAtEffectiveEnds_IsNotFullTerm_AndBonusTimeIsNotBilled()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(
            db, planId: 21304, monthlyPrice: 1000m, durationMonths: 12, bonusMonths: 2);

        Assert.Equal(12, f.Subscription.DurationMonths);
        Assert.Equal(2, f.Subscription.BonusMonths);
        Assert.Equal(Utc(2027, 1), f.Subscription.BaseEndsAtUtc);
        Assert.Equal(Utc(2027, 3), f.Subscription.EffectiveEndsAtUtc);
        Assert.Equal(12000m, f.Contract.ContractedAmount);

        // [start, EFFECTIVE end] = 14 months of access but only 12 months of debt.
        var overSpanning = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.EffectiveEndsAtUtc);
        Assert.False(overSpanning.IsFullTermFor(f.Subscription));

        var invoice = await InvoiceFromCycleAsync(db, overSpanning.Id);
        Assert.Equal(12000m, invoice.Subtotal);   // 1 000 × 12 — the 2 bonus months bill nothing
        Assert.Equal(0m, invoice.DiscountAmount);
        Assert.Equal(12000m, invoice.TotalAmount);
        AssertReconcilesExactly(invoice);
        AssertTwoDecimalScale(invoice);

        // [start, BASE end] is the only period that IS the paid term.
        var paidTerm = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.BaseEndsAtUtc);
        Assert.True(paidTerm.IsFullTermFor(f.Subscription));
    }

    // ==================================================================
    // 5. FREE TIME ONLY — a cycle wholly inside bonus entitlement has nothing to
    //    bill: an explicit failure, no invoice, and the cycle stays Draft.
    // ==================================================================
    [Fact]
    public async Task BillingCycle_InsideBonusEntitlementOnly_FailsWithNoBillablePeriod_AndCreatesNoInvoice()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(
            db, planId: 21305, monthlyPrice: 1000m, durationMonths: 12, bonusMonths: 2);

        // [2027-01-01 → 2027-03-01] lies entirely inside the free bonus window.
        var cycle = await AddCycleAsync(db, f.Subscription, Utc(2027, 1), Utc(2027, 3));
        Assert.False(cycle.IsFullTermFor(f.Subscription));

        var handler = new CreateInvoiceFromBillingCycleHandler(db, TenantService(), FrozenClock(Now2026));
        var result = await handler.Handle(
            new CreateInvoiceFromBillingCycleCommand(cycle.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Errors);
        Assert.Contains(result.Errors!, e => e.Code == "BillingCycle.NoBillablePeriod");

        // No invoice was created, and the cycle was not advanced to Invoiced.
        Assert.Equal(0, await db.Invoices.IgnoreQueryFilters().CountAsync());
        var reloaded = await db.BillingCycles.IgnoreQueryFilters().SingleAsync(c => c.Id == cycle.Id);
        Assert.Equal(BillingCycleStatus.Draft, reloaded.Status);
    }

    // ==================================================================
    // 6. Idempotency — a cycle already invoiced cannot be invoiced twice, and
    //    the failure leaves no orphan invoice behind.
    // ==================================================================
    [Fact]
    public async Task CreateInvoiceFromBillingCycle_SecondAttempt_FailsAndLeavesNoOrphanInvoice()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(db, planId: 21306, monthlyPrice: 1000m, promotion: Percentage(12, 10m));
        var cycle = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.BaseEndsAtUtc);

        var first = await InvoiceFromCycleAsync(db, cycle.Id);

        var handler = new CreateInvoiceFromBillingCycleHandler(db, TenantService(), FrozenClock(Now2026));
        var second = await handler.Handle(
            new CreateInvoiceFromBillingCycleCommand(cycle.Id), CancellationToken.None);

        Assert.False(second.IsSuccess);
        Assert.NotNull(second.Errors);
        Assert.Contains(second.Errors!, e => e.Code.StartsWith("BillingCycle.", StringComparison.Ordinal));

        Assert.Equal(1, await db.Invoices.IgnoreQueryFilters().CountAsync());
        var surviving = await db.Invoices.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(first.Id, surviving.Id);
        Assert.Equal(cycle.Id, surviving.BillingCycleId);
        Assert.Equal(BillingCycleStatus.Invoiced,
            (await db.BillingCycles.IgnoreQueryFilters().SingleAsync(c => c.Id == cycle.Id)).Status);
    }

    // ==================================================================
    // 7. PRECISION — the full-term invariant holds for EVERY promotion shape:
    //    Invoice.TotalAmount == Contract.ContractedAmount == Offer.FinalAmount,
    //    and the stored invoice reconciles with ZERO drift.
    // ==================================================================
    [Fact]
    public async Task Precision_FullTermInvoiceEqualsContractedAmount_AcrossPromotionShapes()
    {
        var shapes = new (int PlanId, Func<Promotion?> Promotion)[]
        {
            (21401, () => null),                          // no discount
            (21402, () => Percentage(12, 12.5m)),         // percentage discount
            (21403, () => PromotionalPrice(12, 9999.99m)),// promotional total price
            (21404, () => PayForX(12, 10)),               // pay 10 get 12 (bonus months free)
        };

        foreach (var (planId, build) in shapes)
        {
            using var db = CreateDbContext();
            var f = await BuildChainAsync(db, planId, monthlyPrice: 1000m, promotion: build());

            Assert.True(
                f.Contract.ContractedAmount > 0m,
                $"Plan {planId}: fixture must produce a positive contracted amount.");

            var cycle = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.BaseEndsAtUtc);
            Assert.True(cycle.IsFullTermFor(f.Subscription), $"Plan {planId}: cycle must be the paid term.");

            var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

            Assert.Equal(f.Contract.GrossAmount, invoice.Subtotal);
            Assert.Equal(f.Contract.DiscountAmount, invoice.DiscountAmount);
            Assert.Equal(f.Contract.ContractedAmount, invoice.TotalAmount);
            Assert.Equal(f.Offer.FinalAmount, invoice.TotalAmount);

            // Offer → Contract → Invoice is one unbroken equality, with no rounding drift.
            AssertReconcilesExactly(invoice);
            AssertTwoDecimalScale(invoice);
        }
    }

    // ==================================================================
    // 8. PRECISION — awkward two-decimal list price and a mid-term percentage.
    //    Rounding happens ONCE, at derivation; the total is DERIVED from the
    //    rounded components, so the stored identity is exact, not approximate.
    // ==================================================================
    [Fact]
    public async Task Precision_AwkwardTwoDecimalContract_ReconcilesWithZeroDrift()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(
            db, planId: 21408, monthlyPrice: 3333.33m, promotion: Percentage(12, 10m));

        // 12 × 3 333.33 = 39 999.96 gross; 10 % = 3 999.996 → rounded once → 4 000.00.
        Assert.Equal(39999.96m, f.Contract.GrossAmount);
        Assert.Equal(4000.00m, f.Contract.DiscountAmount);
        Assert.Equal(35999.96m, f.Contract.ContractedAmount);

        var cycle = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.BaseEndsAtUtc);
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(39999.96m, invoice.Subtotal);
        Assert.Equal(4000.00m, invoice.DiscountAmount);
        Assert.Equal(35999.96m, invoice.TotalAmount);
        Assert.Equal(f.Contract.ContractedAmount, invoice.TotalAmount);
        AssertReconcilesExactly(invoice);   // drift == 0m, not merely ≤ 0.01m
        AssertTwoDecimalScale(invoice);
    }

    // ==================================================================
    // 9. PRECISION — partial cycle whose snapshot monthly charge does not divide
    //    evenly (35 999.96 / 12 = 2 999.996666…). Each component is rounded once
    //    and the total is derived from those rounded components.
    // ==================================================================
    [Fact]
    public async Task Precision_PartialCycleWithNonDivisibleCharge_RoundsOnceAndReconcilesExactly()
    {
        using var db = CreateDbContext();
        var f = await BuildChainAsync(
            db, planId: 21409, monthlyPrice: 3333.33m, promotion: Percentage(12, 10m));

        // The snapshot charge is the authoritative post-discount monthly rate.
        Assert.Equal(f.Contract.ContractedAmount / f.Contract.DurationMonths, f.Subscription.SnapshotMonthlyCharge);
        Assert.Equal(3333.33m, f.Subscription.SnapshotPrice);

        // A 7-month cycle inside the term — NOT the paid term, so the snapshot path bills it.
        var cycle = await AddCycleAsync(db, f.Subscription, Start2026, Utc(2026, 8));
        Assert.False(cycle.IsFullTermFor(f.Subscription));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        const int months = 7;
        var expectedSubtotal = Math.Round(
            f.Subscription.SnapshotPrice * months, 2, MidpointRounding.AwayFromZero);
        var expectedDiscount = Math.Round(
            (f.Subscription.SnapshotPrice - f.Subscription.SnapshotMonthlyCharge) * months,
            2,
            MidpointRounding.AwayFromZero);

        Assert.Equal(23333.31m, invoice.Subtotal);          // 3 333.33 × 7
        Assert.Equal(expectedSubtotal, invoice.Subtotal);
        Assert.Equal(expectedDiscount, invoice.DiscountAmount);
        Assert.Equal(expectedSubtotal - expectedDiscount, invoice.TotalAmount);
        Assert.Equal(0m, invoice.TaxAmount);

        // The discount surfaces exactly once, and the invoice is clearly not the term total.
        Assert.True(invoice.DiscountAmount > 0m);
        Assert.True(invoice.DiscountAmount < invoice.Subtotal);
        Assert.NotEqual(f.Contract.ContractedAmount, invoice.TotalAmount);
        AssertReconcilesExactly(invoice);
        AssertTwoDecimalScale(invoice);
    }

    // ==================================================================
    // 10. PRECISION — the scale ceiling. decimal(10,2) tops out at
    //     999 999 999.99; a Contract can legitimately exceed that, and the
    //     full-term identity must then still hold instead of overflowing.
    //     (The SQL-side proof of the same claim lives in
    //     Task21_FinalInvoiceIntegritySqlServerTests.)
    // ==================================================================
    [Fact]
    public async Task Precision_ContractAboveLegacyDecimalTenScaleLimit_RoundTripsWithoutLoss()
    {
        const decimal LegacyDecimal10Max = 999_999_999.99m; // decimal(10,2) ceiling

        using var db = CreateDbContext();
        var f = await BuildChainAsync(db, planId: 21410, monthlyPrice: 100_000_000m);

        Assert.Equal(1_200_000_000.00m, f.Contract.ContractedAmount);
        Assert.True(
            f.Contract.ContractedAmount > LegacyDecimal10Max,
            "Fixture must exceed the legacy decimal(10,2) ceiling to be meaningful.");

        var cycle = await AddCycleAsync(db, f.Subscription, Start2026, f.Subscription.BaseEndsAtUtc);
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // Under decimal(10,2) this value could not be stored at all (arithmetic overflow);
        // at decimal(18,2) it round-trips and the full-term identity holds.
        Assert.Equal(1_200_000_000.00m, invoice.Subtotal);
        Assert.Equal(0m, invoice.DiscountAmount);
        Assert.Equal(f.Contract.ContractedAmount, invoice.TotalAmount);
        Assert.True(invoice.TotalAmount > LegacyDecimal10Max);
        AssertReconcilesExactly(invoice);
        AssertTwoDecimalScale(invoice);
    }

    // ==================================================================
    // 11. PRECISION — documents the INV-01 boundary the derivation relies on.
    //     Invoice.Create tolerates a one-cent arithmetic drift (legacy data and
    //     hand-entered invoices); the derivation in this task never uses it,
    //     which is what AssertReconcilesExactly proves above.
    // ==================================================================
    [Fact]
    public void InvoiceCreate_ArithmeticTolerance_AcceptsOneCentDriftAndRejectsTwoCents()
    {
        var oneCent = Invoice.Create(
            Guid.NewGuid(), "INV-TOL-0001", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            subtotal: 100.00m, discountAmount: 0m, taxAmount: 0m, totalAmount: 100.01m);

        Assert.True(oneCent.IsSuccess, Describe(oneCent.Errors));
        Assert.Equal(100.01m, oneCent.Value.TotalAmount);

        var twoCents = Invoice.Create(
            Guid.NewGuid(), "INV-TOL-0002", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            subtotal: 100.00m, discountAmount: 0m, taxAmount: 0m, totalAmount: 100.02m);

        Assert.False(twoCents.IsSuccess);
        Assert.NotNull(twoCents.Errors);
        Assert.Contains(twoCents.Errors!, e => e.Code == "Invoice.TotalAmountMismatch");
    }
}
