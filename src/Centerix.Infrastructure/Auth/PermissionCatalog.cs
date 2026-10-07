namespace Centerix.Infrastructure.Auth;

/// <summary>
/// Canonical permission definitions: (Module, Action, Code, Scope, Description). Used by EF seeding
/// and by <see cref="Permissions"/> helpers so the runtime constants and DB rows stay in sync.
/// <para>
/// <b>Scope (T22 final correction)</b>: every entry carries an EXPLICIT
/// <see cref="PermissionScope"/>. The runtime classifier (<see cref="PermissionScopes"/>) is NOT
/// allowed to treat "catalog membership" as a synonym for <see cref="PermissionScope.Tenant"/>: an
/// entry whose scope is <see cref="PermissionScope.Unknown"/> stays in <see cref="PermissionScope.Unknown"/>
/// after classification, so the fail-closed rule applies uniformly. A developer who adds a new
/// catalog entry MUST pick a scope — the <see cref="PermissionScope"/> parameter is required, not
/// optional, and omitting it is a compile-time error.
/// </para>
/// <para>
/// Platform-scoped permissions must ALSO be present in
/// <see cref="Permissions.PlatformScope.PermissionCodes"/>. That set is preserved as-is to keep
/// controller-enforced codes (e.g. <c>PlatformUsers.*</c>, <c>PlatformRoles.*</c>,
/// <c>PlatformPermissions.Read</c>) classified as platform even though they are not catalog rows.
/// </para>
/// </summary>
public static class PermissionCatalog
{
    public readonly record struct Entry(
        string Module,
        string Action,
        string Code,
        PermissionScope Scope,
        string? Description,
        bool RequiresPlatformAuthority = false);

