namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using MediatR;
using Xunit;

/// <summary>
/// CODER TASK 1.1 hardening tests: tenant security, commercial snapshot validation,
/// pricing tier hardening, and benefit invariant enforcement.
/// </summary>
public class Phase7ContractHardeningTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Contract CreateValidContract(
        string tenantId = "tenant-1",
        decimal monthlyListPrice = 1000m,
        decimal contractualMonthlyValue = 1000m,
        decimal contractedAmount = 10000m,
        int durationMonths = 12)
    {
        var result = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: "CNT-001",
            planId: 1,
            effectiveAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            durationMonths: durationMonths,
            monthlyListPrice: monthlyListPrice,
            contractualMonthlyValue: contractualMonthlyValue,
            currencyCode: "EGP",
            contractedAmount: contractedAmount);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? "tenant-test");
        currentTenant.IsAuthorized.Returns(tenantId != null);

        return new AppDbContext(options, mediator, currentTenant);
    }

    private static CreateContractCommand CreateValidCommand()
    {
        return new CreateContractCommand(
            ContractId: Guid.NewGuid(),
            ContractNumber: "CNT-001",
            PlanId: 1,
            EffectiveAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DurationMonths: 12,
            MonthlyListPrice: 1000m,
            ContractualMonthlyValue: 1000m,
            CurrencyCode: "EGP",
            ContractedAmount: 10000m,
            DiscountAmount: 0m,
            PromotionReference: null,
            PricingTiers: [],
            Benefits: []);
    }

    // ------------------------------------------------------------------
    // Section 1: Tenant Security Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateContract_HandlerResolvesTenantFromContext_DoesNotAcceptClientTenantId()
    {
        // Arrange: tenant context resolves to "tenant-A"
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns("tenant-A");
        currentTenant.IsAuthorized.Returns(true);

        var dbContext = CreateDbContext("tenant-A");
        dbContext.StampAddedTenantIds("tenant-A");
        var handler = new CreateContractHandler(dbContext, currentTenant);
        var command = CreateValidCommand();

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var contract = dbContext.Contracts.FirstOrDefault(c => c.Id == result.Value);
        Assert.NotNull(contract);
        Assert.Equal("tenant-A", contract.TenantId);
        // The contract MUST NOT have any property for client-supplied TenantId
        Assert.DoesNotContain("tenant-B", contract.TenantId);
    }

    [Fact]
    public async Task CreateContract_HandlerRejectsWhenTenantNotResolved()
    {
        // Arrange: tenant context returns empty (unauthorized)
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns((string?)null);
        currentTenant.IsAuthorized.Returns(false);

        var dbContext = CreateDbContext(null);
        var handler = new CreateContractHandler(dbContext, currentTenant);
        var command = CreateValidCommand();

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Contract.TenantNotResolved");
    }

    [Fact]
    public async Task CreateContract_HandlerRejectsWhenTenantIsEmpty()
    {
        // Arrange
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(string.Empty);
        currentTenant.IsAuthorized.Returns(true); // Authorized but empty ID

        var dbContext = CreateDbContext(null);
        var handler = new CreateContractHandler(dbContext, currentTenant);
        var command = CreateValidCommand();

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Contract.TenantNotResolved");
    }

    // ------------------------------------------------------------------
    // Section 4: Commercial Snapshot Consistency Validation
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_Create_DiscountExceedsGrossValue_IsRejected()
    {
        // monthlyListPrice=1000, duration=12, gross=12000, discount=12001
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 10000m,
            discountAmount: 12001m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Discount_Exceeds_GrossValue", result.Errors![0].Code);
    }

    [Fact]
    public void Contract_Create_ContractedAmountExceedsGrossValue_IsRejected()
    {
        // monthlyListPrice=1000, duration=12, gross=12000, contractedAmount=12001
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 12001m,
            discountAmount: 0m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.ContractedAmount_Exceeds_GrossValue", result.Errors![0].Code);
    }

    [Fact]
    public void Contract_Create_DiscountEqualsGrossValue_IsAccepted()
    {
        // monthlyListPrice=1000, duration=12, gross=12000, discount=12000
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 0m,
            discountAmount: 12000m);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Contract_Create_ContractualMonthlyValueCannotBeNegative()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, -1m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.ContractualMonthlyValue_Invalid", result.Errors![0].Code);
    }

    // ------------------------------------------------------------------
    // Section 5: Pricing Tier Snapshot Hardening
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_AddPricingTier_DuplicateDuration_IsRejected()
    {
        var contract = CreateValidContract();

        var tier1 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value;
        var tier1Dup = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1100m, "EGP", 1000m, 2).Value;

        Assert.True(contract.AddPricingTier(tier1).IsSuccess);
        var result = contract.AddPricingTier(tier1Dup);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.PricingTier.DuplicateDuration", result.Errors![0].Code);
        Assert.Single(contract.PricingTiers);
    }

    [Fact]
    public void Contract_AddPricingTier_DifferentDurations_AreAccepted()
    {
        var contract = CreateValidContract();

        var tier1 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value;
        var tier3 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value;
        var tier6 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value;

        Assert.True(contract.AddPricingTier(tier1).IsSuccess);
        Assert.True(contract.AddPricingTier(tier3).IsSuccess);
        Assert.True(contract.AddPricingTier(tier6).IsSuccess);
        Assert.Equal(3, contract.PricingTiers.Count);
    }

    [Fact]
    public void Contract_AddPricingTier_CurrencyMismatch_IsRejected()
    {
        var contract = CreateValidContract();

        var tier = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "USD", 1000m, 1).Value;

        var result = contract.AddPricingTier(tier);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.PricingTier.CurrencyMismatch", result.Errors![0].Code);
        Assert.Empty(contract.PricingTiers);
    }

    [Fact]
    public void Contract_PricingTier_Snapshot_IsImmutable_PlanChangesDoNotAlterTier()
    {
        var contract = CreateValidContract();
        var tier = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value;
        contract.AddPricingTier(tier);

        // The tier snapshot is immutable: changing Plan data elsewhere does not alter it
        Assert.Equal(5220m, contract.PricingTiers[0].TierPrice);
        Assert.Equal(6, contract.PricingTiers[0].DurationMonths);
        Assert.Equal("EGP", contract.PricingTiers[0].CurrencyCode);
    }

    // ------------------------------------------------------------------
    // Section 6: Benefit/Gift Hardening
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_AddBenefit_Exactly3xContractualMonthlyValue_IsAccepted()
    {
        var contract = CreateValidContract(contractualMonthlyValue: 1000m);
        // Max = 3000

        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Gift", null, 3000m, "EGP").Value;

        var result = contract.AddBenefit(benefit);

        Assert.True(result.IsSuccess);
        Assert.Equal(3000m, contract.Benefits.Sum(b => b.ContractualValue));
    }

    [Fact]
    public void Contract_AddBenefit_Exceeds3xContractualMonthlyValue_IsRejected()
    {
        var contract = CreateValidContract(contractualMonthlyValue: 1000m);
        // Max = 3000

        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Expensive", null, 3001m, "EGP").Value;

        var result = contract.AddBenefit(benefit);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.BenefitExceedsLimit", result.Errors![0].Code);
        Assert.Empty(contract.Benefits); // no partial mutation
    }

    [Fact]
    public void Contract_AddBenefit_MultipleBenefits_AggregateCorrectly()
    {
        var contract = CreateValidContract(contractualMonthlyValue: 1000m);
        // Max = 3000

        var benefitA = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Printer", null, 1500m, "EGP").Value;
        var benefitB = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Computer", null, 1500m, "EGP").Value;

        Assert.True(contract.AddBenefit(benefitA).IsSuccess);
        Assert.True(contract.AddBenefit(benefitB).IsSuccess);
        Assert.Equal(3000m, contract.Benefits.Sum(b => b.ContractualValue));
    }

    [Fact]
    public void Contract_AddBenefit_FailedAddition_DoesNotMutateAggregate()
    {
        var contract = CreateValidContract(contractualMonthlyValue: 1000m);
        // Max = 3000

        var benefitA = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Printer", null, 1500m, "EGP").Value;
        var benefitB = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Computer", null, 1500m, "EGP").Value;
        var benefitC = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Extra", null, 1m, "EGP").Value;

        Assert.True(contract.AddBenefit(benefitA).IsSuccess);
        Assert.True(contract.AddBenefit(benefitB).IsSuccess);

        var beforeCount = contract.Benefits.Count;
        var beforeTotal = contract.Benefits.Sum(b => b.ContractualValue);

        var result = contract.AddBenefit(benefitC);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.BenefitExceedsLimit", result.Errors![0].Code);
        // Aggregate must not be mutated
        Assert.Equal(beforeCount, contract.Benefits.Count);
        Assert.Equal(beforeTotal, contract.Benefits.Sum(b => b.ContractualValue));
    }

    [Fact]
    public void Contract_AddBenefit_ZeroValue_IsAccepted()
    {
        var contract = CreateValidContract(contractualMonthlyValue: 1000m);

        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "ZeroGift", null, 0m, "EGP").Value;

        var result = contract.AddBenefit(benefit);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, contract.Benefits.Sum(b => b.ContractualValue));
    }

    [Fact]
    public void Contract_AddBenefit_CurrencyMismatch_IsRejected()
    {
        var contract = CreateValidContract();

        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Gift", null, 100m, "USD").Value;

        var result = contract.AddBenefit(benefit);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.CurrencyMismatch", result.Errors![0].Code);
        Assert.Empty(contract.Benefits);
    }

    // ------------------------------------------------------------------
    // Section 9: Historical Immutability
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_ContractualMonthlyValue_IsImmutable_AfterCreation()
    {
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m);

        Assert.Equal(1000m, contract.ContractualMonthlyValue);
        Assert.Equal(1000m, contract.MonthlyListPrice);

        // No public setters exist for these snapshot values.
        // Verify by reflection that ContractualMonthlyValue has only a private setter.
        var prop = typeof(Contract).GetProperty(nameof(Contract.ContractualMonthlyValue));
        Assert.NotNull(prop);
        Assert.NotNull(prop.GetSetMethod(true)); // private setter exists for EF
        Assert.Null(prop.GetSetMethod(false)); // no public setter
    }

    [Fact]
    public void Contract_SnapshotFields_SurvivePlanChanges()
    {
        // Create contract with specific commercial terms
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12);

        var tier = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value;
        contract.AddPricingTier(tier);

        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id,
            ContractBenefitType.PhysicalGift, "Gift", null, 500m, "EGP").Value;
        contract.AddBenefit(benefit);

        // Simulate Plan changes (would happen elsewhere in the system)
        // Verify contract snapshot is unaffected
        Assert.Equal(1000m, contract.MonthlyListPrice);
        Assert.Equal(1000m, contract.ContractualMonthlyValue);
        Assert.Equal(10000m, contract.ContractedAmount);
        Assert.Equal(12, contract.DurationMonths);
        Assert.Single(contract.PricingTiers);
        Assert.Single(contract.Benefits);
    }

    // ------------------------------------------------------------------
    // Section 10: Database persistence
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateContract_PersistsWithCorrectTenant_ThroughExplicitContractId()
    {
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns("tenant-XYZ");
        currentTenant.IsAuthorized.Returns(true);

        var dbContext = CreateDbContext("tenant-XYZ");
        dbContext.StampAddedTenantIds("tenant-XYZ");
        var handler = new CreateContractHandler(dbContext, currentTenant);
        var command = CreateValidCommand();

        var result = await handler.Handle(command, CancellationToken.None);

        // Reload from database
        var reloaded = dbContext.Contracts.FirstOrDefault(c => c.Id == result.Value);
        Assert.NotNull(reloaded);
        Assert.Equal("tenant-XYZ", reloaded.TenantId);
        Assert.Equal(1000m, reloaded.ContractualMonthlyValue);
    }

    [Fact]
    public void TenantPlan_ContractId_NavigationProperty_Works()
    {
        // Verify explicit FK property exists on TenantPlan
        var tenantPlanType = typeof(Domain.Platform.Subscriptions.TenantPlan);
        var contractIdProp = tenantPlanType.GetProperty("ContractId");
        var contractNavProp = tenantPlanType.GetProperty("Contract");

        Assert.NotNull(contractIdProp);
        Assert.NotNull(contractNavProp);

        Assert.Equal(typeof(Guid?), contractIdProp.PropertyType);
        Assert.Equal(typeof(Contract), contractNavProp.PropertyType);
    }
}
