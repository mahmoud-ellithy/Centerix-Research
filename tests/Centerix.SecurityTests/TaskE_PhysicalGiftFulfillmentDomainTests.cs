namespace Centerix.SecurityTests;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.Events;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// Domain tests for the Task E PhysicalGift fulfillment lifecycle:
///   Pending → Granted → Delivered (terminal)
/// Eligibility: NotEligible ⇄ Eligible (independent, reversible)
/// </summary>
public class TaskE_PhysicalGiftFulfillmentDomainTests
{
    private static readonly DateTime UtcNow = new(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);

    private static ContractBenefit CreatePhysicalGift(Contract? contract = null)
    {
        contract ??= Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-e",
            contractNumber: "C-E",
            planId: 1,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 100m,
            contractualMonthlyValue: 100m,
            currencyCode: "EGP",
            grossAmount: 1200m,
            contractedAmount: 1200m,
            entitlementSnapshotVersion: 1,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;
        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            contract.Id,
            ContractBenefitType.PhysicalGift,
            "Barcode Printer",
            null,
            1500m,
            "EGP").Value;
        contract.AddBenefit(benefit);
        return benefit;
    }

    // ──────────────── Creation invariants ────────────────

    [Fact]
    public void TestE1_NewPhysicalGift_Starts_NotEligible_Pending()
    {
        var benefit = CreatePhysicalGift();

        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Pending, benefit.FulfillmentStatus);
        Assert.False(benefit.IsGranted);
        Assert.False(benefit.IsDelivered);
        Assert.Null(benefit.GrantedAtUtc);
        Assert.Null(benefit.GrantedBy);
        Assert.Null(benefit.DeliveredAtUtc);
        Assert.Null(benefit.DeliveredBy);
    }

    // ──────────────── Grant ────────────────

    [Fact]
    public void TestE2_Eligible_Pending_Grant_Transitions_To_Granted()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);

        var result = benefit.Grant(UtcNow, "user-grant");

        Assert.True(result.IsSuccess);
        Assert.Equal(FulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.True(benefit.IsGranted);
        Assert.False(benefit.IsDelivered);
        Assert.Equal(UtcNow, benefit.GrantedAtUtc);
        Assert.Equal("user-grant", benefit.GrantedBy);
        Assert.Null(benefit.DeliveredAtUtc);
        Assert.Equal(BenefitEligibilityStatus.Eligible, benefit.EligibilityStatus);
        Assert.Single(benefit.DomainEvents.OfType<BenefitGrantedEvent>());
    }

    [Fact]
    public void TestE3_Grant_Requires_Eligible()
    {
        var benefit = CreatePhysicalGift();
        // Skip MarkEligible — remains NotEligible.

        var result = benefit.Grant(UtcNow, "user-grant");

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.NotEligible", result.Errors![0].Code);
        Assert.Equal(FulfillmentStatus.Pending, benefit.FulfillmentStatus);
    }

    [Fact]
    public void TestE4_Grant_Requires_Pending()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant"); // → Granted

        var result = benefit.Grant(UtcNow, "user-grant-2");

        // Idempotent on already-Granted.
        Assert.True(result.IsSuccess);
        Assert.Equal(FulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.Equal(UtcNow, benefit.GrantedAtUtc);
        Assert.Equal("user-grant", benefit.GrantedBy); // unchanged
    }

    [Fact]
    public void TestE5_Grant_Rejects_NonPhysicalGift()
    {
        var contract = Contract.Create(id: Guid.NewGuid(), tenantId: "tenant-e", contractNumber: "C-E", planId: 1, effectiveAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddMonths(12), durationMonths: 12, monthlyListPrice: 100m, contractualMonthlyValue: 100m, currencyCode: "EGP", grossAmount: 1200m, contractedAmount: 1200m, entitlementSnapshotVersion: 1, paymentTerms: PaymentTerms.FullUpfront, discountAmount: 0m).Value;
        var benefit = ContractBenefit.Create(
            Guid.NewGuid(), contract.Id,
            ContractBenefitType.Service, "Support", null, 500m, "EGP").Value;
        contract.AddBenefit(benefit);
        // Make Service benefit eligible (eligibility evaluator does not care about type).
        typeof(ContractBenefit)
            .GetProperty(nameof(ContractBenefit.EligibilityStatus))!
            .SetValue(benefit, BenefitEligibilityStatus.Eligible);

        var result = benefit.Grant(UtcNow, "user-grant");

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors![0].Code);
    }

    [Fact]
    public void TestE6_Grant_Is_Idempotent()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-1");
        var firstEventCount = benefit.DomainEvents.OfType<BenefitGrantedEvent>().Count();

        var result = benefit.Grant(UtcNow.AddMinutes(5), "user-2");

        Assert.True(result.IsSuccess);
        Assert.Equal(UtcNow, benefit.GrantedAtUtc);
        Assert.Equal("user-1", benefit.GrantedBy);
        Assert.Equal(firstEventCount, benefit.DomainEvents.OfType<BenefitGrantedEvent>().Count());
    }

    [Fact]
    public void TestE7_Grant_Does_Not_Mark_Delivered()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);

        benefit.Grant(UtcNow, "user-grant");

        Assert.False(benefit.IsDelivered);
        Assert.Equal(FulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.Null(benefit.DeliveredAtUtc);
        Assert.Null(benefit.DeliveredBy);
    }

    // ──────────────── Deliver ────────────────

    [Fact]
    public void TestE8_Granted_Deliver_Transitions_To_Delivered()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");

        var deliveredAt = UtcNow.AddMinutes(10);
        var result = benefit.Deliver(deliveredAt, "user-deliver");

        Assert.True(result.IsSuccess);
        Assert.Equal(FulfillmentStatus.Delivered, benefit.FulfillmentStatus);
        Assert.True(benefit.IsDelivered);
        Assert.Equal(deliveredAt, benefit.DeliveredAtUtc);
        Assert.Equal("user-deliver", benefit.DeliveredBy);
        Assert.Single(benefit.DomainEvents.OfType<BenefitDeliveredEvent>());
    }

    [Fact]
    public void TestE9_Delivery_Requires_Granted()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        // Pending — never granted.

        var result = benefit.Deliver(UtcNow, "user-deliver");

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.NotGranted", result.Errors![0].Code);
    }

    [Fact]
    public void TestE10_Delivery_Rejects_NonPhysicalGift()
    {
        var contract = Contract.Create(id: Guid.NewGuid(), tenantId: "tenant-e", contractNumber: "C-E", planId: 1, effectiveAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddMonths(12), durationMonths: 12, monthlyListPrice: 100m, contractualMonthlyValue: 100m, currencyCode: "EGP", grossAmount: 1200m, contractedAmount: 1200m, entitlementSnapshotVersion: 1, paymentTerms: PaymentTerms.FullUpfront, discountAmount: 0m).Value;
        var benefit = ContractBenefit.Create(
            Guid.NewGuid(), contract.Id,
            ContractBenefitType.Service, "Support", null, 500m, "EGP").Value;
        contract.AddBenefit(benefit);

        var result = benefit.Deliver(UtcNow, "user-deliver");

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors![0].Code);
    }

    [Fact]
    public void TestE11_Delivery_Is_Idempotent()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");
        benefit.Deliver(UtcNow, "user-1");

        var result = benefit.Deliver(UtcNow.AddHours(1), "user-2");

        Assert.True(result.IsSuccess);
        Assert.Equal(FulfillmentStatus.Delivered, benefit.FulfillmentStatus);
        Assert.Equal(UtcNow, benefit.DeliveredAtUtc);
        Assert.Equal("user-1", benefit.DeliveredBy);
        Assert.Single(benefit.DomainEvents.OfType<BenefitDeliveredEvent>());
    }

    [Fact]
    public void TestE12_Delivered_Remains_Delivered()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");
        benefit.Deliver(UtcNow, "user-deliver");

        // Any subsequent grant/deliver is a no-op.
        Assert.True(benefit.Grant(UtcNow.AddDays(1), "user-x").IsSuccess);
        Assert.True(benefit.Deliver(UtcNow.AddDays(1), "user-y").IsSuccess);

        Assert.Equal(FulfillmentStatus.Delivered, benefit.FulfillmentStatus);
        Assert.Equal(UtcNow, benefit.DeliveredAtUtc);
    }

    // ──────────────── Eligibility independence ────────────────

    [Fact]
    public void TestE13_Eligibility_Can_Flip_Back_After_Granted()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");

        // Eligibility evaluator flips back to NotEligible.
        benefit.MarkNotEligible();

        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.True(benefit.IsGranted);
    }

    [Fact]
    public void TestE14_Eligibility_Can_Flip_Back_After_Delivered()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");
        benefit.Deliver(UtcNow, "user-deliver");

        benefit.MarkNotEligible();

        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Delivered, benefit.FulfillmentStatus);
        Assert.True(benefit.IsDelivered);
    }

    // ──────────────── Fulfillment monotonicity ────────────────

    [Fact]
    public void TestE15_Fulfillment_Never_Moves_Backward()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");
        var grantedState = benefit.FulfillmentStatus;

        benefit.MarkNotEligible();
        benefit.MarkEligible(UtcNow.AddMinutes(5));

        Assert.Equal(grantedState, benefit.FulfillmentStatus);
    }

    [Fact]
    public void TestE16_FulfillmentStatus_Pending_Forbidden_From_Delivery()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        // FulfillmentStatus is still Pending.

        var result = benefit.Deliver(UtcNow, "user");

        Assert.False(result.IsSuccess);
        Assert.Equal(FulfillmentStatus.Pending, benefit.FulfillmentStatus);
    }

    // ──────────────── Snapshot immutability ────────────────

    [Fact]
    public void TestE17_Commercial_Snapshot_Unchanged_After_Grant_Delivery()
    {
        var benefit = CreatePhysicalGift();
        var originalName = benefit.Name;
        var originalValue = benefit.ContractualValue;
        var originalType = benefit.BenefitType;
        var originalCurrency = benefit.CurrencyCode;

        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");
        benefit.Deliver(UtcNow, "user-deliver");

        Assert.Equal(originalName, benefit.Name);
        Assert.Equal(originalValue, benefit.ContractualValue);
        Assert.Equal(originalType, benefit.BenefitType);
        Assert.Equal(originalCurrency, benefit.CurrencyCode);
    }

    // ──────────────── Audit field semantics ────────────────

    [Fact]
    public void TestE18_GrantedAtUtc_Represents_Grant_NotDelivery()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        var grantMoment = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var deliveryMoment = grantMoment.AddMinutes(30);

        benefit.Grant(grantMoment, "user-grant");
        benefit.Deliver(deliveryMoment, "user-deliver");

        Assert.Equal(grantMoment, benefit.GrantedAtUtc);
        Assert.Equal(deliveryMoment, benefit.DeliveredAtUtc);
        Assert.NotEqual(benefit.GrantedAtUtc, benefit.DeliveredAtUtc);
    }

    [Fact]
    public void TestE19_DeliveredAtUtc_Represents_Delivery()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");

        var deliveredAt = new DateTime(2026, 3, 16, 12, 0, 0, DateTimeKind.Utc);
        benefit.Deliver(deliveredAt, "user-deliver");

        Assert.Equal(deliveredAt, benefit.DeliveredAtUtc);
        Assert.Equal("user-deliver", benefit.DeliveredBy);
    }

    [Fact]
    public void TestE20_Grant_Actor_And_Delivery_Actor_Remain_Distinct()
    {
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "actor-grant");
        benefit.Deliver(UtcNow, "actor-deliver");

        Assert.Equal("actor-grant", benefit.GrantedBy);
        Assert.Equal("actor-deliver", benefit.DeliveredBy);
        Assert.NotEqual(benefit.GrantedBy, benefit.DeliveredBy);
    }

    // ──────────────── FulfillmentStatus enum shape ────────────────

    [Fact]
    public void TestE21_FulfillmentStatus_UnderlyingNumericValues_ArePinned()
    {
        Assert.Equal(0, (byte)FulfillmentStatus.Pending);
        Assert.Equal(1, (byte)FulfillmentStatus.Granted);
        Assert.Equal(2, (byte)FulfillmentStatus.Delivered);
        Assert.Equal(3, (byte)FulfillmentStatus.AppliedToSubscription);
    }

    [Fact]
    public void TestE22_FulfillmentStatus_AppliedToSubscription_NotValid_For_ContractBenefit()
    {
        // The shared enum exposes AppliedToSubscription, but ContractBenefit's
        // Deliver() and domain transitions only allow Pending/Granted/Delivered.
        // Grant() does not promote to AppliedToSubscription, and Deliver() only
        // accepts Granted → Delivered.
        var benefit = CreatePhysicalGift();
        benefit.MarkEligible(UtcNow);
        benefit.Grant(UtcNow, "user-grant");

        // After Grant: still Granted, never AppliedToSubscription.
        Assert.Equal(FulfillmentStatus.Granted, benefit.FulfillmentStatus);

        // Deliver goes to Delivered, not AppliedToSubscription.
        benefit.Deliver(UtcNow, "user-deliver");
        Assert.Equal(FulfillmentStatus.Delivered, benefit.FulfillmentStatus);
        Assert.NotEqual(FulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
    }
}