namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

using System.Collections.Generic;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Immutable aggregate of facts required to evaluate a <see cref="EligibilityRule"/> against a
/// <c>ContractBenefit</c>. The context is a pure data carrier — it MUST NOT perform I/O and
/// MUST NOT mutate after construction.
/// </summary>
/// <remarks>
/// <para>
/// The context is built by <c>EligibilityContextBuilder</c>, which in turn reads facts via
/// <c>IOwnerOnlyFactQuery</c>. The evaluator (<c>EligibilityRuleEvaluator</c>) receives an
/// already-built context and is itself pure. This three-layer split keeps the rule algebra
/// pure, the I/O seam single, and the cross-tenant guard explicit.
/// </para>
/// <para>
/// All date-time fields are <see cref="DateTimeKind.Utc"/>. The construction guard
/// rejects any other kind with <see cref="ArgumentException"/>.
/// </para>
/// </remarks>
public sealed class EligibilityContext
{
    /// <summary>The authorized tenant ID. Every fact in this context belongs to this tenant.</summary>
    public string TenantId { get; }

    /// <summary>The contract ID the benefit is attached to.</summary>
    public Guid ContractId { get; }

    /// <summary>The benefit ID being evaluated.</summary>
    public Guid BenefitId { get; }

    /// <summary>Current contract status (snapshot at evaluation time).</summary>
    public ContractStatus ContractStatus { get; }

    /// <summary>Contract payment terms (snapshot at evaluation time).</summary>
    public PaymentTerms PaymentTerms { get; }

    /// <summary>Current UTC instant (used by <c>CompletedByUtc</c> and <c>DaysFromContractStartGte</c> rules).</summary>
    public DateTime UtcNow { get; }

    /// <summary>
    /// Authoritative contract-start (effective) instant. Used by <c>DaysFromContractStartGte</c>:
    /// elapsed duration is computed as <c>UtcNow - ContractStartUtc</c>, never by date-range day-counting.
    /// </summary>
    public DateTime ContractStartUtc { get; }

    /// <summary>
    /// Authoritative contract currency. Used by <c>AmountPaidAtLeast</c> to scope the sum to payments
    /// whose currency matches the contract. Payments in other currencies MUST NOT contribute.
    /// </summary>
    public string ContractCurrencyCode { get; }

    /// <summary>The contract's contracted amount (snapshot).</summary>
    public decimal ContractedAmount { get; }

    /// <summary>The contract's duration in months (snapshot). Used by <c>DurationMonthsGte</c>.</summary>
    public int ContractDurationMonths { get; }

    /// <summary>True iff the contract has at least one overdue installment at <see cref="UtcNow"/>.</summary>
    public bool HasOverdueInstallment { get; }

    /// <summary>
    /// Authoritative completion facts. One entry per <c>Payment</c> row with
    /// <c>Status = Completed</c> whose allocations reference an invoice belonging to
    /// <see cref="ContractId"/>. Empty when no qualifying payments exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules that need "any payment matching X" iterate this list. Rules that need a single
    /// aggregate (e.g. <c>AmountPaidAtLeast</c>) reduce over it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CompletedPaymentFact> CompletedPayments { get; }

    /// <summary>
    /// Extension property bag reserved for future rule types. The bag is fully immutable: any
    /// attempt to mutate it after construction throws <see cref="NotSupportedException"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object> Properties { get; }

    private EligibilityContext(
        string tenantId,
        Guid contractId,
        Guid benefitId,
        ContractStatus contractStatus,
        PaymentTerms paymentTerms,
        DateTime utcNow,
        DateTime contractStartUtc,
        string contractCurrencyCode,
        decimal contractedAmount,
        int contractDurationMonths,
        bool hasOverdueInstallment,
        IReadOnlyList<CompletedPaymentFact> completedPayments,
        IReadOnlyDictionary<string, object> properties)
    {
        TenantId = tenantId;
        ContractId = contractId;
        BenefitId = benefitId;
        ContractStatus = contractStatus;
        PaymentTerms = paymentTerms;
        UtcNow = utcNow;
        ContractStartUtc = contractStartUtc;
        ContractCurrencyCode = contractCurrencyCode;
        ContractedAmount = contractedAmount;
        ContractDurationMonths = contractDurationMonths;
        HasOverdueInstallment = hasOverdueInstallment;
        CompletedPayments = completedPayments;
        Properties = properties;
    }

