namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;

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
