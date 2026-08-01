namespace Centerix.Domain.Platform.Billing.Invoicing.Events;

using Centerix.Domain.Common;

public class InvoicePaidEvent(Guid invoiceId) : DomainEvent
{
    public Guid InvoiceId { get; } = invoiceId;
}
