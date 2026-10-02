namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

using System.Collections.Generic;
using System.Linq;
using Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Abstract base for the closed algebraic expression that describes WHAT commercial
/// conditions must hold for a benefit to become eligible.
/// </summary>
/// <remarks>
/// This is a DATA MODEL ONLY — it represents the rule definition and does NOT evaluate
/// whether the conditions are currently satisfied. Evaluation is a later task.
///
/// Algebra:
///   EligibilityRule
///     ::= AllOf(EligibilityRule[])
///       | AnyOf(EligibilityRule[])
///       | ContractActive
///       | PaymentTermsEq(PaymentTerms)
///       | PaymentMethodEq(string)
///       | CompletedByUtc(DateTime)
///       | NoOverdueInstallment
///       | AmountPaidAtLeast(decimal)
///       | DaysFromContractStartGte(int)
///       | DurationMonthsGte(int)
///
/// Immutability: all concrete rule types are sealed with no public setters.
/// Equality: structural value equality is implemented on all rule types.
/// Serialization: use EligibilityRuleSerializer; CLR type names are never stored.
/// </remarks>
public abstract class EligibilityRule : IEquatable<EligibilityRule>
{
    // ─── Factory methods (domain API) ─────────────────────────────────────────

    /// <summary>Creates a rule that requires the contract to be in Active status.</summary>
    public static EligibilityRule ContractActive() => ContractActiveRule.Instance;

    /// <summary>Creates a rule that requires no overdue installment on the contract.</summary>
    public static EligibilityRule NoOverdueInstallment() => NoOverdueInstallmentRule.Instance;

    /// <summary>Creates a rule that requires the contract's PaymentTerms to equal <paramref name="paymentTerms"/>.</summary>
    /// <param name="paymentTerms">The required payment terms value. Must be a defined enum member.</param>
    public static EligibilityRule PaymentTermsEquals(PaymentTerms paymentTerms)
        => new PaymentTermsEqualsRule(paymentTerms);

    /// <summary>Creates a rule that requires the payment method to equal <paramref name="paymentMethod"/>.</summary>
    /// <param name="paymentMethod">Non-null, non-empty payment method string.</param>
    public static EligibilityRule PaymentMethodEquals(string paymentMethod)
        => new PaymentMethodEqualsRule(paymentMethod);

    /// <summary>Creates a rule that requires the contract to be completed by <paramref name="completedByUtc"/>.</summary>
    /// <param name="completedByUtc">A valid UTC DateTime instant.</param>
    public static EligibilityRule CompletedByUtc(DateTime completedByUtc)
        => new CompletedByUtcRule(completedByUtc);

    /// <summary>Creates a rule that requires at least <paramref name="amount"/> to have been paid.</summary>
    /// <param name="amount">Must be &gt;= 0.</param>
    public static EligibilityRule AmountPaidAtLeast(decimal amount)
        => new AmountPaidAtLeastRule(amount);

    /// <summary>Creates a rule that requires at least <paramref name="days"/> days to have elapsed since contract start.</summary>
    /// <param name="days">Must be &gt;= 0.</param>
    public static EligibilityRule DaysFromContractStartGte(int days)
        => new DaysFromContractStartGteRule(days);

    /// <summary>Creates a rule that requires the contract duration to be at least <paramref name="months"/> months.</summary>
    /// <param name="months">Must be &gt;= 0.</param>
    public static EligibilityRule DurationMonthsGte(int months)
        => new DurationMonthsGteRule(months);

    /// <summary>
    /// Creates a composite rule requiring ALL child rules to hold.
    /// </summary>
    /// <param name="rules">At least one non-null child rule.</param>
    public static EligibilityRule AllOf(params EligibilityRule[] rules)
        => new AllOfRule(rules);

    /// <summary>
    /// Creates a composite rule requiring ALL child rules to hold.
    /// </summary>
    /// <param name="rules">At least one non-null child rule.</param>
    public static EligibilityRule AllOf(IEnumerable<EligibilityRule> rules)
        => new AllOfRule(rules);

    /// <summary>
    /// Creates a composite rule requiring ANY ONE child rule to hold.
    /// </summary>
    /// <param name="rules">At least one non-null child rule.</param>
    public static EligibilityRule AnyOf(params EligibilityRule[] rules)
        => new AnyOfRule(rules);

    /// <summary>
    /// Creates a composite rule requiring ANY ONE child rule to hold.
    /// </summary>
    /// <param name="rules">At least one non-null child rule.</param>
    public static EligibilityRule AnyOf(IEnumerable<EligibilityRule> rules)
        => new AnyOfRule(rules);

    // ─── Canonical commercial rules ───────────────────────────────────────────

    /// <summary>
    /// The canonical eligibility rule for a physical-gift benefit as it was
    /// implemented globally before per-benefit rules existed.
    ///
    /// Rule: ContractActive AND AmountPaidAtLeast(contractedAmount) AND NoOverdueInstallment
    ///
    /// Notes:
    /// - PaymentTerms is NOT part of this rule to preserve existing behavior.
    /// - This factory does NOT inspect any contract, payment, installment, or PromotionType.
    /// </summary>
    /// <param name="contractedAmount">The contracted amount for the contract (>= 0). Defaults to 0m.</param>
    public static EligibilityRule DefaultPhysicalGiftRule(decimal contractedAmount = 0m) =>
        AllOf(
            ContractActive(),
            AmountPaidAtLeast(contractedAmount),
            NoOverdueInstallment());

    // ─── Abstract equality contract ───────────────────────────────────────────

    /// <inheritdoc />
    public abstract bool Equals(EligibilityRule? other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EligibilityRule rule && Equals(rule);

    /// <inheritdoc />
    public override abstract int GetHashCode();

    // ─── Evaluation surface (Task F) ──────────────────────────────────────────

    /// <summary>
    /// Evaluates this rule tree against the supplied <see cref="EligibilityContext"/>. Delegates
    /// to <see cref="EligibilityRuleEvaluator"/> which walks the tree recursively and emits a
    /// reason code on failure.
    /// </summary>
    /// <remarks>
    /// The evaluator is held as a static singleton to keep this method allocation-free for the
    /// common case. It is stateless and thread-safe by construction.
    /// </remarks>
    /// <param name="context">The immutable fact aggregate.</param>
    /// <returns><c>true</c> iff the rule passes against the context.</returns>
    public bool IsEligible(EligibilityContext context)
            => new EligibilityRuleEvaluator().Evaluate(this, context).IsEligible;
}
