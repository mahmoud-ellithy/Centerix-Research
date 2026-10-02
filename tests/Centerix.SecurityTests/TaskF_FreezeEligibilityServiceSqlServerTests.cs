namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task F — Freeze Eligibility Service SQL Server Integration Tests.
///
/// Uses the <see cref="SqlServerIntegrationFactory"/> collection. Verifies against Local SQL
/// Server (no Docker/Testcontainers):
///   SQL-F01: Eligible rule snapshot freezes the benefit on real SQL Server
///   SQL-F02: Reason code round-trip across the persistence boundary
///   SQL-F03: Cross-tenant freeze is rejected (no MarkEligible, returns CrossTenantBenefit)
///   SQL-F04: ContractNotActive reason flows end-to-end through SQL Server
///   SQL-F05: Composite (AllOf) reason path is preserved
///   SQL-F06: Eligibility fields persist but FulfillmentStatus / GrantedAtUtc are not touched
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class TaskF_FreezeEligibilityServiceSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public TaskF_FreezeEligibilityServiceSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
        => TaskCFakeCurrentTenant.SetTenantId(tenantId);

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
            code: $"PlanF_{Guid.NewGuid():N}"[..28],
            displayName: "TaskF Plan",
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

    private async Task<(Contract contract, ContractBenefit benefit)> SeedContractAndBenefitAsync(
        string tenantId,
        int planId,
        EligibilityRule? rule,
        ContractStatus finalStatus = ContractStatus.Active)
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-F-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow.AddDays(-30),
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        // Drive the contract to the desired lifecycle status. SubmitForApproval/Activate
        // return Result<Updated>; we ignore the result because the test inputs are constructed
        // to satisfy the precondition (Draft → PendingApproval → Active).
        if (finalStatus == ContractStatus.PendingApproval || finalStatus == ContractStatus.Active || finalStatus == ContractStatus.Suspended)
        {
            contract.SubmitForApproval();
        }
        if (finalStatus == ContractStatus.Active || finalStatus == ContractStatus.Suspended)
        {
            contract.Activate(DateTime.UtcNow);
        }
        if (finalStatus == ContractStatus.Suspended)
        {
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

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return (contract, benefit);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F01: Eligible rule snapshot freezes the benefit on real SQL Server
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF01_EligibleRule_PersistsEligibility_OnSqlServer()
    {
        var tenantId = $"F-1-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.DurationMonthsGte(1));

        var (contract, benefit) = await SeedContractAndBenefitAsync(tenantId, planId, rule);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
            Assert.True(result.IsSuccess, result.Errors?[0].Description ?? "no error");
            Assert.True(result.Value.IsEligible);
            Assert.True(result.Value.StatusChanged);
        }

        using var scope2 = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();

        var refreshed = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefit.Id);

        Assert.Equal(BenefitEligibilityStatus.Eligible, refreshed.EligibilityStatus);
        Assert.NotNull(refreshed.EligibleAtUtc);
        Assert.Equal(FulfillmentStatus.Pending, refreshed.FulfillmentStatus);
        Assert.Null(refreshed.GrantedAtUtc);
        Assert.Null(refreshed.GrantedBy);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F02: Ineligible rule surfaces a reason code end-to-end
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF02_IneligibleRule_ReturnsReasonCode_WithoutMutatingBenefit()
    {
        var tenantId = $"F-2-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        // AmountPaidAtLeast(99999999) — no payments exist, so this will fail.
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.AmountPaidAtLeast(99_999_999m));

        var (contract, benefit) = await SeedContractAndBenefitAsync(tenantId, planId, rule);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));
            Assert.True(result.IsSuccess);
            var response = result.Value;
            Assert.False(response.IsEligible);
            Assert.False(response.StatusChanged);
            Assert.Equal("AmountBelowMinimum", response.ReasonCode);
            Assert.Contains("AmountPaidAtLeast", response.ReasonPath!);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F03: Cross-tenant freeze is rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF03_CrossTenantFreeze_IsRejected()
    {
        var tenantA = $"F-A-{Guid.NewGuid():N}"[..16];
        var tenantB = $"F-B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantA);
        await SeedTenantAsync(tenantB);
        var planId = await EnsurePlanAsync(tenantA);

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment());

        var (contract, benefit) = await SeedContractAndBenefitAsync(tenantA, planId, rule);

        // Switch to tenant B and try to freeze tenant A's benefit.
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantB);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Errors);
        Assert.Contains(result.Errors!, e => e.Code == "Contract.Benefit.CrossTenant");

        // Tenant A's benefit must remain NotEligible on disk.
        using var scope2 = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope2.ServiceProvider, tenantA);
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var refreshed = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, refreshed.EligibilityStatus);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F04: ContractNotActive reason flows through SQL Server
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF04_ContractSuspended_ReturnsContractNotActive()
    {
        var tenantId = $"F-4-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment());

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId, rule, finalStatus: ContractStatus.Suspended);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsEligible);
        Assert.Equal("ContractNotActive", result.Value.ReasonCode);
        Assert.Equal("AllOf[0].ContractActive", result.Value.ReasonPath);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-F05: Composite (AllOf) reason path is preserved
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlF05_AllOfShortCircuit_ReasonPath_Preserved()
    {
        var tenantId = $"F-5-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        // First child: passes. Second child: fails (PaymentTermsMismatch).
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments), // contract is FullUpfront ⇒ mismatch
            EligibilityRule.NoOverdueInstallment());

        var (contract, benefit) = await SeedContractAndBenefitAsync(tenantId, planId, rule);

        using var s = _env.Factory.Services.CreateScope();
        AuthorizeTenant(s.ServiceProvider, tenantId);
        var mediator = s.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new FreezeBenefitEligibilityCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        var r = result.Value;
        Assert.False(r.IsEligible);
        Assert.Equal("PaymentTermsMismatch", r.ReasonCode);
        Assert.Equal("AllOf[1].PaymentTermsEquals(Installments)", r.ReasonPath);
    }
}