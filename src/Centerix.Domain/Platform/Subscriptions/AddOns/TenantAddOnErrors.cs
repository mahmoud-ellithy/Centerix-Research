namespace Centerix.Domain.Platform.Subscriptions.AddOns;

using Centerix.Domain.Common.Results;

public static class TenantAddOnErrors
{
    public static Error AddOnCatalogIdRequired =>
        Error.Validation("TenantAddOn.AddOnCatalogId_Required", "Add-on catalog ID is required");

    public static Error InvalidQuantity =>
        Error.Validation("TenantAddOn.InvalidQuantity", "Quantity must be greater than zero");

    public static Error InvalidUnitPrice =>
        Error.Validation("TenantAddOn.InvalidUnitPrice", "Unit price must be greater than or equal to zero");

    public static Error AlreadyCancelled =>
        Error.Conflict("TenantAddOn.AlreadyCancelled", "This add-on subscription is already cancelled");
}
