namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

using Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// A rule that requires the contract's <see cref="PaymentTerms"/> to equal a specific value.
/// Use <see cref="EligibilityRule.PaymentTermsEquals(PaymentTerms)"/>.
/// </summary>
public sealed class PaymentTermsEqualsRule : EligibilityRule
{
    /// <summary>The required payment terms value.</summary>
    public PaymentTerms PaymentTerms { get; }

    /// <summary>
    /// Initialises the rule.
    /// </summary>
    /// <param name="paymentTerms">Must be a defined <see cref="PaymentTerms"/> enum member.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="paymentTerms"/> is not a defined enum value.</exception>
    internal PaymentTermsEqualsRule(PaymentTerms paymentTerms)
    {
        if (!Enum.IsDefined(typeof(PaymentTerms), paymentTerms))
            throw new ArgumentException(
                $"The value {(int)paymentTerms} is not a defined PaymentTerms enum member.",
                nameof(paymentTerms));

        PaymentTerms = paymentTerms;
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
        => other is PaymentTermsEqualsRule r && r.PaymentTerms == PaymentTerms;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(typeof(PaymentTermsEqualsRule), PaymentTerms);

    /// <inheritdoc />
    public override string ToString() => $"PaymentTermsEq({PaymentTerms})";
}

/// <summary>
/// A rule that requires the payment method to equal a specific string.
/// Use <see cref="EligibilityRule.PaymentMethodEquals(string)"/>.
/// </summary>
/// <remarks>
/// The payment method string is canonicalised at construction time:
/// <c>Trim().ToUpperInvariant()</c>. Therefore <c>"Cash"</c>, <c>" cash "</c>, and
/// <c>"CASH"</c> all collapse to the same stored value, producing identical hash codes
/// and byte-identical serialised JSON. The canonical form follows the existing
/// Centerix convention used by <see cref="Centerix.Domain.Platform.Promotions.OfferFeature"/>.
/// </remarks>
public sealed class PaymentMethodEqualsRule : EligibilityRule
{
    /// <summary>The canonical payment method (trimmed, upper-invariant).</summary>
    public string PaymentMethod { get; }

    /// <summary>
    /// Initialises the rule.
    /// </summary>
    /// <param name="paymentMethod">Non-null, non-empty string. Canonicalised via <c>Trim().ToUpperInvariant()</c>.</param>
    /// <exception cref="ArgumentException">Thrown when the value is null, empty, or whitespace-only.</exception>
    internal PaymentMethodEqualsRule(string paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod))
            throw new ArgumentException("Payment method must be a non-empty string.", nameof(paymentMethod));

        PaymentMethod = paymentMethod.Trim().ToUpperInvariant();
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
        => other is PaymentMethodEqualsRule r &&
           string.Equals(r.PaymentMethod, PaymentMethod, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(typeof(PaymentMethodEqualsRule), PaymentMethod);

    /// <inheritdoc />
    public override string ToString() => $"PaymentMethodEq(\"{PaymentMethod}\")";
}

/// <summary>
/// A rule that requires the benefit to be completed by a specific UTC instant.
/// Use <see cref="EligibilityRule.CompletedByUtc(DateTime)"/>.
/// </summary>
public sealed class CompletedByUtcRule : EligibilityRule
{
    /// <summary>The deadline UTC instant.</summary>
    public DateTime CompletedBy { get; }

    /// <summary>
    /// Initialises the rule.
    /// </summary>
    /// <param name="completedByUtc">Must be a UTC DateTime (<see cref="DateTimeKind.Utc"/>).</param>
    /// <exception cref="ArgumentException">Thrown when the DateTime is not UTC.</exception>
    internal CompletedByUtcRule(DateTime completedByUtc)
    {
        if (completedByUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "CompletedByUtc must be a UTC DateTime (Kind == DateTimeKind.Utc). " +
                "Do not silently reinterpret local or unspecified time.",
                nameof(completedByUtc));

        CompletedBy = completedByUtc;
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
        => other is CompletedByUtcRule r && r.CompletedBy == CompletedBy;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(typeof(CompletedByUtcRule), CompletedBy);

    /// <inheritdoc />
    public override string ToString() => $"CompletedByUtc({CompletedBy:O})";
}

/// <summary>
/// A rule that requires the total amount paid to be at least <see cref="Amount"/>.
/// Use <see cref="EligibilityRule.AmountPaidAtLeast(decimal)"/>.
/// </summary>
public sealed class AmountPaidAtLeastRule : EligibilityRule
{
    /// <summary>The minimum required paid amount (&gt;= 0).</summary>
    public decimal Amount { get; }

    /// <summary>
    /// Initialises the rule.
    /// </summary>
    /// <param name="amount">Must be &gt;= 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="amount"/> is negative.</exception>
    internal AmountPaidAtLeastRule(decimal amount)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "AmountPaidAtLeast requires a non-negative amount.");

        Amount = amount;
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
        => other is AmountPaidAtLeastRule r && r.Amount == Amount;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(typeof(AmountPaidAtLeastRule), Amount);

    /// <inheritdoc />
    public override string ToString() => $"AmountPaidAtLeast({Amount})";
}

/// <summary>
/// A rule that requires at least <see cref="Days"/> days to have elapsed since the contract start date.
/// Use <see cref="EligibilityRule.DaysFromContractStartGte(int)"/>.
/// </summary>
public sealed class DaysFromContractStartGteRule : EligibilityRule
{
    /// <summary>The minimum number of days (&gt;= 0).</summary>
    public int Days { get; }

    /// <summary>
    /// Initialises the rule.
    /// </summary>
    /// <param name="days">Must be &gt;= 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="days"/> is negative.</exception>
    internal DaysFromContractStartGteRule(int days)
    {
        if (days < 0)
            throw new ArgumentOutOfRangeException(
                nameof(days),
                days,
                "DaysFromContractStartGte requires a non-negative day count.");

        Days = days;
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
        => other is DaysFromContractStartGteRule r && r.Days == Days;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(typeof(DaysFromContractStartGteRule), Days);

    /// <inheritdoc />
    public override string ToString() => $"DaysFromContractStartGte({Days})";
}

/// <summary>
/// A rule that requires the contract duration to be at least <see cref="Months"/> months.
/// Use <see cref="EligibilityRule.DurationMonthsGte(int)"/>.
/// </summary>
public sealed class DurationMonthsGteRule : EligibilityRule
{
    /// <summary>The minimum duration in months (&gt;= 0).</summary>
    public int Months { get; }

    /// <summary>
    /// Initialises the rule.
    /// </summary>
    /// <param name="months">Must be &gt;= 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="months"/> is negative.</exception>
    internal DurationMonthsGteRule(int months)
    {
        if (months < 0)
            throw new ArgumentOutOfRangeException(
                nameof(months),
                months,
                "DurationMonthsGte requires a non-negative month count.");

        Months = months;
    }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other)
        => other is DurationMonthsGteRule r && r.Months == Months;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(typeof(DurationMonthsGteRule), Months);

    /// <inheritdoc />
    public override string ToString() => $"DurationMonthsGte({Months})";
}
