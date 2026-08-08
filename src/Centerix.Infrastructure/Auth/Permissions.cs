namespace Centerix.Infrastructure.Auth;

public static class Permissions
{
    public const string ClaimType = "Permission";

    public static class Plans
    {
        public const string Create = "Plans.Create";
        public const string Read = "Plans.Read";
        public const string Update = "Plans.Update";
        public const string Delete = "Plans.Delete";
    }

    public static class Features
    {
        public const string Create = "Features.Create";
        public const string Read = "Features.Read";
        public const string Update = "Features.Update";
        public const string Delete = "Features.Delete";
    }

    public static class Tenants
    {
        public const string Create = "Tenants.Create";
        public const string Read = "Tenants.Read";
        public const string Update = "Tenants.Update";
        public const string Delete = "Tenants.Delete";
    }

    public static class TenantPlans
    {
        public const string Create = "TenantPlans.Create";
        public const string Read = "TenantPlans.Read";
        public const string Update = "TenantPlans.Update";
        public const string Delete = "TenantPlans.Delete";
    }

    public static class TenantCRMLeads
    {
        public const string Create = "TenantCRMLeads.Create";
        public const string Read = "TenantCRMLeads.Read";
        public const string Update = "TenantCRMLeads.Update";
        public const string Delete = "TenantCRMLeads.Delete";
    }

    public static class Students
    {
        public const string Create = "Students.Create";
        public const string Read = "Students.Read";
        public const string Update = "Students.Update";
        public const string Delete = "Students.Delete";
    }

    public static class AttendanceLogs
    {
        public const string Create = "AttendanceLogs.Create";
        public const string Read = "AttendanceLogs.Read";
    }

    public static class Branches
    {
        public const string Create = "Branches.Create";
        public const string Read = "Branches.Read";
        public const string Update = "Branches.Update";
        public const string Delete = "Branches.Delete";
    }

    public static class AcademicStages
    {
        public const string Create = "AcademicStages.Create";
        public const string Read = "AcademicStages.Read";
        public const string Update = "AcademicStages.Update";
    }

    public static class AcademicYears
    {
        public const string Create = "AcademicYears.Create";
        public const string Read = "AcademicYears.Read";
        public const string Update = "AcademicYears.Update";
    }

    public static class AddOnCatalogs
    {
        public const string Create = "AddOnCatalogs.Create";
        public const string Read = "AddOnCatalogs.Read";
        public const string Update = "AddOnCatalogs.Update";
    }

    public static class TenantAddOns
    {
        public const string Create = "TenantAddOns.Create";
        public const string Read = "TenantAddOns.Read";
        public const string Update = "TenantAddOns.Update";
    }

    public static class TenantLimitOverrides
    {
        public const string Create = "TenantLimitOverrides.Create";
        public const string Read = "TenantLimitOverrides.Read";
    }

    public static class TenantReferralCodes
    {
        public const string Create = "TenantReferralCodes.Create";
        public const string Read = "TenantReferralCodes.Read";
    }

    public static class TenantReferrals
    {
        public const string Create = "TenantReferrals.Create";
        public const string Read = "TenantReferrals.Read";
    }

    public static class TenantProvisioningJobs
    {
        public const string Create = "TenantProvisioningJobs.Create";
        public const string Read = "TenantProvisioningJobs.Read";
        public const string Update = "TenantProvisioningJobs.Update";
    }

    public static class PlatformUsers
    {
        public const string Create = "PlatformUsers.Create";
        public const string Read = "PlatformUsers.Read";
        public const string Update = "PlatformUsers.Update";
        public const string Delete = "PlatformUsers.Delete";
    }

    public static class PlatformRoles
    {
        public const string Create = "PlatformRoles.Create";
        public const string Read = "PlatformRoles.Read";
        public const string Update = "PlatformRoles.Update";
        public const string Delete = "PlatformRoles.Delete";
    }

    public static class PlatformPermissions
    {
        public const string Read = "PlatformPermissions.Read";
    }

    public static class Invoices
    {
        public const string Create = "Invoices.Create";
        public const string Read = "Invoices.Read";
        public const string Update = "Invoices.Update";
        public const string Delete = "Invoices.Delete";
    }

    public static class TenantCredits
    {
        public const string Create = "TenantCredits.Create";
        public const string Read = "TenantCredits.Read";
    }

    /// <summary>
    /// All permission codes registered in the canonical catalog (see <see cref="PermissionCatalog"/>).
    /// </summary>
    public static string[] GetAll() => PermissionCatalog.All.Select(e => e.Code).ToArray();

    public static string[] GetPlatformAdminPermissions() => GetAll();

    public static string[] GetTenantAdminPermissions() =>
    [
        TenantPlans.Create, TenantPlans.Read, TenantPlans.Update, TenantPlans.Delete,
        TenantCRMLeads.Create, TenantCRMLeads.Read, TenantCRMLeads.Update, TenantCRMLeads.Delete,
    ];

    public static string[] GetTenantUserPermissions() =>
    [
        TenantPlans.Read,
        TenantCRMLeads.Read,
    ];
}
