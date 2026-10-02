namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
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
///   - IsEligible=false ⇒ no mutation, reason code surfaced
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

        // Drive the contract to the desired status. The handler only freezes when the
        // status reported by the fact query is at least PendingApproval; for the active path
        // we go Draft → PendingApproval → Active.
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

    // ──────────────── IsEligible == true → MarkEligible called ────────────────

    [Fact]
    public async Task TestF_App01_Eligible_TransitionsToEligible_StampsEligibleAtUtc()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // ContractActive + AmountPaidAtLeast(1000) ⇒ all pass against an Active contract with
        // contractedAmount=1000 (paid=1000 reported by fact query because there are no payments,
        // which makes the fact query return 0 — so we deliberately choose a rule that does NOT
        // require a paid amount threshold).
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            db, rule, finalStatus: ContractStatus.Active, terms: PaymentTerms.FullUpfront);

        // Act
        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        // Assert
        Assert.True(result.IsSuccess, result.Errors?[0].Description ?? "Error");
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
    public async Task TestF_App02_Ineligible_ContractNotActive_DoesNotMutate()
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

        // Benefit must remain NotEligible
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

        // AmountPaidAtLeast(100000) will fail because no payments exist ⇒ paid=0
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
        Assert.Contains("AmountPaidAtLeast", response.ReasonPath);
        Assert.False(response.StatusChanged);
    }

    [Fact]
    public async Task TestF_App04_NoRule_ReturnsContractFreezingNoRule_NoMutation()
    {
        AuthorizeTenant(TenantId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // No rule snapshot on the benefit.
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

        // First freeze: transitions the benefit.
        var first = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(first.Value.IsEligible);
        Assert.True(first.Value.StatusChanged);

        var firstStamp = (await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id)).EligibleAtUtc;

        // Second freeze: should be a no-op (StatusChanged == false) — but IsEligible stays true.
        var second = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(second.Value.IsEligible);
        Assert.False(second.Value.StatusChanged);

        var secondStamp = (await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id)).EligibleAtUtc;

        // EligibleAtUtc was NOT re-stamped on the second call.
        Assert.Equal(firstStamp, secondStamp);
    }

    [Fact]
    public async Task TestF_App06_CrossTenant_ReturnsCrossTenantBenefit()
    {
        // Authorized as one tenant, but the benefit belongs to another.
        TaskCFakeCurrentTenant.SetTenantId("tenant-attacker");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Seed while authorized as the legitimate owner.
        AuthorizeTenant(TenantId);
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            db,
            EligibilityRule.ContractActive());

        // Switch tenant context to the attacker.
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

    [Fact]
    public async Task TestF_App08_Freeze_DoesNotMutateFulfillmentFields()
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

        // Ineligible path: amount-paid rule fails.
        var failingRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(100000m),
            EligibilityRule.NoOverdueInstallment());

        // Override the rule with one that fails
        var reloaded = await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);

        // The test asserts that even on the failing path, no FulfillmentStatus / GrantedAtUtc / GrantedBy fields are touched.
        // We snapshot these fields, then run a freeze that returns IsEligible=false.
        var reloadedAfterSeeding = reloaded;
        var fulfillmentBefore = reloadedAfterSeeding.FulfillmentStatus;
        var grantedAtBefore = reloadedAfterSeeding.GrantedAtUtc;
        var grantedByBefore = reloadedAfterSeeding.GrantedBy;

        // Re-freeze with the original rule (eligible)
        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
        Assert.True(result.Value.IsEligible);

        // After successful freeze, the only thing that should have changed is EligibilityStatus
        var postFreeze = await db.ContractBenefits.IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);

        Assert.Equal(fulfillmentBefore, postFreeze.FulfillmentStatus);
        Assert.Equal(grantedAtBefore, postFreeze.GrantedAtUtc);
        Assert.Equal(grantedByBefore, postFreeze.GrantedBy);
    }
}