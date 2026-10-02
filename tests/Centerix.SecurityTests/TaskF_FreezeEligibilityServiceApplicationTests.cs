namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task F — Freeze Eligibility Application Tests (InMemory).
///
/// Uses the production DI graph with the InMemory EF provider via
/// <see cref="TaskCFakeTenantTestFactory"/>. The <c>OwnerOnlyFactQueryEfAdapter</c> queries the
/// same InMemory DbContext so end-to-end freeze behavior can be exercised without SQL Server.
///
/// Coverage:
///   - FreezeBenefitEligibilityCommand dispatches through the production pipeline
///   - IsEligible=true ⇒ MarkEligible + EligibleAtUtc stamped, FulfillmentStatus untouched
///   - IsEligible=false ⇒ MarkNotEligible, FulfillmentStatus preserved
///   - EligibilityStatus reversibility (5 cases): NotEligible→Eligible, Eligible→NotEligible
///     under Pending, Granted, Delivered, AppliedToSubscription.
///   - Idempotency: re-freeze is a no-op
///   - Cross-tenant guard returns CrossTenantBenefit
///   - Missing rule snapshot is reported as "ContractFreezing.NoRule"
/// </summary>
public class TaskF_FreezeEligibilityServiceApplicationTests : IClassFixture<TaskCFakeTenantTestFactory>
{
    private const string TenantId = "tenant-freemonths-c";
    private readonly TaskCFakeTenantTestFactory _factory;

    public TaskF_FreezeEligibilityServiceApplicationTests(TaskCFakeTenantTestFactory factory)
        => _factory = factory;

    private static void AuthorizeTenant(string tenantId) => TaskCFakeCurrentTenant.SetTenantId(tenantId);

