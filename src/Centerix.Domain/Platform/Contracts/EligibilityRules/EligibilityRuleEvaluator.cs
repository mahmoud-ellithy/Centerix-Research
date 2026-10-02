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
        // The rule is satisfied if at least one authoritative completed payment carries the
        // matching method. We do NOT silently pick "the latest payment" — the existence of any
        // qualifying completion fact suffices. Whitespace + casing follow Task B canonicalisation.
        var expected = rule.PaymentMethod;
        var anyMatch = false;
        for (int i = 0; i < context.CompletedPayments.Count; i++)
        {
            var observed = context.CompletedPayments[i].MethodCanonical;
            if (string.Equals(observed, expected, StringComparison.Ordinal))
            {
                anyMatch = true;
                break;
            }
        }

        return anyMatch
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.PaymentMethodMismatch,
                $"PaymentMethodEquals(\"{expected}\")");
    }

    private static EligibilityRuleEvaluationResult EvaluateCompletedByUtc(
        CompletedByUtcRule rule, EligibilityContext context)
    {
        // FIX F1: A deadline is satisfied iff at least one authoritative completion fact has
        // CompletedAtUtc <= deadline. The deadline passing alone is NOT enough — there must be
        // a qualifying completed payment. We never fabricate a completion timestamp from the
        // contract creation date, invoice date, allocation date, current time, or installments.
        var deadline = rule.CompletedBy;
        for (int i = 0; i < context.CompletedPayments.Count; i++)
        {
            var completedAt = context.CompletedPayments[i].CompletedAtUtc;
            if (completedAt.Kind != DateTimeKind.Utc)
            {
                // Defensive: any non-UTC timestamp is a model bug; treat as never qualifying.
                continue;
            }
            if (completedAt <= deadline)
                return EligibilityRuleEvaluationResult.Eligible();
        }

        return EligibilityRuleEvaluationResult.Ineligible(
            WhyIneligible.DeadlinePassed,
            $"CompletedByUtc({deadline:O})");
    }

    private static EligibilityRuleEvaluationResult EvaluateAmountPaidAtLeast(
        AmountPaidAtLeastRule rule, EligibilityContext context)
    {
        // Currency-consistent sum: only payments whose canonicalised currency matches the
        // contract's currency contribute. Payments in other currencies MUST NOT silently
        // contribute — that would corrupt fee/tax thresholds. EGP is not a hardcoded fallback.
        //
        // The amount summed is AllocatedAmountForThisContract (NOT Payment.Amount). A single
        // Payment may be allocated across multiple contracts/invoices, and only the slice
        // attributable to the evaluated Contract contributes. This prevents double-counting
        // and ensures contract-level eligibility reflects settlement attributable to that
        // contract only.
        var contractCurrency = context.ContractCurrencyCode;
        decimal total = 0m;
        for (int i = 0; i < context.CompletedPayments.Count; i++)
        {
            var fact = context.CompletedPayments[i];
            if (string.Equals(fact.CurrencyCode, contractCurrency, StringComparison.Ordinal))
                total += fact.AllocatedAmountForThisContract;
        }

        return total >= rule.Amount
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.AmountBelowMinimum,
                $"AmountPaidAtLeast({rule.Amount})");
    }

    private static EligibilityRuleEvaluationResult EvaluateDaysFromContractStartGte(
        DaysFromContractStartGteRule rule, EligibilityContext context)
    {
        // FIX F3: Elapsed is computed as a TimeSpan from the authoritative contract start instant
        // to UtcNow. We do NOT slice the calendar into .Date boundaries — that approach counts
        // midnight crossings rather than elapsed duration and silently breaks the 1-hour
        // 23:00→00:00 boundary test below.
        var elapsed = context.UtcNow - context.ContractStartUtc;
        var required = TimeSpan.FromDays(rule.Days);

        return elapsed >= required
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.DaysFromContractStartNotMet,
                $"DaysFromContractStartGte({rule.Days})");
    }

    private static EligibilityRuleEvaluationResult EvaluateDurationMonthsGte(
        DurationMonthsGteRule rule, EligibilityContext context)
    {
        // DurationMonthsGte preserves the repository's existing calendar-month semantics. We do
        // NOT translate "months" into 30 days. The contract's duration is a snapshot value already
        // validated at contract creation.
        return context.ContractDurationMonths >= rule.Months
            ? EligibilityRuleEvaluationResult.Eligible()
            : EligibilityRuleEvaluationResult.Ineligible(
                WhyIneligible.DurationMonthsNotMet,
                $"DurationMonthsGte({rule.Months})");
    }
}