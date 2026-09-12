namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Persisted immutable commercial Offer snapshot. Represents a calculated offer
/// that a tenant can accept and then convert into a Contract.
///
/// Lifecycle: Calculated → Accepted → ConvertedToContract
///           Calculated → Expired
///
/// Once accepted, the commercial values are frozen and must not change.
/// Changing the original Promotion must not alter an accepted or converted Offer.
/// </summary>
public class Offer : AuditableEntity<Guid>
{
    /// <summary>Current offer status (domain-controlled).</summary>
    public OfferStatus Status { get; private set; }

    // ---- Commercial snapshot ----

    /// <summary>Reference to the selected commercial Plan (catalog entity).</summary>
    public int PlanId { get; private set; }

    /// <summary>Contract duration in calendar months.</summary>
    public int DurationMonths { get; private set; }

    /// <summary>Base amount before any discount (from PricingTier or MonthlyPrice × Duration).</summary>
    public decimal BaseAmount { get; private set; }

    /// <summary>Total discount amount applied.</summary>
    public decimal DiscountAmount { get; private set; }

    /// <summary>Final amount after discount.</summary>
    public decimal FinalAmount { get; private set; }

    /// <summary>Monthly list price at calculation time.</summary>
    public decimal MonthlyListPrice { get; private set; }

    /// <summary>Currency code (ISO-4217).</summary>
    public string CurrencyCode { get; private set; } = default!;

    // ---- Promotion snapshot ----

    /// <summary>Reference to the Promotion entity that was applied (snapshot).</summary>
    public int? PromotionId { get; private set; }

    /// <summary>Human-readable promotion name.</summary>
    public string? PromotionName { get; private set; }

    /// <summary>Promotion code.</summary>
    public string? PromotionCode { get; private set; }

    /// <summary>The type of promotion applied (e.g., "PercentageDiscount", "PayForXMonths").</summary>
    public string PromotionType { get; private set; } = default!;

    /// <summary>Discount percentage if applicable.</summary>
    public decimal? DiscountPercentage { get; private set; }

    /// <summary>
    /// For PayForXMonths promotions: the number of months the customer is charged for.
    /// null when not a PayForXMonths promotion.
    /// </summary>
    public int? ChargedMonths { get; private set; }

    // ---- Lifecycle timestamps ----

    /// <summary>UTC timestamp when the offer was calculated.</summary>
    public DateTime CalculatedAtUtc { get; private set; }

    /// <summary>UTC timestamp when the offer expires. null = no expiration.</summary>
    public DateTime? ExpiresAtUtc { get; private set; }

    /// <summary>UTC timestamp when the offer was accepted.</summary>
    public DateTime? AcceptedAtUtc { get; private set; }

    /// <summary>UTC timestamp when the offer was converted to a contract.</summary>
    public DateTime? ConvertedAtUtc { get; private set; }

    /// <summary>Reference to the Contract created from this offer.</summary>
    public Guid? ContractId { get; private set; }

    // ---- Benefits snapshot ----

    /// <summary>Benefits/gifts snapshot attached to this offer. Authoritative source for Contract creation.</summary>
    private readonly List<OfferBenefit> _benefits = [];
    public IReadOnlyList<OfferBenefit> Benefits => _benefits.AsReadOnly();

    private Offer() { }

