namespace Centerix.Domain.Platform.Billing.Credits;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits.Enums;

public class TenantCredit : AuditableEntity<Guid>
{
    public decimal Amount { get; private set; }
    public decimal RemainingAmount { get; private set; }
    public CreditSourceType SourceType { get; private set; }
    public Guid? SourceId { get; private set; }
    public CreditStatus Status { get; private set; }
    public Guid? ReversalOfCreditId { get; private set; }
    public string CurrencyCode { get; private set; } = "EGP";
    public string? IdempotencyKey { get; private set; }

    /// <summary>
    /// Task 18.5 — economic-origin lineage (immutable after creation).
    /// The portion of <see cref="Amount"/> that is customer-paid value TRANSFERRED from prior
    /// <see cref="CreditSourceType.SubscriptionChange"/> credits (value that already existed as
    /// customer-paid economic value on a predecessor contract). The remainder of a
    /// SubscriptionChange credit's amount is DIRECT customer-paid origin (payments and
    /// Overpayment credits settled on the old contract).
    /// Always 0 for Overpayment (direct, payment-backed: <see cref="SourceId"/> = payment id)
    /// and for non-customer-paid sources (ReferralReward, Promotional, Compensation, Manual).
    /// Bound: 0 &lt;= TransferredPaidAmount &lt;= Amount.
    /// </summary>
    public decimal TransferredPaidAmount { get; private set; }

    /// <summary>
    /// Task 18.5 — the portion of <see cref="Amount"/> that is DIRECT customer-paid origin
    /// (not transferred through a prior SubscriptionChange credit). Computed, not persisted.
    /// </summary>
    public decimal DirectPaidAmount =>
        SourceType == CreditSourceType.SubscriptionChange
            ? Amount - TransferredPaidAmount
            : CustomerPaidEconomicValue;

    /// <summary>
    /// Task 18.5 — the customer-paid economic value represented by this credit. Task 18.4.2
    /// business rule: only <see cref="CreditSourceType.Overpayment"/> (real cash received from
    /// the customer) and <see cref="CreditSourceType.SubscriptionChange"/> (value converted from
    /// a previously paid contract) carry customer economic value. Granted/free/discretionary
    /// sources always represent 0 customer-paid economic value. Computed, not persisted.
    /// </summary>
    public decimal CustomerPaidEconomicValue =>
        SourceType == CreditSourceType.Overpayment || SourceType == CreditSourceType.SubscriptionChange
            ? Amount
            : 0m;

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    private TenantCredit() { }

    private TenantCredit(
        Guid id,
        decimal amount,
        CreditSourceType sourceType,
        Guid? sourceId,
        CreditStatus status,
        string currencyCode,
        string? idempotencyKey,
        decimal transferredPaidAmount = 0m)
        : base(id)
    {
        Amount = amount;
        RemainingAmount = amount;
        SourceType = sourceType;
        SourceId = sourceId;
        Status = status;
        CurrencyCode = currencyCode;
        IdempotencyKey = idempotencyKey;
        TransferredPaidAmount = transferredPaidAmount;
    }

    public static Result<TenantCredit> Create(
        Guid id,
        decimal amount,
        CreditSourceType sourceType,
        Guid? sourceId = null,
        string currencyCode = "EGP",
        string? idempotencyKey = null)
    {
        if (amount <= 0)
            return TenantCreditErrors.InvalidAmount;

        if (!Enum.IsDefined(sourceType))
            return TenantCreditErrors.InvalidSourceType;

        if (string.IsNullOrWhiteSpace(currencyCode))
            return Error.Validation("TenantCredit.CurrencyRequired", "Currency code is required.");

        // Generic creation path: no explicit lineage composition is known, so the credit is
        // classified without transferred origin (direct customer-paid for eligible sources,
        // zero customer-paid value for granted sources). The plan-change handler always uses
        // CreateSubscriptionChange to persist the exact multi-generation composition.
        return new TenantCredit(id, amount, sourceType, sourceId, CreditStatus.Available, currencyCode.ToUpperInvariant(), idempotencyKey);
    }

    /// <summary>
    /// Task 18.5 — creates a SubscriptionChange credit carrying its immutable economic-origin
    /// lineage. <paramref name="transferredPaidAmount"/> is the portion of <paramref name="amount"/>
    /// that is customer-paid value transferred from prior SubscriptionChange credits which
    /// settled the old contract; the remainder is direct customer-paid origin. This guarantees
    /// generation N+1 can never attribute more transferred value than the prior generation
    /// credits actually contributed (no multiplication across generations).
    /// </summary>
    public static Result<TenantCredit> CreateSubscriptionChange(
        Guid id,
        decimal amount,
        Guid sourceId,
        decimal transferredPaidAmount,
        string currencyCode = "EGP",
        string? idempotencyKey = null)
    {
        if (amount <= 0)
            return TenantCreditErrors.InvalidAmount;

        if (string.IsNullOrWhiteSpace(currencyCode))
            return Error.Validation("TenantCredit.CurrencyRequired", "Currency code is required.");

        if (transferredPaidAmount < 0 || transferredPaidAmount > amount)
            return TenantCreditErrors.InvalidTransferredPaidAmount;

        return new TenantCredit(
            id,
            amount,
            CreditSourceType.SubscriptionChange,
            sourceId,
            CreditStatus.Available,
            currencyCode.ToUpperInvariant(),
            idempotencyKey,
            transferredPaidAmount);
    }

    public Result<Updated> Apply(Guid invoiceLineId)
    {
        if (Status != CreditStatus.Available)
            return TenantCreditErrors.NotAvailable;

        Status = CreditStatus.Applied;
        RemainingAmount = 0;

        return Result.Updated;
    }

    public Result<Updated> ApplyToInvoice(Guid invoiceId)
    {
        if (Status != CreditStatus.Available)
            return TenantCreditErrors.NotAvailable;

        Status = CreditStatus.Applied;
        RemainingAmount = 0;

        return Result.Updated;
    }

    /// <summary>
    /// Consumes a portion of the credit. If the entire remaining amount is consumed,
    /// transitions to Applied. If partial, transitions to PartiallyApplied.
    /// </summary>
    public Result<Updated> ConsumeAmount(decimal amount)
    {
        if (Status != CreditStatus.Available && Status != CreditStatus.PartiallyApplied)
            return TenantCreditErrors.NotAvailable;

        if (amount <= 0)
            return TenantCreditErrors.InvalidAmount;

        if (amount > RemainingAmount)
            return TenantCreditErrors.InsufficientRemaining;

        RemainingAmount -= amount;

        Status = RemainingAmount == 0
            ? CreditStatus.Applied
            : CreditStatus.PartiallyApplied;

        return Result.Updated;
    }

    public Result<Updated> Expire()
    {
        if (Status != CreditStatus.Available)
            return TenantCreditErrors.NotAvailable;

        Status = CreditStatus.Expired;

        return Result.Updated;
    }

    public Result<Updated> Revoke()
    {
        if (Status != CreditStatus.Available)
            return TenantCreditErrors.NotAvailable;

        Status = CreditStatus.Revoked;

        return Result.Updated;
    }

    public Result<Updated> Reverse(Guid reversalCreditId)
    {
        if (Status != CreditStatus.Available)
            return TenantCreditErrors.NotAvailable;

        Status = CreditStatus.Reversed;
        ReversalOfCreditId = reversalCreditId;

        return Result.Updated;
    }
}
