namespace Centerix.Domain.Platform.Billing.Credits;

using Centerix.Domain.Common.Results;

public static class TenantCreditErrors
{
    public static Error InvalidAmount =>
        Error.Validation("TenantCredit.InvalidAmount", "Credit amount must be greater than zero");

    public static Error InvalidSourceType =>
        Error.Validation("TenantCredit.InvalidSourceType", "Invalid credit source type");

    public static Error NotAvailable =>
        Error.Conflict("TenantCredit.NotAvailable", "Credit is not available for this operation");

    public static Error NotFound =>
        Error.NotFound("TenantCredit.NotFound", "Tenant credit was not found");
}
