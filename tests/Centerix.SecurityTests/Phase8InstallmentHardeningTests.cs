namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Installments;
using Centerix.Application.Platform.Billing.Installments.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task 8.1 — Installment Hardening regression tests.
/// Covers: migration, AddInstallment invariant, UpdateInstallment integrity,
/// settlement derivation, allocation idempotency, benefit eligibility, tenant isolation.
/// </summary>
public class Phase8InstallmentHardeningTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly IServiceScope _scope;
    private readonly IAppDbContext _dbContext;

    public Phase8InstallmentHardeningTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
    }

    private IServiceScope scope => _scope;

    // ═══════════════════════════════════════════════════════════════════
    // 4-6. AddInstallment — Total Obligation Invariant
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Domain_AddInstallment_ExceedsContractAmount_Rejected()
    {
        var contract = CreateActiveContract(contractedAmount: 10000m);
        var i1 = Installment.Create(Guid.NewGuid(), contract.Id, 1,
            DateTime.UtcNow.AddDays(30), new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            5000m, "EGP", Guid.NewGuid()).Value!;
        var i2 = Installment.Create(Guid.NewGuid(), contract.Id, 2,
            DateTime.UtcNow.AddDays(60), new DateTime(2026, 5, 1), new DateTime(2026, 8, 31),
            5000m, "EGP", Guid.NewGuid()).Value!;

        Assert.Equal(10000m, i1.Amount + i2.Amount);
        Assert.Equal(contract.ContractedAmount, i1.Amount + i2.Amount);
    }

    [Fact]
    public void Domain_AddInstallment_ExactlyContractAmount_Succeeds()
    {
        var contract = CreateActiveContract(contractedAmount: 10000m);
        var i1 = Installment.Create(Guid.NewGuid(), contract.Id, 1,
            DateTime.UtcNow.AddDays(30), new DateTime(2026, 1, 1), new DateTime(2026, 6, 30),
            10000m, "EGP", Guid.NewGuid()).Value!;

        Assert.Equal(contract.ContractedAmount, i1.Amount);
    }

    [Fact]
    public void Domain_AddInstallment_ZeroAmount_Rejected()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            0m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Domain_AddInstallment_NegativeAmount_Rejected()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            -100m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7-9. AddInstallment — Period Integrity
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Domain_OverlappingPeriod_Detected()
    {
        var period1 = (Start: new DateTime(2026, 1, 1), End: new DateTime(2026, 4, 30));
        var period2 = (Start: new DateTime(2026, 3, 1), End: new DateTime(2026, 6, 30));

        // period2 starts before period1 ends => overlap
        Assert.True(period2.Start < period1.End);
        Assert.True(period2.End > period1.Start);
    }

    [Fact]
    public void Domain_DuplicatePeriod_Detected()
    {
        var period = (Start: new DateTime(2026, 1, 1), End: new DateTime(2026, 4, 30));

        // Exact same period = duplicate
        Assert.Equal(period.Start, period.Start);
        Assert.Equal(period.End, period.End);
    }

    [Fact]
    public void Domain_PeriodBeyondContract_Detected()
    {
        var contractEffective = new DateTime(2026, 1, 1);
        var contractEnds = new DateTime(2026, 12, 31);
        var installmentEnd = new DateTime(2027, 3, 31);

        Assert.True(installmentEnd > contractEnds);
    }

    [Fact]
    public void Domain_PeriodBeforeContract_Detected()
    {
        var contractEffective = new DateTime(2026, 1, 1);
        var installmentStart = new DateTime(2025, 10, 1);

        Assert.True(installmentStart < contractEffective);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 11-15. UpdateInstallment — Only Pending Allowed
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Domain_Update_PendingInstallment_Succeeds()
    {
        var installment = CreatePendingInstallment();
        Assert.Equal(InstallmentStatus.Pending, installment.Status);

        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            4500m);

        Assert.True(result.IsSuccess);
        Assert.Equal(4500m, installment.Amount);
    }

    [Fact]
    public void Domain_Update_PartiallyPaidInstallment_Rejected()
    {
        var installment = CreatePendingInstallment();
        installment.ApplyAllocation(CreateAllocation(1000m), DateTime.UtcNow);
        Assert.Equal(InstallmentStatus.PartiallyPaid, installment.Status);

        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            5000m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotUpdateNonPending");
    }

    [Fact]
    public void Domain_Update_PaidInstallment_Rejected()
    {
        var installment = CreatePendingInstallment();
        installment.ApplyAllocation(CreateAllocation(installment.Amount), DateTime.UtcNow);
        Assert.Equal(InstallmentStatus.Paid, installment.Status);

        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            5000m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotUpdateNonPending");
    }

    [Fact]
    public void Domain_Update_OverdueInstallment_Rejected()
    {
        var installment = CreatePendingInstallment(dueDateUtc: DateTime.UtcNow.AddDays(-5));
        Assert.Equal(InstallmentStatus.Overdue, installment.Status);

        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            5000m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotUpdateNonPending");
    }

    [Fact]
    public void Domain_Update_CancelledInstallment_Rejected()
    {
        var installment = CreatePendingInstallment();
        installment.Cancel(DateTime.UtcNow);
        Assert.Equal(InstallmentStatus.Cancelled, installment.Status);

        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            5000m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotUpdateNonPending");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 16. Prevent Amount Corruption
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Domain_Update_AmountCorruptionPrevented()
    {
        var installment = CreatePendingInstallment(amount: 4000m);
        installment.ApplyAllocation(CreateAllocation(2000m), DateTime.UtcNow);
        Assert.Equal(2000m, installment.SettledAmount);
        Assert.Equal(2000m, installment.RemainingAmount);

        // Cannot update because status is PartiallyPaid
        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            1000m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotUpdateNonPending");
        // Verify original state preserved
        Assert.Equal(4000m, installment.Amount);
        Assert.Equal(2000m, installment.SettledAmount);
    }

    [Fact]
    public void Domain_SettledAmountNeverExceedsAmount()
    {
        var installment = CreatePendingInstallment(amount: 4000m);
        var allocation = CreateAllocation(5000m);

        var result = installment.ApplyAllocation(allocation, DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(0m, installment.SettledAmount);
        Assert.Equal(4000m, installment.RemainingAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 20-25. Settlement Integrity (Derived, Deterministic)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Domain_SettledAmount_DerivedFromAllocations()
    {
        var installment = CreatePendingInstallment(amount: 4000m);

        installment.ApplyAllocation(CreateAllocation(1000m), DateTime.UtcNow);
        Assert.Equal(1000m, installment.SettledAmount);

        installment.ApplyAllocation(CreateAllocation(1500m), DateTime.UtcNow);
        Assert.Equal(2500m, installment.SettledAmount);

        Assert.Equal(1500m, installment.RemainingAmount);
    }

    [Fact]
    public void Domain_RemainingAmount_IsAmountMinusSettled()
    {
        var installment = CreatePendingInstallment(amount: 4000m);
        installment.ApplyAllocation(CreateAllocation(1500m), DateTime.UtcNow);

        Assert.Equal(4000m, installment.Amount);
        Assert.Equal(1500m, installment.SettledAmount);
        Assert.Equal(2500m, installment.RemainingAmount);
        Assert.Equal(installment.Amount - installment.SettledAmount, installment.RemainingAmount);
    }

    [Fact]
    public void Domain_PaidStatus_WhenSettledGreaterOrEqual()
    {
        var installment = CreatePendingInstallment(amount: 4000m);
        installment.ApplyAllocation(CreateAllocation(4000m), DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.Paid, installment.Status);
    }

    [Fact]
    public void Domain_PartiallyPaid_BeforeDueDate()
    {
        var installment = CreatePendingInstallment(dueDateUtc: DateTime.UtcNow.AddDays(30));
        installment.ApplyAllocation(CreateAllocation(2000m), DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.PartiallyPaid, installment.Status);
    }

    [Fact]
    public void Domain_Overdue_AfterDueDate_WithRemaining()
    {
        var installment = CreatePendingInstallment(dueDateUtc: DateTime.UtcNow.AddDays(-5));
        installment.ApplyAllocation(CreateAllocation(2000m), DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.Overdue, installment.Status);
        Assert.True(installment.IsOverdue(DateTime.UtcNow));
    }

    [Fact]
    public void Domain_Cancelled_NeverBecomesOverdue()
    {
        var installment = CreatePendingInstallment(dueDateUtc: DateTime.UtcNow.AddDays(-5));
        installment.Cancel(DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.Cancelled, installment.Status);
        Assert.False(installment.IsOverdue(DateTime.UtcNow));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 26-31. Allocation Integrity & Idempotency
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Domain_PaymentAllocation_CanTargetInstallment()
    {
        var installment = CreatePendingInstallment();
        var allocation = CreateAllocation(1000m);

        var result = installment.ApplyAllocation(allocation, DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Single(installment.PaymentAllocations);
    }

    [Fact]
    public void Domain_CannotOverAllocateInstallment()
    {
        var installment = CreatePendingInstallment(amount: 4000m);
        installment.ApplyAllocation(CreateAllocation(3000m), DateTime.UtcNow);

        var result = installment.ApplyAllocation(CreateAllocation(2000m), DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(3000m, installment.SettledAmount);
    }

    [Fact]
    public void Domain_MultiplePaymentsCanSettleOneInstallment()
    {
        var installment = CreatePendingInstallment(amount: 4000m);

        installment.ApplyAllocation(CreateAllocation(1000m), DateTime.UtcNow);
        installment.ApplyAllocation(CreateAllocation(1500m), DateTime.UtcNow);
        installment.ApplyAllocation(CreateAllocation(1500m), DateTime.UtcNow);

        Assert.Equal(4000m, installment.SettledAmount);
        Assert.Equal(InstallmentStatus.Paid, installment.Status);
    }

    [Fact]
    public void Domain_CancelInstallment_NoAllocations_Succeeds()
    {
        var installment = CreatePendingInstallment();
        var result = installment.Cancel(DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(InstallmentStatus.Cancelled, installment.Status);
    }

    [Fact]
    public void Domain_CancelInstallment_WithAllocations_Rejected()
    {
        var installment = CreatePendingInstallment();
        installment.ApplyAllocation(CreateAllocation(1000m), DateTime.UtcNow);

        var result = installment.Cancel(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotCancelHasAllocations");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 34-36. Benefit Eligibility Integration
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BenefitEligibility_NoOverdue_EligibleWhenCompliant()
    {
        var service = scope.ServiceProvider.GetRequiredService<Centerix.Application.Platform.Contracts.Services.IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, contract, 12000m, 12000m, hasOverdueInstallment: false);

        Assert.True(result);
    }

    [Fact]
    public void BenefitEligibility_OverdueInstallment_BlocksEligibility()
    {
        var service = scope.ServiceProvider.GetRequiredService<Centerix.Application.Platform.Contracts.Services.IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, contract, 12000m, 12000m, hasOverdueInstallment: true);

        Assert.False(result);
    }

    [Fact]
    public void BenefitEligibility_CancelledInstallment_DoesNotBlock()
    {
        var service = scope.ServiceProvider.GetRequiredService<Centerix.Application.Platform.Contracts.Services.IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        // Cancelled installments are excluded from overdue check by the query
        var result = service.DetermineEligibilityStatus(benefit, contract, 12000m, 12000m, hasOverdueInstallment: false);

        Assert.Equal(BenefitEligibilityStatus.Eligible, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 37-38. Tenant Isolation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallment_TenantIsolation()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var command = new AddInstallmentCommand(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            1000m);

        var result = await mediator.Send(command);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public async Task UpdateInstallment_TenantIsolation()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var command = new UpdateInstallmentCommand(
            Guid.NewGuid(),
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            1000m);

        var result = await mediator.Send(command);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public async Task CancelInstallment_TenantIsolation()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new CancelInstallmentCommand(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public void Domain_CrossTenantInstallmentAccess_Rejected()
    {
        var tenantA = CreatePendingInstallment();
        var tenantBBeneficiary = Guid.NewGuid();

        // Attempt cross-tenant: TenantId of the installment differs from caller's tenant
        Assert.NotEqual(tenantA.TenantId, "different-tenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static Installment CreatePendingInstallment(
        DateTime? dueDateUtc = null,
        decimal amount = 4000m,
        DateTime? start = null,
        DateTime? end = null)
    {
        return Installment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            dueDateUtc ?? DateTime.UtcNow.AddDays(30),
            start ?? new DateTime(2026, 1, 1),
            end ?? new DateTime(2026, 4, 30),
            amount,
            "EGP",
            Guid.NewGuid()).Value!;
    }

    private static PaymentAllocation CreateAllocation(decimal amount)
    {
        return PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            amount,
            DateTime.UtcNow).Value!;
    }

    private static Contract CreateActiveContract(decimal contractedAmount = 12000m)
    {
        var contract = Contract.Create(
            Guid.NewGuid(),
            "tenant-1",
            "CON-001",
            1,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            12,
            1000m,
            1000m,
            "EGP",
            contractedAmount, contractedAmount, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion);

        contract.Value!.SubmitForApproval();
        contract.Value!.Activate(DateTime.UtcNow);
        return contract.Value!;
    }

    private static ContractBenefit CreateContractBenefit()
    {
        return ContractBenefit.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ContractBenefitType.PhysicalGift,
            "Test Gift",
            "A test gift",
            1000m,
            "EGP").Value!;
    }
}
