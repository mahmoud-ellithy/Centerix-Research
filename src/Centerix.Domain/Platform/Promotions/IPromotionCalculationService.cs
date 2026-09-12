namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;

/// <summary>
/// Service interface for calculating commercial offers by applying Promotions to Plans.
/// All calculations are deterministic and side-effect-free.
/// </summary>
public interface IPromotionCalculationService
{
    /// <summary>
    /// Calculates the best offer for the given plan and duration at the specified evaluation time.
    /// Selects the highest-priority eligible promotion (no stacking).
    /// </summary>
    /// <param name="plan">The plan to calculate the offer for.</param>
    /// <param name="durationMonths">The contract duration in months.</param>
    /// <param name="evaluationTime">The point in time for promotion evaluation.</param>
    /// <param name="eligiblePromotions">Pre-filtered list of promotions that match plan/duration scope.</param>
    /// <returns>A calculated offer with all commercial terms.</returns>
    Result<CalculatedOffer> Calculate(
        Plan plan,
        int durationMonths,
        DateTime evaluationTime,
        IReadOnlyList<Promotion> eligiblePromotions);
}
