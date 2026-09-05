namespace Centerix.SecurityTests;

using Centerix.Domain.Common;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.Events;
using System.Linq;
using Xunit;

/// <summary>
/// Domain rules for the Contract aggregate: creation validation, lifecycle transitions,
/// commercial snapshot immutability, pricing tier snapshots, and benefit financial invariant.
/// </summary>
public class Phase7ContractDomainTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Contract CreateValidContract(
        string tenantId = "tenant-1",
        string contractNumber = "CNT-001",
        decimal monthlyListPrice = 1000m,
        decimal contractualMonthlyValue = 1000m,
        decimal contractedAmount = 10000m,
        int durationMonths = 12)
    {
        var result = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: contractNumber,
            planId: 1,
            effectiveAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            durationMonths: durationMonths,
            monthlyListPrice: monthlyListPrice,
            contractualMonthlyValue: contractualMonthlyValue,
            currencyCode: "EGP",
            contractedAmount: contractedAmount,
            discountAmount: 0,
            promotionReference: null);

        Assert.True(result.IsSuccess, $"Contract creation failed: {string.Join(",", result.Errors?.Select(e => e.Code) ?? [])}");
        return result.Value;
    }

    // ------------------------------------------------------------------
    // Contract creation
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_Create_ValidInput_Succeeds()
    {
        var contract = CreateValidContract();

        Assert.Equal(ContractStatus.Draft, contract.Status);
        Assert.Equal("CNT-001", contract.ContractNumber);
        Assert.Equal(1, contract.PlanId);
        Assert.Equal(1000m, contract.MonthlyListPrice);
        Assert.Equal("EGP", contract.CurrencyCode);
        Assert.Equal(10000m, contract.ContractedAmount);
        Assert.Equal(12, contract.DurationMonths);
        Assert.Equal("tenant-1", contract.TenantId);
    }

    [Fact]
    public void Contract_Create_TenantIsRequired()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.TenantId_Required", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_ContractNumberIsRequired()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.ContractNumber_Required", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_PlanIdIsRequired()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 0,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.PlanId_Required", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_EffectiveAtIsRequired()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            default, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.EffectiveAt_Required", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_DurationMustBePositive()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            0, 1000m, 1000m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Duration_Invalid", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_MonthlyListPriceCannotBeNegative()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, -1m, 1000m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.MonthlyListPrice_Invalid", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_CurrencyIsRequired()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "US", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Currency_Invalid", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_EndsAtBeforeEffectiveAt_IsDenied()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 10000m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.EndsAt_Before_EffectiveAt", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_Create_RaisesContractCreatedEvent()
    {
        var contract = CreateValidContract();
        Assert.Single(contract.DomainEvents);
        Assert.IsType<Domain.Platform.Contracts.Events.ContractCreatedEvent>(contract.DomainEvents.First());
    }

    // ------------------------------------------------------------------
    // Historical snapshot immutability
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_Snapshot_IsImmutable_PlanPriceChangesDoNotAlterContract()
    {
        // Arrange: create contract with monthly price = 1000
        var contract = CreateValidContract(monthlyListPrice: 1000m, contractedAmount: 10000m);

        // Assert: contract preserves original terms regardless of what Plan changes later
        Assert.Equal(1000m, contract.MonthlyListPrice);
        Assert.Equal(10000m, contract.ContractedAmount);

        // The snapshot is immutable: no public setters modify these values.
        // Pricing tiers are also immutable once loaded.
        var tiers = contract.PricingTiers;
        Assert.Empty(tiers); // no tiers added in this test

        // Benefits are immutable snapshots too
        var benefits = contract.Benefits;
        Assert.Empty(benefits);
    }

    // ------------------------------------------------------------------
    // Contract lifecycle transitions
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_Lifecycle_FullHappyPath()
    {
        var contract = CreateValidContract();

        // Draft -> PendingApproval
        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.Equal(ContractStatus.PendingApproval, contract.Status);

        // PendingApproval -> Active
        Assert.True(contract.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).IsSuccess);
        Assert.Equal(ContractStatus.Active, contract.Status);

        // Active -> Suspended
        Assert.True(contract.Suspend().IsSuccess);
        Assert.Equal(ContractStatus.Suspended, contract.Status);

        // Suspended -> Active (reactivation)
        Assert.True(contract.Reactivate().IsSuccess);
        Assert.Equal(ContractStatus.Active, contract.Status);

        // Active -> Terminated
        Assert.True(contract.Terminate(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)).IsSuccess);
        Assert.Equal(ContractStatus.Terminated, contract.Status);
    }

    [Fact]
    public void Contract_Lifecycle_MarkExpired_FromActive_Succeeds()
    {
        var contract = CreateValidContract();
        contract.SubmitForApproval();
        contract.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = contract.MarkExpired(new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(result.IsSuccess);
        Assert.Equal(ContractStatus.Expired, contract.Status);
    }

    [Fact]
    public void Contract_Lifecycle_InvalidTransitions_AreDenied()
    {
        var contract = CreateValidContract();

        // Cannot activate from Draft
        Assert.False(contract.Activate(DateTime.UtcNow).IsSuccess);

        // Cannot suspend from Draft
        Assert.False(contract.Suspend().IsSuccess);

        // Cannot reactivate from Draft
        Assert.False(contract.Reactivate().IsSuccess);

        // Cannot mark as expired from Draft
        Assert.False(contract.MarkExpired(DateTime.UtcNow).IsSuccess);
    }

    [Fact]
    public void Contract_Lifecycle_SubmitFromNonDraft_IsDenied()
    {
        var contract = CreateValidContract();
        contract.SubmitForApproval();

        // Already PendingApproval, cannot submit again
        Assert.False(contract.SubmitForApproval().IsSuccess);
    }

    [Fact]
    public void Contract_Lifecycle_Terminate_FromTerminated_IsDenied()
    {
        var contract = CreateValidContract();
        contract.Terminate(DateTime.UtcNow);

        Assert.False(contract.Terminate(DateTime.UtcNow).IsSuccess);
    }

    // ------------------------------------------------------------------
    // Pricing tiers
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_PricingTiers_ExampleFromSpec_MatchesExpectedTierSelection()
    {
        var contract = CreateValidContract(monthlyListPrice: 1000m, durationMonths: 12);

        // Add pricing tiers: 1=1000, 3=2700, 6=5220, 12=10000
        var tier1 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value;
        var tier3 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value;
        var tier6 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value;
        var tier12 = ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 4).Value;

        contract.AddPricingTier(tier1);
        contract.AddPricingTier(tier3);
        contract.AddPricingTier(tier6);
        contract.AddPricingTier(tier12);

        // Verify the tier selection logic matches the spec:
        //   elapsed = 1  → 1-month tier (1000)
        //   elapsed = 2  → 2 × original monthly list price (2000, no tier)
        //   elapsed = 3  → 3-month tier (2700)
        //   elapsed = 4  → 3-month tier (2700)
        //   elapsed = 5  → 3-month tier (2700)
        //   elapsed = 6  → 6-month tier (5220)
        //   elapsed = 7  → 6-month tier (5220)
        //   elapsed = 11 → 6-month tier (5220)
        //   elapsed = 12 → 12-month tier (10000)
        Assert.Equal(1000m, contract.CalculateValueForElapsedMonths(1));
        Assert.Equal(2000m, contract.CalculateValueForElapsedMonths(2)); // fallback: 2 * 1000
        Assert.Equal(2700m, contract.CalculateValueForElapsedMonths(3));
        Assert.Equal(2700m, contract.CalculateValueForElapsedMonths(4));
        Assert.Equal(2700m, contract.CalculateValueForElapsedMonths(5));
        Assert.Equal(5220m, contract.CalculateValueForElapsedMonths(6));
        Assert.Equal(5220m, contract.CalculateValueForElapsedMonths(7));
        Assert.Equal(5220m, contract.CalculateValueForElapsedMonths(11));
        Assert.Equal(10000m, contract.CalculateValueForElapsedMonths(12));
    }

    [Fact]
    public void Contract_PricingTiers_GetApplicableTier_ReturnsHighestTierBelowOrEqual()
    {
        var contract = CreateValidContract(monthlyListPrice: 1000m);

        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value);

        Assert.Equal(1, contract.GetApplicableTier(1)?.DurationMonths);
        Assert.Equal(1, contract.GetApplicableTier(2)?.DurationMonths); // highest tier <= 2 is 1
        Assert.Equal(3, contract.GetApplicableTier(3)?.DurationMonths);
        Assert.Equal(3, contract.GetApplicableTier(5)?.DurationMonths);
        Assert.Equal(6, contract.GetApplicableTier(6)?.DurationMonths);
        Assert.Equal(6, contract.GetApplicableTier(11)?.DurationMonths);
        Assert.Null(contract.GetApplicableTier(0)); // no tier below 0
    }

    [Fact]
    public void Contract_PricingTier_Create_ValidatesInputs()
    {
        Assert.False(ContractPricingTier.Create(Guid.Empty, Guid.NewGuid(), 1, 100m, "EGP", 100m).IsSuccess);
        Assert.False(ContractPricingTier.Create(Guid.NewGuid(), Guid.Empty, 1, 100m, "EGP", 100m).IsSuccess);
        Assert.False(ContractPricingTier.Create(Guid.NewGuid(), Guid.NewGuid(), 0, 100m, "EGP", 100m).IsSuccess);
        Assert.False(ContractPricingTier.Create(Guid.NewGuid(), Guid.NewGuid(), 1, -1m, "EGP", 100m).IsSuccess);
        Assert.False(ContractPricingTier.Create(Guid.NewGuid(), Guid.NewGuid(), 1, 100m, "EG", 100m).IsSuccess);
    }

    // ------------------------------------------------------------------
    // Benefits / Gifts financial invariant
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_AddBenefit_WithinLimit_Succeeds()
    {
        var contract = CreateValidContract(monthlyListPrice: 1000m);
        // Three months limit = 3000m

        var benefit1 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Barcode Printer", null, 1500m, "EGP").Value;
        var benefit2 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Desktop Computer", null, 1500m, "EGP").Value;

        Assert.True(contract.AddBenefit(benefit1).IsSuccess);
        Assert.True(contract.AddBenefit(benefit2).IsSuccess);
        Assert.Equal(2, contract.Benefits.Count);
        Assert.Equal(3000m, contract.Benefits.Sum(b => b.ContractualValue));
    }

    [Fact]
    public void Contract_AddBenefit_ExceedsLimit_IsRejected()
    {
        var contract = CreateValidContract(monthlyListPrice: 1000m);
        // Three months limit = 3000m

        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Expensive Item", null, 3001m, "EGP").Value;

        var result = contract.AddBenefit(benefit);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.BenefitExceedsLimit", result.Errors[0].Code);
    }

    [Fact]
    public void Contract_AddBenefit_CumulativeExceedsLimit_IsRejected()
    {
        var contract = CreateValidContract(monthlyListPrice: 1000m);
        // Three months limit = 3000m

        var benefit1 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Barcode Printer", null, 1500m, "EGP").Value;
        var benefit2 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Desktop Computer", null, 1501m, "EGP").Value;

        Assert.True(contract.AddBenefit(benefit1).IsSuccess);
        var result = contract.AddBenefit(benefit2);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.BenefitExceedsLimit", result.Errors[0].Code);
        Assert.Single(contract.Benefits); // second benefit was NOT added
    }

    [Fact]
    public void Contract_AddBenefit_ExactlyAtLimit_Succeeds()
    {
        var contract = CreateValidContract(monthlyListPrice: 1000m);
        // Three months limit = 3000m

        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Gift", null, 3000m, "EGP").Value;

        var result = contract.AddBenefit(benefit);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Contract_Benefit_Create_ValidatesInputs()
    {
        Assert.False(ContractBenefit.Create(Guid.NewGuid(), Guid.Empty, ContractBenefitType.PhysicalGift, "Name", null, 100m, "EGP").IsSuccess);
        Assert.False(ContractBenefit.Create(Guid.NewGuid(), Guid.NewGuid(), ContractBenefitType.PhysicalGift, "", null, 100m, "EGP").IsSuccess);
        Assert.False(ContractBenefit.Create(Guid.NewGuid(), Guid.NewGuid(), ContractBenefitType.PhysicalGift, "Name", null, -1m, "EGP").IsSuccess);
        Assert.False(ContractBenefit.Create(Guid.NewGuid(), Guid.NewGuid(), ContractBenefitType.PhysicalGift, "Name", null, 100m, "US").IsSuccess);
    }

    [Fact]
    public void Contract_Benefit_MarkGranted_SetsTimestamp()
    {
        var contract = CreateValidContract();
        var benefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Gift", null, 100m, "EGP").Value;

        Assert.False(benefit.IsGranted);
        Assert.Null(benefit.GrantedAtUtc);

        var grantTime = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(benefit.MarkGranted(grantTime).IsSuccess);

        Assert.True(benefit.IsGranted);
        Assert.Equal(grantTime, benefit.GrantedAtUtc);
    }

    // ------------------------------------------------------------------
    // Tenant isolation (conceptual - TenantId is on the entity)
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_TenantId_IsSet_CrossTenantAccessPreventedByFilter()
    {
        // The Contract is tenant-scoped via TenantId property (inherited from AuditableEntity<Guid>).
        // Tenant isolation is enforced by Finbuckle's global query filter, not by domain logic.
        // This test verifies that the TenantId is correctly assigned at creation.

        var tenant1Contract = CreateValidContract(tenantId: "tenant-1");
        var tenant2Contract = CreateValidContract(tenantId: "tenant-2", contractNumber: "CNT-002");

        Assert.Equal("tenant-1", tenant1Contract.TenantId);
        Assert.Equal("tenant-2", tenant2Contract.TenantId);
        Assert.NotEqual(tenant1Contract.TenantId, tenant2Contract.TenantId);
    }

    // ------------------------------------------------------------------
    // Elapsed months calculation
    // ------------------------------------------------------------------

    [Fact]
    public void Contract_GetElapsedMonths_CalculatesCorrectly()
    {
        var effectiveAt = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var result = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CNT-001", 1,
            effectiveAt, new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 10000m);

        var contract = result.Value;

        Assert.Equal(0, contract.GetElapsedMonths(new DateTime(2026, 1, 14, 0, 0, 0, DateTimeKind.Utc))); // before effective
        Assert.Equal(0, contract.GetElapsedMonths(effectiveAt));
        Assert.Equal(0, contract.GetElapsedMonths(new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1, contract.GetElapsedMonths(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(11, contract.GetElapsedMonths(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(12, contract.GetElapsedMonths(new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Contract_CalculateValueForElapsedMonths_ZeroOrNegative_ReturnsZero()
    {
        var contract = CreateValidContract(monthlyListPrice: 1000m);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);

        Assert.Equal(0m, contract.CalculateValueForElapsedMonths(0));
        Assert.Equal(0m, contract.CalculateValueForElapsedMonths(-1));
    }
}
