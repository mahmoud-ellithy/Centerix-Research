namespace Centerix.Domain.Platform.Billing.Refunds;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents the allocation of a refund amount to a specific original completed Payment.
/// Each RefundAllocation records how much of a Refund is sourced from a particular Payment,
/// preserving the original payment method for financial traceability.
/// </summary>
public class RefundAllocation : AuditableEntity<Guid>
{
    public Guid RefundId { get; private set; }
    public Guid PaymentId { get; private set; }
    public decimal Amount { get; private set; }
    public string PaymentMethod { get; private set; } = default!;
    public string CurrencyCode { get; private set; } = "EGP";
    public string? PaymentNumber { get; private set; }
    public byte[] RowVersion { get; internal set; } = [];

    private RefundAllocation() { }

    private RefundAllocation(
        Guid id,
        Guid refundId,
        Guid paymentId,
        decimal amount,
        string paymentMethod,
        string currencyCode,
        string? paymentNumber)
        : base(id)
    {
        RefundId = refundId;
        PaymentId = paymentId;
        Amount = amount;
        PaymentMethod = paymentMethod;
        CurrencyCode = currencyCode;
        PaymentNumber = paymentNumber;
    }

    public static Result<RefundAllocation> Create(
        Guid id,
        Guid refundId,
        Guid paymentId,
        decimal amount,
        PaymentMethod method,
        string currencyCode,
        string? paymentNumber = null)
    {
        if (id == Guid.Empty)
            return RefundErrors.AllocationIdRequired;

        if (refundId == Guid.Empty)
            return RefundErrors.AllocationRefundIdRequired;

        if (paymentId == Guid.Empty)
            return RefundErrors.AllocationPaymentIdRequired;

        if (amount <= 0)
            return RefundErrors.AllocationAmountMustBePositive;

        if (!Enum.IsDefined(method))
            return RefundErrors.AllocationInvalidPaymentMethod;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return RefundErrors.InvalidCurrency;

        return new RefundAllocation(
            id,
            refundId,
            paymentId,
            amount,
            method.ToString(),
            currencyCode.Trim().ToUpperInvariant(),
            paymentNumber);
    }
}