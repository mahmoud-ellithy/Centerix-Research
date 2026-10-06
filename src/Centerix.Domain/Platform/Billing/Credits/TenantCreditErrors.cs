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

    // ===== FIN-001 — discretionary credit-mint hardening =====

    /// <summary>
    /// Overpayment and SubscriptionChange credits are SYSTEM-generated, produced by their own
    /// command handlers (<c>AllocatePaymentCommand</c>, <c>ChangeSubscriptionPlanCommand</c>)
    /// with a real <c>SourceId</c> and, for SubscriptionChange, an immutable economic-origin
    /// lineage. Minting them by hand would forge customer-paid value, so no caller may create
    /// them through the discretionary endpoint.
    /// </summary>
    public static Error SystemSourceNotCreatable =>
        Error.Validation(
            "TenantCredit.SystemSourceNotCreatable",
            "Overpayment and SubscriptionChange credits are created by the payment and plan-change flows and cannot be created manually.");

    /// <summary>
    /// Discretionary sources carry no source entity, so their <c>SourceId</c> must be null. A
    /// client-supplied id would fabricate lineage and could collide with
    /// UX_TenantCredits_TenantId_SourceType_SourceId.
    /// </summary>
    public static Error SourceIdNotAllowed =>
        Error.Validation(
            "TenantCredit.SourceIdNotAllowed",
            "SourceId is only produced by the system for system-generated credits and cannot be supplied for a discretionary credit.");

    /// <summary>
    /// Required for every API-created credit: it is the authoritative logical retry token that
    /// makes concurrent/duplicate minting idempotent on top of the structural unique indexes.
    /// </summary>
    public static Error IdempotencyKeyRequired =>
        Error.Validation(
            "TenantCredit.IdempotencyKeyRequired",
            "IdempotencyKey is required when creating a credit.");

    public static Error AmountExceedsMaximum =>
        Error.Validation(
            "TenantCredit.AmountExceedsMaximum",
            $"Credit amount cannot exceed {TenantCredit.MaxCreatableAmount:0.00}.");
}
