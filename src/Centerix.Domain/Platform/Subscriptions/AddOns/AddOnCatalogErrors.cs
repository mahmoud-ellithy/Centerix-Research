namespace Centerix.Domain.Platform.Subscriptions.AddOns;

using Centerix.Domain.Common.Results;

public static class AddOnCatalogErrors
{
    public static Error CodeRequired =>
        Error.Validation("AddOnCatalog.Code_Required", "Add-on code is required");

    public static Error DisplayNameRequired =>
        Error.Validation("AddOnCatalog.DisplayName_Required", "Display name is required");

    public static Error UnitTypeRequired =>
        Error.Validation("AddOnCatalog.UnitType_Required", "Unit type is required");

    public static Error InvalidUnitQuantity =>
        Error.Validation("AddOnCatalog.InvalidUnitQuantity", "Unit quantity must be greater than zero");

    public static Error AlreadyDeactivated =>
        Error.Conflict("AddOnCatalog.AlreadyDeactivated", "Add-on is already deactivated");

    public static Error AlreadyActive =>
        Error.Conflict("AddOnCatalog.AlreadyActive", "Add-on is already active");
}
