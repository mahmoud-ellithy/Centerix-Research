namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// A composite rule that requires ALL child rules to hold simultaneously.
/// Use <see cref="EligibilityRule.AllOf(EligibilityRule[])"/> or <see cref="EligibilityRule.AllOf(IEnumerable{EligibilityRule})"/>.
/// </summary>
/// <remarks>
/// Child rules are stored in the order provided. No deduplication or sorting is performed
/// because the rule is a commercial fact whose order may be significant.
/// The collection is immutable from the caller's perspective.
/// </remarks>
public sealed class AllOfRule : EligibilityRule
{
    private readonly List<EligibilityRule> _rules;

    /// <summary>
    /// Immutable view of the child rules (defensive copy was taken at construction).
    /// </summary>
    public IReadOnlyList<EligibilityRule> Rules => _rules.AsReadOnly();

    /// <summary>
    /// Initialises the composite rule with a defensive copy of the supplied rules.
    /// </summary>
    /// <param name="rules">At least one non-null <see cref="EligibilityRule"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="rules"/> is empty or contains null entries.
    /// </exception>
    internal AllOfRule(IEnumerable<EligibilityRule> rules)
    {
        if (rules is null)
            throw new ArgumentNullException(nameof(rules), "AllOf rule set cannot be null.");

        // Defensive copy
        var list = rules.ToList();

        if (list.Count == 0)
            throw new ArgumentException("AllOf requires at least one child rule.", nameof(rules));

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
                throw new ArgumentException($"AllOf child rule at index {i} is null.", nameof(rules));
        }

        _rules = list;
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
    {
        if (other is not AllOfRule r) return false;
        if (r._rules.Count != _rules.Count) return false;
        return _rules.Zip(r._rules, (a, b) => a.Equals(b)).All(eq => eq);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(typeof(AllOfRule));
        foreach (var rule in _rules)
            hash.Add(rule.GetHashCode());
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => $"AllOf({string.Join(", ", _rules)})";
}

/// <summary>
/// A composite rule that requires ANY ONE of the child rules to hold.
/// Use <see cref="EligibilityRule.AnyOf(EligibilityRule[])"/> or <see cref="EligibilityRule.AnyOf(IEnumerable{EligibilityRule})"/>.
/// </summary>
/// <remarks>
/// Child rules are stored in the order provided. No deduplication or sorting is performed.
/// The collection is immutable from the caller's perspective.
/// </remarks>
public sealed class AnyOfRule : EligibilityRule
{
    private readonly List<EligibilityRule> _rules;

    /// <summary>
    /// Immutable view of the child rules (defensive copy was taken at construction).
    /// </summary>
    public IReadOnlyList<EligibilityRule> Rules => _rules.AsReadOnly();

    /// <summary>
    /// Initialises the composite rule with a defensive copy of the supplied rules.
    /// </summary>
    /// <param name="rules">At least one non-null <see cref="EligibilityRule"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="rules"/> is empty or contains null entries.
    /// </exception>
    internal AnyOfRule(IEnumerable<EligibilityRule> rules)
    {
        if (rules is null)
            throw new ArgumentNullException(nameof(rules), "AnyOf rule set cannot be null.");

        // Defensive copy
        var list = rules.ToList();

        if (list.Count == 0)
            throw new ArgumentException("AnyOf requires at least one child rule.", nameof(rules));

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
                throw new ArgumentException($"AnyOf child rule at index {i} is null.", nameof(rules));
        }

        _rules = list;
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
    {
        if (other is not AnyOfRule r) return false;
        if (r._rules.Count != _rules.Count) return false;
        return _rules.Zip(r._rules, (a, b) => a.Equals(b)).All(eq => eq);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(typeof(AnyOfRule));
        foreach (var rule in _rules)
            hash.Add(rule.GetHashCode());
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => $"AnyOf({string.Join(", ", _rules)})";
}
