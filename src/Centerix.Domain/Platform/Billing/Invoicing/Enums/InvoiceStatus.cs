namespace Centerix.Domain.Platform.Billing.Invoicing.Enums;

public enum InvoiceStatus : byte
{
    Draft = 0,
    Issued = 1,
    Sent = 2,
    Paid = 3,
    PartiallyPaid = 4,
    Overdue = 5,
    Cancelled = 6
}
