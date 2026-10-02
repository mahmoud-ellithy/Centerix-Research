namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task E — PhysicalGift Grant &amp; Deliver Application Tests (InMemory).
///
/// Verifies the REAL production handlers (GrantBenefitHandler,
/// MarkBenefitDeliveredHandler) with an InMemory database.
/// </summary>
public class TaskE_PhysicalGiftFulfillmentApplicationTests : IClassFixture<TaskCFakeTenantTestFactory>
{
    private const string TenantId = "tenant-freemonths-c";
    private readonly TaskCFakeTenantTestFactory _factory;
    private readonly IServiceScope _scope;
    private readonly DbContext _db;
    private readonly IMediator _mediator;

    public TaskE_PhysicalGiftFulfillmentApplicationTests(TaskCFakeTenantTestFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
        _db = (DbContext)_scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        _mediator = _scope.ServiceProvider.GetRequiredService<IMediator>();
    }

    private async Task<(Contract contract, ContractBenefit benefit)> SeedContractAndBenefitAsync(
        ContractBenefitType type = ContractBenefitType.PhysicalGift,
        BenefitEligibilityStatus eligibility = BenefitEligibilityStatus.NotEligible)
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-E-INMEM-{Guid.NewGuid():N}"[..16],
            planId: 1,
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
            typeof(ContractBenefit)
                .GetProperty(nameof(ContractBenefit.EligibilityStatus))!
                .SetValue(benefit, eligibility);
        }
        _db.Set<Contract>().Add(contract);
        await _db.SaveChangesAsync();
        return (contract, benefit);
    }

    // ──────────────── Grant ────────────────

    [Fact]
    public async Task TestE_InMem01_Grant_EligiblePhysicalGift_Succeeds()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);

        var result = await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(FulfillmentStatus.Granted, result.Value!.FulfillmentStatus);
        Assert.NotNull(result.Value.GrantedAtUtc);
    }

    [Fact]
    public async Task TestE_InMem02_Grant_NotEligible_Fails()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync();

        var result = await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.NotEligible", result.Errors![0].Code);
    }

    [Fact]
    public async Task TestE_InMem03_Grant_AlreadyGranted_Idempotent()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);

        var firstResult = await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));
        Assert.True(firstResult.IsSuccess);
        var firstGrantedAt = firstResult.Value!.GrantedAtUtc;

        await Task.Delay(5);
        var secondResult = await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));

        Assert.True(secondResult.IsSuccess);
        Assert.Equal(firstGrantedAt, secondResult.Value!.GrantedAtUtc);
    }

    [Fact]
    public async Task TestE_InMem04_Grant_WrongBenefitId_Fails()
    {
        var (contract, _) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);

        var result = await _mediator.Send(new GrantBenefitCommand(contract.Id, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.NotFound", result.Errors![0].Code);
    }

    [Fact]
    public async Task TestE_InMem05_Grant_NonPhysicalGift_Fails()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.Service,
            BenefitEligibilityStatus.Eligible);

        var result = await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors![0].Code);
    }

    // ──────────────── Delivery ────────────────

    [Fact]
    public async Task TestE_InMem06_Delivery_GrantedPhysicalGift_Succeeds()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));

        var result = await _mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsDelivered);
        Assert.NotNull(result.Value.DeliveredAtUtc);
    }

    [Fact]
    public async Task TestE_InMem07_Delivery_Pending_Fails()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);

        var result = await _mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.NotGranted", result.Errors![0].Code);
    }

    [Fact]
    public async Task TestE_InMem08_Delivery_AlreadyDelivered_Idempotent()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));
        var firstDelivery = await _mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id));
        var firstDeliveredAt = firstDelivery.Value!.DeliveredAtUtc;

        await Task.Delay(5);
        var secondDelivery = await _mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id));

        Assert.True(secondDelivery.IsSuccess);
        Assert.Equal(firstDeliveredAt, secondDelivery.Value!.DeliveredAtUtc);
    }

    [Fact]
    public async Task TestE_InMem09_Delivery_NonPhysicalGift_Fails()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.Service,
            BenefitEligibilityStatus.Eligible);

        var result = await _mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors![0].Code);
    }

    // ──────────────── Lifecycle combinations ────────────────

    [Fact]
    public async Task TestE_InMem10_Lifecycle_FullFlow_PendingToDelivered()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);

        Assert.Equal(FulfillmentStatus.Pending, benefit.FulfillmentStatus);

        var grantResult = await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));
        Assert.True(grantResult.IsSuccess);
        Assert.Equal(FulfillmentStatus.Granted, benefit.FulfillmentStatus);

        var deliveryResult = await _mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id));
        Assert.True(deliveryResult.IsSuccess);
        Assert.Equal(FulfillmentStatus.Delivered, benefit.FulfillmentStatus);
    }

    [Fact]
    public async Task TestE_InMem11_Eligibility_FlipsBack_AfterDelivered_FulfillmentUnchanged()
    {
        var (contract, benefit) = await SeedContractAndBenefitAsync(
            ContractBenefitType.PhysicalGift,
            BenefitEligibilityStatus.Eligible);
        await _mediator.Send(new GrantBenefitCommand(contract.Id, benefit.Id));
        await _mediator.Send(new MarkBenefitDeliveredCommand(contract.Id, benefit.Id));

        // Eligibility flips back to NotEligible.
        benefit.MarkNotEligible();
        await _db.SaveChangesAsync();

        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Delivered, benefit.FulfillmentStatus);
        Assert.True(benefit.IsDelivered);
    }
}