    /// <summary>
    /// Builds a validated <see cref="EligibilityContext"/>. Throws <see cref="ArgumentException"/>
    /// when invariants are violated.
    /// </summary>
    public static EligibilityContext Create(
        string tenantId,
        Guid contractId,
        Guid benefitId,
        ContractStatus contractStatus,
        PaymentTerms paymentTerms,
        DateTime utcNow,
        DateTime contractStartUtc,
        string contractCurrencyCode,
        decimal contractedAmount,
        int contractDurationMonths,
        bool hasOverdueInstallment,
        IReadOnlyList<CompletedPaymentFact>? completedPayments = null,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("TenantId is required.", nameof(tenantId));

        if (contractId == Guid.Empty)
            throw new ArgumentException("ContractId is required.", nameof(contractId));

        if (benefitId == Guid.Empty)
            throw new ArgumentException("BenefitId is required.", nameof(benefitId));

        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "UtcNow must be a UTC DateTime (Kind == DateTimeKind.Utc). " +
                "Do not silently reinterpret local or unspecified time.",
                nameof(utcNow));

        if (contractStartUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "ContractStartUtc must be a UTC DateTime (Kind == DateTimeKind.Utc).",
                nameof(contractStartUtc));

        if (string.IsNullOrWhiteSpace(contractCurrencyCode) || contractCurrencyCode.Trim().Length != 3)
            throw new ArgumentException(
                "ContractCurrencyCode must be a 3-letter ISO currency code.",
                nameof(contractCurrencyCode));

        if (contractedAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(contractedAmount), contractedAmount,
                "ContractedAmount cannot be negative.");

        if (contractDurationMonths < 0)
            throw new ArgumentOutOfRangeException(nameof(contractDurationMonths), contractDurationMonths,
                "ContractDurationMonths cannot be negative.");

        var facts = completedPayments is null
            ? (IReadOnlyList<CompletedPaymentFact>)Array.Empty<CompletedPaymentFact>()
            : completedPayments.ToArray();

        var propertyBag = properties is null
            ? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>()
            : new Dictionary<string, object>(properties);

        return new EligibilityContext(
            tenantId.Trim(),
            contractId,
            benefitId,
            contractStatus,
            paymentTerms,
            utcNow,
            contractStartUtc,
            contractCurrencyCode.Trim().ToUpperInvariant(),
            contractedAmount,
            contractDurationMonths,
            hasOverdueInstallment,
            facts,
            new ReadOnlyPropertyBag(propertyBag));
    }
}

/// <summary>
/// Authoritative fact describing a single completed payment row that counts toward settlement.
/// One fact per <c>Payment</c> row with <c>PaymentStatus.Completed</c> whose allocation chain
/// links back to a contract invoice.
/// </summary>
/// <param name="PaymentId">The payment row id (audit / debugging).</param>
/// <param name="CompletedAtUtc">
/// The authoritative <c>Payment.CompletedAtUtc</c>. NEVER derived from contract creation,
/// invoice, or current time — see the "important" clause of the correction spec.
/// </param>
/// <param name="Amount">The payment amount.</param>
/// <param name="CurrencyCode">
/// The payment's ISO currency code, canonicalised to <c>Trim().ToUpperInvariant()</c>.
/// </param>
/// <param name="MethodCanonical">
/// The payment method canonicalised to <c>Trim().ToUpperInvariant()</c>. May be <c>null</c>
/// when no method is recorded.
/// </param>
public sealed record CompletedPaymentFact(
    Guid PaymentId,
    DateTime CompletedAtUtc,
    decimal Amount,
    string CurrencyCode,
    string? MethodCanonical);

/// <summary>
/// Read-only wrapper that throws when a caller attempts to mutate the underlying dictionary.
/// Preserves the immutability contract of <see cref="EligibilityContext.Properties"/>.
/// </summary>
internal sealed class ReadOnlyPropertyBag : IReadOnlyDictionary<string, object>
{
    private readonly IReadOnlyDictionary<string, object> _inner;

    public ReadOnlyPropertyBag(IReadOnlyDictionary<string, object> inner) => _inner = inner;

    public object this[string key] => _inner[key];

    public IEnumerable<string> Keys => _inner.Keys;

    public IEnumerable<object> Values => _inner.Values;

    public int Count => _inner.Count;

    public bool ContainsKey(string key) => _inner.ContainsKey(key);

    public bool TryGetValue(string key, out object value) => _inner.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _inner.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}