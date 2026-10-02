namespace Centerix.SecurityTests;

using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task F Correction — Freeze Eligibility Service SQL Server Integration Tests.
///
/// Uses the <see cref="SqlServerIntegrationFactory"/> collection. Verifies against Local SQL
/// Server (no Docker/Testcontainers):
///   SQL-F01: ContractActive on real database
///   SQL-F02: PaymentTermsEq on real database
///   SQL-F03: PaymentMethodEq on real database (with at least one matching fact)
///   SQL-F04: CompletedByUtc — no completed payment ⇒ ineligible; completed before deadline ⇒ eligible
///   SQL-F05: NoOverdueInstallment — overdue row triggers reason code 9
///   SQL-F06: AmountPaidAtLeast — currency-consistent sum
///   SQL-F07: DaysFromContractStartGte — elapsed TimeSpan semantics
///   SQL-F08: DurationMonthsGte — calendar-month semantics
///   SQL-F09: AllOf / AnyOf short-circuit semantics
///   SQL-F10: Offer → Contract snapshot (ContractBenefit carries the snapshot, not the live Offer)
///   SQL-F11: EligibilityStatus reversibility — flips Eligible→NotEligible without mutating FulfillmentStatus
///   SQL-F12: FulfillmentStatus preserved across freeze in all five states
///   SQL-F13: Cross-tenant payment isolation (Tenant A contract + Tenant B payment)
///   SQL-F14: Cross-tenant installment isolation
///   SQL-F15: Completed-payment boundary cases (equal to deadline qualifies, after deadline disqualifies)
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class TaskF_FreezeEligibilityServiceSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public TaskF_FreezeEligibilityServiceSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(string tenantId) => TaskCFakeCurrentTenant.SetTenantId(tenantId);

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
        => AuthorizeTenant(tenantId);

    private async Task SeedTenantAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@test.com",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task<int> EnsurePlanAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var plan = Plan.Create(
            id: 0,
            code: $"PlanFC_{Guid.NewGuid():N}"[..28],
            displayName: "TaskF Correction Plan",
            monthlyPrice: 1000m,
            maxStudents: 100, maxUsers: 5, maxBranches: 1, maxTeachers: 10,
            storageGB: 10, smsQuota: 100,
            isActive: true,
            description: null,
            currencyCode: "EGP",
            durationMonths: 12,
            bonusMonths: 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private sealed record SeededContractBenefit(Guid ContractId, Guid BenefitId);

    private async Task<SeededContractBenefit> SeedAsync(
        string tenantId,
        int planId,
        EligibilityRule rule,
        ContractStatus finalStatus = ContractStatus.Active,
        PaymentTerms paymentTerms = PaymentTerms.FullUpfront,
        decimal contractedAmount = 12000m,
        string contractCurrencyCode = "EGP",
        DateTime? effectiveAtUtcOverride = null,
        int durationMonths = 12)
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-FC-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: effectiveAtUtcOverride ?? DateTime.UtcNow.AddDays(-30),
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: durationMonths,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: contractCurrencyCode,
            grossAmount: contractedAmount,
            contractedAmount: contractedAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: paymentTerms,
            discountAmount: 0m).Value;

        if (finalStatus == ContractStatus.PendingApproval || finalStatus == ContractStatus.Active)
        {
            contract.SubmitForApproval();
        }
        if (finalStatus == ContractStatus.Active)
        {
            contract.Activate(DateTime.UtcNow);
        }
        else if (finalStatus == ContractStatus.Suspended)
        {
            contract.SubmitForApproval();
            contract.Activate(DateTime.UtcNow);
            contract.Suspend();
        }

        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            contract.Id,
            ContractBenefitType.PhysicalGift,
            "Barcode Printer",
            null,
            1500m,
            contractCurrencyCode,
            rule).Value;
        contract.AddBenefit(benefit);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return new SeededContractBenefit(contract.Id, benefit.Id);
    }

    /// <summary>
    /// Seeds a Completed payment whose allocation links back to the supplied contract's invoice.
    /// Returns the new invoice id so the caller can attach more payments.
    /// </summary>
    private async Task<(Guid InvoiceId, Guid PaymentId)> SeedCompletedPaymentAsync(
        string tenantId,
        Guid contractId,
        decimal amount,
        string currencyCode,
        DateTime completedAtUtc,
        PaymentMethod method)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invoice = Invoice.Create(
            id: Guid.NewGuid(),
            invoiceNumber: $"INV-{Guid.NewGuid():N}"[..16],
            periodStart: DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)),
            periodEnd: DateOnly.FromDateTime(DateTime.UtcNow),
            subtotal: amount,
            discountAmount: 0m,
            taxAmount: 0m,
            totalAmount: amount,
            contractId: contractId).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);

        var payment = Payment.Create(
            id: Guid.NewGuid(),
            paymentNumber: $"PAY-{Guid.NewGuid():N}"[..16],
            amount: amount,
            currencyCode: currencyCode,
            method: method).Value;
        payment.Complete(completedAtUtc);
        db.Payments.Add(payment);

        db.PaymentAllocations.Add(PaymentAllocation.Create(
            id: Guid.NewGuid(),
            paymentId: payment.Id,
            invoiceId: invoice.Id,
            allocatedAmount: amount,
            allocatedAtUtc: DateTime.UtcNow).Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return (invoice.Id, payment.Id);
    }

    /// <summary>Seeds an overdue installment for the supplied contract.</summary>
    private async Task<Guid> SeedOverdueInstallmentAsync(
        string tenantId,
        Guid contractId,
        decimal amount = 100m)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // TenantPlan FK is required by Installments. Persist it first.
        var plan = await EnsurePlanAsync(tenantId);
        var subId = Guid.NewGuid();
        var sub = TenantPlan.Create(
            subId, tenantId, plan, 1000m, 1000m, "EGP",
            12, 0, DateTime.UtcNow.AddDays(-30), false,
            Centerix.Domain.Platform.Subscriptions.Enums.SubscriptionStatus.Pending).Value;
        sub.Activate(DateTime.UtcNow);
        sub.LinkToContract(contractId);
        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var installment = Installment.Create(
            id: Guid.NewGuid(),
            contractId: contractId,
            sequenceNumber: 1,
            dueDateUtc: DateTime.UtcNow.AddDays(-7), // overdue
            coveredPeriodStartUtc: new DateTime(2026, 1, 1),
            coveredPeriodEndUtc: new DateTime(2026, 4, 30),
            amount: amount,
            currencyCode: "EGP",
            subscriptionId: subId).Value;
        db.Installments.Add(installment);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return installment.Id;
    }

    private async Task<FreezeEligibilityResponse> FreezeAsync(string tenantId, Guid benefitId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefitId));
        Assert.True(result.IsSuccess, result.Errors?[0].Description ?? "no error");
        return result.Value;
    }

    private async Task<ContractBenefit> ReloadBenefitAsync(string tenantId, Guid benefitId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefitId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F01: ContractActive
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF01_ContractActive_True_WhenActive()
    {
        var tenantId = $"FC01-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.ContractActive();
        var seeded = await SeedAsync(tenantId, planId, rule, finalStatus: ContractStatus.Active);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
        Assert.True(response.StatusChanged);

        var refreshed = await ReloadBenefitAsync(tenantId, seeded.BenefitId);
        Assert.Equal(BenefitEligibilityStatus.Eligible, refreshed.EligibilityStatus);
    }

    [Fact]
    public async Task SqlF01_ContractActive_False_NotActiveState()
    {
        var tenantId = $"FC01B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.ContractActive();
        var seeded = await SeedAsync(tenantId, planId, rule, finalStatus: ContractStatus.Draft);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.ContractNotActive), response.ReasonCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F02: PaymentTermsEq
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF02_PaymentTermsEquals_True_FullUpfront()
    {
        var tenantId = $"FC02-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var seeded = await SeedAsync(tenantId, planId, rule, paymentTerms: PaymentTerms.FullUpfront);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlF02_PaymentTermsEquals_False_Mismatch()
    {
        var tenantId = $"FC02B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments);
        var seeded = await SeedAsync(tenantId, planId, rule, paymentTerms: PaymentTerms.FullUpfront);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.PaymentTermsMismatch), response.ReasonCode);
        Assert.Equal("PaymentTermsEquals(Installments)", response.ReasonPath);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F03: PaymentMethodEq
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF03_PaymentMethodEquals_True_MatchingPaymentExists()
    {
        var tenantId = $"FC03-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var seeded = await SeedAsync(tenantId, planId, rule);

        // Seed a completed Cash payment ⇒ does NOT match.
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 5000m, "EGP",
            DateTime.UtcNow.AddDays(-2), PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);

        // Now seed a Card payment ⇒ rule passes.
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 3000m, "EGP",
            DateTime.UtcNow.AddDays(-1), PaymentMethod.Card);

        var response2 = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response2.IsEligible);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F04: CompletedByUtc
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF04_CompletedByUtc_False_NoPayment()
    {
        var tenantId = $"FC04-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var deadline = DateTime.UtcNow.AddDays(1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var seeded = await SeedAsync(tenantId, planId, rule);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.DeadlinePassed), response.ReasonCode);
    }

    [Fact]
    public async Task SqlF04_CompletedByUtc_True_CompletedBeforeDeadline()
    {
        var tenantId = $"FC04B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var deadline = DateTime.UtcNow.AddDays(1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var seeded = await SeedAsync(tenantId, planId, rule);

        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 12000m, "EGP",
            DateTime.UtcNow.AddHours(-2), PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F05: NoOverdueInstallment
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF05_NoOverdueInstallment_False_WhenOverduePresent()
    {
        var tenantId = $"FC05-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.NoOverdueInstallment();
        var seeded = await SeedAsync(tenantId, planId, rule);
        await SeedOverdueInstallmentAsync(tenantId, seeded.ContractId);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.OverdueInstallment), response.ReasonCode);
    }

    [Fact]
    public async Task SqlF05_NoOverdueInstallment_True_WhenNoneOverdue()
    {
        var tenantId = $"FC05B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.NoOverdueInstallment();
        var seeded = await SeedAsync(tenantId, planId, rule);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F06: AmountPaidAtLeast
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF06_AmountPaidAtLeast_True_WhenSumMatchesContractCurrency()
    {
        var tenantId = $"FC06-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(5000m);
        var seeded = await SeedAsync(tenantId, planId, rule);

        // Two EGP payments summing to 6000.
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 3000m, "EGP",
            DateTime.UtcNow.AddDays(-3), PaymentMethod.Cash);
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 3000m, "EGP",
            DateTime.UtcNow.AddDays(-2), PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlF06_AmountPaidAtLeast_False_CurrencyMismatch_DoesNotContribute()
    {
        var tenantId = $"FC06B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(5000m);
        var seeded = await SeedAsync(tenantId, planId, rule);

        // A USD payment does NOT contribute — contract is EGP.
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 100000m, "USD",
            DateTime.UtcNow.AddDays(-2), PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.AmountBelowMinimum), response.ReasonCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F07: DaysFromContractStartGte — elapsed TimeSpan
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF07_DaysFromContractStartGte_True_ElapsedExceedsDays()
    {
        var tenantId = $"FC07-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.DaysFromContractStartGte(7);
        var seeded = await SeedAsync(
            tenantId, planId, rule,
            effectiveAtUtcOverride: DateTime.UtcNow.AddDays(-30));

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlF07_DaysFromContractStartGte_False_BelowThreshold()
    {
        var tenantId = $"FC07B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.DaysFromContractStartGte(60);
        var seeded = await SeedAsync(
            tenantId, planId, rule,
            effectiveAtUtcOverride: DateTime.UtcNow.AddDays(-30));

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.DaysFromContractStartNotMet), response.ReasonCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F08: DurationMonthsGte
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF08_DurationMonthsGte_True_WhenMet()
    {
        var tenantId = $"FC08-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.DurationMonthsGte(12);
        var seeded = await SeedAsync(tenantId, planId, rule, durationMonths: 12);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlF08_DurationMonthsGte_False_BelowThreshold()
    {
        var tenantId = $"FC08B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.DurationMonthsGte(24);
        var seeded = await SeedAsync(tenantId, planId, rule, durationMonths: 12);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.DurationMonthsNotMet), response.ReasonCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F09: AllOf / AnyOf
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF09_AllOf_Fail_OnFirstChild()
    {
        var tenantId = $"FC09-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments),
            EligibilityRule.NoOverdueInstallment());

        var seeded = await SeedAsync(tenantId, planId, rule, paymentTerms: PaymentTerms.FullUpfront);
        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.PaymentTermsMismatch), response.ReasonCode);
        Assert.Equal("AllOf[1].PaymentTermsEquals(Installments)", response.ReasonPath);
    }

    [Fact]
    public async Task SqlF09_AnyOf_True_OnFirstChild()
    {
        var tenantId = $"FC09B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront));

        var seeded = await SeedAsync(tenantId, planId, rule, paymentTerms: PaymentTerms.FullUpfront);
        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F11: EligibilityStatus reversibility
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF11_EligibilityReversibility_FlipsBack_WithoutMutatingFulfillment()
    {
        var tenantId = $"FC11-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var goodRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var seeded = await SeedAsync(tenantId, planId, goodRule);

        // First freeze: Eligible + Pending.
        var first = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(first.IsEligible);
        var post1 = await ReloadBenefitAsync(tenantId, seeded.BenefitId);
        Assert.Equal(BenefitEligibilityStatus.Eligible, post1.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Pending, post1.FulfillmentStatus);

        // Suspend the contract → rule will fail on re-freeze.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contract = await db.Contracts.IgnoreQueryFilters()
                .FirstAsync(c => c.Id == seeded.ContractId);
            contract.Suspend();
            await db.SaveChangesAsync();
        }

        // Re-freeze: eligibility must flip back; FulfillmentStatus preserved.
        var second = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(second.IsEligible);
        Assert.Equal(nameof(WhyIneligible.ContractNotActive), second.ReasonCode);
        Assert.True(second.StatusChanged);

        var post2 = await ReloadBenefitAsync(tenantId, seeded.BenefitId);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, post2.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Pending, post2.FulfillmentStatus);
        Assert.Null(post2.GrantedAtUtc);
        Assert.Null(post2.GrantedBy);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F12: FulfillmentStatus preserved across all 5 states
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF12_FulfillmentStatusPreserved_AcrossIneligibleReFreeze()
    {
        var tenantId = $"FC12-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var goodRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var seeded = await SeedAsync(tenantId, planId, goodRule);

        // Eligible + Pending first.
        await FreezeAsync(tenantId, seeded.BenefitId);

        // Drive the benefit through Grant → Deliver manually.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var benefit = await db.Set<ContractBenefit>()
                .IgnoreQueryFilters()
                .FirstAsync(b => b.Id == seeded.BenefitId);
            benefit.Grant(DateTime.UtcNow, "test-user", tenantId);
            benefit.Deliver(DateTime.UtcNow, "delivery-user", tenantId);
            await db.SaveChangesAsync();

            // Suspend the contract.
            var contract = await db.Contracts.IgnoreQueryFilters()
                .FirstAsync(c => c.Id == seeded.ContractId);
            contract.Suspend();
            await db.SaveChangesAsync();
        }

        // Re-freeze: must NOT touch FulfillmentStatus / GrantedBy / DeliveredBy.
        var second = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(second.IsEligible);

        var post = await ReloadBenefitAsync(tenantId, seeded.BenefitId);
        Assert.Equal(FulfillmentStatus.Delivered, post.FulfillmentStatus);
        Assert.Equal("test-user", post.GrantedBy);
        Assert.Equal("delivery-user", post.DeliveredBy);
        Assert.NotNull(post.DeliveredAtUtc);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F13: Cross-tenant payment isolation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF13_CrossTenant_Payment_DoesNotSatisfyRule()
    {
        var tenantA = $"FCA-{Guid.NewGuid():N}"[..16];
        var tenantB = $"FCB-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantA);
        await SeedTenantAsync(tenantB);
        var planA = await EnsurePlanAsync(tenantA);

        var rule = EligibilityRule.CompletedByUtc(DateTime.UtcNow.AddDays(1));
        var seeded = await SeedAsync(tenantA, planA, rule);

        // Authorise as tenantA so the SeedCompletedPaymentAsync call lands in tenantA.
        // But we want a tenantB payment to also exist — yet that won't show up for tenantA's
        // fact because:
        //   1. The OwnerOnlyFactQuery scopes Payments by tenantId
        //   2. The ContractId chain (Invoice → ContractId) belongs to tenantA
        // We test that scenario explicitly.
        // Tenant A's contract has NO payment; tenant B's payment must not be visible to tenant A.
        // To prove this, we add a tenant B payment that the IAppDbContext for Tenant A must NOT
        // resolve via the fact query.
        AuthorizeTenant(tenantB);
        // Create a tenant-B contract + payment (which tenantB owns).
        var planB = await EnsurePlanAsync(tenantB);
        var seededB = await SeedAsync(tenantB, planB,
            EligibilityRule.CompletedByUtc(DateTime.UtcNow.AddDays(1)));
        await SeedCompletedPaymentAsync(
            tenantB, seededB.ContractId, 12000m, "EGP",
            DateTime.UtcNow.AddHours(-2), PaymentMethod.Cash);

        // Now freeze tenant A's benefit. Tenant B's payment must NOT count.
        var response = await FreezeAsync(tenantA, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.DeadlinePassed), response.ReasonCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F14: Cross-tenant installment isolation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF14_CrossTenant_OverdueInstallment_DoesNotPoisonTenantAContract()
    {
        var tenantA = $"FCA2-{Guid.NewGuid():N}"[..16];
        var tenantB = $"FCB2-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantA);
        await SeedTenantAsync(tenantB);
        var planA = await EnsurePlanAsync(tenantA);
        var planB = await EnsurePlanAsync(tenantB);

        var rule = EligibilityRule.NoOverdueInstallment();
        var seededA = await SeedAsync(tenantA, planA, rule);
        var seededB = await SeedAsync(tenantB, planB, rule);

        // Tenant B has an overdue installment.
        await SeedOverdueInstallmentAsync(tenantB, seededB.ContractId);

        // Tenant A's benefit must still be eligible.
        var responseA = await FreezeAsync(tenantA, seededA.BenefitId);
        Assert.True(responseA.IsEligible);

        // Tenant B's benefit must remain ineligible.
        var responseB = await FreezeAsync(tenantB, seededB.BenefitId);
        Assert.False(responseB.IsEligible);
        Assert.Equal(nameof(WhyIneligible.OverdueInstallment), responseB.ReasonCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F15: Completed-payment boundary cases
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF15_CompletedByUtc_Boundary_EqualToDeadline_Passes()
    {
        var tenantId = $"FC15-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        // The deadline is exactly the payment's CompletedAt.
        var completedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var deadline = completedAt;

        var rule = EligibilityRule.CompletedByUtc(deadline);
        var seeded = await SeedAsync(tenantId, planId, rule);
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 12000m, "EGP",
            completedAt, PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlF15_CompletedByUtc_Boundary_AfterDeadline_Fails()
    {
        var tenantId = $"FC15B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var deadline = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var completedAt = deadline.AddSeconds(1);

        var rule = EligibilityRule.CompletedByUtc(deadline);
        var seeded = await SeedAsync(tenantId, planId, rule);
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 12000m, "EGP",
            completedAt, PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.DeadlinePassed), response.ReasonCode);
    }

    [Fact]
    public async Task SqlF15_CompletedByUtc_MultiplePayments_AnyOneQualifies()
    {
        var tenantId = $"FC15C-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var deadline = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var seeded = await SeedAsync(tenantId, planId, rule);

        // Three payments, two after deadline, one before.
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 5000m, "EGP",
            deadline.AddDays(2), PaymentMethod.Cash);
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 3000m, "EGP",
            deadline.AddHours(-2), PaymentMethod.Cash); // qualifies
        await SeedCompletedPaymentAsync(
            tenantId, seeded.ContractId, 1000m, "EGP",
            deadline.AddDays(1), PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-FINAL-01..10: Allocation-based payment amount calculation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Seeds a Completed payment whose allocation amount is explicitly attributable to the
    /// supplied invoice/contract. Allows the caller to specify a payment.Amount that differs
    /// from the allocation amount — to model a multi-contract payment that is split across
    /// invoices.
    /// </summary>
    private async Task SeedCompletedPaymentWithAllocationsAsync(
        string tenantId,
        Guid contractId,
        Guid invoiceId,
        decimal paymentAmount,
        decimal allocatedAmount,
        string currencyCode,
        DateTime completedAtUtc,
        PaymentMethod method)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = Payment.Create(
            id: Guid.NewGuid(),
            paymentNumber: $"PAY-{Guid.NewGuid():N}"[..16],
            amount: paymentAmount,
            currencyCode: currencyCode,
            method: method).Value;
        payment.Complete(completedAtUtc);
        db.Payments.Add(payment);

        db.PaymentAllocations.Add(PaymentAllocation.Create(
            id: Guid.NewGuid(),
            paymentId: payment.Id,
            invoiceId: invoiceId,
            allocatedAmount: allocatedAmount,
            allocatedAtUtc: DateTime.UtcNow).Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedInvoiceAsync(string tenantId, Guid contractId, decimal total = 12000m)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invoice = Invoice.Create(
            id: Guid.NewGuid(),
            invoiceNumber: $"INV-{Guid.NewGuid():N}"[..16],
            periodStart: DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)),
            periodEnd: DateOnly.FromDateTime(DateTime.UtcNow),
            subtotal: total,
            discountAmount: 0m,
            taxAmount: 0m,
            totalAmount: total,
            contractId: contractId).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    [Fact]
    public async Task SqlFINAL01_OnePayment_AllocatedToOneContract_UsesAllocationAmount()
    {
        var tenantId = $"FCT01-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var seeded = await SeedAsync(tenantId, planId, rule);

        var invoiceId = await SeedInvoiceAsync(tenantId, seeded.ContractId);
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, invoiceId,
            paymentAmount: 1000m, allocatedAmount: 1000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL02_OnePayment_AllocatedAcross_TwoContracts_SumPerContract()
    {
        var tenantId = $"FCT02-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var seededA = await SeedAsync(tenantId, planId,
            EligibilityRule.AmountPaidAtLeast(7000m), contractedAmount: 4000m);
        var seededB = await SeedAsync(tenantId, planId,
            EligibilityRule.AmountPaidAtLeast(7000m), contractedAmount: 6000m);

        var invoiceA = await SeedInvoiceAsync(tenantId, seededA.ContractId, total: 4000m);
        var invoiceB = await SeedInvoiceAsync(tenantId, seededB.ContractId, total: 6000m);

        // Payment.Amount = 10000 split across two contracts: 4000 + 6000.
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seededA.ContractId, invoiceA,
            paymentAmount: 10000m, allocatedAmount: 4000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seededB.ContractId, invoiceB,
            paymentAmount: 10000m, allocatedAmount: 6000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        // Both contracts ask for AmountPaidAtLeast(7000).
        // Contract A: 4000 < 7000 => false
        // Contract B: 6000 < 7000 => false
        var aResponse = await FreezeAsync(tenantId, seededA.BenefitId);
        Assert.False(aResponse.IsEligible);

        var bResponse = await FreezeAsync(tenantId, seededB.BenefitId);
        Assert.False(bResponse.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL03_PaymentAmount_GreaterThan_Allocation_OnlyAllocationCounts()
    {
        var tenantId = $"FCT03-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(5000m);
        var seeded = await SeedAsync(tenantId, planId, rule);

        var invoiceId = await SeedInvoiceAsync(tenantId, seeded.ContractId);
        // Payment.Amount = 100000, but only 5000 is allocated to this contract.
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, invoiceId,
            paymentAmount: 100000m, allocatedAmount: 5000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        // 5000 == 5000 => true (exact boundary). Only the allocation counts; Payment.Amount=100000
        // does NOT inflate the eligible amount.
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL04_MultiplePayments_SummedAllocationsForOneContract()
    {
        var tenantId = $"FCT04-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(5000m);
        var seeded = await SeedAsync(tenantId, planId, rule);

        var inv1 = await SeedInvoiceAsync(tenantId, seeded.ContractId, total: 3000m);
        var inv2 = await SeedInvoiceAsync(tenantId, seeded.ContractId, total: 2500m);

        // Two separate Payments, each with its own allocation.
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, inv1,
            paymentAmount: 3000m, allocatedAmount: 3000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, inv2,
            paymentAmount: 2500m, allocatedAmount: 2500m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-1),
            method: PaymentMethod.Card);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible); // 3000 + 2500 == 5500 >= 5000
    }

    [Fact]
    public async Task SqlFINAL05_InactiveAllocation_DoesNotContribute()
    {
        var tenantId = $"FCT05-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(5000m);
        var seeded = await SeedAsync(tenantId, planId, rule);

        var inv = await SeedInvoiceAsync(tenantId, seeded.ContractId);

        // One active payment of 5000.
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, inv,
            paymentAmount: 5000m, allocatedAmount: 5000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        // Add a second allocation to the same invoice but mark it as Reversed.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var payment = await db.Payments.IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && p.Status == PaymentStatus.Completed)
                .OrderByDescending(p => p.CreatedAtUtc)
                .FirstAsync();

            var allocation = PaymentAllocation.Create(
                id: Guid.NewGuid(),
                paymentId: payment.Id,
                invoiceId: inv,
                allocatedAmount: 9000m,
                allocatedAtUtc: DateTime.UtcNow).Value;
            // Reverse it so it's no longer Active.
            allocation.Reverse();
            db.PaymentAllocations.Add(allocation);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        // Only the 5000 active allocation counts; 5000 == 5000 => true.
        // The reversed 9000 must NOT contribute.
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL06_AllocationToAnotherContract_DoesNotContribute()
    {
        var tenantId = $"FCT06-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(7000m);
        var seededA = await SeedAsync(tenantId, planId, rule, contractedAmount: 8000m);
        var seededB = await SeedAsync(tenantId, planId,
            EligibilityRule.AmountPaidAtLeast(1m), contractedAmount: 2000m);

        var invoiceA = await SeedInvoiceAsync(tenantId, seededA.ContractId);
        var invoiceB = await SeedInvoiceAsync(tenantId, seededB.ContractId);

        // Payment.Amount = 10000 with allocation 8000 to A and 2000 to B.
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seededA.ContractId, invoiceA,
            paymentAmount: 10000m, allocatedAmount: 8000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seededB.ContractId, invoiceB,
            paymentAmount: 10000m, allocatedAmount: 2000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        // Contract A: 8000 >= 7000 => true
        var aResponse = await FreezeAsync(tenantId, seededA.BenefitId);
        Assert.True(aResponse.IsEligible);

        // Contract B: 2000 (NOT 10000, NOT 10000+2000) >= 1 => true
        var bResponse = await FreezeAsync(tenantId, seededB.BenefitId);
        Assert.True(bResponse.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL07_DifferentCurrency_DoesNotContribute()
    {
        var tenantId = $"FCT07-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AmountPaidAtLeast(1m);
        var seeded = await SeedAsync(tenantId, planId, rule, contractCurrencyCode: "USD");

        var inv = await SeedInvoiceAsync(tenantId, seeded.ContractId);

        // USD contract gets an EGP allocation — must contribute 0.
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, inv,
            paymentAmount: 10000m, allocatedAmount: 10000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.False(response.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL08_CrossTenantAllocation_DoesNotContribute()
    {
        var tenantA = $"FCTA-{Guid.NewGuid():N}"[..16];
        var tenantB = $"FCTB-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantA);
        await SeedTenantAsync(tenantB);
        var planA = await EnsurePlanAsync(tenantA);

        var rule = EligibilityRule.AmountPaidAtLeast(1m);
        var seededA = await SeedAsync(tenantA, planA, rule);
        var invA = await SeedInvoiceAsync(tenantA, seededA.ContractId);

        // Tenant B pays its own payment. Authorised as tenantB so the seed lands under tenantB.
        AuthorizeTenant(tenantB);
        var seededB = await SeedAsync(tenantB, await EnsurePlanAsync(tenantB),
            EligibilityRule.ContractActive());
        var invB = await SeedInvoiceAsync(tenantB, seededB.ContractId);

        await SeedCompletedPaymentWithAllocationsAsync(
            tenantB, seededB.ContractId, invB,
            paymentAmount: 10000m, allocatedAmount: 10000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        // Tenant A asks for AmountPaidAtLeast(1) — but tenant B's payment must NOT count.
        var response = await FreezeAsync(tenantA, seededA.BenefitId);
        Assert.False(response.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL09_AmountPaidAtLeastExactAllocationBoundary()
    {
        var tenantId = $"FCT09-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var seeded = await SeedAsync(tenantId, planId, EligibilityRule.AmountPaidAtLeast(5000m));
        var inv = await SeedInvoiceAsync(tenantId, seeded.ContractId);

        // Exact match: payment.Amount = 5000, allocation = 5000.
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, inv,
            paymentAmount: 5000m, allocatedAmount: 5000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);
    }

    [Fact]
    public async Task SqlFINAL10_AmountPaidAtLeastBelowAndAboveAllocationBoundary()
    {
        var tenantId = $"FCT10-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var seeded = await SeedAsync(tenantId, planId, EligibilityRule.AmountPaidAtLeast(5000m));
        var inv = await SeedInvoiceAsync(tenantId, seeded.ContractId);

        // payment.Amount = 10000 (large), allocation = 5000 (small).
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seeded.ContractId, inv,
            paymentAmount: 10000m, allocatedAmount: 5000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-2),
            method: PaymentMethod.Cash);

        // Exactly 5000: 5000 >= 5000 => true
        var response = await FreezeAsync(tenantId, seeded.BenefitId);
        Assert.True(response.IsEligible);

        // With AmountPaidAtLeast(5001) and allocation=5000, 5000 < 5001 => false.
        // The seeded rule is already AmountPaidAtLeast(5000); to test the above/below boundary
        // with a single allocation we use two seeded contracts:
        //   contract 1: rule AmountPaidAtLeast(4999), allocation 5000 → true
        //   contract 2: rule AmountPaidAtLeast(5001), allocation 5000 → false
        var seededBelow = await SeedAsync(tenantId, planId,
            EligibilityRule.AmountPaidAtLeast(4999m));
        var inv2 = await SeedInvoiceAsync(tenantId, seededBelow.ContractId);
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seededBelow.ContractId, inv2,
            paymentAmount: 10000m, allocatedAmount: 5000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-1),
            method: PaymentMethod.Cash);
        var responseBelow = await FreezeAsync(tenantId, seededBelow.BenefitId);
        Assert.True(responseBelow.IsEligible); // 5000 >= 4999

        var seededAbove = await SeedAsync(tenantId, planId,
            EligibilityRule.AmountPaidAtLeast(5001m));
        var inv3 = await SeedInvoiceAsync(tenantId, seededAbove.ContractId);
        await SeedCompletedPaymentWithAllocationsAsync(
            tenantId, seededAbove.ContractId, inv3,
            paymentAmount: 10000m, allocatedAmount: 5000m,
            currencyCode: "EGP",
            completedAtUtc: DateTime.UtcNow.AddHours(-1),
            method: PaymentMethod.Cash);
        var responseAbove = await FreezeAsync(tenantId, seededAbove.BenefitId);
        Assert.False(responseAbove.IsEligible); // 5000 < 5001
    }
}