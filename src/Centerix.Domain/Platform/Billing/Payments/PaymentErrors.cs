namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common.Results;

/// <summary>
/// Error codes for the Payments module.
/// </summary>
public static class PaymentErrors
{
    public static Error AmountMustBePositive =>
        Error.Validation("Payment.Amount_MustBePositive", "Payment amount must be greater than zero.");

    public static Error MethodRequired =>
        Error.Validation("Payment.Method_Required", "Payment method is required.");

    public static Error TenantIdRequired =>
        Error.Validation("Payment.TenantId_Required", "TenantId is required.");

    public static Error NotFound =>
        Error.NotFound("Payment.NotFound", "Payment was not found.");

    public static Error CannotCompleteWrongStatus =>
        Error.Conflict("Payment.CannotCompleteWrongStatus", "Only a Pending or Processing payment can be completed.");

    public static Error CannotFailWrongStatus =>
        Error.Conflict("Payment.CannotFailWrongStatus", "Only a Pending or Processing payment can be marked as failed.");

    public static Error AlreadyCompleted =>
        Error.Conflict("Payment.AlreadyCompleted", "Payment is already completed and cannot be modified.");

    public static Error ExternalReferenceRequired =>
        Error.Validation("Payment.ExternalReferenceRequired", "External reference is required for completion.");

    public static Error AllocationAmountMustBePositive =>
        Error.Validation("PaymentAllocation.Amount_MustBePositive", "Allocation amount must be greater than zero.");

    public static Error AllocationExceedsPayment =>
        Error.Conflict("PaymentAllocation.ExceedsPayment", "Total allocations exceed the payment amount.");

    public static Error AllocationExceedsInvoiceRemaining =>
        Error.Conflict("PaymentAllocation.ExceedsInvoiceRemaining", "Allocation exceeds the invoice remaining amount.");

    public static Error CannotAllocateFailedPayment =>
        Error.Conflict("PaymentAllocation.CannotAllocateFailedPayment", "Cannot allocate against a failed payment.");

    public static Error CannotAllocatePendingPayment =>
        Error.Conflict("PaymentAllocation.CannotAllocatePendingPayment", "Cannot allocate against a pending/processing payment.");

    public static Error InvoiceNotFound =>
        Error.NotFound("PaymentAllocation.InvoiceNotFound", "Invoice was not found.");

    public static Error CrossTenantAllocation =>
        Error.Forbidden("PaymentAllocation.CrossTenant", "Cannot allocate payment to an invoice belonging to a different tenant.");

    public static Error ReceiptNumberRequired =>
        Error.Validation("Receipt.Number_Required", "Receipt number is required.");

    public static Error ReceiptAlreadyExists =>
        Error.Conflict("Receipt.AlreadyExists", "A receipt already exists for this payment.");

    public static Error CannotReceiptFailedPayment =>
        Error.Conflict("Receipt.CannotReceiptFailedPayment", "Cannot issue receipt for a failed payment.");

    public static Error CannotReceiptPendingPayment =>
        Error.Conflict("Receipt.CannotReceiptPendingPayment", "Cannot issue receipt for a pending/processing payment.");

    /// <summary>
    /// Returned when two concurrent allocations race on the same Payment or Invoice
    /// and this request lost the optimistic-concurrency check.
    /// </summary>
    public static Error AllocationConcurrencyConflict =>
        Error.Conflict("PaymentAllocation.ConcurrencyConflict",
            "This allocation conflicted with another concurrent request. Please retry.");
}
