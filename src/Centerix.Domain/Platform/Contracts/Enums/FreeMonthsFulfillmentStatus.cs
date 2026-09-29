namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Monotone (one-way) fulfillment status for a <c>FreeMonthsBenefit</c>.
///
/// Mirrors the lifecycle declared in <c>docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md</c>
/// §F.3 and §G.2. The Grant and Application transitions are append-only; the
/// <c>EligibilityStatus</c> is the reversible counter-part. Once a row reaches
/// <see cref="AppliedToSubscription"/> it is terminal: the bonus has been
/// written into the subscription's entitlement period and may not extend it
/// again.
/// </summary>
/// <remarks>
/// Lifecycle (monotone):
/// <code>
/// Pending  →  Granted  →  AppliedToSubscription (terminal)
/// </code>
///
/// <list type="bullet">
///   <item><description><c>Pending</c> — initial state on row creation.</description></item>
///   <item><description><c>Granted</c> — recorded exactly once when the eligibility evaluator first returns true AND the grant step is executed. Sets <c>GrantedAtUtc</c>.</description></item>
///   <item><description><c>AppliedToSubscription</c> — recorded exactly once when the bonus has been added to <c>TenantPlan.EffectiveEndsAtUtc</c> via the explicit <c>ApplyFreeMonthsToSubscriptionCommand</c>. Terminal state.</description></item>
/// </list>
///
/// There is no <c>Delivered</c> value — that state belongs to <c>ContractBenefit</c>
/// with <c>BenefitType = PhysicalGift</c>, not FreeMonths.
/// </remarks>
public enum FreeMonthsFulfillmentStatus
{
    /// <summary>
    /// Initial state on row creation. The benefit has not been granted yet.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The grant decision has been recorded. <c>GrantedAtUtc</c> is set.
    /// The benefit has not yet been added to the subscription's
    /// <c>EffectiveEndsAtUtc</c>.
    /// </summary>
    Granted = 1,

    /// <summary>
    /// Terminal state. The bonus has been added to the subscription's
    /// <c>EffectiveEndsAtUtc</c> exactly once; <c>TenantPlan.AppliedFreeMonthsBenefitIds</c>
    /// contains this row's <c>Id</c> and may not contain it twice.
    /// </summary>
    AppliedToSubscription = 2
}
