namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Plans;

/// <summary>
/// Result of applying a Promotion to a Plan at a specific point in time.
/// Contains all commercial terms needed to create a Contract snapshot.
/// </summary>
public sealed record CalculatedOffer
{
    public int PlanId { get; init; }
    public int DurationMonths { get; init; }
    public decimal BaseAmount { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal FinalAmount { get; init; }
    public int? PromotionId { get; init; }
    public string? PromotionName { get; init; }
    public string? PromotionCode { get; init; }
    public string PromotionType { get; init; } = default!;
    public decimal? DiscountPercentage { get; init; }
    public int? ChargedMonths { get; init; }
    public decimal MonthlyListPrice { get; init; }
    public string CurrencyCode { get; init; } = default!;
    public DateTime CalculatedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }

    // ---- Entitlement snapshot (granted by the promotion; independent of the charged amount) ----

    /// <summary>
    /// Free months granted on top of the purchased term. null when the promotion grants none.
    /// Never inferred from <c>ChargedMonths</c>, <c>Plan.BonusMonths</c>, or the discount.
    /// </summary>
    public int? FreeMonths { get; init; }

    /// <summary>Name of the additional benefit. null when the promotion grants none.</summary>
    public string? BenefitName { get; init; }

    /// <summary>Optional description of the additional benefit.</summary>
    public string? BenefitDescription { get; init; }

    /// <summary>Contractual value of the additional benefit. null when the promotion grants none.</summary>
    public decimal? BenefitValue { get; init; }

    /// <summary>Category of the additional benefit. null when the promotion grants none.</summary>
    public ContractBenefitType? BenefitType { get; init; }

    /// <summary>ISO-4217 currency of <see cref="BenefitValue"/>. null when the promotion grants none.</summary>
    public string? BenefitCurrencyCode { get; init; }

    /// <summary>
    /// The immutable commercial eligibility rule that must hold before the granted entitlement
    /// becomes eligible. Built deterministically from the calculated offer itself (never from the
    /// current Plan catalog) and snapshotted onto the Offer, so Contract creation reads the Offer
    /// snapshot only. null when the promotion grants no entitlement.
    /// </summary>
    public EligibilityRule? EntitlementEligibilityRule { get; init; }

    /// <summary>Whether this offer carries a free months entitlement.</summary>
    public bool HasFreeMonths => FreeMonths is > 0;

    /// <summary>Whether this offer carries an additional benefit entitlement.</summary>
    public bool HasAdditionalBenefit => !string.IsNullOrWhiteSpace(BenefitName) && BenefitValue is > 0;
}
