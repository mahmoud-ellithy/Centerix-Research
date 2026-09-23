namespace Centerix.Domain.Platform.Billing.Credits;

using Centerix.Domain.Common.Results;

public static class TenantCreditErrors
{
    public static Error InvalidAmount =>
        Error.Validation("TenantCredit.InvalidAmount", "Credit amount must be greater than zero");

    public static Error InvalidSourceType =>
        Error.Validation("TenantCredit.InvalidSourceType", "Invalid credit source type");

    public static Error InvalidTransferredPaidAmount =>
        Error.Validation("TenantCredit.InvalidTransferredPaidAmount",
            "Transferred customer-paid amount must be between zero and the credit amount.");

    public static Error NotAvailable =>
        Error.Conflict("TenantCredit.NotAvailable", "Credit is not available for this operation");

    public static Error InsufficientRemaining =>
        Error.Validation("TenantCredit.InsufficientRemaining", "Credit remaining amount is insufficient for this application.");

    public static Error NotFound =>
        Error.NotFound("TenantCredit.NotFound", "Tenant credit was not found");

    public static Error CrossTenant =>
        Error.Forbidden("TenantCredit.CrossTenant", "Cannot apply a credit from a different tenant to this invoice.");

    public static Error CurrencyMismatch =>
        Error.Validation("TenantCredit.CurrencyMismatch", "Credit currency does not match the invoice currency.");

    public static Error IdempotencyKeyConflict =>
        Error.Conflict("CreditApplication.IdempotencyKeyConflict",
            "A credit application with this idempotency key already exists with different parameters. " +
            "Reuse the same key with identical parameters for idempotent retry, or use a new key for a new operation.");
}