    private Offer(
        Guid id,
        string tenantId,
        int planId,
        int durationMonths,
        decimal baseAmount,
        decimal discountAmount,
        decimal finalAmount,
        decimal monthlyListPrice,
        string currencyCode,
        int? promotionId,
        string? promotionName,
        string? promotionCode,
        string promotionType,
        decimal? discountPercentage,
        int? chargedMonths,
        DateTime calculatedAtUtc,
        DateTime? expiresAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        Status = OfferStatus.Calculated;
        PlanId = planId;
        DurationMonths = durationMonths;
        BaseAmount = baseAmount;
        DiscountAmount = discountAmount;
        FinalAmount = finalAmount;
        MonthlyListPrice = monthlyListPrice;
        CurrencyCode = currencyCode;
        PromotionId = promotionId;
        PromotionName = promotionName;
        PromotionCode = promotionCode;
        PromotionType = promotionType;
        DiscountPercentage = discountPercentage;
        ChargedMonths = chargedMonths;
        CalculatedAtUtc = calculatedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>
    /// Creates a new Offer with validated commercial terms.
    /// </summary>
    public static Result<Offer> Create(
        Guid id,
        string tenantId,
        int planId,
        int durationMonths,
        decimal baseAmount,
        decimal discountAmount,
        decimal finalAmount,
        decimal monthlyListPrice,
        string currencyCode,
        int? promotionId = null,
        string? promotionName = null,
        string? promotionCode = null,
        string promotionType = "None",
        decimal? discountPercentage = null,
        int? chargedMonths = null,
        DateTime calculatedAtUtc = default,
        DateTime? expiresAtUtc = null)
    {
        if (id == Guid.Empty)
            return Error.Validation("Offer.Id_Required", "Offer ID is required");

        if (string.IsNullOrWhiteSpace(tenantId))
            return OfferErrors.TenantNotResolved;

        if (planId <= 0)
            return Error.Validation("Offer.PlanId_Required", "Plan ID is required");

        if (durationMonths <= 0)
            return Error.Validation("Offer.Duration_Invalid", "Duration must be at least one month");

        if (baseAmount < 0)
            return Error.Validation("Offer.BaseAmount_Invalid", "Base amount cannot be negative");

        if (discountAmount < 0)
            return Error.Validation("Offer.DiscountAmount_Invalid", "Discount amount cannot be negative");

        if (finalAmount < 0)
            return Error.Validation("Offer.FinalAmount_Invalid", "Final amount cannot be negative");

        if (monthlyListPrice < 0)
            return Error.Validation("Offer.MonthlyListPrice_Invalid", "Monthly list price cannot be negative");

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return Error.Validation("Offer.Currency_Invalid", "Currency must be a 3-letter ISO-4217 code");

        if (discountAmount > baseAmount)
            return Error.Validation("Offer.Discount_Exceeds_Base", "Discount cannot exceed base amount");

        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= calculatedAtUtc)
            return Error.Validation("Offer.ExpiresAt_Invalid", "Expiration must be after calculation time");

        return new Offer(
            id,
            tenantId.Trim(),
            planId,
            durationMonths,
            baseAmount,
            discountAmount,
            finalAmount,
            monthlyListPrice,
            currencyCode.Trim().ToUpperInvariant(),
            promotionId,
            promotionName?.Trim(),
            promotionCode?.Trim(),
            promotionType,
            discountPercentage,
            chargedMonths,
            calculatedAtUtc == default ? DateTime.UtcNow : calculatedAtUtc,
            expiresAtUtc);
    }

    /// <summary>Whether this offer can still be accepted.</summary>
    public bool IsAcceptable =>
        Status == OfferStatus.Calculated &&
        (ExpiresAtUtc == null || ExpiresAtUtc > DateTime.UtcNow);

    /// <summary>Whether this offer can be converted to a contract.</summary>
    public bool IsConvertible =>
        Status == OfferStatus.Accepted;

    /// <summary>
    /// Accepts this offer. Marks it as accepted and freezes the snapshot.
    /// Idempotent if already accepted.
    /// </summary>
    public Result<Updated> Accept(DateTime utcNow)
    {
        if (Status == OfferStatus.Accepted)
            return Result.Updated; // idempotent

        if (Status != OfferStatus.Calculated)
            return OfferErrors.InvalidStateTransition(Status, "accept");

        if (ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= utcNow)
            return OfferErrors.Expired;

        Status = OfferStatus.Accepted;
        AcceptedAtUtc = utcNow;
        return Result.Updated;
    }

    /// <summary>
    /// Marks this offer as converted to a Contract.
    /// </summary>
    public Result<Updated> MarkConverted(Guid contractId, DateTime utcNow)
    {
        if (Status == OfferStatus.ConvertedToContract)
            return Result.Updated; // idempotent

        if (Status != OfferStatus.Accepted)
            return OfferErrors.InvalidStateTransition(Status, "convert to contract");

        Status = OfferStatus.ConvertedToContract;
        ContractId = contractId;
        ConvertedAtUtc = utcNow;
        return Result.Updated;
    }

    /// <summary>
    /// Marks this offer as expired.
    /// </summary>
    public Result<Updated> MarkExpired(DateTime utcNow)
    {
        if (Status != OfferStatus.Calculated)
            return OfferErrors.InvalidStateTransition(Status, "mark as expired");

        Status = OfferStatus.Expired;
        return Result.Updated;
    }

    /// <summary>
    /// Adds a benefit snapshot to this offer. Benefits stored here are the
    /// authoritative source when creating a Contract from this Offer.
    /// </summary>
    public Result<Updated> AddBenefit(OfferBenefit benefit)
    {
        if (benefit == null) throw new ArgumentNullException(nameof(benefit));

        _benefits.Add(benefit);
        return Result.Updated;
    }

    /// <summary>EF navigation mutator for rehydration of benefits.</summary>
    internal void LoadBenefits(IEnumerable<OfferBenefit> benefits)
        => _benefits.AddRange(benefits);
}
