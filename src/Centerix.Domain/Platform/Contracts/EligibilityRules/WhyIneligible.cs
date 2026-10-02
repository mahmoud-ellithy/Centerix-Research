namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

/// <summary>
/// Reason code emitted by <see cref="EligibilityRuleEvaluator"/> when evaluation rejects a benefit.
/// Reason codes are stable string constants so they can be persisted into the
/// <c>FreezeReason</c> snapshot column without coupling the column to enum ordinals.
/// </summary>
public enum WhyIneligible
{
    /// <summary>Default / sentinel; never returned by a successful evaluation.</summary>
    None = 0,

    /// <summary>The contract is not in <c>Active</c> status.</summary>
    ContractNotActive = 1,

    /// <summary>The contract's <c>PaymentTerms</c> does not match the rule's required value.</summary>
    PaymentTermsMismatch = 2,

    /// <summary>The contract's payment method does not match the rule's required value.</summary>
    PaymentMethodMismatch = 3,

    /// <summary>The rule's <c>CompletedByUtc</c> deadline has already passed.</summary>
    DeadlinePassed = 4,

    /// <summary>The contract has at least one overdue installment.</summary>
    OverdueInstallment = 5,

    /// <summary>The total amount paid is below the rule's required minimum.</summary>
    AmountBelowMinimum = 6,

    /// <summary>The number of days elapsed since contract start is below the rule's minimum.</summary>
    DaysFromContractStartNotMet = 7,

    /// <summary>The contract duration is below the rule's minimum.</summary>
    DurationMonthsNotMet = 8,

    /// <summary>An <c>AnyOf</c> composite had no child rule pass.</summary>
    AnyOfNoChildPassed = 9,

    /// <summary>Internal evaluator failure (defensive fallback).</summary>
    EvaluatorError = 99
}