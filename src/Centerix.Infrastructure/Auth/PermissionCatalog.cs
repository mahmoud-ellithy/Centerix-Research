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

        // Phase 2: platform-only commercial subscription workflows.
        new("Subscriptions",   "Read",   "Subscriptions.Read",    "View all tenant subscriptions (platform)"),
        new("Subscriptions",   "Manage", "Subscriptions.Manage",  "Approve tenants and manage subscriptions (platform)"),

        new("TenantCRMLeads", "Create", "TenantCRMLeads.Create", "Create a CRM lead"),
        new("TenantCRMLeads", "Read",   "TenantCRMLeads.Read",   "Read CRM leads"),
        new("TenantCRMLeads", "Update", "TenantCRMLeads.Update", "Update a CRM lead"),
        new("TenantCRMLeads", "Delete", "TenantCRMLeads.Delete", "Delete a CRM lead"),

        new("AttendanceLogs", "Create", "AttendanceLogs.Create", "Create an attendance log"),
        new("AttendanceLogs", "Read",   "AttendanceLogs.Read",   "Read attendance logs"),

        new("Students",       "Create", "Students.Create",       "Create a student"),
        new("Students",       "Read",   "Students.Read",         "Read students"),
        new("Students",       "Update", "Students.Update",       "Update a student"),
        new("Students",       "Delete", "Students.Delete",       "Delete a student"),

        new("AcademicStages", "Create", "AcademicStages.Create", "Create an academic stage"),
        new("AcademicStages", "Read",   "AcademicStages.Read",   "Read academic stages"),
        new("AcademicStages", "Update", "AcademicStages.Update", "Update an academic stage"),

        new("AcademicYears",  "Create", "AcademicYears.Create",   "Create an academic year"),
        new("AcademicYears",  "Read",   "AcademicYears.Read",     "Read academic years"),
        new("AcademicYears",  "Update", "AcademicYears.Update",   "Update an academic year"),

        new("Subjects",       "Create", "Subjects.Create",        "Create a subject"),
        new("Subjects",       "Read",   "Subjects.Read",          "Read subjects"),
        new("Subjects",       "Update", "Subjects.Update",        "Update a subject"),
        new("Subjects",       "Delete", "Subjects.Delete",        "Delete a subject"),

        new("Teachers",       "Create", "Teachers.Create",        "Create a teacher"),
        new("Teachers",       "Read",   "Teachers.Read",          "Read teachers"),
        new("Teachers",       "Update", "Teachers.Update",        "Update a teacher"),
        new("Teachers",       "Delete", "Teachers.Delete",        "Soft-delete a teacher"),

        new("TeacherSalaryConfigs", "Create", "TeacherSalaryConfigs.Create", "Create a teacher salary config"),
        new("TeacherSalaryConfigs", "Read",   "TeacherSalaryConfigs.Read",   "Read teacher salary configs"),
        new("TeacherSalaryConfigs", "Update", "TeacherSalaryConfigs.Update", "Update a teacher salary config"),
        new("TeacherSalaryConfigs", "Delete", "TeacherSalaryConfigs.Delete", "Delete a teacher salary config"),

        new("SalaryPayments", "Create", "SalaryPayments.Create",  "Create a salary payment"),
        new("SalaryPayments", "Read",   "SalaryPayments.Read",    "Read salary payments"),
        new("SalaryPayments", "Update", "SalaryPayments.Update",  "Mark a salary payment as paid/cancelled"),

        new("TeacherRatings", "Create", "TeacherRatings.Create",  "Submit a teacher rating"),
        new("TeacherRatings", "Read",   "TeacherRatings.Read",    "Read teacher ratings"),

        new("Branches",       "Create", "Branches.Create",        "Create a branch"),
        new("Branches",       "Read",   "Branches.Read",          "Read branches"),
        new("Branches",       "Update", "Branches.Update",        "Update a branch"),
        new("Branches",       "Delete", "Branches.Delete",        "Delete a branch"),

        new("AddOnCatalogs",       "Create", "AddOnCatalogs.Create",       "Create an add-on catalog"),
        new("AddOnCatalogs",       "Read",   "AddOnCatalogs.Read",         "Read add-on catalogs"),
        new("AddOnCatalogs",       "Update", "AddOnCatalogs.Update",       "Update an add-on catalog"),

        new("TenantAddOns",        "Create", "TenantAddOns.Create",        "Create a tenant add-on"),
        new("TenantAddOns",        "Read",   "TenantAddOns.Read",          "Read tenant add-ons"),
        new("TenantAddOns",        "Update", "TenantAddOns.Update",        "Update a tenant add-on"),

        new("TenantLimitOverrides","Create", "TenantLimitOverrides.Create", "Create a tenant limit override"),
        new("TenantLimitOverrides","Read",   "TenantLimitOverrides.Read",   "Read tenant limit overrides"),

        new("TenantReferralCodes", "Create", "TenantReferralCodes.Create", "Create a tenant referral code"),
        new("TenantReferralCodes", "Read",   "TenantReferralCodes.Read",   "Read tenant referral codes"),

        new("TenantReferrals",     "Create", "TenantReferrals.Create",     "Create a tenant referral"),
        new("TenantReferrals",     "Read",   "TenantReferrals.Read",       "Read tenant referrals"),

        new("TenantProvisioningJobs","Create", "TenantProvisioningJobs.Create", "Create a provisioning job"),
        new("TenantProvisioningJobs","Read",   "TenantProvisioningJobs.Read",   "Read provisioning jobs"),
        new("TenantProvisioningJobs","Update", "TenantProvisioningJobs.Update", "Update a provisioning job"),

        new("Invoices",       "Create", "Invoices.Create",       "Create an invoice"),
        new("Invoices",       "Read",   "Invoices.Read",         "Read invoices"),
        new("Invoices",       "Update", "Invoices.Update",       "Update an invoice"),
        new("Invoices",       "Delete", "Invoices.Delete",       "Delete an invoice"),

        new("TenantCredits",  "Create", "TenantCredits.Create",  "Create a tenant credit"),
        new("TenantCredits",  "Read",   "TenantCredits.Read",    "Read tenant credits"),

        new("Invitations",    "Create", "Invitations.Create",    "Create a tenant invitation"),
        new("Invitations",    "Read",   "Invitations.Read",      "Read tenant invitations"),
        new("Invitations",    "Revoke", "Invitations.Revoke",    "Revoke a tenant invitation"),

        new("Memberships",    "Read",   "Memberships.Read",      "Read tenant memberships"),
        new("Memberships",    "Manage", "Memberships.Manage",    "Manage tenant memberships"),
    ];
}
