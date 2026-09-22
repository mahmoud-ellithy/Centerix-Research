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

    // ---- Entitlement snapshot (from Plan at calculation time) ----

    /// <summary>
    /// Promotional bonus months captured from the Plan when the Offer was calculated.
    /// Authoritative for Contract period calculation; the current Plan is never consulted.
    /// </summary>
    public int BonusMonths { get; private set; }

    /// <summary>Maximum students captured from the Plan at calculation time.</summary>
    public int MaxStudents { get; private set; }

    /// <summary>Maximum users captured from the Plan at calculation time.</summary>
    public int MaxUsers { get; private set; }

    /// <summary>Maximum branches captured from the Plan at calculation time.</summary>
    public int MaxBranches { get; private set; }

    /// <summary>Maximum teachers captured from the Plan at calculation time.</summary>
    public int MaxTeachers { get; private set; }

    /// <summary>Storage quota (GB) captured from the Plan at calculation time.</summary>
    public int StorageGB { get; private set; }

    /// <summary>SMS quota captured from the Plan at calculation time.</summary>
    public int SMSQuota { get; private set; }

    /// <summary>
    /// Snapshot completeness version. 0 = legacy/migration-era offer with no
    /// entitlement snapshot (cannot be safely converted). 1 = complete snapshot:
    /// all entitlement values and child snapshots (features, pricing tiers) were
    /// captured from the Plan at calculation time. Zero entitlement values are
    /// legitimate (e.g. BonusMonths = 0); completeness is expressed ONLY through
    /// this version — never inferred from numeric zeros.
    /// </summary>
    public int EntitlementSnapshotVersion { get; private set; }

    /// <summary>Snapshot completeness version for a fully captured Offer.</summary>
    public const int CompleteEntitlementSnapshotVersion = 1;

    /// <summary>Whether this Offer carries a complete entitlement snapshot.</summary>
    public bool HasCompleteEntitlementSnapshot =>
        EntitlementSnapshotVersion >= CompleteEntitlementSnapshotVersion;

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

    // ---- Feature entitlement snapshot ----

    /// <summary>Feature codes captured from the Plan at calculation time. Authoritative for Contract creation.</summary>
    private readonly List<OfferFeature> _features = [];
    public IReadOnlyList<OfferFeature> Features => _features.AsReadOnly();

    // ---- Pricing tier snapshot ----

    /// <summary>Pricing tiers captured from the Plan at calculation time. Authoritative for refund/repricing.</summary>
    private readonly List<OfferPricingTier> _pricingTiers = [];
    public IReadOnlyList<OfferPricingTier> PricingTiers => _pricingTiers.AsReadOnly();

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
        int bonusMonths,
        int maxStudents,
        int maxUsers,
        int maxBranches,
        int maxTeachers,
        int storageGb,
        int smsQuota,
        int entitlementSnapshotVersion,
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
        BonusMonths = bonusMonths;
        MaxStudents = maxStudents;
        MaxUsers = maxUsers;
        MaxBranches = maxBranches;
        MaxTeachers = maxTeachers;
        StorageGB = storageGb;
        SMSQuota = smsQuota;
        EntitlementSnapshotVersion = entitlementSnapshotVersion;
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
        DateTime? expiresAtUtc = null,
        int bonusMonths = 0,
        int maxStudents = 0,
        int maxUsers = 0,
        int maxBranches = 0,
        int maxTeachers = 0,
        int storageGb = 0,
        int smsQuota = 0,
        int entitlementSnapshotVersion = 0)
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
            bonusMonths,
            maxStudents,
            maxUsers,
            maxBranches,
            maxTeachers,
            storageGb,
            smsQuota,
            entitlementSnapshotVersion,
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
    /// Only allowed while the Offer has not been accepted (snapshot freeze) —
    /// once accepted or converted, the commercial snapshot is immutable.
    /// </summary>
    public Result<Updated> AddBenefit(OfferBenefit benefit)
    {
        if (benefit == null) throw new ArgumentNullException(nameof(benefit));

        if (Status != OfferStatus.Calculated)
            return OfferErrors.InvalidStateTransition(Status, "add benefit snapshot to");

        _benefits.Add(benefit);
        return Result.Updated;
    }

    /// <summary>
    /// Adds a feature entitlement snapshot to this offer. Features stored here
    /// are the authoritative source when creating a Contract from this Offer.
    /// Only allowed while the Offer has not been accepted (snapshot freeze).
    /// </summary>
    public Result<Updated> AddFeature(OfferFeature feature)
    {
        if (feature == null) throw new ArgumentNullException(nameof(feature));

        if (Status != OfferStatus.Calculated)
            return OfferErrors.InvalidStateTransition(Status, "add feature snapshot to");

        _features.Add(feature);
        return Result.Updated;
    }

    /// <summary>
    /// Adds a pricing tier snapshot to this offer. Tiers stored here are the
    /// authoritative source for refund/repricing calculations.
    /// Only allowed while the Offer has not been accepted (snapshot freeze).
    /// </summary>
    public Result<Updated> AddPricingTier(OfferPricingTier tier)
    {
        if (tier == null) throw new ArgumentNullException(nameof(tier));

        if (Status != OfferStatus.Calculated)
            return OfferErrors.InvalidStateTransition(Status, "add pricing tier snapshot to");

        _pricingTiers.Add(tier);
        return Result.Updated;
    }

    /// <summary>EF navigation mutator for rehydration of benefits.</summary>
    internal void LoadBenefits(IEnumerable<OfferBenefit> benefits)
        => _benefits.AddRange(benefits);

    /// <summary>EF navigation mutator for rehydration of feature snapshots.</summary>
    internal void LoadFeatures(IEnumerable<OfferFeature> features)
        => _features.AddRange(features);

    /// <summary>EF navigation mutator for rehydration of pricing tier snapshots.</summary>
    internal void LoadPricingTiers(IEnumerable<OfferPricingTier> tiers)
        => _pricingTiers.AddRange(tiers);
}
