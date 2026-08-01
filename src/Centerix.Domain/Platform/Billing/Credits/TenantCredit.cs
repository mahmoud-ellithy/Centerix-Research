namespace Centerix.Domain.Platform.Billing.Credits;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits.Enums;

public class TenantCredit : AuditableEntity<Guid>
{
    public decimal Amount { get; private set; }
    public CreditSourceType SourceType { get; private set; }
    public Guid? SourceId { get; private set; }
    public CreditStatus Status { get; private set; }
    public Guid? AppliedToInvoiceLineId { get; private set; }

    private TenantCredit() { }

    private TenantCredit(
        Guid id,
        decimal amount,
        CreditSourceType sourceType,
        Guid? sourceId,
        CreditStatus status)
        : base(id)
    {
        Amount = amount;
        SourceType = sourceType;
        SourceId = sourceId;
        Status = status;
    }

    public static Result<TenantCredit> Create(
        Guid id,
        decimal amount,
        CreditSourceType sourceType,
        Guid? sourceId = null)
    {
        if (amount <= 0)
            return TenantCreditErrors.InvalidAmount;

        if (!Enum.IsDefined(sourceType))
            return TenantCreditErrors.InvalidSourceType;

        return new TenantCredit(id, amount, sourceType, sourceId, CreditStatus.Available);
    }

    public Result<Updated> Apply(Guid invoiceLineId)
    {
        if (Status != CreditStatus.Available)
            return TenantCreditErrors.NotAvailable;

        Status = CreditStatus.Applied;
        AppliedToInvoiceLineId = invoiceLineId;

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
}
