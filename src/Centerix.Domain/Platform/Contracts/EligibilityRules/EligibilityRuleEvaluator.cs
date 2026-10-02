namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

/// <summary>
/// Pure, recursive evaluator for the closed <see cref="EligibilityRule"/> algebra. The evaluator
/// does not perform I/O, has no external dependencies, and is therefore registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator walks the rule tree recursively. Composite rules short-circuit on the first
/// failing child (<c>AllOf</c>) or the first passing child (<c>AnyOf</c>). Leaf rules translate
/// directly into <see cref="WhyIneligible"/> reason codes that callers can persist into the
/// <c>FreezeReason</c> snapshot column.
/// </para>
/// <para>
/// Reason paths are stable strings of the form <c>"AllOf[0].AmountPaidAtLeast(100)"</c> so
/// audit snapshots can be diffed against the rule algebra without resolving CLR type names.
/// </para>
/// </remarks>
public sealed class EligibilityRuleEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="rule"/> against <paramref name="context"/>.
    /// </summary>
    /// <param name="rule">The rule tree to evaluate. Must not be <c>null</c>.</param>
    /// <param name="context">The immutable fact aggregate. Must not be <c>null</c>.</param>
    /// <returns>An <see cref="EligibilityRuleEvaluationResult"/> describing the outcome.</returns>
    public EligibilityRuleEvaluationResult Evaluate(EligibilityRule rule, EligibilityContext context)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (context is null) throw new ArgumentNullException(nameof(context));

        return rule switch
        {
            AllOfRule r => EvaluateAllOf(r, context),
            AnyOfRule r => EvaluateAnyOf(r, context),
            ContractActiveRule r => EvaluateContractActive(r, context),
            NoOverdueInstallmentRule r => EvaluateNoOverdueInstallment(r, context),
            PaymentTermsEqualsRule r => EvaluatePaymentTermsEquals(r, context),
            PaymentMethodEqualsRule r => EvaluatePaymentMethodEquals(r, context),
            CompletedByUtcRule r => EvaluateCompletedByUtc(r, context),
            AmountPaidAtLeastRule r => EvaluateAmountPaidAtLeast(r, context),
            DaysFromContractStartGteRule r => EvaluateDaysFromContractStartGte(r, context),
            DurationMonthsGteRule r => EvaluateDurationMonthsGte(r, context),
            _ => EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.EvaluatorError,
                $"Unknown({rule.GetType().Name})")
        };
    }

    // ─── Composites ──────────────────────────────────────────────────────────

    private EligibilityRuleEvaluationResult EvaluateAllOf(AllOfRule rule, EligibilityContext context)
    {
        var children = rule.Rules;
        // The constructor of AllOfRule guarantees a non-empty set, but we defend defensively.
        if (children.Count == 0)
            return EligibilityRuleEvaluationResult.Eligible();

        for (int i = 0; i < children.Count; i++)
        {
            var inner = Evaluate(children[i], context);
            if (!inner.IsEligible)
            {
                var path = $"AllOf[{i}].{inner.ReasonPath ?? children[i].ToString()}";
                return EligibilityRuleEvaluationResult.Ineligible(inner.Reason, path);
            }
        }

        return EligibilityRuleEvaluationResult.Eligible();
    }

    private EligibilityRuleEvaluationResult EvaluateAnyOf(AnyOfRule rule, EligibilityContext context)
    {
        var children = rule.Rules;
        if (children.Count == 0)
            return EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.AnyOfNoChildPassed,
                "AnyOf[]");

        for (int i = 0; i < children.Count; i++)
        {
            var inner = Evaluate(children[i], context);
            if (inner.IsEligible)
                return EligibilityRuleEvaluationResult.Eligible();
        }

        return EligibilityRuleEvaluationResult.Ineligible(
            WhyIneligible.AnyOfNoChildPassed,
            "AnyOf[]");
    }

    // ─── Leaf rules ──────────────────────────────────────────────────────────

    private static EligibilityRuleEvaluationResult EvaluateContractActive(
        ContractActiveRule rule, EligibilityContext context)
    {
        return context.ContractStatus == Centerix.Domain.Platform.Contracts.Enums.ContractStatus.Active
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.ContractNotActive,
                "ContractActive");
    }

    private static EligibilityRuleEvaluationResult EvaluateNoOverdueInstallment(
        NoOverdueInstallmentRule rule, EligibilityContext context)
    {
        return !context.HasOverdueInstallment
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.OverdueInstallment,
                "NoOverdueInstallment");
    }

    private static EligibilityRuleEvaluationResult EvaluatePaymentTermsEquals(
        PaymentTermsEqualsRule rule, EligibilityContext context)
    {
        return context.PaymentTerms == rule.PaymentTerms
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.PaymentTermsMismatch,
                $"PaymentTermsEquals({rule.PaymentTerms})");
    }

    private static EligibilityRuleEvaluationResult EvaluatePaymentMethodEquals(
        PaymentMethodEqualsRule rule, EligibilityContext context)
    {
        // Canonicalise the context's payment method to match how the rule stores its value.
        var observed = context.PaymentMethod is null
            ? null
            : context.PaymentMethod.Trim().ToUpperInvariant();

        var expected = rule.PaymentMethod;

        return string.Equals(observed, expected, StringComparison.Ordinal)
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.PaymentMethodMismatch,
                $"PaymentMethodEquals(\"{expected}\")");
    }

    private static EligibilityRuleEvaluationResult EvaluateCompletedByUtc(
        CompletedByUtcRule rule, EligibilityContext context)
    {
        var deadline = context.CompletedByUtc ?? rule.CompletedBy;
        return context.UtcNow <= deadline
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.DeadlinePassed,
                $"CompletedByUtc({deadline:O})");
    }

    private static EligibilityRuleEvaluationResult EvaluateAmountPaidAtLeast(
        AmountPaidAtLeastRule rule, EligibilityContext context)
    {
        return context.AmountPaid >= rule.Amount
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.AmountBelowMinimum,
                $"AmountPaidAtLeast({rule.Amount})");
    }

    private static EligibilityRuleEvaluationResult EvaluateDaysFromContractStartGte(
        DaysFromContractStartGteRule rule, EligibilityContext context)
    {
        return context.DaysFromContractStart >= rule.Days
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.DaysFromContractStartNotMet,
                $"DaysFromContractStartGte({rule.Days})");
    }

    private static EligibilityRuleEvaluationResult EvaluateDurationMonthsGte(
        DurationMonthsGteRule rule, EligibilityContext context)
    {
        return context.ContractDurationMonths >= rule.Months
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.DurationMonthsNotMet,
                $"DurationMonthsGte({rule.Months})");
    }
}