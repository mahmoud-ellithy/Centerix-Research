namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

/// <summary>
/// Immutable outcome of evaluating a closed rule tree against an
/// <see cref="EligibilityContext"/>. Produced exclusively by
/// <see cref="EligibilityRuleEvaluator"/>; consumed by callers that need to surface
/// reason codes (e.g. <c>FreezeEligibilityService</c>).
/// </summary>
/// <remarks>
/// When <see cref="IsEligible"/> is <c>true</c>, <see cref="Reason"/> is <see cref="WhyIneligible.None"/>.
/// When <see cref="IsEligible"/> is <c>false</c>, <see cref="Reason"/> identifies the leaf rule that
/// caused rejection.
/// </remarks>
public sealed class EligibilityRuleEvaluationResult
{
    /// <summary>Whether the rule tree passed against the supplied context.</summary>
    public bool IsEligible { get; }

    /// <summary>The reason code explaining the outcome.</summary>
    public WhyIneligible Reason { get; }

    /// <summary>
    /// A short, stable string identifying the rule branch that decided the outcome
    /// (e.g. <c>"AllOf[0].AmountPaidAtLeast"</c>). <c>null</c> when <see cref="IsEligible"/> is <c>true</c>.
    /// </summary>
    public string? ReasonPath { get; }

    private EligibilityRuleEvaluationResult(bool isEligible, WhyIneligible reason, string? reasonPath)
    {
        IsEligible = isEligible;
        Reason = reason;
        ReasonPath = reasonPath;
    }

    /// <summary>Constructs an "eligible" result.</summary>
    public static EligibilityRuleEvaluationResult Eligible()
        => new(true, WhyIneligible.None, null);

    /// <summary>Constructs an "ineligible" result with the leaf reason.</summary>
    /// <param name="reason">Must not be <see cref="WhyIneligible.None"/>.</param>
    /// <param name="reasonPath">
    /// Stable identifier of the rule branch that decided the outcome. May be <c>null</c> only when
    /// <paramref name="reason"/> is the generic <see cref="WhyIneligible.EvaluatorError"/>.
    /// </param>
    public static EligibilityRuleEvaluationResult Ineligible(WhyIneligible reason, string? reasonPath)
    {
        if (reason == WhyIneligible.None)
            throw new ArgumentException(
                "Ineligible result cannot be constructed with WhyIneligible.None. " +
                "Use the parameterless Eligible() factory instead.",
                nameof(reason));

        return new EligibilityRuleEvaluationResult(false, reason, reasonPath);
    }
}