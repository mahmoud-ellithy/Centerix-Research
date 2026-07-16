namespace Centerix.Domain.Platform.Billing;

using Centerix.Domain.Common.Results;

public static class TenantBillingErrors
{
    public static Error PlanIdRequired =>
        Error.Validation("Billing.PlanId_Required", "Plan ID is required");

    public static Error AmountRequired =>
        Error.Validation("Billing.Amount_Required", "Amount is required");

    public static Error InvalidAmount =>
        Error.Validation("Billing.InvalidAmount", "Amount must be greater than zero");

    public static Error MethodRequired =>
        Error.Validation("Billing.Method_Required", "Payment method is required");

    public static Error AlreadyPaid =>
        Error.Conflict("Billing.AlreadyPaid", "This invoice has already been paid");

    public static Error InvoiceLocked =>
        Error.Conflict("Billing.InvoiceLocked", "Cannot modify a paid invoice");
}
