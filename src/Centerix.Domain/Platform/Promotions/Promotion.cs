namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Platform-scoped commercial promotion rule. Determines how a discount or offer
/// is calculated before a Contract is created. Once a Contract is created, changes
/// to the Promotion do NOT affect that Contract (historical immutability).
/// </summary>
/// <remarks>
/// Promotion types:
/// - PercentageDiscount: discount = base × percentage / 100
/// - FixedAmountDiscount: discount = fixed amount (must be ≤ base)
/// - PayForXMonths: customer pays for ChargedMonths but receives DurationMonths entitlement
/// - PromotionalPrice: explicit final price (discount = base - promotional price)
///
/// Lifecycle: Draft → Active → Expired/Disabled
/// Only Active promotions with a valid date range can be applied.
/// </remarks>
public class Promotion : GlobalAuditableEntity<int>
{
    public string Name { get; private set; } = default!;

    public string? Code { get; private set; }

    public PromotionType Type { get; private set; }

    public PromotionStatus Status { get; private set; }

    /// <summary>Reference to the Plan this promotion applies to. 0 = applies to all plans.</summary>
    public int PlanId { get; private set; }

    /// <summary>The contract duration (in months) this promotion targets. 0 = any duration.</summary>
    public int DurationMonths { get; private set; }

    public DateTime StartsAtUtc { get; private set; }

    public DateTime EndsAtUtc { get; private set; }

    /// <summary>Deterministic ordering when multiple promotions are eligible. Higher = preferred.</summary>
    public int Priority { get; private set; }

    // Type-specific fields (only semantically meaningful for the relevant Type)

    /// <summary>Percentage discount value (e.g., 10 for 10%). Required for PercentageDiscount.</summary>
    public decimal? Percentage { get; private set; }

    /// <summary>Fixed monetary discount. Required for FixedAmountDiscount.</summary>
    public decimal? FixedAmount { get; private set; }

    /// <summary>Explicit promotional final price. Required for PromotionalPrice.</summary>
    public decimal? PromotionalPrice { get; private set; }

    /// <summary>Number of months the customer is charged for. Required for PayForXMonths.</summary>
    public int? ChargedMonths { get; private set; }

    // ---- Benefit configuration (optional; only semantically meaningful for the benefit types) ----

    /// <summary>
    /// Number of free months this promotion grants on top of the purchased term.
    /// Required for <see cref="PromotionType.FreeMonthsBonus"/>.
    /// <para>
    /// For <see cref="PromotionType.PayForXMonths"/> this acts as the opt-in switch: when set, the
    /// months difference (<c>DurationMonths - ChargedMonths</c>) — NOT this value — is granted as
    /// free months, so the entitlement can never contradict the commercial "pay X get Y" promise.
    /// </para>
    /// </summary>
    public int? FreeMonthsCount { get; private set; }

    /// <summary>Human-readable name of the additional benefit. Required for <see cref="PromotionType.AdditionalBenefits"/>.</summary>
    public string? BenefitName { get; private set; }

    /// <summary>Optional description of the additional benefit.</summary>
    public string? BenefitDescription { get; private set; }

    /// <summary>
    /// Contractual value of the additional benefit in <see cref="BenefitCurrencyCode"/>.
    /// Required for <see cref="PromotionType.AdditionalBenefits"/> and must be strictly positive.
    /// </summary>
    public decimal? BenefitValue { get; private set; }

    /// <summary>Category of the additional benefit. Required for <see cref="PromotionType.AdditionalBenefits"/>.</summary>
    public ContractBenefitType? BenefitType { get; private set; }

    /// <summary>
    /// ISO-4217 currency of <see cref="BenefitValue"/>. Required for
    /// <see cref="PromotionType.AdditionalBenefits"/> and must be a 3-letter code.
    /// </summary>
    public string? BenefitCurrencyCode { get; private set; }

