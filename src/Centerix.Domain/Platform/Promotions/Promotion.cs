namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
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
        int? chargedMonths)
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
        int? chargedMonths = null)
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
            chargedMonths);
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
        int? chargedMonths = null)
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
}
