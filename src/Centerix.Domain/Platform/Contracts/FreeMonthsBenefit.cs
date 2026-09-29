namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// A commercial entitlement that grants one or more free months of subscription
/// service as part of a Contract.
///
/// <para>
/// Four independent counters/states (per
/// <c>docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md</c> §F):
/// </para>
/// <list type="number">
///   <item>
///     <term>Commercial Entitlement</term>
///     <description>
///       <see cref="EntitlementMonths"/> — the number of free months the contract
///       grants. Configured at Offer/Contract creation; <b>immutable</b> after
///       Contract creation.
///     </description>
///   </item>
///   <item>
///     <term>Eligibility</term>
///     <description>
///       <see cref="EligibilityStatus"/> — derived from
///       <see cref="EligibilityRule"/>; <b>reversible</b> via <see cref="MarkEligible"/>
///       and <see cref="MarkNotEligible"/>.
///     </description>
///   </item>
///   <item>
///     <term>Grant</term>
///     <description>
///       <see cref="FulfillmentStatus"/> = <see cref="FreeMonthsFulfillmentStatus.Granted"/>
///       and <see cref="GrantedAtUtc"/> — recorded exactly once by <see cref="Grant"/>.
///       <b>Monotone</b>.
///     </description>
///   </item>
///   <item>
///     <term>Application</term>
///     <description>
///       <see cref="FulfillmentStatus"/> = <see cref="FreeMonthsFulfillmentStatus.AppliedToSubscription"/>
///       and <see cref="AppliedAtUtc"/> — recorded exactly once by
///       <see cref="MarkAppliedToSubscription"/>; the bonus has been added to
///       the subscription's <c>EffectiveEndsAtUtc</c>. <b>Terminal</b>.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// Critical invariants (per docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md §F.2):
/// </para>
/// <list type="bullet">
///   <item><description><c>EntitlementMonths</c> is the commercial definition. It does <b>not</b> mean <c>Granted = true</c> or <c>Applied = true</c>.</description></item>
///   <item><description>Eligibility is reversible; Fulfillment is monotone; Application is idempotent.</description></item>
///   <item><description>Bonus months never increase the billable <c>ContractedAmount</c>.</description></item>
///   <item><description><c>Contract.BonusMonths</c> is <b>NOT</b> <c>Σ FreeMonthsBenefits.EntitlementMonths</c> and is <b>NOT</b> <c>Σ FreeMonthsBenefits.GrantedMonths</c>. The legacy scalar is preserved unchanged until T11 makes it derived.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Out of scope (handled by later tasks): the eligibility evaluator
/// (<c>BenefitEligibilityEvaluator</c>), <c>GrantBenefitCommand</c>,
/// <c>ApplyFreeMonthsToSubscriptionCommand</c>, the
/// <c>TenantPlan.AppliedFreeMonthsBenefitIds[]</c> invariant, and
/// <c>RefundCalculationService</c> changes. This aggregate exposes the state
/// transitions those commands will invoke.
/// </para>
/// <para>
/// Persistence split rationale (per design §D.2): FreeMonths must call
/// <c>TenantPlan.ApplyBonusMonths(...)</c> on fulfillment, while
/// <c>ContractBenefit</c> records <c>DeliveredBy</c>. The two fulfill on
/// different lifecycles, so they remain separate persistence aggregates that
/// share the <c>EligibilityRule</c> evaluation pipeline.
/// </para>
/// </remarks>
public class FreeMonthsBenefit : Entity
{
    public Guid Id { get; private set; }

    /// <summary>The <c>Contract</c> this benefit belongs to.</summary>
    public Guid ContractId { get; private set; }

    /// <summary>
    /// The number of free months the contract grants as a commercial entitlement.
    /// <b>Immutable</b> after creation. Distinct from <c>Contract.BonusMonths</c>
    /// (legacy scalar preserved by design until T11).
    /// </summary>
    public int EntitlementMonths { get; private set; }

    /// <summary>ISO-4217 currency code (e.g., EGP, USD). Snapshotted from the contract.</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>
    /// Reversible eligibility flag. Re-derived from <see cref="EligibilityRule"/>
    /// by the (future) evaluator; this row carries the latest evaluated value.
    /// </summary>
    public FreeMonthsEligibilityStatus EligibilityStatus { get; private set; }

    /// <summary>UTC timestamp of the most recent transition to <see cref="FreeMonthsEligibilityStatus.Eligible"/>.</summary>
    public DateTime? EligibleAtUtc { get; private set; }

    /// <summary>
    /// Monotone fulfillment flag. Always <see cref="FreeMonthsFulfillmentStatus.Pending"/>
    /// on row creation; transitions to <see cref="FreeMonthsFulfillmentStatus.Granted"/>
    /// then <see cref="FreeMonthsFulfillmentStatus.AppliedToSubscription"/>.
    /// </summary>
    public FreeMonthsFulfillmentStatus FulfillmentStatus { get; private set; }