    /// <summary>
    /// The commercial eligibility rule that governs when the entitlement granted by this promotion
    /// becomes eligible. This is TRUSTED PLATFORM CONFIGURATION owned by the Promotion — it is the
    /// single authoritative source of the rule and is never derived from
    /// <see cref="PromotionType"/>, the discount, or any runtime state.
    /// <para>
    /// Required for every benefit-bearing promotion: <see cref="PromotionType.FreeMonthsBonus"/>,
    /// <see cref="PromotionType.AdditionalBenefits"/>, and <see cref="PromotionType.PayForXMonths"/>
    /// configured with a <see cref="FreeMonthsCount"/> entitlement. Rejected for discount-only
    /// promotion types. No default is ever generated — a benefit-bearing promotion without a
    /// configured rule is a validation failure.
    /// </para>
    /// <para>
    /// Nullable so that Promotion rows created before this rule existed are never given an
    /// invented historical rule.
    /// </para>
    /// </summary>
    public EligibilityRule? BenefitEligibilityRule { get; private set; }

    private Promotion() { }

    private Promotion(
        int id,
        string name,
        string? code,
        PromotionType type,
        PromotionStatus status,
        int planId,
        int durationMonths,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int priority,
        decimal? percentage,
        decimal? fixedAmount,
        decimal? promotionalPrice,
        int? chargedMonths,
        int? freeMonthsCount,
        string? benefitName,
        string? benefitDescription,
        decimal? benefitValue,
        ContractBenefitType? benefitType,
        string? benefitCurrencyCode,
        EligibilityRule? benefitEligibilityRule)
        : base(id)
    {
        Name = name;
        Code = code;
        Type = type;
        Status = status;
        PlanId = planId;
        DurationMonths = durationMonths;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Priority = priority;
        Percentage = percentage;
        FixedAmount = fixedAmount;
        PromotionalPrice = promotionalPrice;
        ChargedMonths = chargedMonths;
        FreeMonthsCount = freeMonthsCount;
        BenefitName = benefitName;
        BenefitDescription = benefitDescription;
        BenefitValue = benefitValue;
        BenefitType = benefitType;
        BenefitCurrencyCode = benefitCurrencyCode;
        BenefitEligibilityRule = benefitEligibilityRule;
    }

    public static Result<Promotion> Create(
        int id,
        string name,
        PromotionType type,
        int planId,
        int durationMonths,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int priority = 0,
        string? code = null,
        decimal? percentage = null,
        decimal? fixedAmount = null,
        decimal? promotionalPrice = null,
        int? chargedMonths = null,
        int? freeMonthsCount = null,
        string? benefitName = null,
        string? benefitDescription = null,
        decimal? benefitValue = null,
        ContractBenefitType? benefitType = null,
        string? benefitCurrencyCode = null,
        EligibilityRule? benefitEligibilityRule = null)
    {
        if (id < 0)
            return PromotionErrors.InvalidId;

        if (string.IsNullOrWhiteSpace(name))
            return PromotionErrors.NameRequired;

        if (name.Length > 200)
            return PromotionErrors.NameTooLong;

        if (planId < 0)
            return PromotionErrors.InvalidPlanId;

        if (durationMonths < 0)
            return PromotionErrors.InvalidDurationMonths;

        if (startsAtUtc == default)
            return PromotionErrors.StartsAtRequired;

        if (endsAtUtc == default)
            return PromotionErrors.EndsAtRequired;

        if (startsAtUtc >= endsAtUtc)
            return PromotionErrors.InvalidDateRange;

        if (code is { Length: > 50 })
            return PromotionErrors.CodeTooLong;

        // Validate type-specific fields
        var typeValidation = ValidateTypeFields(type, percentage, fixedAmount, promotionalPrice, chargedMonths);
        if (!typeValidation.IsSuccess)
            return typeValidation.Errors!;

        var benefitValidation = ValidateBenefitFields(
            type,
            freeMonthsCount,
            benefitName,
            benefitValue,
            benefitType,
            benefitCurrencyCode,
            benefitEligibilityRule);
        if (!benefitValidation.IsSuccess)
            return benefitValidation.Errors!;

        return new Promotion(
            id,
            name.Trim(),
            code?.Trim().ToUpperInvariant(),
            type,
            PromotionStatus.Draft,
            planId,
            durationMonths,
            startsAtUtc,
            endsAtUtc,
            priority,
            percentage,
            fixedAmount,
            promotionalPrice,
            chargedMonths,
            freeMonthsCount,
            benefitName?.Trim(),
            benefitDescription?.Trim(),
            benefitValue,
            benefitType,
            benefitCurrencyCode?.Trim().ToUpperInvariant(),
            benefitEligibilityRule);
    }

