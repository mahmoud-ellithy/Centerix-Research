namespace Centerix.SecurityTests;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// Task 18.4 — Gap F-18.4.1: Offer benefit snapshot immutability.
/// AddBenefit must enforce the same snapshot-freeze invariant as AddFeature and
/// AddPricingTier: mutation is allowed ONLY while Status == Calculated.
/// </summary>
public class Task18_4OfferBenefitImmutabilityTests
{
    private static readonly DateTime FixedNow = new(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);

    private static Offer CreateCalculatedOffer() => Offer.Create(
        Guid.NewGuid(), "tenant-184", 1,
        durationMonths: 12,
        baseAmount: 12000m,
        discountAmount: 0m,
        finalAmount: 12000m,
        monthlyListPrice: 1000m,
        currencyCode: "EGP",
        calculatedAtUtc: FixedNow,
        expiresAtUtc: FixedNow.AddDays(1),
        bonusMonths: 0,
        maxStudents: 100,
        maxUsers: 50,
        maxBranches: 10,
        maxTeachers: 20,
        storageGb: 100,
        smsQuota: 1000,
        entitlementSnapshotVersion: Offer.CompleteEntitlementSnapshotVersion).Value;

    private static OfferBenefit CreateBenefit(Guid offerId) => OfferBenefit.Create(
        Guid.NewGuid(), offerId, ContractBenefitType.PhysicalGift,
        "Gift", null, 500m, "EGP").Value;

    [Fact]
    public void CalculatedOffer_AddBenefit_Succeeds()
    {
        var offer = CreateCalculatedOffer();

        var result = offer.AddBenefit(CreateBenefit(offer.Id));

        Assert.True(result.IsSuccess);
        Assert.Single(offer.Benefits);
    }

    [Fact]
    public void AcceptedOffer_AddBenefit_Fails()
    {
        var offer = CreateCalculatedOffer();
        Assert.True(offer.Accept(FixedNow).IsSuccess);

        var result = offer.AddBenefit(CreateBenefit(offer.Id));

        Assert.False(result.IsSuccess);
        Assert.Empty(offer.Benefits);
        Assert.Equal(OfferStatus.Accepted, offer.Status);
    }

    [Fact]
    public void ConvertedOffer_AddBenefit_Fails()
    {
        var offer = CreateCalculatedOffer();
        Assert.True(offer.Accept(FixedNow).IsSuccess);
        Assert.True(offer.MarkConverted(Guid.NewGuid(), FixedNow).IsSuccess);

        var result = offer.AddBenefit(CreateBenefit(offer.Id));

        Assert.False(result.IsSuccess);
        Assert.Empty(offer.Benefits);
        Assert.Equal(OfferStatus.ConvertedToContract, offer.Status);
    }

    [Fact]
    public void ExpiredOffer_AddBenefit_Fails()
    {
        var offer = CreateCalculatedOffer();
        Assert.True(offer.MarkExpired(FixedNow).IsSuccess);

        var result = offer.AddBenefit(CreateBenefit(offer.Id));

        Assert.False(result.IsSuccess);
        Assert.Empty(offer.Benefits);
        Assert.Equal(OfferStatus.Expired, offer.Status);
    }

    [Fact]
    public void ErrorCode_MatchesExistingStateTransitionConvention()
    {
        var offer = CreateCalculatedOffer();
        Assert.True(offer.Accept(FixedNow).IsSuccess);

        var result = offer.AddBenefit(CreateBenefit(offer.Id));

        var error = Assert.Single(result.Errors!);
        Assert.Equal("Offer.InvalidStateTransition", error.Code);
    }

    [Fact]
    public void AcceptedOffer_AddFeature_And_AddPricingTier_StillFail_Regression()
    {
        // Existing feature/tier immutability invariants must continue to hold
        // (regression guard for the Task 18.4 change).
        var offer = CreateCalculatedOffer();
        Assert.True(offer.Accept(FixedNow).IsSuccess);

        var feature = OfferFeature.Create(Guid.NewGuid(), offer.Id, "STUDENTS").Value;
        var tier = OfferPricingTier.Create(Guid.NewGuid(), offer.Id, 6, 6000m).Value;

        Assert.False(offer.AddFeature(feature).IsSuccess);
        Assert.False(offer.AddPricingTier(tier).IsSuccess);
        Assert.Empty(offer.Features);
        Assert.Empty(offer.PricingTiers);
    }

    [Fact]
    public void CalculatedOffer_AddFeature_And_AddPricingTier_StillSucceed_Regression()
    {
        var offer = CreateCalculatedOffer();

        var feature = OfferFeature.Create(Guid.NewGuid(), offer.Id, "students").Value;
        var tier = OfferPricingTier.Create(Guid.NewGuid(), offer.Id, 6, 6000m).Value;

        Assert.True(offer.AddFeature(feature).IsSuccess);
        Assert.True(offer.AddPricingTier(tier).IsSuccess);
        Assert.Single(offer.Features);
        Assert.Single(offer.PricingTiers);
    }
}
