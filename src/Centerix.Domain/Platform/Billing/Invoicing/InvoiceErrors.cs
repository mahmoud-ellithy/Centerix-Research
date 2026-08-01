namespace Centerix.Domain.Platform.Billing.Invoicing;

using Centerix.Domain.Common.Results;

public static class InvoiceErrors
{
    public static Error InvoiceNumberRequired =>
        Error.Validation("Invoice.InvoiceNumber_Required", "Invoice number is required");

    public static Error InvalidPeriod =>
        Error.Validation("Invoice.InvalidPeriod", "Period end must be after period start");

    public static Error InvalidAmount =>
        Error.Validation("Invoice.InvalidAmount", "Amount must be greater than or equal to zero");

    public static Error InvalidTotalAmount =>
        Error.Validation("Invoice.InvalidTotalAmount", "Total amount must be greater than or equal to zero");

    public static Error NotFound =>
        Error.NotFound("Invoice.NotFound", "Invoice was not found");

    public static Error CannotIssueDraftOnly =>
        Error.Conflict("Invoice.CannotIssueDraftOnly", "Only draft invoices can be issued");

    public static Error CannotPayNotIssued =>
        Error.Conflict("Invoice.CannotPayNotIssued", "Only issued or sent invoices can be marked as paid");

    public static Error AlreadyCancelled =>
        Error.Conflict("Invoice.AlreadyCancelled", "Invoice is already cancelled");

    public static Error CannotCancelNonDraft =>
        Error.Conflict("Invoice.CannotCancelNonDraft", "Only draft invoices can be cancelled");
}