    /// <summary>
    /// Evaluates whether this promotion is applicable at the given time.
    /// A promotion is applicable when:
    /// - Status == Active
    /// - StartsAtUtc <= evaluationTime
    /// - evaluationTime < EndsAtUtc (exclusive end)
    /// </summary>
    public bool IsApplicableAt(DateTime evaluationTime)
    {
        if (Status != PromotionStatus.Active)
            return false;

        return StartsAtUtc <= evaluationTime && evaluationTime < EndsAtUtc;
    }

    /// <summary>Activates a Draft promotion.</summary>
    public Result<Updated> Activate()
    {
        if (Status != PromotionStatus.Draft)
            return PromotionErrors.InvalidStateTransition(Status, "activate");

        Status = PromotionStatus.Active;
        return Result.Updated;
    }

    /// <summary>Deactivates an Active promotion.</summary>
    public Result<Updated> Deactivate()
    {
        if (Status != PromotionStatus.Active)
            return PromotionErrors.InvalidStateTransition(Status, "deactivate");

        Status = PromotionStatus.Disabled;
        return Result.Updated;
    }

    /// <summary>Marks an Active promotion as Expired.</summary>
    public Result<Updated> MarkExpired()
    {
        if (Status != PromotionStatus.Active)
            return PromotionErrors.InvalidStateTransition(Status, "mark as expired");

        Status = PromotionStatus.Expired;
        return Result.Updated;
    }

    /// <summary>Updates a Draft or Active promotion's configuration.</summary>
    public Result<Updated> Update(
        string name,
        PromotionType type,
        int planId,
        int durationMonths,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int priority,
        string? code = null,
        decimal? percentage = null,
        decimal? fixedAmount = null,
        decimal? promotionalPrice = null,
        int? chargedMonths = null,
        int? freeMonthsCount = null,
        string? benefitName = null,
        string? benefitDescription = null,
        decimal? benefitValue = null,
        ContractBenefitType? benefitType = null,
        string? benefitCurrencyCode = null,
        EligibilityRule? benefitEligibilityRule = null)
    {
        if (Status is PromotionStatus.Expired or PromotionStatus.Disabled)
            return PromotionErrors.InvalidStateTransition(Status, "update");

        if (string.IsNullOrWhiteSpace(name))
            return PromotionErrors.NameRequired;

        if (name.Length > 200)
            return PromotionErrors.NameTooLong;

        if (planId < 0)
            return PromotionErrors.InvalidPlanId;

        if (durationMonths < 0)
            return PromotionErrors.InvalidDurationMonths;

        if (startsAtUtc == default)
            return PromotionErrors.StartsAtRequired;

        if (endsAtUtc == default)
            return PromotionErrors.EndsAtRequired;

        if (startsAtUtc >= endsAtUtc)
            return PromotionErrors.InvalidDateRange;

        if (code is { Length: > 50 })
            return PromotionErrors.CodeTooLong;

        var typeValidation = ValidateTypeFields(type, percentage, fixedAmount, promotionalPrice, chargedMonths);
        if (!typeValidation.IsSuccess)
            return typeValidation.Errors!;

        var benefitValidation = ValidateBenefitFields(
            type,
            freeMonthsCount,
            benefitName,
            benefitValue,
            benefitType,
            benefitCurrencyCode,
            benefitEligibilityRule);
        if (!benefitValidation.IsSuccess)
            return benefitValidation.Errors!;

        Name = name.Trim();
        Type = type;
        PlanId = planId;
        DurationMonths = durationMonths;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Priority = priority;
        Code = code?.Trim().ToUpperInvariant();
        Percentage = percentage;
        FixedAmount = fixedAmount;
        PromotionalPrice = promotionalPrice;
        ChargedMonths = chargedMonths;
        FreeMonthsCount = freeMonthsCount;
        BenefitName = benefitName?.Trim();
        BenefitDescription = benefitDescription?.Trim();
        BenefitValue = benefitValue;
        BenefitType = benefitType;
        BenefitCurrencyCode = benefitCurrencyCode?.Trim().ToUpperInvariant();
        BenefitEligibilityRule = benefitEligibilityRule;

        return Result.Updated;
    }

