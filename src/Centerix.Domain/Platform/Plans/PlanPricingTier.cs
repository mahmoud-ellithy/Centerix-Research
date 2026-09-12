namespace Centerix.Domain.Platform.Plans;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Authoritative pricing tier defined at the Plan catalog level.
/// Determines the base price for a given contract duration.
/// When a Plan has applicable pricing tiers, the tier price is the authoritative base,
/// NOT MonthlyPrice × DurationMonths.
/// </summary>
/// <remarks>
/// Example:
///   1 Month  = 1,000
///   3 Months = 2,700
///   6 Months = 5,220
///   12 Months = 10,000
///
/// This is a global catalog entity (NOT tenant-scoped).
/// </remarks>
public class PlanPricingTier : Entity
{
    public int Id { get; private set; }
    public int PlanId { get; private set; }

    /// <summary>Duration of this tier in calendar months (e.g., 1, 3, 6, 12).</summary>
    public int DurationMonths { get; private set; }

    /// <summary>Total price for this tier duration (NOT per-month; total for the period).</summary>
    public decimal TierPrice { get; private set; }

    /// <summary>Display order for UI rendering.</summary>
    public int DisplayOrder { get; private set; }

    /// <summary>The Plan this pricing tier belongs to.</summary>
    public Plan Plan { get; private set; } = default!;

    private PlanPricingTier() { }

    private PlanPricingTier(
        int id,
        int planId,
        int durationMonths,
        decimal tierPrice,
        int displayOrder)
    {
        Id = id;
        PlanId = planId;
        DurationMonths = durationMonths;
        TierPrice = tierPrice;
        DisplayOrder = displayOrder;
    }

    /// <summary>
    /// Creates a plan pricing tier with validated parameters.
    /// </summary>
    public static Result<PlanPricingTier> Create(
        int id,
        int planId,
        int durationMonths,
        decimal tierPrice,
        int displayOrder = 0)
    {
        if (id < 0)
            return Error.Validation("PlanPricingTier.Id_Invalid", "Pricing tier ID must be non-negative");

        if (planId <= 0)
            return Error.Validation("PlanPricingTier.PlanId_Required", "Plan ID is required");

        if (durationMonths <= 0)
            return Error.Validation("PlanPricingTier.Duration_Invalid", "Duration must be at least one month");

        if (tierPrice < 0)
            return Error.Validation("PlanPricingTier.Price_Invalid", "Tier price cannot be negative");

        return new PlanPricingTier(id, planId, durationMonths, tierPrice, displayOrder);
    }
}
