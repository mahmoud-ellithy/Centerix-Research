namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Reversible eligibility state for any commercial benefit. Independent of
/// <see cref="FulfillmentStatus"/>: eligibility changes MUST NEVER move fulfillment
/// backward.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: <c>NotEligible ⇄ Eligible</c>. Bidirectional — the eligibility
/// evaluator may flip the benefit back to <c>NotEligible</c> on any re-evaluation.
/// </para>
/// <para>
/// This axis is intentionally limited to two values. Historical, monotonic
/// delivery / application progress lives on <see cref="FulfillmentStatus"/>.
/// </para>
/// </remarks>
public enum BenefitEligibilityStatus
{
    /// <summary>Benefit is configured but contractual eligibility conditions are not yet met.</summary>
    NotEligible = 0,

    /// <summary>Contractual conditions are satisfied; benefit is ready for grant / application.</summary>
    Eligible = 1
}