    /// <summary>UTC timestamp of the <see cref="FreeMonthsFulfillmentStatus.Granted"/> transition. Null before grant.</summary>
    public DateTime? GrantedAtUtc { get; private set; }

    /// <summary>UTC timestamp of the <see cref="FreeMonthsFulfillmentStatus.AppliedToSubscription"/> transition. Null before apply.</summary>
    public DateTime? AppliedAtUtc { get; private set; }

    /// <summary>
    /// The commercial eligibility rule attached to this benefit (required, not nullable).
    /// Describes WHAT conditions must hold for the benefit to become eligible.
    /// Distinct from <c>ContractBenefit.EligibilityRule</c> — FreeMonths rules
    /// commonly include <c>PaymentTermsEq(FullUpfront)</c> per Scenario A.
    /// </summary>
    public EligibilityRule EligibilityRule { get; private set; } = default!;

    /// <summary>Navigation back to the parent Contract.</summary>
    public Contract Contract { get; private set; } = default!;

    private FreeMonthsBenefit() { }

    private FreeMonthsBenefit(
        Guid id,
        Guid contractId,
        int entitlementMonths,
        string currencyCode,
        EligibilityRule eligibilityRule)
    {
        Id = id;
        ContractId = contractId;
        EntitlementMonths = entitlementMonths;
        CurrencyCode = currencyCode;
        EligibilityRule = eligibilityRule;
        EligibilityStatus = FreeMonthsEligibilityStatus.NotEligible;
        FulfillmentStatus = FreeMonthsFulfillmentStatus.Pending;
    }