    public static readonly Entry[] All =
    [
        // PLATFORM scope — cross-tenant platform resources. Must also appear in
        // Permissions.PlatformScope.PermissionCodes (drift guard test enforces parity).
        new("Plans",         "Create", "Plans.Create",         PermissionScope.Platform, "Create a plan"),
        new("Plans",         "Read",   "Plans.Read",           PermissionScope.Platform, "Read plans"),
        new("Plans",         "Update", "Plans.Update",         PermissionScope.Platform, "Update a plan"),
        new("Plans",         "Delete", "Plans.Delete",         PermissionScope.Platform, "Delete a plan"),

        new("Features",      "Create", "Features.Create",      PermissionScope.Platform, "Create a feature"),
        new("Features",      "Read",   "Features.Read",        PermissionScope.Platform, "Read features"),
        new("Features",      "Update", "Features.Update",      PermissionScope.Platform, "Update a feature"),
        new("Features",      "Delete", "Features.Delete",      PermissionScope.Platform, "Delete a feature"),

        new("Tenants",       "Create", "Tenants.Create",       PermissionScope.Platform, "Create a tenant"),
        new("Tenants",       "Read",   "Tenants.Read",         PermissionScope.Platform, "Read tenants"),
        new("Tenants",       "Update", "Tenants.Update",       PermissionScope.Platform, "Update a tenant"),
        new("Tenants",       "Delete", "Tenants.Delete",       PermissionScope.Platform, "Delete a tenant"),

        // Phase 2: platform-only commercial subscription workflows.
        new("Subscriptions",   "Read",   "Subscriptions.Read",    PermissionScope.Platform, "View all tenant subscriptions (platform)"),
        new("Subscriptions",   "Manage", "Subscriptions.Manage",  PermissionScope.Platform, "Approve tenants and manage subscriptions (platform)"),

        new("AddOnCatalogs",       "Create", "AddOnCatalogs.Create",       PermissionScope.Platform, "Create an add-on catalog"),
        new("AddOnCatalogs",       "Read",   "AddOnCatalogs.Read",         PermissionScope.Platform, "Read add-on catalogs"),
        new("AddOnCatalogs",       "Update", "AddOnCatalogs.Update",       PermissionScope.Platform, "Update an add-on catalog"),

        // Promotion / Offer Engine: platform-scoped commercial rules for calculating promotional offers.
        new("Promotions",     "View",     "Promotions.View",     PermissionScope.Platform, "View promotions"),
        new("Promotions",     "Create",   "Promotions.Create",   PermissionScope.Platform, "Create a promotion"),
        new("Promotions",     "Update",   "Promotions.Update",   PermissionScope.Platform, "Update a promotion"),
        new("Promotions",     "Activate", "Promotions.Activate", PermissionScope.Platform, "Activate a promotion"),
        new("Promotions",     "Calculate","Promotions.Calculate", PermissionScope.Platform, "Calculate an offer"),

        // FIN-001 second key. Minting tenant credit balance is a TWO-KEY operation:
        //   * TenantCredits.Create (Tenant scope)  -> WHICH TENANT the request may act in;
        //   * PlatformCredits.Mint (Platform scope) -> whether the caller may mint balance AT ALL.
        // The module is deliberately PlatformCredits, NOT TenantCredits: a permission that a
        // tenant role may hold must never be classifiable as platform, and a permission whose only
        // acceptable grant is the PlatformAdmin verifier must never sit in a tenant-partitioned
        // module. Enforced on the endpoint by a second [HasPermission] attribute (ASP.NET combines
        // every attribute into ONE policy, so both must pass) and re-checked in the handler by
        // IPlatformAdminGuard.
        new("PlatformCredits", "Mint",   "PlatformCredits.Mint", PermissionScope.Platform, "Mint tenant credit balance (platform key)"),

        // TENANT scope — every entry below is tenant-partitioned data; authorization requires
        // an active TenantMembership and an authorized tenant context. NONE of these codes
        // appear in Permissions.PlatformScope.PermissionCodes.
        new("TenantPlans",     "Create", "TenantPlans.Create",     PermissionScope.Tenant, "Subscribe a tenant to a plan"),
        new("TenantPlans",     "Read",   "TenantPlans.Read",       PermissionScope.Tenant, "Read tenant subscriptions"),
        new("TenantPlans",     "Update", "TenantPlans.Update",     PermissionScope.Tenant, "Update a tenant subscription"),
        new("TenantPlans",     "Delete", "TenantPlans.Delete",     PermissionScope.Tenant, "Cancel a tenant subscription"),

        new("TenantCRMLeads", "Create", "TenantCRMLeads.Create", PermissionScope.Tenant, "Create a CRM lead"),
        new("TenantCRMLeads", "Read",   "TenantCRMLeads.Read",   PermissionScope.Tenant, "Read CRM leads"),
        new("TenantCRMLeads", "Update", "TenantCRMLeads.Update", PermissionScope.Tenant, "Update a CRM lead"),
        new("TenantCRMLeads", "Delete", "TenantCRMLeads.Delete", PermissionScope.Tenant, "Delete a CRM lead"),

        new("AttendanceLogs", "Create", "AttendanceLogs.Create", PermissionScope.Tenant, "Create an attendance log"),
        new("AttendanceLogs", "Read",   "AttendanceLogs.Read",   PermissionScope.Tenant, "Read attendance logs"),

        new("Students",       "Create", "Students.Create",       PermissionScope.Tenant, "Create a student"),
        new("Students",       "Read",   "Students.Read",         PermissionScope.Tenant, "Read students"),
        new("Students",       "Update", "Students.Update",       PermissionScope.Tenant, "Update a student"),
        new("Students",       "Delete", "Students.Delete",       PermissionScope.Tenant, "Delete a student"),

        new("AcademicStages", "Create", "AcademicStages.Create", PermissionScope.Tenant, "Create an academic stage"),
        new("AcademicStages", "Read",   "AcademicStages.Read",   PermissionScope.Tenant, "Read academic stages"),
        new("AcademicStages", "Update", "AcademicStages.Update", PermissionScope.Tenant, "Update an academic stage"),

        new("AcademicYears",  "Create", "AcademicYears.Create",   PermissionScope.Tenant, "Create an academic year"),
        new("AcademicYears",  "Read",   "AcademicYears.Read",     PermissionScope.Tenant, "Read academic years"),
        new("AcademicYears",  "Update", "AcademicYears.Update",   PermissionScope.Tenant, "Update an academic year"),

        new("Subjects",       "Create", "Subjects.Create",        PermissionScope.Tenant, "Create a subject"),
        new("Subjects",       "Read",   "Subjects.Read",          PermissionScope.Tenant, "Read subjects"),
        new("Subjects",       "Update", "Subjects.Update",        PermissionScope.Tenant, "Update a subject"),
        new("Subjects",       "Delete", "Subjects.Delete",        PermissionScope.Tenant, "Delete a subject"),

        new("Teachers",       "Create", "Teachers.Create",        PermissionScope.Tenant, "Create a teacher"),
        new("Teachers",       "Read",   "Teachers.Read",          PermissionScope.Tenant, "Read teachers"),
        new("Teachers",       "Update", "Teachers.Update",        PermissionScope.Tenant, "Update a teacher"),
        new("Teachers",       "Delete", "Teachers.Delete",        PermissionScope.Tenant, "Soft-delete a teacher"),

        new("TeacherSalaryConfigs", "Create", "TeacherSalaryConfigs.Create", PermissionScope.Tenant, "Create a teacher salary config"),
        new("TeacherSalaryConfigs", "Read",   "TeacherSalaryConfigs.Read",   PermissionScope.Tenant, "Read teacher salary configs"),
        new("TeacherSalaryConfigs", "Update", "TeacherSalaryConfigs.Update", PermissionScope.Tenant, "Update a teacher salary config"),
        new("TeacherSalaryConfigs", "Delete", "TeacherSalaryConfigs.Delete", PermissionScope.Tenant, "Delete a teacher salary config"),

        new("SalaryPayments", "Create", "SalaryPayments.Create",  PermissionScope.Tenant, "Create a salary payment"),
        new("SalaryPayments", "Read",   "SalaryPayments.Read",    PermissionScope.Tenant, "Read salary payments"),
        new("SalaryPayments", "Update", "SalaryPayments.Update",  PermissionScope.Tenant, "Mark a salary payment as paid/cancelled"),

        new("TeacherRatings", "Create", "TeacherRatings.Create",  PermissionScope.Tenant, "Submit a teacher rating"),
        new("TeacherRatings", "Read",   "TeacherRatings.Read",    PermissionScope.Tenant, "Read teacher ratings"),

        new("Branches",       "Create", "Branches.Create",        PermissionScope.Tenant, "Create a branch"),
        new("Branches",       "Read",   "Branches.Read",          PermissionScope.Tenant, "Read branches"),
        new("Branches",       "Update", "Branches.Update",        PermissionScope.Tenant, "Update a branch"),
        new("Branches",       "Delete", "Branches.Delete",        PermissionScope.Tenant, "Delete a branch"),

        new("TenantAddOns",        "Create", "TenantAddOns.Create",        PermissionScope.Tenant, "Create a tenant add-on"),
        new("TenantAddOns",        "Read",   "TenantAddOns.Read",          PermissionScope.Tenant, "Read tenant add-ons"),
        new("TenantAddOns",        "Update", "TenantAddOns.Update",        PermissionScope.Tenant, "Update a tenant add-on"),

        new("TenantLimitOverrides","Create", "TenantLimitOverrides.Create", PermissionScope.Tenant, "Create a tenant limit override"),
        new("TenantLimitOverrides","Read",   "TenantLimitOverrides.Read",   PermissionScope.Tenant, "Read tenant limit overrides"),

        new("TenantReferralCodes", "Create", "TenantReferralCodes.Create", PermissionScope.Tenant, "Create a tenant referral code"),
        new("TenantReferralCodes", "Read",   "TenantReferralCodes.Read",   PermissionScope.Tenant, "Read tenant referral codes"),

        new("TenantReferrals",     "Create", "TenantReferrals.Create",     PermissionScope.Tenant, "Create a tenant referral"),
        new("TenantReferrals",     "Read",   "TenantReferrals.Read",       PermissionScope.Tenant, "Read tenant referrals"),

        new("TenantProvisioningJobs","Create", "TenantProvisioningJobs.Create", PermissionScope.Tenant, "Create a provisioning job"),
        new("TenantProvisioningJobs","Read",   "TenantProvisioningJobs.Read",   PermissionScope.Tenant, "Read provisioning jobs"),
        new("TenantProvisioningJobs","Update", "TenantProvisioningJobs.Update", PermissionScope.Tenant, "Update a provisioning job"),

        new("Invoices",       "Create", "Invoices.Create",       PermissionScope.Tenant, "Create an invoice"),
        new("Invoices",       "Read",   "Invoices.Read",         PermissionScope.Tenant, "Read invoices"),
        new("Invoices",       "Update", "Invoices.Update",       PermissionScope.Tenant, "Update an invoice"),
        new("Invoices",       "Delete", "Invoices.Delete",       PermissionScope.Tenant, "Delete an invoice"),

        new("Payments",       "Create", "Payments.Create",       PermissionScope.Tenant, "Create a payment"),
        new("Payments",       "Read",   "Payments.Read",         PermissionScope.Tenant, "Read payments"),
        new("Payments",       "Complete", "Payments.Complete",   PermissionScope.Tenant, "Complete a payment"),
        new("Payments",       "Allocate", "Payments.Allocate",   PermissionScope.Tenant, "Allocate payment to invoice"),

        new("Refunds",        "Create", "Refunds.Create",        PermissionScope.Tenant, "Create a refund"),
        new("Refunds",        "Read",   "Refunds.Read",          PermissionScope.Tenant, "Read refunds"),
        new("Refunds",        "Approve", "Refunds.Approve",      PermissionScope.Tenant, "Approve a refund"),
        new("Refunds",        "Execute", "Refunds.Execute",      PermissionScope.Tenant, "Execute a refund"),

        new("Receipts",       "Read",   "Receipts.Read",         PermissionScope.Tenant, "Read payment receipts"),

        new("Ledger",         "Read",   "Ledger.Read",           PermissionScope.Tenant, "Read customer ledger entries"),

        // F4 two-key catalog flag: RequiresPlatformAuthority marks a TENANT-scoped permission
        // that additionally demands a VERIFIED platform authority at the authorization choke
        // point (PermissionAuthorizationHandler). TenantCredits.Create is the tenant key of the
        // FIN-001 mint operation: a tenant-membership grant proves WHICH tenant the request may
        // act in, never WHETHER the caller may mint balance. See RequiresPlatformAuthority(string).
        new("TenantCredits",  "Create", "TenantCredits.Create",  PermissionScope.Tenant, "Create a tenant credit", RequiresPlatformAuthority: true),
        new("TenantCredits",  "Read",   "TenantCredits.Read",    PermissionScope.Tenant, "Read tenant credits"),
        new("TenantCredits",  "Apply",  "TenantCredits.Apply",   PermissionScope.Tenant, "Apply credit to invoice"),

        new("Invitations",    "Create", "Invitations.Create",    PermissionScope.Tenant, "Create a tenant invitation"),
        new("Invitations",    "Read",   "Invitations.Read",      PermissionScope.Tenant, "Read tenant invitations"),
        new("Invitations",    "Revoke", "Invitations.Revoke",    PermissionScope.Tenant, "Revoke a tenant invitation"),

        new("Memberships",    "Read",   "Memberships.Read",      PermissionScope.Tenant, "Read tenant memberships"),
        new("Memberships",    "Manage", "Memberships.Manage",    PermissionScope.Tenant, "Manage tenant memberships"),

        // Phase 7: Contracts (commercial agreements)
        new("Contracts",      "Create", "Contracts.Create",      PermissionScope.Tenant, "Create a contract"),
        new("Contracts",      "Read",   "Contracts.Read",        PermissionScope.Tenant, "Read contracts"),

        // Contract Benefits: eligibility, delivery, and management
        new("Benefits",       "View",   "Benefits.View",         PermissionScope.Tenant, "View contract benefits"),
        new("Benefits",       "Manage", "Benefits.Manage",       PermissionScope.Tenant, "Manage contract benefits eligibility"),
        new("Benefits",       "Deliver","Benefits.Deliver",      PermissionScope.Tenant, "Deliver a physical gift benefit"),

        // Installments (payment obligations)
        new("Installments",   "Read",   "Installments.Read",     PermissionScope.Tenant, "Read installment schedules"),
        new("Installments",   "Create", "Installments.Create",   PermissionScope.Tenant, "Create installment schedule"),
        new("Installments",   "Update", "Installments.Update",   PermissionScope.Tenant, "Update installment details"),
        new("Installments",   "Cancel", "Installments.Cancel",   PermissionScope.Tenant, "Cancel an installment"),

        // Offer lifecycle (tenant-scoped commercial offers)
        new("Offers",         "Read",     "Offers.Read",         PermissionScope.Tenant, "Read offers"),
        new("Offers",         "Calculate","Offers.Calculate",    PermissionScope.Tenant, "Calculate and persist an offer"),
        new("Offers",         "Accept",   "Offers.Accept",       PermissionScope.Tenant, "Accept an offer"),
    ];

    /// <summary>
    /// F4: the catalog's two-key flag. True only for a permission that, beyond its own scope's
    /// normal grant rules, ALSO requires a verified platform authority at the single
    /// authorization choke point every requirement passes through
    /// (<c>PermissionAuthorizationHandler</c>). The flagged permission still runs the ordinary
    /// tenant grant path afterwards — the flag is an ADDITIONAL gate, never a shortcut, so
    /// satisfying it alone cannot authorize the request.
    /// <para>
    /// Exactly one code carries the flag today: <c>TenantCredits.Create</c> — the tenant key of
    /// the FIN-001 credit-mint operation. A tenant-membership grant proves WHICH tenant; only
    /// <c>IPlatformAdminVerifier</c> proves WHETHER the caller may mint.
    /// </para>
    /// </summary>
    public static bool RequiresPlatformAuthority(string? code)
        => All.Any(entry =>
            entry.RequiresPlatformAuthority
            && string.Equals(entry.Code, code, StringComparison.Ordinal));
}