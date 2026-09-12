namespace Centerix.Domain.Platform.Billing.Refunds;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Refunds.Enums;

/// <summary>
/// Error codes for the Refunds module.
/// </summary>
public static class RefundErrors
{
    public static Error IdRequired =>
        Error.Validation("Refund.Id_Required", "Refund ID is required.");

    public static Error RefundNumberRequired =>
        Error.Validation("Refund.RefundNumber_Required", "Refund number is required.");

    public static Error ContractIdRequired =>
        Error.Validation("Refund.ContractId_Required", "Contract ID is required.");

    public static Error AmountMustBePositive =>
        Error.Validation("Refund.Amount_MustBePositive", "Refund amount must be greater than zero.");

    public static Error InvalidCurrency =>
        Error.Validation("Refund.Currency_Invalid", "Currency must be a 3-letter ISO-4217 code.");

    public static Error ReasonRequired =>
        Error.Validation("Refund.Reason_Required", "Refund reason is required.");

    public static Error CreatedByRequired =>
        Error.Validation("Refund.CreatedBy_Required", "Created by is required.");

    public static Error ApprovedByRequired =>
        Error.Validation("Refund.ApprovedBy_Required", "Approved by is required.");

    public static Error ExecutedByRequired =>
        Error.Validation("Refund.ExecutedBy_Required", "Executed by is required.");

    public static Error NotFound =>
        Error.NotFound("Refund.NotFound", "Refund was not found.");

    public static Error InvalidStateTransition(RefundStatus current, string action) =>
        Error.Conflict("Refund.InvalidStateTransition",
            $"Cannot {action} a refund in status '{current}'.");

    public static Error AlreadyExecuted =>
        Error.Conflict("Refund.AlreadyExecuted", "Refund has already been executed and cannot be modified.");

    public static Error ExecutionConcurrencyConflict =>
        Error.Conflict("Refund.ExecutionConcurrencyConflict",
            "This refund execution conflicted with another concurrent request. Please retry.");

    public static Error ContractNotFound =>
        Error.NotFound("Refund.ContractNotFound", "Contract was not found.");

    public static Error CrossTenantRefund =>
        Error.Forbidden("Refund.CrossTenant", "Cannot create a refund for a contract belonging to a different tenant.");

    public static Error InvalidRefundAmount =>
        Error.Conflict("Refund.InvalidAmount", "Refund amount exceeds the calculated refundable amount.");

    public static Error DuplicateRefundNumber =>
        Error.Conflict("Refund.DuplicateNumber", "A refund with this number already exists for the current tenant.");

    public static Error NoRefundDue(decimal outstandingAmount) =>
        Error.Conflict("Refund.NoRefundDue",
            $"No refund is due. CustomerOutstandingAmount = {outstandingAmount}. A refund record can only be created when RefundAmount > 0.");

    public static Error SubscriptionContractMismatch =>
        Error.Validation("Refund.SubscriptionContractMismatch",
            "The supplied Subscription does not belong to the Refund's Contract.");

    public static Error InvoiceContractMismatch =>
        Error.Validation("Refund.InvoiceContractMismatch",
            "The supplied Invoice does not belong to the Refund's Contract.");

    public static Error CrossTenantSubscription =>
        Error.Forbidden("Refund.CrossTenantSubscription",
            "Cannot attach a Subscription belonging to a different tenant.");

    public static Error CrossTenantInvoice =>
        Error.Forbidden("Refund.CrossTenantInvoice",
            "Cannot attach an Invoice belonging to a different tenant.");
}
