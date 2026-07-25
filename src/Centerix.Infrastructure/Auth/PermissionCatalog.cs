namespace Centerix.Infrastructure.Auth;

/// <summary>
/// Canonical permission definitions: (Module, Action, Code, Description). Used by EF seeding
/// and by <see cref="Permissions"/> helpers so the runtime constants and DB rows stay in sync.
/// </summary>
public static class PermissionCatalog
{
    public readonly record struct Entry(string Module, string Action, string Code, string? Description);

    public static readonly Entry[] All =
    [
        new("Plans",         "Create", "Plans.Create",         "Create a plan"),
        new("Plans",         "Read",   "Plans.Read",           "Read plans"),
        new("Plans",         "Update", "Plans.Update",         "Update a plan"),
        new("Plans",         "Delete", "Plans.Delete",         "Delete a plan"),

        new("Features",      "Create", "Features.Create",      "Create a feature"),
        new("Features",      "Read",   "Features.Read",        "Read features"),
        new("Features",      "Update", "Features.Update",      "Update a feature"),
        new("Features",      "Delete", "Features.Delete",      "Delete a feature"),

        new("Tenants",       "Create", "Tenants.Create",       "Create a tenant"),
        new("Tenants",       "Read",   "Tenants.Read",         "Read tenants"),
        new("Tenants",       "Update", "Tenants.Update",       "Update a tenant"),
        new("Tenants",       "Delete", "Tenants.Delete",       "Delete a tenant"),

        new("TenantPlans",     "Create", "TenantPlans.Create",     "Subscribe a tenant to a plan"),
        new("TenantPlans",     "Read",   "TenantPlans.Read",       "Read tenant subscriptions"),
        new("TenantPlans",     "Update", "TenantPlans.Update",     "Update a tenant subscription"),
        new("TenantPlans",     "Delete", "TenantPlans.Delete",     "Cancel a tenant subscription"),

        new("TenantBillings", "Create", "TenantBillings.Create", "Record a billing payment"),
        new("TenantBillings", "Read",   "TenantBillings.Read",   "Read billing history"),
        new("TenantBillings", "Update", "TenantBillings.Update", "Update a billing record"),
        new("TenantBillings", "Delete", "TenantBillings.Delete", "Delete a billing record"),

        new("TenantCRMLeads", "Create", "TenantCRMLeads.Create", "Create a CRM lead"),
        new("TenantCRMLeads", "Read",   "TenantCRMLeads.Read",   "Read CRM leads"),
        new("TenantCRMLeads", "Update", "TenantCRMLeads.Update", "Update a CRM lead"),
        new("TenantCRMLeads", "Delete", "TenantCRMLeads.Delete", "Delete a CRM lead"),
    ];
}
