namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// TASK 21.2 — BillingCycle → Invoice lifecycle integrity.
///
/// Invariants under test (Task 21.2 §3 / §4 / §9):
///   • Subtotal       = Subscription.SnapshotPrice × billing-cycle duration (list price, display only)
///   • DiscountAmount = (SnapshotPrice − SnapshotMonthlyCharge) × duration  (ONLY the snapshot discount)
///   • TotalAmount    = SnapshotMonthlyCharge × duration                    (the real charge)
///   • TotalAmount   == Contract.ContractedAmount  (contract-derived path is the authority)
///   • No double discount, no promotion re-evaluation, no client-supplied amounts.
///
/// The immutable chain is Offer → Contract → Subscription snapshot → BillingCycle → Invoice.
/// </summary>
public class TASK21_2_BillingCycleInvoiceLifecycleTests
{
    // ------------------------------------------------------------------
    // Constants & helpers
    // ------------------------------------------------------------------

    /// <summary>Must parse as a Guid — renewal/plan-change look up Tenant.Id via Guid.Parse.</summary>
    private const string TenantA = "6c1e2f3a-1b2c-4d5e-8f90-1234567890ab";
    private const decimal Tolerance = 0.01m;

    private static readonly DateTime Start2026 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now2026 = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed record CommercialFixture(
        Plan Plan,
        CalculatedOffer Offer,
        Contract Contract,
        SubscriptionSnapshot Snapshot,
        TenantPlan Subscription);

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TASK212_{Guid.NewGuid():N}")
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? TenantA);
        currentTenant.IsAuthorized.Returns(true);

        var db = new AppDbContext(options, mediator, currentTenant);
        db.StampAddedTenantIds(tenantId ?? TenantA);
        return db;
    }

    private static ICurrentTenant TenantService(string tenantId)
    {
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId);
        currentTenant.IsAuthorized.Returns(true);
        return currentTenant;
    }

    private static TimeProvider FrozenClock(DateTime utcNow)
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(new DateTimeOffset(utcNow));
        return timeProvider;
    }

    private static Plan BuildPlan(int planId, decimal monthlyPrice, int durationMonths = 12, int bonusMonths = 0)
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
            description: "TASK 21.2 fixture",
            currencyCode: "EGP",
            durationMonths: durationMonths,
            bonusMonths: bonusMonths);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        return result.Value;
    }

    private static Promotion Percentage(int durationMonths, decimal percentage, int planId = 0, DateTime? endsAt = null)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"{percentage}% off {durationMonths} months",
            type: PromotionType.PercentageDiscount,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: endsAt ?? new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PCT10",
            percentage: percentage);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static Promotion FixedAmount(int durationMonths, decimal fixedAmount, int planId = 0)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"{fixedAmount} off {durationMonths} months",
            type: PromotionType.FixedAmountDiscount,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "FIX1500",
            fixedAmount: fixedAmount);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static Promotion PayForXMonths(int durationMonths, int chargedMonths, int planId = 0)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"Pay {chargedMonths} get {durationMonths}",
            type: PromotionType.PayForXMonths,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PAY4X",
            chargedMonths: chargedMonths);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static Promotion PromotionalPrice(int durationMonths, decimal promotionalPrice, int planId = 0)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"Special price {durationMonths} months",
            type: PromotionType.PromotionalPrice,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PRC9000",
            promotionalPrice: promotionalPrice);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    /// <summary>
    /// Offer → Contract → Subscription snapshot, exactly like the production chain.
    /// </summary>
    private static async Task<CommercialFixture> BuildCommercialAsync(
        AppDbContext db,
        int planId,
        decimal monthlyPrice,
        IReadOnlyList<Promotion>? promotions = null,
        int durationMonths = 12,
        int bonusMonths = 0)
    {
        var plan = BuildPlan(planId, monthlyPrice, durationMonths, bonusMonths);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var calculation = new PromotionCalculationService().Calculate(
            plan, durationMonths, Start2026, promotions ?? Array.Empty<Promotion>());

        Assert.True(calculation.IsSuccess, Describe(calculation.Errors));
        var offer = calculation.Value;

        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(Start2026, durationMonths, bonusMonths);

        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantA,
            contractNumber: $"CTR-212-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
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
        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.True(contract.Activate(Start2026).IsSuccess);
        db.Contracts.Add(contract);

        // Snapshot ONLY — no Plan catalog reads on this path.
        var snapshot = contract.GetSubscriptionSnapshot();

        var subscriptionResult = await new SubscriptionFactory(db).CreateFromSnapshotAsync(
            TenantA, plan.Id, snapshot, Start2026, autoRenew: false, activate: true, CancellationToken.None);

        Assert.True(subscriptionResult.IsSuccess, Describe(subscriptionResult.Errors));
        var subscription = subscriptionResult.Value;
        Assert.True(subscription.LinkToContract(contract.Id).IsSuccess);
        db.TenantPlans.Add(subscription);

        await db.SaveChangesAsync();

        return new CommercialFixture(plan, offer, contract, snapshot, subscription);
    }

    private static async Task<BillingCycle> AddDraftCycleAsync(
        AppDbContext db, TenantPlan subscription, DateTime periodStart, DateTime periodEnd)
    {
        var cycleResult = BillingCycle.Create(
            Guid.NewGuid(), TenantA, subscription.Id, periodStart, periodEnd);

        Assert.True(cycleResult.IsSuccess, Describe(cycleResult.Errors));
        db.BillingCycles.Add(cycleResult.Value);
        await db.SaveChangesAsync();
        return cycleResult.Value;
    }

    private static CreateInvoiceFromBillingCycleHandler CreateCycleInvoiceHandler(
        AppDbContext db, DateTime? clockUtc = null)
    {
        return new CreateInvoiceFromBillingCycleHandler(
            db, TenantService(TenantA), FrozenClock(clockUtc ?? Now2026));
    }

    private static async Task<Invoice> InvoiceFromCycleAsync(
        AppDbContext db, Guid billingCycleId, DateTime? clockUtc = null)
    {
        var handler = CreateCycleInvoiceHandler(db, clockUtc);
        var result = await handler.Handle(
            new CreateInvoiceFromBillingCycleCommand(billingCycleId), CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Errors));

        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .SingleAsync(i => i.BillingCycleId == billingCycleId);

        return invoice;
    }

    private static void Amount(decimal expected, decimal actual, string because)
    {
        Assert.True(
            Math.Abs(expected - actual) <= Tolerance,
            $"{because}: expected {expected}, actual {actual}");
    }

    private static string? Describe(IReadOnlyCollection<Error>? errors)
        => errors is null || errors.Count == 0
            ? null
            : string.Join("; ", errors.Select(e => $"{e.Code}: {e.Description}"));

    // ==================================================================
    // 1. No discount — full-term cycle
    // ==================================================================
    [Fact]
    public async Task Scenario01_NoDiscount_FullTermCycle_InvoiceEqualsContractedAmount()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(db, planId: 2101, monthlyPrice: 1000m);

        Assert.Equal(12000m, fixture.Contract.GrossAmount);
        Assert.Equal(12000m, fixture.Contract.ContractedAmount);
        Assert.Equal(1000m, fixture.Snapshot.MonthlyCharge);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(0m, invoice.DiscountAmount);
        Assert.Equal(0m, invoice.TaxAmount);
        Assert.Equal(12000m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);
    }

    // ==================================================================
    // 2. Percentage discount — snapshot discount appears exactly once
    // ==================================================================
    [Fact]
    public async Task Scenario02_PercentageDiscount_DiscountReachesInvoiceExactlyOnce()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2102, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        Assert.Equal(12000m, fixture.Offer.BaseAmount);
        Assert.Equal(1200m, fixture.Offer.DiscountAmount);
        Assert.Equal(10800m, fixture.Offer.FinalAmount);
        Assert.Equal(10800m, fixture.Contract.ContractedAmount);
        Assert.Equal(900m, fixture.Snapshot.MonthlyCharge);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(1200m, invoice.DiscountAmount);
        Assert.Equal(10800m, invoice.TotalAmount);

        // No double discount (9600) and no missing discount (12000).
        Assert.NotEqual(9600m, invoice.TotalAmount);
        Assert.NotEqual(12000m, invoice.TotalAmount);
    }

    // ==================================================================
    // 3. Fixed amount discount
    // ==================================================================
    [Fact]
    public async Task Scenario03_FixedAmountDiscount_UsesDiscountedMonthlyCharge()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2103, monthlyPrice: 1000m,
            promotions: new[] { FixedAmount(durationMonths: 12, fixedAmount: 1500m) });

        Assert.Equal(10500m, fixture.Contract.ContractedAmount);
        Assert.Equal(1500m, fixture.Contract.DiscountAmount);
        Assert.Equal(875m, fixture.Snapshot.MonthlyCharge);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(1500m, invoice.DiscountAmount);
        Assert.Equal(10500m, invoice.TotalAmount);
        Amount(fixture.Contract.ContractedAmount, invoice.TotalAmount, "total must equal contracted amount");
    }

    // ==================================================================
    // 4. Pay-for-X (12 months entitlement, 10 months charged)
    // ==================================================================
    [Fact]
    public async Task Scenario04_PayForXMonths_12Entitlement10Charged_InvoiceChargesTenMonths()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2104, monthlyPrice: 1000m,
            promotions: new[] { PayForXMonths(durationMonths: 12, chargedMonths: 10) });

        Assert.Equal(12, fixture.Offer.DurationMonths);
        Assert.Equal(10, fixture.Offer.ChargedMonths);
        Assert.Equal(12000m, fixture.Offer.BaseAmount);
        Assert.Equal(10000m, fixture.Offer.FinalAmount);
        Assert.Equal(10000m, fixture.Contract.ContractedAmount);
        Assert.Equal(10, fixture.Contract.ChargedMonths);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Amount(12000m, invoice.Subtotal, "subtotal = list price × 12 entitlement months");
        Amount(2000m, invoice.DiscountAmount, "discount = the pay-for-10 discount");
        Amount(10000m, invoice.TotalAmount, "total = 10 charged months only");
        Amount(fixture.Contract.ContractedAmount, invoice.TotalAmount, "total = contracted amount");
    }

    // ==================================================================
    // 5. Promotional price
    // ==================================================================
    [Fact]
    public async Task Scenario05_PromotionalPrice_UsesPromotionalMonthlyCharge()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2105, monthlyPrice: 1000m,
            promotions: new[] { PromotionalPrice(durationMonths: 12, promotionalPrice: 9000m) });

        Assert.Equal(9000m, fixture.Contract.ContractedAmount);
        Assert.Equal(3000m, fixture.Contract.DiscountAmount);
        Assert.Equal(750m, fixture.Snapshot.MonthlyCharge);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(3000m, invoice.DiscountAmount);
        Assert.Equal(9000m, invoice.TotalAmount);
    }

    // ==================================================================
    // 6. Single-month cycle inside a discounted term
    // ==================================================================
    [Fact]
    public async Task Scenario06_SingleMonthCycle_PartialPeriodUsesSnapshotCharge()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2106, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(1000m, invoice.Subtotal);
        Assert.Equal(100m, invoice.DiscountAmount);
        Assert.Equal(900m, invoice.TotalAmount);
        Amount(fixture.Snapshot.MonthlyCharge, invoice.TotalAmount, "1-month total = monthly charge");
    }

    // ==================================================================
    // 7. Three-month cycle scales the snapshot charge
    // ==================================================================
    [Fact]
    public async Task Scenario07_ThreeMonthCycle_ScalesSnapshotCharge()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2107, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(3000m, invoice.Subtotal);
        Assert.Equal(300m, invoice.DiscountAmount);
        Assert.Equal(2700m, invoice.TotalAmount);
        Amount(fixture.Snapshot.MonthlyCharge * 3, invoice.TotalAmount, "3-month total");
    }

    // ==================================================================
    // 8. Plan catalog mutation after subscription creation
    // ==================================================================
    [Fact]
    public async Task Scenario08_PlanPriceMutation_AfterSubscriptionCreation_InvoiceUnaffected()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2108, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var firstCycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var firstInvoice = await InvoiceFromCycleAsync(db, firstCycle.Id);

        // Mutate the live catalog after the commercial snapshot was taken.
        var update = fixture.Plan.Update(
            fixture.Plan.Code, fixture.Plan.DisplayName, 9999m,
            maxStudents: 999, maxUsers: 999, maxBranches: 99, maxTeachers: 99,
            storageGB: 999, smsQuota: 9999, isActive: true);
        Assert.True(update.IsSuccess, Describe(update.Errors));
        await db.SaveChangesAsync();

        Assert.Equal(1000m, fixture.Subscription.SnapshotPrice);
        Assert.Equal(900m, fixture.Subscription.SnapshotMonthlyCharge);

        // A later cycle for the SAME subscription must produce identical amounts.
        var secondCycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026.AddMonths(12), Start2026.AddMonths(24));
        var secondInvoice = await InvoiceFromCycleAsync(db, secondCycle.Id);

        Assert.Equal(firstInvoice.Subtotal, secondInvoice.Subtotal);
        Assert.Equal(firstInvoice.DiscountAmount, secondInvoice.DiscountAmount);
        Assert.Equal(firstInvoice.TotalAmount, secondInvoice.TotalAmount);
        Assert.Equal(10800m, secondInvoice.TotalAmount);
    }

    // ==================================================================
    // 9. Promotion mutation after contract creation
    // ==================================================================
    [Fact]
    public async Task Scenario09_PromotionMutation_AfterContractCreation_InvoiceUnaffected()
    {
        using var db = CreateDbContext();
        var promo = Percentage(durationMonths: 12, percentage: 10m);
        db.Promotions.Add(promo);

        var fixture = await BuildCommercialAsync(
            db, planId: 2109, monthlyPrice: 1000m, promotions: new[] { promo });
        await db.SaveChangesAsync();

        var firstCycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var firstInvoice = await InvoiceFromCycleAsync(db, firstCycle.Id);

        // Kill the promotion after the contract snapshot was taken.
        Assert.True(promo.Deactivate().IsSuccess);
        await db.SaveChangesAsync();

        var secondCycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026.AddMonths(12), Start2026.AddMonths(24));
        var secondInvoice = await InvoiceFromCycleAsync(db, secondCycle.Id);

        Assert.Equal(10800m, firstInvoice.TotalAmount);
        Assert.Equal(firstInvoice.TotalAmount, secondInvoice.TotalAmount);
        Assert.Equal(1200m, secondInvoice.DiscountAmount);
    }

    // ==================================================================
    // 10. Invoice arithmetic identity
    // ==================================================================
    [Fact]
    public async Task Scenario10_InvoiceArithmeticIdentity_HoldsForDiscountedFixture()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2110, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // INV-01: TotalAmount = Subtotal − DiscountAmount + TaxAmount
        Amount(
            invoice.Subtotal - invoice.DiscountAmount + invoice.TaxAmount,
            invoice.TotalAmount,
            "invoice internal arithmetic");

        // Contract-derivation identity (Task 21.1.1 / 21.2 §4)
        Amount(fixture.Contract.GrossAmount, invoice.Subtotal, "subtotal = contract gross");
        Amount(fixture.Contract.DiscountAmount, invoice.DiscountAmount, "discount = contract discount");
        Amount(fixture.Contract.ContractedAmount, invoice.TotalAmount, "total = contract contracted amount");

        // Subscription-snapshot derivation identity (Task 21.2 §3/§4)
        Amount(
            fixture.Subscription.SnapshotPrice * 12,
            invoice.Subtotal,
            "subtotal = snapshot list price × duration");
        Amount(
            fixture.Subscription.SnapshotMonthlyCharge * 12,
            invoice.TotalAmount,
            "total = snapshot monthly charge × duration");
    }

    // ==================================================================
    // 11. Commercial traceability
    // ==================================================================
    [Fact]
    public async Task Scenario11_Traceability_ContractSubscriptionBillingCycleLinked()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2111, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(fixture.Contract.Id, invoice.ContractId);
        Assert.Equal(fixture.Subscription.Id, invoice.SubscriptionId);
        Assert.Equal(cycle.Id, invoice.BillingCycleId);
        Assert.StartsWith("INV-", invoice.InvoiceNumber);

        var reloadedCycle = await db.BillingCycles.IgnoreQueryFilters().SingleAsync(c => c.Id == cycle.Id);
        Assert.Equal(BillingCycleStatus.Invoiced, reloadedCycle.Status);

        Assert.Equal(DateOnly.FromDateTime(cycle.PeriodStart), invoice.PeriodStart);
        Assert.Equal(DateOnly.FromDateTime(cycle.PeriodEnd), invoice.PeriodEnd);
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
    }

    // ==================================================================
    // 12. No double discount, no zero discount
    // ==================================================================
    [Fact]
    public async Task Scenario12_NoDoubleDiscount_TotalIsNeitherDoubleNorZeroDiscounted()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2112, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        var gross = fixture.Contract.GrossAmount;         // 12000
        var discount = fixture.Contract.DiscountAmount;   // 1200
        var contracted = fixture.Contract.ContractedAmount; // 10800

        Assert.Equal(contracted, invoice.TotalAmount);
        Assert.Equal(gross, invoice.Subtotal);                     // subtotal is NOT discounted too
        Assert.NotEqual(gross, invoice.TotalAmount);               // 12000 → discount dropped entirely
        Assert.NotEqual(contracted - discount, invoice.TotalAmount); // 9600 → discount applied twice
        Assert.NotEqual(gross - (discount * 2), invoice.TotalAmount); // 9600 → discount applied twice
        Assert.Equal(discount, invoice.DiscountAmount);
    }

    // ==================================================================
    // 13. Upgrade / downgrade (ChangeSubscriptionPlan) invoice
    // ==================================================================
    [Fact]
    public async Task Scenario13_ChangePlan_InvoiceDerivedFromNewContractSnapshot()
    {
        using var db = CreateDbContext();

        var oldPlan = BuildPlan(21130, monthlyPrice: 800m, durationMonths: 12);
        db.Plans.Add(oldPlan);
        await db.SaveChangesAsync();

        var oldSubscription = TenantPlan.Create(
            Guid.NewGuid(), TenantA, oldPlan.Id, 800m, 800m, "EGP", 12, 0,
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).Value;
        Assert.True(oldSubscription.Activate(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).IsSuccess);
        db.TenantPlans.Add(oldSubscription);

        var newPlan = BuildPlan(21131, monthlyPrice: 1000m, durationMonths: 12);
        db.Plans.Add(newPlan);

        var promo = Percentage(durationMonths: 12, percentage: 10m);
        db.Promotions.Add(promo);
        await db.SaveChangesAsync();

        var handler = new ChangeSubscriptionPlanHandler(
            db,
            AdminGuard(),
            new SubscriptionFactory(db),
            new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(),
            FrozenClock(Now2026));

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubscription.Id, newPlan.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Errors));

        var newContract = await db.Contracts.IgnoreQueryFilters()
            .Include(c => c.PricingTiers)
            .SingleAsync(c => c.Id == result.Value);

        var invoice = await db.Invoices.IgnoreQueryFilters()
            .SingleAsync(i => i.ContractId == newContract.Id);

        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(1200m, invoice.DiscountAmount);
        Assert.Equal(10800m, invoice.TotalAmount);
        Assert.Equal(newContract.ContractedAmount, invoice.TotalAmount);
        Assert.NotEqual(newContract.ContractedAmount - newContract.DiscountAmount, invoice.TotalAmount);

        var newSubscription = await db.TenantPlans.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == invoice.SubscriptionId);

        Assert.Equal(900m, newSubscription.SnapshotMonthlyCharge);
        Assert.Equal(newContract.Id, newSubscription.ContractId);
        Assert.Equal(newContract.ContractedAmount / 12, newSubscription.SnapshotMonthlyCharge);

        var cycle = await db.BillingCycles.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == invoice.BillingCycleId);
        Assert.Equal(BillingCycleStatus.Invoiced, cycle.Status);
        Assert.Equal(DateOnly.FromDateTime(cycle.PeriodStart), invoice.PeriodStart);
        Assert.Equal(DateOnly.FromDateTime(cycle.PeriodEnd), invoice.PeriodEnd);

        Assert.Equal(SubscriptionStatus.Cancelled,
            (await db.TenantPlans.IgnoreQueryFilters().SingleAsync(t => t.Id == oldSubscription.Id)).Status);
    }

    // ==================================================================
    // 14. Renewal invoice
    // ==================================================================
    [Fact]
    public async Task Scenario14_Renewal_InvoiceDerivedFromNewContractSnapshot()
    {
        using var db = CreateDbContext();

        var plan = BuildPlan(2114, monthlyPrice: 1000m, durationMonths: 12);
        db.Plans.Add(plan);

        var oldSubscription = TenantPlan.Create(
            Guid.NewGuid(), TenantA, plan.Id, 1000m, 1000m, "EGP", 12, 0,
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).Value;
        Assert.True(oldSubscription.Activate(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).IsSuccess);
        db.TenantPlans.Add(oldSubscription);

        var promo = Percentage(durationMonths: 12, percentage: 10m);
        db.Promotions.Add(promo);
        await db.SaveChangesAsync();

        var handler = new RenewSubscriptionOfferHandler(
            db,
            AdminGuard(),
            new SubscriptionFactory(db),
            new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(),
            FrozenClock(Now2026));

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(oldSubscription.Id, PlanId: plan.Id, DurationMonths: 12),
            CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Errors));

        var newContract = await db.Contracts.IgnoreQueryFilters()
            .Include(c => c.PricingTiers)
            .SingleAsync(c => c.Id == result.Value);

        var invoice = await db.Invoices.IgnoreQueryFilters()
            .SingleAsync(i => i.ContractId == newContract.Id);

        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(1200m, invoice.DiscountAmount);
        Assert.Equal(10800m, invoice.TotalAmount);
        Assert.Equal(newContract.ContractedAmount, invoice.TotalAmount);
        Assert.NotEqual(newContract.ContractedAmount - newContract.DiscountAmount, invoice.TotalAmount);

        var newSubscription = await db.TenantPlans.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == invoice.SubscriptionId);

        Assert.Equal(900m, newSubscription.SnapshotMonthlyCharge);
        Assert.Equal(newContract.ContractedAmount / 12, newSubscription.SnapshotMonthlyCharge);

        var cycle = await db.BillingCycles.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == invoice.BillingCycleId);
        Assert.Equal(BillingCycleStatus.Invoiced, cycle.Status);
        Assert.Equal(DateOnly.FromDateTime(cycle.PeriodStart), invoice.PeriodStart);
        Assert.Equal(DateOnly.FromDateTime(cycle.PeriodEnd), invoice.PeriodEnd);
    }

    // ==================================================================
    // Extra 15. One invoice per billing cycle
    // ==================================================================
    [Fact]
    public async Task Scenario15_SecondInvoiceForSameCycle_Rejected_NoDuplicateRow()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2115, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var first = await InvoiceFromCycleAsync(db, cycle.Id);

        var handler = CreateCycleInvoiceHandler(db);
        var second = await handler.Handle(
            new CreateInvoiceFromBillingCycleCommand(cycle.Id), CancellationToken.None);

        Assert.False(second.IsSuccess);
        Assert.NotNull(second.Errors);
        Assert.Equal(1, await db.Invoices.IgnoreQueryFilters().CountAsync());
        Assert.Equal(first.Id, (await db.Invoices.IgnoreQueryFilters().SingleAsync()).Id);
    }

    // ==================================================================
    // Extra 16. Invoice numbers stay unique within the same second
    // ==================================================================
    [Fact]
    public async Task Scenario16_SameTimestampTwoCycles_ProduceDistinctInvoiceNumbers()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2116, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycleA = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var cycleB = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026.AddMonths(12), Start2026.AddMonths(24));

        var invoiceA = await InvoiceFromCycleAsync(db, cycleA.Id, Now2026);
        var invoiceB = await InvoiceFromCycleAsync(db, cycleB.Id, Now2026);

        Assert.Equal(2, await db.Invoices.IgnoreQueryFilters().CountAsync());
        Assert.NotEqual(invoiceA.InvoiceNumber, invoiceB.InvoiceNumber);
    }

    // ==================================================================
    // Extra 17. Issued invoice amounts cannot be mutated afterwards
    // ==================================================================
    [Fact]
    public async Task Scenario17_IssuedInvoice_AmountsAreImmutable()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 2117, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        var issue = invoice.Issue(Now2026, Now2026.AddDays(14));
        Assert.True(issue.IsSuccess, Describe(issue.Errors));

        // Cancel is Draft-only: an issued invoice keeps its commercial amounts.
        var cancel = invoice.Cancel();
        Assert.False(cancel.IsSuccess);

        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(1200m, invoice.DiscountAmount);
        Assert.Equal(10800m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);
    }

    // ==================================================================
    // Extra 18. SnapshotMonthlyCharge semantics (the corrected derivation)
    // ==================================================================
    [Fact]
    public async Task Scenario18_SnapshotMonthlyCharge_IsAlwaysThePostDiscountMonthlyCharge()
    {
        using (var db = CreateDbContext())
        {
            var undiscounted = await BuildCommercialAsync(db, planId: 21181, monthlyPrice: 1000m);
            Assert.Equal(undiscounted.Snapshot.MonthlyListPrice, undiscounted.Snapshot.MonthlyCharge);
            Assert.Equal(undiscounted.Contract.ContractedAmount / 12, undiscounted.Snapshot.MonthlyCharge);
        }

        using (var db = CreateDbContext())
        {
            var discounted = await BuildCommercialAsync(
                db, planId: 21182, monthlyPrice: 1000m,
                promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

            Assert.Equal(900m, discounted.Snapshot.MonthlyCharge);
            Assert.Equal(discounted.Contract.ContractedAmount / 12, discounted.Snapshot.MonthlyCharge);
            Assert.NotEqual(discounted.Snapshot.MonthlyListPrice, discounted.Snapshot.MonthlyCharge);

            // Snapshot semantics (TenantPlan.SnapshotMonthlyCharge docs): post-discount charge.
            Assert.Equal(
                discounted.Contract.ContractedAmount,
                discounted.Snapshot.MonthlyCharge * discounted.Snapshot.DurationMonths);
        }

        using (var db = CreateDbContext())
        {
            var payForX = await BuildCommercialAsync(
                db, planId: 21183, monthlyPrice: 1000m,
                promotions: new[] { PayForXMonths(durationMonths: 12, chargedMonths: 10) });

            Amount(
                payForX.Contract.ContractedAmount,
                payForX.Snapshot.MonthlyCharge * payForX.Snapshot.DurationMonths,
                "pay-for-X snapshot charge × duration = contracted amount");
        }
    }

    private static IPlatformAdminGuard AdminGuard()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        return guard;
    }
}