    private async Task<(Contract contract, ContractBenefit benefit)> SeedContractAndBenefitAsync(
        IAppDbContext db,
        EligibilityRule? rule,
        ContractStatus finalStatus = ContractStatus.Active,
        PaymentTerms terms = PaymentTerms.FullUpfront,
        decimal contractedAmount = 1000m)
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-F-{Guid.NewGuid():N}"[..16],
            planId: 1,
            effectiveAtUtc: DateTime.UtcNow.AddDays(-30),
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: contractedAmount,
            contractedAmount: contractedAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: terms,
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
            "EGP",
            rule).Value;

        contract.AddBenefit(benefit);

        db.Contracts.Add(contract);
        db.StampAddedTenantIds(TenantId);
        await db.SaveChangesAsync();

        return (contract, benefit);
    }

    // ──────────────── Eligible path ────────────────

    [Fact]
    public async Task TestF_App01_Eligible_TransitionsToEligible_StampsEligibleAtUtc()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            db, rule, finalStatus: ContractStatus.Active, terms: PaymentTerms.FullUpfront);

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.True(result.IsSuccess, result.Errors?[0].Description ?? "no error");
        var response = result.Value;
        Assert.True(response.IsEligible);
        Assert.True(response.StatusChanged);
        Assert.Null(response.ReasonCode);

        // Verify the benefit was actually transitioned
        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var reloaded = await db2.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.Eligible, reloaded.EligibilityStatus);
        Assert.NotNull(reloaded.EligibleAtUtc);

        // FulfillmentStatus is downstream of eligibility — MUST NOT be touched by freeze.
        Assert.Equal(FulfillmentStatus.Pending, reloaded.FulfillmentStatus);
        Assert.Null(reloaded.GrantedAtUtc);
        Assert.Null(reloaded.GrantedBy);
    }

    [Fact]
    public async Task TestF_App02_Ineligible_ContractNotActive_SyncsBackToNotEligible()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment());

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            db, rule, finalStatus: ContractStatus.Suspended);

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.ContractNotActive), response.ReasonCode);
        Assert.False(response.StatusChanged);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var reloaded = await db2.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, reloaded.EligibilityStatus);
        Assert.Null(reloaded.EligibleAtUtc);
    }

    [Fact]
    public async Task TestF_App03_Ineligible_AmountBelowMinimum_DoesNotMutate()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // No payments seeded ⇒ paidAmount=false
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(100000m),
            EligibilityRule.NoOverdueInstallment());

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule);

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.False(response.IsEligible);
        Assert.Equal(nameof(WhyIneligible.AmountBelowMinimum), response.ReasonCode);
        Assert.Contains("AmountPaidAtLeast", response.ReasonPath!);
        Assert.False(response.StatusChanged);
    }

    [Fact]
    public async Task TestF_App04_NoRule_ReturnsContractFreezingNoRule_NoMutation()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule: null);

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.False(response.IsEligible);
        Assert.Equal("ContractFreezing.NoRule", response.ReasonCode);
        Assert.False(response.StatusChanged);
    }

    [Fact]
    public async Task TestF_App05_Idempotency_AlreadyEligible_ReFreezeIsNoOp()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule);

        var first = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(first.Value.IsEligible);
        Assert.True(first.Value.StatusChanged);

        var firstStamp = (await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id)).EligibleAtUtc;

        var second = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(second.Value.IsEligible);
        Assert.False(second.Value.StatusChanged);

        var secondStamp = (await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id)).EligibleAtUtc;

        Assert.Equal(firstStamp, secondStamp);
    }

    [Fact]
    public async Task TestF_App06_CrossTenant_ReturnsCrossTenantBenefit()
    {
        TaskCFakeCurrentTenant.SetTenantId("tenant-attacker");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Seed while authorized as the legitimate owner.
        AuthorizeTenant(TenantId);
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            db,
            EligibilityRule.ContractActive());

        // Switch tenant context.
        TaskCFakeCurrentTenant.SetTenantId("tenant-attacker");

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Errors);
        Assert.Contains(result.Errors!, e => e.Code == "Contract.Benefit.CrossTenant");
    }

    [Fact]
    public async Task TestF_App07_BenefitNotFound_ReturnsNotFound()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Errors);
        Assert.Contains(result.Errors!, e => e.Code == "Contract.Benefit.NotFound");
    }

    // ──────────────── EligibilityStatus reversibility ────────────────

    [Fact]
    public async Task TestF_App10_EligibilityReversibility_Case1_NotEligiblePending_RuleTrue_EligiblePending()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule);

        // Pre-state: NotEligible + Pending.
        var pre = await db.ContractBenefits.IgnoreQueryFilters().FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, pre.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Pending, pre.FulfillmentStatus);

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(result.Value.IsEligible);
        Assert.True(result.Value.StatusChanged);

        var post = await db.ContractBenefits.IgnoreQueryFilters().FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.Eligible, post.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Pending, post.FulfillmentStatus); // preserved
    }

    [Fact]
    public async Task TestF_App11_EligibilityReversibility_Case2_EligiblePending_RuleFalse_NotEligiblePending()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule);

        // First freeze: becomes Eligible + Pending.
        var first = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(first.Value.IsEligible);

        // Suspend the contract so the rule no longer evaluates true.
        var freshContract = await db.Contracts.IgnoreQueryFilters()
            .FirstAsync(c => c.Id == contract.Id);
        freshContract.Suspend();
        await db.SaveChangesAsync();

        var second = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.False(second.Value.IsEligible);
        Assert.Equal(nameof(WhyIneligible.ContractNotActive), second.Value.ReasonCode);

        var post = await db.ContractBenefits.IgnoreQueryFilters().FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, post.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Pending, post.FulfillmentStatus);
        Assert.Null(post.GrantedAtUtc);
        Assert.Null(post.GrantedBy);
    }

    [Fact]
    public async Task TestF_App12_EligibilityReversibility_Case3_EligibleGranted_RuleFalse_NotEligibleGranted_Preserved()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule);

        // First freeze: Eligible + Pending.
        var first = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(first.Value.IsEligible);

        // Grant the benefit so FulfillmentStatus → Granted.
        var benefitForGrant = await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);
        benefitForGrant.Grant(DateTime.UtcNow, "test-user", TenantId);
        await db.SaveChangesAsync();

        // Suspend the contract so the rule no longer evaluates true.
        var freshContract = await db.Contracts.IgnoreQueryFilters()
            .FirstAsync(c => c.Id == contract.Id);
        freshContract.Suspend();
        await db.SaveChangesAsync();

        // Re-freeze: eligibility flips back, but FulfillmentStatus must remain Granted.
        var second = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.False(second.Value.IsEligible);

        var post = await db.ContractBenefits.IgnoreQueryFilters().FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, post.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Granted, post.FulfillmentStatus); // preserved
        Assert.NotNull(post.GrantedAtUtc);
        Assert.Equal("test-user", post.GrantedBy);
    }

    [Fact]
    public async Task TestF_App13_EligibilityReversibility_Case4_EligibleDelivered_RuleFalse_NotEligibleDelivered_Preserved()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule);

        var first = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(first.Value.IsEligible);

        // Grant + Deliver.
        var fresh = await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);
        fresh.Grant(DateTime.UtcNow, "test-user", TenantId);
        fresh.Deliver(DateTime.UtcNow, "delivery-user", TenantId);
        await db.SaveChangesAsync();

        // Suspend the contract so the rule no longer evaluates true.
        var freshContract = await db.Contracts.IgnoreQueryFilters()
            .FirstAsync(c => c.Id == contract.Id);
        freshContract.Suspend();
        await db.SaveChangesAsync();

        var second = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.False(second.Value.IsEligible);

        var post = await db.ContractBenefits.IgnoreQueryFilters().FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, post.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Delivered, post.FulfillmentStatus); // preserved
        Assert.NotNull(post.DeliveredAtUtc);
        Assert.Equal("delivery-user", post.DeliveredBy);
    }

    [Fact]
    public async Task TestF_App14_Freeze_DoesNotMutateFulfillmentFields_OnEligiblePath()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(db, rule);

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(result.Value.IsEligible);

        var post = await db.ContractBenefits.IgnoreQueryFilters().FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(FulfillmentStatus.Pending, post.FulfillmentStatus);
        Assert.Null(post.GrantedAtUtc);
        Assert.Null(post.GrantedBy);
        Assert.Null(post.DeliveredAtUtc);
        Assert.Null(post.DeliveredBy);
    }
}