namespace Centerix.Domain.Platform.Billing.Invoicing;

using Centerix.Domain.Common;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;

public class PlatformPayment : Entity
{
    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }
    public string Method { get; private set; } = default!;
    public string? GatewayRef { get; private set; }
    public DateTime PaidAt { get; private set; }
    public PlatformPaymentStatus Status { get; private set; }

    public Invoice Invoice { get; private set; } = default!;

    private PlatformPayment() { }

    private PlatformPayment(
        Guid id,
        Guid invoiceId,
        decimal amount,
        string method,
        string? gatewayRef,
        DateTime paidAt,
        PlatformPaymentStatus status)
    {
        Id = id;
        InvoiceId = invoiceId;
        Amount = amount;
        Method = method;
        GatewayRef = gatewayRef;
        PaidAt = paidAt;
        Status = status;
    }

    public static PlatformPayment Create(
        Guid id,
        Guid invoiceId,
        decimal amount,
        string method,
        string? gatewayRef,
        DateTime paidAt,
        PlatformPaymentStatus status)
    {
        return new PlatformPayment(id, invoiceId, amount, method, gatewayRef, paidAt, status);
    }
}
