namespace Centerix.Domain.Platform.Tenants;

using Centerix.Domain.Common.Results;

public static class TenantErrors
{
    public static Error SlugRequired =>
        Error.Validation("Tenant.Slug_Required", "Tenant slug is required");

    public static Error SubdomainRequired =>
        Error.Validation("Tenant.Subdomain_Required", "Tenant subdomain is required");

    public static Error DisplayNameRequired =>
        Error.Validation("Tenant.DisplayName_Required", "Display name is required");

    public static Error CountryRequired =>
        Error.Validation("Tenant.Country_Required", "Country code is required");

    public static Error CurrencyRequired =>
        Error.Validation("Tenant.Currency_Required", "Currency code is required");

    public static Error TimezoneRequired =>
        Error.Validation("Tenant.Timezone_Required", "Timezone is required");

    public static Error OwnerFirstNameRequired =>
        Error.Validation("Tenant.OwnerFirstName_Required", "Owner first name is required");

    public static Error OwnerLastNameRequired =>
        Error.Validation("Tenant.OwnerLastName_Required", "Owner last name is required");

    public static Error OwnerEmailRequired =>
        Error.Validation("Tenant.OwnerEmail_Required", "Owner email is required");

    public static Error SlugAlreadyExists =>
        Error.Conflict("Tenant.Slug_AlreadyExists", "A tenant with this slug already exists");

    public static Error SubdomainAlreadyExists =>
        Error.Conflict("Tenant.Subdomain_AlreadyExists", "A tenant with this subdomain already exists");

    public static Error InvalidIsolationMode =>
        Error.Validation("Tenant.IsolationMode_Invalid", "Invalid isolation mode");

    public static Error InvalidLifecycleStatus =>
        Error.Validation("Tenant.LifecycleStatus_Invalid", "Invalid lifecycle status");

    public static Error AlreadySuspended =>
        Error.Conflict("Tenant.AlreadySuspended", "Tenant is already suspended");

    public static Error AlreadyActive =>
        Error.Conflict("Tenant.AlreadyActive", "Tenant is already active");

    public static Error AlreadyCancelled =>
        Error.Conflict("Tenant.AlreadyCancelled", "Tenant is already cancelled");

    public static Error CannotCancelFromSuspended =>
        Error.Conflict("Tenant.CannotCancelFromSuspended", "Cannot cancel a suspended tenant directly");
}
