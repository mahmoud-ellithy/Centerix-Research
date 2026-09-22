namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Immutable snapshot of a feature entitlement attached to an Offer.
/// Captures the feature codes that were active on the Plan when the Offer
/// was calculated. When a Contract is created from an Accepted Offer, these
/// codes are copied into ContractFeatures — the Offer snapshot is authoritative
/// and the current Plan catalog is never consulted.
/// </summary>
public class OfferFeature : Entity
{
    public Guid Id { get; private set; }
    public Guid OfferId { get; private set; }

    /// <summary>Feature catalog code (immutable snapshot).</summary>
    public string FeatureCode { get; private set; } = default!;

    private OfferFeature() { }

    private OfferFeature(Guid id, Guid offerId, string featureCode)
    {
        Id = id;
        OfferId = offerId;
        FeatureCode = featureCode;
    }

    /// <summary>
    /// Creates an OfferFeature with validated parameters.
    /// </summary>
    public static Result<OfferFeature> Create(Guid id, Guid offerId, string featureCode)
    {
        if (id == Guid.Empty)
            return Error.Validation("OfferFeature.Id_Required", "Offer feature ID is required");

        if (offerId == Guid.Empty)
            return Error.Validation("OfferFeature.OfferId_Required", "Offer ID is required");

        if (string.IsNullOrWhiteSpace(featureCode))
            return Error.Validation("OfferFeature.Code_Required", "Feature code is required");

        return new OfferFeature(id, offerId, featureCode.Trim().ToUpperInvariant());
    }
}

/// <summary>
/// Immutable snapshot of a pricing tier captured on an Offer at calculation
/// time. Preserves the historical pricing tiers used for the commercial
/// transaction (required for refund/repricing calculations) — the current
/// Plan pricing tiers are never consulted after the Offer exists.
/// </summary>
public class OfferPricingTier : Entity
{
    public Guid Id { get; private set; }
    public Guid OfferId { get; private set; }

    /// <summary>Duration of this tier in calendar months.</summary>
    public int DurationMonths { get; private set; }

    /// <summary>Total price for this tier duration (NOT per-month).</summary>
    public decimal TierPrice { get; private set; }

    /// <summary>Display order preserved from the Plan catalog at calculation time.</summary>
    public int DisplayOrder { get; private set; }

    private OfferPricingTier() { }

    private OfferPricingTier(Guid id, Guid offerId, int durationMonths, decimal tierPrice, int displayOrder)
    {
        Id = id;
        OfferId = offerId;
        DurationMonths = durationMonths;
        TierPrice = tierPrice;
        DisplayOrder = displayOrder;
    }

    /// <summary>
    /// Creates an OfferPricingTier with validated parameters.
    /// </summary>
    public static Result<OfferPricingTier> Create(
        Guid id,
        Guid offerId,
        int durationMonths,
        decimal tierPrice,
        int displayOrder = 0)
    {
        if (id == Guid.Empty)
            return Error.Validation("OfferPricingTier.Id_Required", "Offer pricing tier ID is required");

        if (offerId == Guid.Empty)
            return Error.Validation("OfferPricingTier.OfferId_Required", "Offer ID is required");

        if (durationMonths <= 0)
            return Error.Validation("OfferPricingTier.Duration_Invalid", "Duration must be at least one month");

        if (tierPrice < 0)
            return Error.Validation("OfferPricingTier.Price_Invalid", "Tier price cannot be negative");

        return new OfferPricingTier(id, offerId, durationMonths, tierPrice, displayOrder);
    }
}

/// <summary>
/// Immutable snapshot of a benefit/gift attached to an Offer.
/// When a Contract is created from an Accepted Offer, these OfferBenefits
/// are copied into ContractBenefits. The client cannot override benefit
/// values during Contract creation — the Offer snapshot is authoritative.
/// </summary>
public class OfferBenefit : Entity
{
    public Guid Id { get; private set; }
    public Guid OfferId { get; private set; }

    /// <summary>Category/type of benefit (e.g., PhysicalGift, Service).</summary>
    public ContractBenefitType BenefitType { get; private set; }

    /// <summary>Human-readable name of the benefit.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Optional description providing additional detail.</summary>
    public string? Description { get; private set; }

    /// <summary>Contractual value of this benefit (immutable snapshot).</summary>
    public decimal ContractualValue { get; private set; }

    /// <summary>Currency code (ISO-4217, e.g., EGP, USD).</summary>
    public string CurrencyCode { get; private set; } = default!;

    private OfferBenefit() { }

    private OfferBenefit(
        Guid id,
        Guid offerId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode)
    {
        Id = id;
        OfferId = offerId;
        BenefitType = benefitType;
        Name = name;
        Description = description;
        ContractualValue = contractualValue;
        CurrencyCode = currencyCode;
    }

    /// <summary>
    /// Creates an OfferBenefit with validated parameters.
    /// </summary>
    public static Result<OfferBenefit> Create(
        Guid id,
        Guid offerId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode)
    {
        if (id == Guid.Empty)
            return Error.Validation("OfferBenefit.Id_Required", "Offer benefit ID is required");

        if (offerId == Guid.Empty)
            return Error.Validation("OfferBenefit.OfferId_Required", "Offer ID is required");

        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("OfferBenefit.Name_Required", "Benefit name is required");

        if (contractualValue < 0)
            return Error.Validation("OfferBenefit.InvalidValue", "Benefit value cannot be negative");

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return Error.Validation("OfferBenefit.InvalidCurrency", "Currency must be a 3-letter ISO-4217 code");

        return new OfferBenefit(
            id,
            offerId,
            benefitType,
            name.Trim(),
            description?.Trim(),
            contractualValue,
            currencyCode.Trim().ToUpperInvariant());
    }
}
