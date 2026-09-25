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

    public static Error CannotModifyAfterIssuance =>
        Error.Conflict("Invoice.CannotModifyAfterIssuance", "Invoice lines cannot be modified after the invoice has been issued");

    public static Error CannotAddLineNonDraft =>
        Error.Conflict("Invoice.CannotAddLineNonDraft", "Invoice lines can only be added to draft invoices");

    public static Error CannotRemoveLineNonDraft =>
        Error.Conflict("Invoice.CannotRemoveLineNonDraft", "Invoice lines can only be removed from draft invoices");

    public static Error TotalAmountMismatch =>
        Error.Validation("Invoice.TotalAmountMismatch", "TotalAmount must equal Subtotal - DiscountAmount + TaxAmount");

    public static Error DuplicateInvoiceNumber =>
        Error.Conflict("Invoice.DuplicateInvoiceNumber", "An invoice with this number already exists");

    public static Error ContractNotFound =>
        Error.NotFound("Invoice.ContractNotFound", "Contract was not found");

    public static Error ContractNotOwnedByTenant =>
        Error.Forbidden("Invoice.ContractNotOwnedByTenant", "The specified contract does not belong to your tenant");

    public static Error SubscriptionContractMismatch =>
        Error.Conflict("Invoice.SubscriptionContractMismatch", "The specified subscription does not belong to the specified contract");

    public static Error ClientAmountMismatch =>
        Error.Validation("Invoice.ClientAmountMismatch", "Client-supplied amount does not match the authoritative server-derived value");
}
