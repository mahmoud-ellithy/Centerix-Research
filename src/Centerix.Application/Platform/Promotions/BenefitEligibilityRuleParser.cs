namespace Centerix.Application.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.EligibilityRules;

/// <summary>
/// Maps a trusted request payload into the domain <see cref="EligibilityRule"/> algebra.
/// <para>
/// The accepted form is the CANONICAL JSON produced by <see cref="EligibilityRuleSerializer"/> —
/// the same representation already used to persist every eligibility rule in this database. It is
/// parsed through the closed domain algebra, which validates each leaf (AllOf arity, defined
/// enums, non-negative amounts).
/// </para>
/// <para>
/// <b>Canonicality is enforced, not assumed.</b> <see cref="EligibilityRuleSerializer.Deserialize"/>
/// accepts any semantically equivalent payload and silently ignores properties it does not know
/// (for example <c>{"type":"contract_active","extra":"ignored"}</c>). Accepting that would mean
/// normalizing arbitrary rule JSON into canonical form behind the caller's back. This parser
/// therefore requires the input to be byte-identical to the canonical serialization of the rule it
/// deserializes to, so a non-canonical payload is rejected instead of being quietly rewritten.
/// </para>
/// </summary>
internal static class BenefitEligibilityRuleParser
{
    private const string InvalidRuleCode = "Promotion.BenefitEligibilityRule_Invalid";

    /// <summary>
    /// Parses a CANONICAL rule JSON. A null result means no rule was supplied; whether that is
    /// acceptable is decided by the domain aggregate (a benefit-bearing promotion requires one).
    /// <para>
    /// Malformed JSON, an unknown discriminator, and a semantically valid but NON-CANONICAL
    /// payload are all rejected with <c>Promotion.BenefitEligibilityRule_Invalid</c> — one code for
    /// this boundary concern, with the message naming the specific cause.
    /// </para>
    /// </summary>
    public static Result<EligibilityRule?> Parse(string? canonicalRuleJson)
    {
        if (string.IsNullOrWhiteSpace(canonicalRuleJson))
        {
            EligibilityRule? noRule = null;
            return noRule;
        }

        EligibilityRule rule;
        string canonical;
        try
        {
            rule = EligibilityRuleSerializer.Deserialize(canonicalRuleJson);
            canonical = EligibilityRuleSerializer.Serialize(rule);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Error.Validation(InvalidRuleCode,
                "The benefit eligibility rule is not a valid canonical eligibility rule: " + ex.Message);
        }

        // Strict canonicality check. The serializer's own contract defines the canonical form
        // (compact, discriminator first, fixed property order, no unknown or alternative
        // properties), so the only permitted representation is that exact string. No semantic
        // normalization is performed here.
        if (!string.Equals(canonicalRuleJson, canonical, StringComparison.Ordinal))
        {
            return Error.Validation(InvalidRuleCode,
                "The benefit eligibility rule is not in canonical form. It must be byte-identical to " +
                $"EligibilityRuleSerializer.Serialize(rule). Expected canonical JSON: {canonical}");
        }

        return rule;
    }

    /// <summary>Canonical JSON for a stored rule, or null when none is configured.</summary>
    public static string? ToCanonical(EligibilityRule? rule) =>
        rule is null ? null : EligibilityRuleSerializer.Serialize(rule);
}