    private static Result<Updated> ValidateTypeFields(
        PromotionType type,
        decimal? percentage,
        decimal? fixedAmount,
        decimal? promotionalPrice,
        int? chargedMonths)
    {
        switch (type)
        {
            case PromotionType.PercentageDiscount:
                if (percentage is null || percentage <= 0 || percentage > 100)
                    return PromotionErrors.InvalidPercentage;
                break;

            case PromotionType.FixedAmountDiscount:
                if (fixedAmount is null || fixedAmount <= 0)
                    return PromotionErrors.InvalidFixedAmount;
                break;

            case PromotionType.PromotionalPrice:
                if (promotionalPrice is null || promotionalPrice < 0)
                    return PromotionErrors.InvalidPromotionalPrice;
                break;

            case PromotionType.PayForXMonths:
                if (chargedMonths is null || chargedMonths <= 0)
                    return PromotionErrors.InvalidChargedMonths;
                break;
        }

        return Result.Updated;
    }

    /// <summary>
    /// Validates the optional benefit configuration of a promotion.
    /// <para>
    /// A promotion grants EITHER free months OR an additional benefit — never both. Rejecting the
    /// mixed configuration keeps the calculated Offer unambiguous and prevents a single promotion
    /// from silently producing two different entitlement kinds at once.
    /// </para>
    /// </summary>
    private static Result<Updated> ValidateBenefitFields(
        PromotionType type,
        int? freeMonthsCount,
        string? benefitName,
        decimal? benefitValue,
        ContractBenefitType? benefitType,
        string? benefitCurrencyCode,
        EligibilityRule? benefitEligibilityRule)
    {
        var grantsFreeMonths = type == PromotionType.FreeMonthsBonus || freeMonthsCount.HasValue;
        var grantsBenefit = type == PromotionType.AdditionalBenefits
                            || benefitName is not null
                            || benefitValue.HasValue
                            || benefitType.HasValue
                            || benefitCurrencyCode is not null;

        if (grantsFreeMonths && grantsBenefit)
            return PromotionErrors.BenefitConfig_Conflicting;

        switch (type)
        {
            case PromotionType.FreeMonthsBonus:
                if (freeMonthsCount is null || freeMonthsCount <= 0)
                    return PromotionErrors.InvalidFreeMonthsCount;
                break;

            case PromotionType.AdditionalBenefits:
                if (string.IsNullOrWhiteSpace(benefitName))
                    return PromotionErrors.BenefitName_Required;
                if (benefitValue is null || benefitValue <= 0)
                    return PromotionErrors.InvalidBenefitValue;
                if (benefitType is null || !Enum.IsDefined(typeof(ContractBenefitType), benefitType.Value))
                    return PromotionErrors.InvalidBenefitType;
                if (string.IsNullOrWhiteSpace(benefitCurrencyCode) || benefitCurrencyCode.Trim().Length != 3)
                    return PromotionErrors.InvalidBenefitCurrencyCode;
                break;
        }

        // A benefit configuration attached to a discount-only promotion is a data-entry error:
        // the operator meant one of the dedicated benefit types. PayForXMonths is the one
        // discount type that may opt in to the free months path.
        if (type is not (PromotionType.FreeMonthsBonus or PromotionType.AdditionalBenefits or PromotionType.PayForXMonths)
            && (freeMonthsCount.HasValue
                || benefitName is not null
                || benefitValue.HasValue
                || benefitType.HasValue
                || benefitCurrencyCode is not null))
        {
            return PromotionErrors.BenefitConfig_NotSupportedForType(type);
        }

        // On AdditionalBenefits the configured benefit fields are required and validated above;
        // on every other type a stray non-positive free months count is still a data-entry error.
        if (type != PromotionType.FreeMonthsBonus && freeMonthsCount is <= 0)
            return PromotionErrors.InvalidFreeMonthsCount;

        // ---- The eligibility rule is trusted platform configuration, not a derived value ----
        // A benefit-bearing promotion MUST carry an explicit rule. There is deliberately no
        // default: silently inventing a rule would let the promotion type decide who gets the
        // entitlement, which is exactly the defect this corrects.
        if (grantsFreeMonths || grantsBenefit)
        {
            if (benefitEligibilityRule is null)
                return PromotionErrors.BenefitEligibilityRule_Required;
        }
        else if (benefitEligibilityRule is not null)
        {
            // A discount-only promotion grants nothing, so a rule on it is meaningless and rejected
            // rather than stored as dead configuration.
            return PromotionErrors.BenefitEligibilityRule_NotSupportedForType(type);
        }

        return Result.Updated;
    }
}
