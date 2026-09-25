namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 18.4 SQL Server integration tests against the REAL migrated database.
///
/// Scenarios covered (task sections #4, #9):
/// - Scenario 2: same Offer + same FeatureCode → rejected by UX_OfferFeatures_OfferId_FeatureCode;
/// - Scenario 3: same Offer + different FeatureCode → allowed;
/// - Scenario 4: different Offer + same FeatureCode → allowed;
/// - Scenario 5: contract 12000, consumed 4000, unused 8000, paid 10000 → credit 8000;
/// - Scenario 6: contract 12000, consumed 4000, unused 8000, paid 5000 → credit 5000;
/// - Scenario 7: payment 5000 + eligible (Overpayment) CreditApplication 7000 → settled 12000, credit 8000;
/// - Scenario 8: refunded 5000 is NOT counted as paid settlement.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task18_4CommercialIntegritySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    public Task18_4CommercialIntegritySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // Helpers
    // ==================================================================

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(currentTenant, true);
    }

    private async Task SeedTenantAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId, Identifier = tenantId, Name = tenantId,
                Email = $"{tenantId}@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task<int> EnsurePlanAsync(string codePrefix, decimal price = 1000m, int duration = 12, int bonusMonths = 0)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = $"{codePrefix}_{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, code, "Plan", price, 100, 50, 10, 20, 100, 1000,
            true, null, "EGP", duration, bonusMonths).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private async Task<(Guid SubscriptionId, Guid ContractId, Guid InvoiceId)> SeedPartiallyPaidSubscriptionAsync(
        string tenantId, int planId, decimal paymentAmount, decimal creditAppliedAmount = 0m, decimal refundedAmount = 0m)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 4 months elapsed → consumed = 4 × 1,000 = 4,000 → unused = 8,000
        // (CalculateValueForElapsedMonths: no pricing tiers on the seeded contract →
        //  fallback = MonthlyListPrice × elapsedMonths)
        var startedAt = DateTime.UtcNow.AddMonths(-4);
        var endsAt = startedAt.AddMonths(12);

        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, $"CTR-184-{Guid.NewGuid():N}"[..16],
            planId, startedAt, endsAt, 12,
            1000m, 1000m, "EGP", 12000m, 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        contract.SubmitForApproval();
        contract.Activate(startedAt);
        db.Contracts.Add(contract);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP",
            12, 0, startedAt, false, SubscriptionStatus.Pending).Value;
        sub.Activate(startedAt);
        sub.LinkToContract(contract.Id);
        db.TenantPlans.Add(sub);

        var invoice = Invoice.Create(
            Guid.NewGuid(), $"INV-184-{Guid.NewGuid():N}"[..16],
            DateOnly.FromDateTime(startedAt), DateOnly.FromDateTime(endsAt),
            12000m, 0m, 0m, 12000m, contractId: contract.Id).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);

        var payment = Payment.Create(
            Guid.NewGuid(), $"PAY-184-{Guid.NewGuid():N}"[..16],
            paymentAmount, "EGP", PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);
        db.PaymentAllocations.Add(PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, paymentAmount, DateTime.UtcNow).Value);

        // Optional prior settlement via a Customer Credit (CreditApplication = settlement).
        // Task 18.4.2 policy: only cash-origin credits (Overpayment / SubscriptionChange)
        // count as eligible paid settlement in D-02, so the seeded credit uses Overpayment.
        if (creditAppliedAmount > 0m)
        {
            var priorCredit = TenantCredit.Create(
                Guid.NewGuid(), creditAppliedAmount, CreditSourceType.Overpayment,
                sourceId: null, "EGP", idempotencyKey: $"seed-{contract.Id:N}").Value;
            db.TenantCredits.Add(priorCredit);
            db.CreditApplications.Add(CreditApplication.Create(
                Guid.NewGuid(), priorCredit.Id, invoice.Id, creditAppliedAmount,
                DateTime.UtcNow, $"seed-app-{contract.Id:N}").Value);
            priorCredit.ConsumeAmount(creditAppliedAmount);
        }

        // Optional executed refund against this contract (refunded money is NOT settlement).
        if (refundedAmount > 0m)
        {
            var refund = Refund.Create(
                Guid.NewGuid(), $"REF-184-{Guid.NewGuid():N}"[..16],
                contract.Id, sub.Id, invoice.Id,
                refundedAmount, "EGP", "Test refund", "admin", DateTime.UtcNow).Value;
            refund.Execute("admin", DateTime.UtcNow, $"seed-refund-{contract.Id:N}");
            db.Refunds.Add(refund);
        }

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return (sub.Id, contract.Id, invoice.Id);
    }

    private async Task<Result<Guid>> RunChangePlanAsync(string tenantId, Guid subscriptionId, int newPlanId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(DateTimeOffset.UtcNow);
        var handler = new ChangeSubscriptionPlanHandler(
            db, guard, new SubscriptionFactory(db), new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(), Substitute.For<IAuditWriter>(), timeProvider);
        using var cts = new CancellationTokenSource(TestTimeout);
        return await handler.Handle(new ChangeSubscriptionPlanCommand(subscriptionId, newPlanId), cts.Token);
    }

    private static Offer CreateCalculatedOffer(string tenantId) => Offer.Create(
        Guid.NewGuid(), tenantId, planId: 1,
        durationMonths: 12,
        baseAmount: 12000m, discountAmount: 0m, finalAmount: 12000m,
        monthlyListPrice: 1000m, currencyCode: "EGP",
        calculatedAtUtc: DateTime.UtcNow, expiresAtUtc: DateTime.UtcNow.AddDays(1),
        bonusMonths: 0, maxStudents: 100, maxUsers: 50, maxBranches: 10,
        maxTeachers: 20, storageGb: 100, smsQuota: 1000,
        entitlementSnapshotVersion: Offer.CompleteEntitlementSnapshotVersion).Value;

    // ==================================================================
    // F-18.4.2 — OfferFeature database uniqueness (Scenarios 2-4)
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario2_SameOffer_SameFeatureCode_DuplicateRejectedByDatabase()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001841";
        await SeedTenantAsync(tenantId);
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offer = CreateCalculatedOffer(tenantId);
        db.Offers.Add(offer);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offer.Id, "STUDENTS").Value);
        db.SaveChanges();

        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offer.Id, "STUDENTS").Value);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario2b_SameOffer_SameFeatureCode_CaseVariants_Rejected()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001842";
        await SeedTenantAsync(tenantId);
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offer = CreateCalculatedOffer(tenantId);
        db.Offers.Add(offer);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offer.Id, "STUDENTS").Value);
        db.SaveChanges();

        // Create() normalizes to upper-invariant, so this collides with the same key.
        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offer.Id, "Students").Value);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario3_SameOffer_DifferentFeatureCode_Allowed()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001843";
        await SeedTenantAsync(tenantId);
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offer = CreateCalculatedOffer(tenantId);
        db.Offers.Add(offer);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offer.Id, "STUDENTS").Value);
        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offer.Id, "TEACHERS").Value);
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.OfferFeatures.CountAsync(f => f.OfferId == offer.Id));
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario4_DifferentOffer_SameFeatureCode_Allowed()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001844";
        await SeedTenantAsync(tenantId);
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offerA = CreateCalculatedOffer(tenantId);
        var offerB = CreateCalculatedOffer(tenantId);
        db.Offers.AddRange(offerA, offerB);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offerA.Id, "STUDENTS").Value);
        db.OfferFeatures.Add(OfferFeature.Create(Guid.NewGuid(), offerB.Id, "STUDENTS").Value);
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.OfferFeatures.CountAsync(f => f.OfferId == offerA.Id));
        Assert.Equal(1, await db.OfferFeatures.CountAsync(f => f.OfferId == offerB.Id));
    }

    // ==================================================================
    // F-18.4.4 — D-02 settlement calculation (Scenarios 5, 6, 7, 8)
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario5_Unused8000_Paid10000_CreditEquals8000()
    {
        // Contract 12,000; consumed 4,000; unused 8,000; paid 10,000 → credit 8,000.
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001845";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P184A", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P184B", price: 2000m, duration: 12);

        var (oldSubId, _, _) = await SeedPartiallyPaidSubscriptionAsync(tenantId, oldPlanId, paymentAmount: 10000m);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credit = await db.TenantCredits.SingleAsync(tc =>
            tc.TenantId == tenantId && tc.SourceType == CreditSourceType.SubscriptionChange);
        Assert.Equal(8000m, credit.Amount);

        var application = await db.CreditApplications.SingleAsync(ca => ca.CreditId == credit.Id);
        Assert.Equal(8000m, application.Amount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario6_Unused8000_Paid5000_CreditEquals5000()
    {
        // Contract 12,000; consumed 4,000; unused 8,000; paid only 5,000 → credit 5,000.
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001846";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P184C", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P184D", price: 2000m, duration: 12);

        var (oldSubId, _, _) = await SeedPartiallyPaidSubscriptionAsync(tenantId, oldPlanId, paymentAmount: 5000m);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credit = await db.TenantCredits.SingleAsync(tc =>
            tc.TenantId == tenantId && tc.SourceType == CreditSourceType.SubscriptionChange);
        Assert.Equal(5000m, credit.Amount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario7_Payment5000_Plus_CreditApplication7000_Settled12000_Credit8000()
    {
        // Payment 5,000 + prior eligible (Overpayment) CreditApplication 7,000 = settled 12,000
        // → credit 8,000. Proves CreditApplication from an eligible source IS paid settlement
        // AND no double counting occurs. (Task 18.4.2: a non-eligible source here would
        // settle only the 5,000 cash → credit 5,000.)
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001847";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P184E", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P184F", price: 2000m, duration: 12);

        var (oldSubId, _, _) = await SeedPartiallyPaidSubscriptionAsync(
            tenantId, oldPlanId, paymentAmount: 5000m, creditAppliedAmount: 7000m);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credit = await db.TenantCredits.SingleAsync(tc =>
            tc.TenantId == tenantId && tc.SourceType == CreditSourceType.SubscriptionChange);
        Assert.Equal(8000m, credit.Amount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Scenario8_RefundedAmount_IsNotCountedAsPaidSettlement()
    {
        // Payment 5,000 but 5,000 refunded → paid settlement = 0 → no SubscriptionChange credit.
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001848";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P184G", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P184H", price: 2000m, duration: 12);

        var (oldSubId, _, _) = await SeedPartiallyPaidSubscriptionAsync(
            tenantId, oldPlanId, paymentAmount: 5000m, refundedAmount: 5000m);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var creditCount = await db.TenantCredits.CountAsync(tc =>
            tc.TenantId == tenantId && tc.SourceType == CreditSourceType.SubscriptionChange);
        Assert.Equal(0, creditCount);
    }
}
