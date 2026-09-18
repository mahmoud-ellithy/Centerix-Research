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

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    private TenantCredit() { }

    private TenantCredit(
        Guid id,
        decimal amount,
        CreditSourceType sourceType,
        Guid? sourceId,
        CreditStatus status,
        string currencyCode)
        : base(id)
    {
        Amount = amount;
        RemainingAmount = amount;
        SourceType = sourceType;
        SourceId = sourceId;
        Status = status;
        CurrencyCode = currencyCode;
    }

    public static Result<TenantCredit> Create(
        Guid id,
        decimal amount,
        CreditSourceType sourceType,
        Guid? sourceId = null,
        string currencyCode = "EGP")
    {
        if (amount <= 0)
            return TenantCreditErrors.InvalidAmount;

        if (!Enum.IsDefined(sourceType))
            return TenantCreditErrors.InvalidSourceType;

        if (string.IsNullOrWhiteSpace(currencyCode))
            return Error.Validation("TenantCredit.CurrencyRequired", "Currency code is required.");

        return new TenantCredit(id, amount, sourceType, sourceId, CreditStatus.Available, currencyCode.ToUpperInvariant());
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
