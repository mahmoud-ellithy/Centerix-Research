namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Shared monotone (one-way) fulfillment lifecycle for any commercial benefit
/// persisted on a contract. Used by both <c>ContractBenefit</c>
/// (PhysicalGift: Pending → Granted → Delivered) and <c>FreeMonthsBenefit</c>
/// (Pending → Granted → AppliedToSubscription).
/// </summary>
/// <remarks>
/// <para>
/// This is the historical, monotonic dimension of the benefit lifecycle.
/// It MUST be monotonic: a row may only move forward.
/// </para>
/// <para>
/// The reversible eligibility dimension is <see cref="BenefitEligibilityStatus"/>
/// (or <c>FreeMonthsEligibilityStatus</c>). Eligibility changes MUST NEVER move
/// fulfillment backward.
/// </para>
/// <para>
/// Allowed transitions:
/// <list type="bullet">
///   <item><description>Pending → Granted</description></item>
///   <item><description>Granted → Delivered (PhysicalGift only)</description></item>
///   <item><description>Granted → AppliedToSubscription (FreeMonths only)</description></item>
/// </list>
/// </para>
/// </remarks>
public enum FulfillmentStatus
{
    /// <summary>
    /// Initial state on row creation. The benefit has not been granted yet.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The grant decision has been recorded. <c>GrantedAtUtc</c> is set.
    /// </summary>
    Granted = 1,

    /// <summary>
    /// Physical gift terminal state. The benefit has been physically handed over.
    /// Used by <c>ContractBenefit</c> with <c>BenefitType = PhysicalGift</c> only.
    /// </summary>
    Delivered = 2,

    /// <summary>
    /// FreeMonths terminal state. The bonus has been applied to the subscription's
    /// <c>EffectiveEndsAtUtc</c>. Used by <c>FreeMonthsBenefit</c> only.
    /// MUST NEVER be used for <c>ContractBenefit</c>.
    /// </summary>
    AppliedToSubscription = 3
}