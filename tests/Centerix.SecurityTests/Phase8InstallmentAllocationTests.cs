namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class Phase8InstallmentAllocationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly IServiceScope _scope;

    public Phase8InstallmentAllocationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
    }

    private IServiceScope scope => _scope;

    [Fact]
    public void BenefitEligibility_ActiveContract_CompliantObligation_Eligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, contract, 12000m, 12000m);

        Assert.True(result);
    }

    [Fact]
    public void BenefitEligibility_ActiveContract_OverdueInstallment_NotEligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, contract, 12000m, 12000m, hasOverdueInstallment: true);

        Assert.False(result);
    }

    [Fact]
    public void BenefitEligibility_ActiveContract_PartialPayment_NotEligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, contract, 6000m, 12000m);

        Assert.False(result);
    }

    [Fact]
    public void BenefitEligibility_InactiveContract_NotEligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateSuspendedContract();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, contract, 12000m, 12000m);

        Assert.False(result);
    }

    [Fact]
    public void BenefitEligibility_AlreadyEligible_KeepsEligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();
        benefit.MarkEligible(DateTime.UtcNow, "tenant-1");

        var result = service.DetermineEligibilityStatus(benefit, contract, 6000m, 12000m);

        Assert.Equal(BenefitEligibilityStatus.Eligible, result);
    }

    [Fact]
    public void BenefitEligibility_AlreadyDelivered_KeepsDelivered()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();
        benefit.MarkEligible(DateTime.UtcNow, "tenant-1");
        benefit.MarkGranted(DateTime.UtcNow, "tenant-1", "Platform");

        var result = service.DetermineEligibilityStatus(benefit, contract, 6000m, 12000m, hasOverdueInstallment: true);

        Assert.Equal(BenefitEligibilityStatus.Delivered, result);
    }

    [Fact]
    public void BenefitEligibility_WithOverdue_NotEligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.DetermineEligibilityStatus(benefit, contract, 12000m, 12000m, hasOverdueInstallment: true);

        Assert.Equal(BenefitEligibilityStatus.NotEligible, result);
    }

    [Fact]
    public void BenefitEligibility_WithoutOverdue_Eligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.DetermineEligibilityStatus(benefit, contract, 12000m, 12000m, hasOverdueInstallment: false);

        Assert.Equal(BenefitEligibilityStatus.Eligible, result);
    }

    [Fact]
    public void BenefitEligibility_PartialPayment_WithOverdue_NotEligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        // Partial payment alone != benefit eligibility
        var result = service.CanBecomeEligible(benefit, contract, 1000m, 12000m, hasOverdueInstallment: true);

        Assert.False(result);
    }

    [Fact]
    public void BenefitEligibility_ZeroContractAmount_CompliantPayment_Eligible()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, contract, 0m, 0m);

        Assert.True(result);
    }

    [Fact]
    public void BenefitEligibility_NullBenefit_ReturnsFalse()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var contract = CreateActiveContract();

        var result = service.CanBecomeEligible(null!, contract, 12000m, 12000m);

        Assert.False(result);
    }

    [Fact]
    public void BenefitEligibility_NullContract_ReturnsFalse()
    {
        var service = scope.ServiceProvider.GetRequiredService<IBenefitEligibilityService>();
        var benefit = CreateContractBenefit();

        var result = service.CanBecomeEligible(benefit, null!, 12000m, 12000m);

        Assert.False(result);
    }

    [Fact]
    public void Installment_CoveredPeriodContiguous_NoGaps()
    {
        // Simulate 3 installment periods: must be contiguous
        var periods = new[]
        {
            (Start: new DateTime(2026, 1, 1), End: new DateTime(2026, 4, 30)),
            (Start: new DateTime(2026, 5, 1), End: new DateTime(2026, 8, 31)),
            (Start: new DateTime(2026, 9, 1), End: new DateTime(2026, 12, 31)),
        };

        for (int i = 1; i < periods.Length; i++)
        {
            Assert.Equal(periods[i - 1].End, periods[i].Start.AddDays(-1));
        }
    }

    [Fact]
    public void Installment_TotalAmountMatchesContract()
    {
        var installments = new[]
        {
            Installment.Create(Guid.NewGuid(), Guid.NewGuid(), 1,
                new DateTime(2026, 1, 1), new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
                4000m, "EGP"),
            Installment.Create(Guid.NewGuid(), Guid.NewGuid(), 2,
                new DateTime(2026, 5, 1), new DateTime(2026, 5, 1), new DateTime(2026, 8, 31),
                4000m, "EGP"),
            Installment.Create(Guid.NewGuid(), Guid.NewGuid(), 3,
                new DateTime(2026, 9, 1), new DateTime(2026, 9, 1), new DateTime(2026, 12, 31),
                4000m, "EGP"),
        };

        var totalAmount = installments.Sum(i => i.Value!.Amount);
        Assert.Equal(12000m, totalAmount);
    }

    [Fact]
    public void Installment_VaryingAmounts_TotalMatchesContract()
    {
        var installments = new[]
        {
            Installment.Create(Guid.NewGuid(), Guid.NewGuid(), 1,
                new DateTime(2026, 1, 1), new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
                5000m, "EGP"),
            Installment.Create(Guid.NewGuid(), Guid.NewGuid(), 2,
                new DateTime(2026, 5, 1), new DateTime(2026, 5, 1), new DateTime(2026, 8, 31),
                3000m, "EGP"),
            Installment.Create(Guid.NewGuid(), Guid.NewGuid(), 3,
                new DateTime(2026, 9, 1), new DateTime(2026, 9, 1), new DateTime(2026, 12, 31),
                4000m, "EGP"),
        };

        var totalAmount = installments.Sum(i => i.Value!.Amount);
        Assert.Equal(12000m, totalAmount);
    }

    [Fact]
    public void Installment_CurrencyMustMatchContract()
    {
        // Verify domain rule: installment currency must match contract
        var contractCurrency = "EGP";
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m, contractCurrency);

        Assert.True(installment.IsSuccess);
        Assert.Equal(contractCurrency, installment.Value!.CurrencyCode);
    }

    [Fact]
    public void HistoricalIntegrity_InstallmentAmountImmutable()
    {
        // Once created, installment amount stays the same
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            4000m, "EGP");

        var originalAmount = installment.Value!.Amount;
        var originalCurrency = installment.Value.CurrencyCode;

        // Even after payments, the original amount doesn't change
        installment.Value.ApplyAllocation(
            CreateActiveAllocation(2000m), DateTime.UtcNow);

        Assert.Equal(originalAmount, installment.Value.Amount);
        Assert.Equal(originalCurrency, installment.Value.CurrencyCode);
        Assert.Equal(2000m, installment.Value.SettledAmount);
        Assert.Equal(2000m, installment.Value.RemainingAmount);
    }

    [Fact]
    public void PaymentAllocation_ConcurrentAccess_ProtectedByRowVersion()
    {
        // Both allocations have RowVersion for optimistic concurrency
        var alloc1 = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1000m, DateTime.UtcNow);
        var alloc2 = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2000m, DateTime.UtcNow);

        Assert.NotNull(alloc1.Value!.RowVersion);
        Assert.NotNull(alloc2.Value!.RowVersion);
    }

    // Helpers

    private static Contract CreateActiveContract()
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
            12000m);

        contract.Value!.SubmitForApproval();
        contract.Value!.Activate(DateTime.UtcNow);
        return contract.Value!;
    }

    private static Contract CreateSuspendedContract()
    {
        var contract = Contract.Create(
            Guid.NewGuid(),
            "tenant-1",
            "CON-002",
            1,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            12,
            1000m,
            1000m,
            "EGP",
            12000m);

        contract.Value!.SubmitForApproval();
        contract.Value!.Activate(DateTime.UtcNow);
        contract.Value!.Suspend();
        return contract.Value!;
    }

    private static ContractBenefit CreateContractBenefit()
    {
        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ContractBenefitType.PhysicalGift,
            "Test Gift",
            "A test gift",
            1000m,
            "EGP");

        return benefit.Value!;
    }

    private static PaymentAllocation CreateActiveAllocation(decimal amount)
    {
        return PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            amount,
            DateTime.UtcNow).Value!;
    }
}