    /// <summary>
    /// Creates a FreeMonthsBenefit with validated parameters.
    /// </summary>
    /// <param name="id">Required, non-empty GUID.</param>
    /// <param name="contractId">Required, non-empty GUID.</param>
    /// <param name="entitlementMonths">Required, positive integer.</param>
    /// <param name="currencyCode">Required, 3-letter ISO-4217 code.</param>
    /// <param name="eligibilityRule">Required, non-null commercial eligibility rule.</param>
    /// <remarks>
    /// FreeMonthsBenefit always carries an EligibilityRule on creation — there
    /// is no legacy null-rule mode (per design §F: "every Entitlement Benefit row
    /// MUST carry exactly one EligibilityRule"). The migration that adds this
    /// table is schema-only; no historical FreeMonthsBenefit rows exist to backfill.
    /// </remarks>
    public static Result<FreeMonthsBenefit> Create(
        Guid id,
        Guid contractId,
        int entitlementMonths,
        string currencyCode,
        EligibilityRule eligibilityRule)
    {
        if (id == Guid.Empty)
            return FreeMonthsBenefitErrors.IdRequired;

        if (contractId == Guid.Empty)
            return FreeMonthsBenefitErrors.ContractIdRequired;

        if (entitlementMonths <= 0)
            return FreeMonthsBenefitErrors.EntitlementMonthsRequired;

        if (eligibilityRule is null)
            return FreeMonthsBenefitErrors.EligibilityRuleRequired;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return FreeMonthsBenefitErrors.CurrencyRequired;

        return new FreeMonthsBenefit(
            id,
            contractId,
            entitlementMonths,
            currencyCode.Trim().ToUpperInvariant(),
            eligibilityRule);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Reversible Eligibility transitions
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions this benefit from <see cref="FreeMonthsEligibilityStatus.NotEligible"/>
    /// to <see cref="FreeMonthsEligibilityStatus.Eligible"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// Rejected once the benefit has reached <see cref="FreeMonthsFulfillmentStatus.Granted"/>
    /// — once a grant decision has been recorded, eligibility is locked. This
    /// preserves the design invariant "Granted is historical / monotone".
    /// </remarks>
    public Result<Updated> MarkEligible(DateTime utcNow)
    {
        if (!Enum.IsDefined(EligibilityStatus))
            return FreeMonthsBenefitErrors.InvalidEligibilityStatus;

        // Once Granted (or AppliedToSubscription), eligibility is historical and
        // cannot be moved to NotEligible; it is also meaningless to mark it Eligible
        // again because the grant decision stands.
        if (FulfillmentStatus is FreeMonthsFulfillmentStatus.Granted
                                or FreeMonthsFulfillmentStatus.AppliedToSubscription)
            return FreeMonthsBenefitErrors.CannotRevertFromGranted;

        if (EligibilityStatus == FreeMonthsEligibilityStatus.Eligible)
            return Result.Updated;

        EligibilityStatus = FreeMonthsEligibilityStatus.Eligible;
        EligibleAtUtc = utcNow;
        return Result.Updated;
    }

    /// <summary>
    /// Transitions this benefit from <see cref="FreeMonthsEligibilityStatus.Eligible"/>
    /// back to <see cref="FreeMonthsEligibilityStatus.NotEligible"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// Rejected once <see cref="FulfillmentStatus"/> has reached
    /// <see cref="FreeMonthsFulfillmentStatus.Granted"/> or
    /// <see cref="FreeMonthsFulfillmentStatus.AppliedToSubscription"/>. See
    /// <see cref="MarkEligible"/> for the rationale.
    /// </remarks>
    public Result<Updated> MarkNotEligible()
    {
        if (!Enum.IsDefined(EligibilityStatus))
            return FreeMonthsBenefitErrors.InvalidEligibilityStatus;

        if (FulfillmentStatus is FreeMonthsFulfillmentStatus.Granted
                                or FreeMonthsFulfillmentStatus.AppliedToSubscription)
            return FreeMonthsBenefitErrors.CannotRevertFromGranted;

        if (EligibilityStatus == FreeMonthsEligibilityStatus.NotEligible)
            return Result.Updated;

        EligibilityStatus = FreeMonthsEligibilityStatus.NotEligible;
        EligibleAtUtc = null;
        return Result.Updated;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Monotone Fulfillment transitions
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the grant decision. Transitions
    /// <see cref="FulfillmentStatus"/> from <see cref="FreeMonthsFulfillmentStatus.Pending"/>
    /// to <see cref="FreeMonthsFulfillmentStatus.Granted"/> and stamps
    /// <see cref="GrantedAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="EligibilityStatus"/> to be
    /// <see cref="FreeMonthsEligibilityStatus.Eligible"/> at the moment of grant —
    /// this is the "GrantBenefitCommand verifies EligibilityStatus == Eligible"
    /// invariant (per design §F.3 and §G.3). Idempotent on already-Granted.
    /// </remarks>
    public Result<Updated> Grant(DateTime utcNow)
    {
        if (!Enum.IsDefined(FulfillmentStatus))
            return FreeMonthsBenefitErrors.InvalidFulfillmentStatus;

        if (FulfillmentStatus == FreeMonthsFulfillmentStatus.Granted)
            return Result.Updated;

        // Application is terminal — calling Grant after MarkAppliedToSubscription
        // is a no-op (the bonus has already extended the subscription once).
        if (FulfillmentStatus == FreeMonthsFulfillmentStatus.AppliedToSubscription)
            return Result.Updated;

        if (FulfillmentStatus != FreeMonthsFulfillmentStatus.Pending)
            return FreeMonthsBenefitErrors.InvalidFulfillmentStatus;

        if (EligibilityStatus != FreeMonthsEligibilityStatus.Eligible)
            return FreeMonthsBenefitErrors.NotEligible;

        FulfillmentStatus = FreeMonthsFulfillmentStatus.Granted;
        GrantedAtUtc = utcNow;
        return Result.Updated;
    }

    /// <summary>
    /// Records application of the bonus to the subscription. Transitions
    /// <see cref="FulfillmentStatus"/> from <see cref="FreeMonthsFulfillmentStatus.Granted"/>
    /// to <see cref="FreeMonthsFulfillmentStatus.AppliedToSubscription"/> and stamps
    /// <see cref="AppliedAtUtc"/>. Terminal: idempotent on already-Applied.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="FulfillmentStatus"/> to be
    /// <see cref="FreeMonthsFulfillmentStatus.Granted"/> at the moment of apply —
    /// this is the "ApplyFreeMonthsToSubscriptionCommand verifies FulfillmentStatus == Granted"
    /// invariant (per design §F.3 and §G.3). The TenantPlan-side idempotency
    /// guard (using <c>TenantPlan.AppliedFreeMonthsBenefitIds[]</c>) is enforced
    /// by the command, not here.
    /// </remarks>
    public Result<Updated> MarkAppliedToSubscription(DateTime utcNow)
    {
        if (!Enum.IsDefined(FulfillmentStatus))
            return FreeMonthsBenefitErrors.InvalidFulfillmentStatus;

        if (FulfillmentStatus == FreeMonthsFulfillmentStatus.AppliedToSubscription)
            return Result.Updated;

        if (FulfillmentStatus != FreeMonthsFulfillmentStatus.Granted)
            return FreeMonthsBenefitErrors.NotGranted;

        FulfillmentStatus = FreeMonthsFulfillmentStatus.AppliedToSubscription;
        AppliedAtUtc = utcNow;
        return Result.Updated;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Read-only projections for callers (commands / queries)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>True iff <see cref="FulfillmentStatus"/> is <see cref="FreeMonthsFulfillmentStatus.AppliedToSubscription"/>.</summary>
    public bool IsAppliedToSubscription =>
        FulfillmentStatus == FreeMonthsFulfillmentStatus.AppliedToSubscription;

    /// <summary>True iff <see cref="FulfillmentStatus"/> is <see cref="FreeMonthsFulfillmentStatus.Granted"/> (or later).</summary>
    public bool IsGranted =>
        FulfillmentStatus is FreeMonthsFulfillmentStatus.Granted
                            or FreeMonthsFulfillmentStatus.AppliedToSubscription;

    /// <summary>True iff <see cref="EligibilityStatus"/> is <see cref="FreeMonthsEligibilityStatus.Eligible"/>.</summary>
    public bool IsEligible =>
        EligibilityStatus == FreeMonthsEligibilityStatus.Eligible;
}
