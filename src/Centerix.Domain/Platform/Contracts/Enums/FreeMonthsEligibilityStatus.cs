namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Reversible eligibility status for a <c>FreeMonthsBenefit</c>.
///
/// Mirrors the lifecycle declared in <c>docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md</c> §F.3
/// and §G.1. This enum is intentionally separate from the existing
/// <see cref="BenefitEligibilityStatus"/> (which is PhysicalGift-specific and
/// forward-only to <c>Delivered</c>); FreeMonths requires two independent states
/// (Eligibility vs Fulfillment) so the same enum cannot be reused.
/// </summary>
/// <remarks>
/// Lifecycle (reversible):
/// <code>
/// NotEligible  ⇄  Eligible
/// </code>
///
/// Re-evaluation by <c>BenefitEligibilityEvaluator</c> may flip the state freely
/// between <c>NotEligible</c> and <c>Eligible</c> as financial state changes.
/// The transition is recorded but is not historical — it does not consume any
/// grant decision and does not affect the <c>FulfillmentStatus</c>.
/// </remarks>
public enum FreeMonthsEligibilityStatus
{
    /// <summary>
    /// The benefit's <c>EligibilityRule</c> currently evaluates to false. No grant
    /// decision has been made. This is the initial state on row creation.
    /// </summary>
    NotEligible = 0,

    /// <summary>
    /// The benefit's <c>EligibilityRule</c> currently evaluates to true. The
    /// row is eligible to be granted, but no grant decision has been recorded
    /// yet — <c>GrantBenefitCommand</c> is still required.
    /// </summary>
    Eligible = 1
}
