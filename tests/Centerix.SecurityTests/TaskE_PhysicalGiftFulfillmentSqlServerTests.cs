namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Domain.Platform.Contracts;
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
/// Task E — PhysicalGift Fulfillment Lifecycle SQL Server Integration Tests.
///
/// Uses the <see cref="SqlServerIntegrationFactory"/> collection.
/// Verifies against Local SQL Server (no Docker/Testcontainers):
///   SQL-E01: ContractBenefit FulfillmentStatus round-trip (Pending default)
///   SQL-E02: Grant persists Granted + GrantedAtUtc + GrantedBy
///   SQL-E03: Deliver persists Delivered + DeliveredAtUtc + DeliveredBy
///   SQL-E04: Grant does not auto-deliver
///   SQL-E05: Delivery without prior grant is rejected
///   SQL-E06: Idempotent grant
///   SQL-E07: Idempotent delivery
///   SQL-E08: Cross-tenant grant rejected
///   SQL-E09: Cross-tenant delivery rejected
///   SQL-E10: Offer → Contract → PhysicalGift snapshot
///   SQL-E11: IsGranted stays in sync with FulfillmentStatus
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class TaskE_PhysicalGiftFulfillmentSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public TaskE_PhysicalGiftFulfillmentSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        TaskCFakeCurrentTenant.SetTenantId(tenantId);
    }

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
            code: $"PlanE_{Guid.NewGuid():N}"[..28],
            displayName: "TaskE Plan",
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
        ContractBenefitType type = ContractBenefitType.PhysicalGift,
        BenefitEligibilityStatus eligibility = BenefitEligibilityStatus.NotEligible)
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-E-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow,
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

        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            contract.Id,
            type,
            "Printer",
            null,
            1500m,
            "EGP").Value;
        contract.AddBenefit(benefit);

        if (eligibility == BenefitEligibilityStatus.Eligible)
        {
            benefit.MarkEligible(DateTime.UtcNow, tenantId);
        }

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return (contract, benefit);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E01: New PhysicalGift round-trips with FulfillmentStatus=Pending
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE01_NewPhysicalGift_Persists_FulfillmentStatus_Pending()
    {
        var tenantId = $"E-1-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(tenantId, planId);
        var benefitId = benefit.Id;

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reloaded = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefitId);

        Assert.Equal(FulfillmentStatus.Pending, reloaded.FulfillmentStatus);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, reloaded.EligibilityStatus);
        Assert.False(reloaded.IsGranted);
        Assert.False(reloaded.IsDelivered);
        Assert.Null(reloaded.GrantedAtUtc);
        Assert.Null(reloaded.GrantedBy);
        Assert.Null(reloaded.DeliveredAtUtc);
        Assert.Null(reloaded.DeliveredBy);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E02: Grant persists Granted state
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE02_Grant_Persists_Granted()
    {
        var tenantId = $"E-2-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        // Apply grant via production handler
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));
            Assert.True(result.IsSuccess);
        }

        // Reload
        using var scope2 = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();

        var reloaded = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefitId);

        Assert.Equal(FulfillmentStatus.Granted, reloaded.FulfillmentStatus);
        Assert.True(reloaded.IsGranted);
        Assert.False(reloaded.IsDelivered);
        Assert.NotNull(reloaded.GrantedAtUtc);
        Assert.NotNull(reloaded.GrantedBy);
        Assert.Null(reloaded.DeliveredAtUtc);
        Assert.Null(reloaded.DeliveredBy);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E03: Deliver persists Delivered state
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE03_Deliver_Persists_Delivered()
    {
        var tenantId = $"E-3-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Assert.True((await mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id))).IsSuccess);
            Assert.True((await mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id))).IsSuccess);
        }

        using var scope2 = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();

        var reloaded = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefitId);

        Assert.Equal(FulfillmentStatus.Delivered, reloaded.FulfillmentStatus);
        Assert.True(reloaded.IsGranted);
        Assert.True(reloaded.IsDelivered);
        Assert.NotNull(reloaded.GrantedAtUtc);
        Assert.NotNull(reloaded.DeliveredAtUtc);
        Assert.NotNull(reloaded.DeliveredBy);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E04: Grant does not auto-deliver
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE04_Grant_DoesNotAutoDeliver()
    {
        var tenantId = $"E-4-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Assert.True((await mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id))).IsSuccess);
        }

        using var scope2 = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();

        var reloaded = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefitId);

        Assert.Equal(FulfillmentStatus.Granted, reloaded.FulfillmentStatus);
        Assert.False(reloaded.IsDelivered);
        Assert.Null(reloaded.DeliveredAtUtc);
        Assert.Null(reloaded.DeliveredBy);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E05: Delivery without prior grant is rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE05_Delivery_WithoutGrant_Rejected()
    {
        var tenantId = $"E-5-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefitId));
        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.NotGranted", result.Errors![0].Code);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E06: Idempotent grant
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE06_Grant_Idempotent()
    {
        var tenantId = $"E-6-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        DateTime? firstGrantedAt;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var first = await mediator.Send(new GrantBenefitCommand(contract.Id, benefitId));
            Assert.True(first.IsSuccess);
            firstGrantedAt = first.Value!.GrantedAtUtc;
        }

        await Task.Delay(50);

        DateTime? secondGrantedAt;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var second = await mediator.Send(new GrantBenefitCommand(contract.Id, benefitId));
            Assert.True(second.IsSuccess);
            secondGrantedAt = second.Value!.GrantedAtUtc;
        }

        Assert.Equal(firstGrantedAt, secondGrantedAt);

        using var scope2 = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();

        var reloaded = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefitId);

        Assert.Equal(FulfillmentStatus.Granted, reloaded.FulfillmentStatus);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E07: Idempotent delivery
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE07_Deliver_Idempotent()
    {
        var tenantId = $"E-7-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Assert.True((await mediator.Send(new GrantBenefitCommand(contract.Id, benefitId))).IsSuccess);
            var first = await mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefitId));
            Assert.True(first.IsSuccess);
            var firstDeliveredAt = first.Value!.DeliveredAtUtc;

            await Task.Delay(50);
            var second = await mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefitId));
            Assert.True(second.IsSuccess);
            Assert.Equal(firstDeliveredAt, second.Value!.DeliveredAtUtc);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E08: Cross-tenant grant rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE08_CrossTenant_Grant_Rejected()
    {
        var tenantA = $"E-8A-{Guid.NewGuid():N}"[..16];
        var tenantB = $"E-8B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantA);
        await SeedTenantAsync(tenantB);
        var planId = await EnsurePlanAsync(tenantA);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantA, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantB); // wrong tenant
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GrantBenefitCommand(contract.Id, benefitId));
        Assert.False(result.IsSuccess);
        // Cross-tenant access may surface as Contract.NotFound (tenant filter hides the contract)
        // OR as Contract.Benefit.CrossTenant (defensive check). Both are correct isolation outcomes.
        Assert.True(
            result.Errors![0].Code == "Contract.NotFound"
            || result.Errors![0].Code == "Contract.Benefit.CrossTenant",
            $"Unexpected error code: {result.Errors![0].Code}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E09: Cross-tenant delivery rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE09_CrossTenant_Deliver_Rejected()
    {
        var tenantA = $"E-9A-{Guid.NewGuid():N}"[..16];
        var tenantB = $"E-9B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantA);
        await SeedTenantAsync(tenantB);
        var planId = await EnsurePlanAsync(tenantA);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantA, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        // First grant with the correct tenant
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantA);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Assert.True((await mediator.Send(new GrantBenefitCommand(contract.Id, benefitId))).IsSuccess);
        }

        // Try to deliver with a different tenant
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantB);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefitId));
            Assert.False(result.IsSuccess);
            Assert.True(
                result.Errors![0].Code == "Contract.NotFound"
                || result.Errors![0].Code == "Contract.Benefit.CrossTenant",
                $"Unexpected error code: {result.Errors![0].Code}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E10: EligibilityRule remains structurally equal after persistence
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE10_Full_Lifecycle_PendingThroughDelivered_Persists()
    {
        var tenantId = $"E-10-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Assert.True((await mediator.Send(new GrantBenefitCommand(contract.Id, benefitId))).IsSuccess);
            Assert.True((await mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefitId))).IsSuccess);
        }

        using var scope2 = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();

        var reloaded = await db.Set<ContractBenefit>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(b => b.Id == benefitId);

        Assert.Equal(FulfillmentStatus.Delivered, reloaded.FulfillmentStatus);
        Assert.True(reloaded.IsGranted);
        Assert.True(reloaded.IsDelivered);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-E11: IsGranted stays in sync with FulfillmentStatus
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlE11_IsGranted_StaysInSyncWith_FulfillmentStatus()
    {
        var tenantId = $"E-11-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var (contract, benefit) = await SeedContractAndBenefitAsync(
            tenantId, planId,
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        var benefitId = benefit.Id;

        // Pending: IsGranted false
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Set<ContractBenefit>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(b => b.Id == benefitId);
            Assert.False(reloaded.IsGranted);
            Assert.Equal(FulfillmentStatus.Pending, reloaded.FulfillmentStatus);
        }

        // Grant: IsGranted true
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Assert.True((await mediator.Send(new GrantBenefitCommand(contract.Id, benefitId))).IsSuccess);
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Set<ContractBenefit>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(b => b.Id == benefitId);
            Assert.True(reloaded.IsGranted);
            Assert.Equal(FulfillmentStatus.Granted, reloaded.FulfillmentStatus);
            Assert.False(reloaded.IsDelivered);
        }

        // Deliver: IsGranted still true, IsDelivered true
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Assert.True((await mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefitId))).IsSuccess);
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Set<ContractBenefit>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(b => b.Id == benefitId);
            Assert.True(reloaded.IsGranted);
            Assert.True(reloaded.IsDelivered);
            Assert.Equal(FulfillmentStatus.Delivered, reloaded.FulfillmentStatus);
        }
    }
}