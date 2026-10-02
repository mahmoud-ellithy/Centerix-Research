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

    /// <summary>
    /// Most recent payment method observed on a completed payment allocation for this contract,
    /// or <c>null</c> when no completed payment allocation exists yet. Canonicalised at the
    /// builder to <c>Trim().ToUpperInvariant()</c>.
    /// </summary>
    public string? PaymentMethod { get; }

    /// <summary>Current UTC instant (used by <c>CompletedByUtc</c> and <c>DaysFromContractStart</c> rules).</summary>
    public DateTime UtcNow { get; }

    /// <summary>Total amount paid (sum of completed-payment amounts) on the current contract.</summary>
    public decimal AmountPaid { get; }

    /// <summary>The contract's contracted amount (snapshot). Used by <c>AmountPaidAtLeast</c>.</summary>
    public decimal ContractedAmount { get; }

    /// <summary>Whole days elapsed since the contract's effective date at <see cref="UtcNow"/>.</summary>
    public int DaysFromContractStart { get; }

    /// <summary>The contract's duration in months (snapshot). Used by <c>DurationMonthsGte</c>.</summary>
    public int ContractDurationMonths { get; }

    /// <summary>True iff the contract has at least one overdue installment at <see cref="UtcNow"/>.</summary>
    public bool HasOverdueInstallment { get; }

    /// <summary>
    /// Optional deadline for the <c>CompletedByUtc</c> rule. When <c>null</c>, the deadline is not
    /// observed (rule has not been set up for this benefit).
    /// </summary>
    public DateTime? CompletedByUtc { get; }

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
        string? paymentMethod,
        DateTime utcNow,
        decimal amountPaid,
        decimal contractedAmount,
        int daysFromContractStart,
        int contractDurationMonths,
        bool hasOverdueInstallment,
        DateTime? completedByUtc,
        IReadOnlyDictionary<string, object> properties)
    {
        TenantId = tenantId;
        ContractId = contractId;
        BenefitId = benefitId;
        ContractStatus = contractStatus;
        PaymentTerms = paymentTerms;
        PaymentMethod = paymentMethod;
        UtcNow = utcNow;
        AmountPaid = amountPaid;
        ContractedAmount = contractedAmount;
        DaysFromContractStart = daysFromContractStart;
        ContractDurationMonths = contractDurationMonths;
        HasOverdueInstallment = hasOverdueInstallment;
        CompletedByUtc = completedByUtc;
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
        string? paymentMethod,
        DateTime utcNow,
        decimal amountPaid,
        decimal contractedAmount,
        int daysFromContractStart,
        int contractDurationMonths,
        bool hasOverdueInstallment,
        DateTime? completedByUtc = null,
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

        if (amountPaid < 0)
            throw new ArgumentOutOfRangeException(nameof(amountPaid), amountPaid,
                "AmountPaid cannot be negative.");

        if (contractedAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(contractedAmount), contractedAmount,
                "ContractedAmount cannot be negative.");

        if (daysFromContractStart < 0)
            throw new ArgumentOutOfRangeException(nameof(daysFromContractStart), daysFromContractStart,
                "DaysFromContractStart cannot be negative.");

        if (contractDurationMonths < 0)
            throw new ArgumentOutOfRangeException(nameof(contractDurationMonths), contractDurationMonths,
                "ContractDurationMonths cannot be negative.");

        if (completedByUtc is { } deadline && deadline.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "CompletedByUtc must be a UTC DateTime (Kind == DateTimeKind.Utc).",
                nameof(completedByUtc));

        var propertyBag = properties is null
            ? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>()
            : new Dictionary<string, object>(properties);

        return new EligibilityContext(
            tenantId,
            contractId,
            benefitId,
            contractStatus,
            paymentTerms,
            paymentMethod,
            utcNow,
            amountPaid,
            contractedAmount,
            daysFromContractStart,
            contractDurationMonths,
            hasOverdueInstallment,
            completedByUtc,
            new ReadOnlyPropertyBag(propertyBag));
    }
}

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