namespace Centerix.Application.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.EligibilityRules;

/// <summary>
/// Maps a trusted request payload into the domain <see cref="EligibilityRule"/> algebra.
/// <para>
/// The accepted form is the CANONICAL JSON produced by <see cref="EligibilityRuleSerializer"/> —
/// the same representation already used to persist every eligibility rule in this database. It is
/// parsed through the closed domain algebra, which validates each leaf (AllOf arity, defined
/// enums, non-negative amounts). Nothing arbitrary is stored: a payload that is not a canonical
/// rule is rejected with a validation error instead of being partially accepted.
/// </para>
/// </summary>
internal static class BenefitEligibilityRuleParser
{
    /// <summary>
    /// Parses the canonical rule JSON. A null result means no rule was supplied; whether that is
    /// acceptable is decided by the domain aggregate (a benefit-bearing promotion requires one).
    /// </summary>
    public static Result<EligibilityRule?> Parse(string? canonicalRuleJson)
    {
        if (string.IsNullOrWhiteSpace(canonicalRuleJson))
        {
            EligibilityRule? noRule = null;
            return noRule;
        }

        EligibilityRule? rule;
        try
        {
            rule = EligibilityRuleSerializer.Deserialize(canonicalRuleJson);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Error.Validation("Promotion.BenefitEligibilityRule_Invalid",
                "The benefit eligibility rule is not a canonical eligibility rule: " + ex.Message);
        }

        if (rule is null)
            return Error.Validation("Promotion.BenefitEligibilityRule_Invalid",
                "The benefit eligibility rule could not be deserialized");

        return rule;
    }

    /// <summary>Canonical JSON for a stored rule, or null when none is configured.</summary>
    public static string? ToCanonical(EligibilityRule? rule) =>
        rule is null ? null : EligibilityRuleSerializer.Serialize(rule);
}
