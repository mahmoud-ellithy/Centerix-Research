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

    /// <summary>
    /// Classifies a permission (and therefore the endpoint that requires it) as PLATFORM-scoped.
    /// Platform-scoped operations act on cross-tenant platform resources that are NOT tenant-partitioned:
    /// the tenant registry, platform staff/RBAC, and global catalogs. They are authorized through
    /// platform permissions and MUST NOT establish a tenant-scoped data context or require a tenant
    /// membership. Everything not listed here is TENANT-scoped and requires an active
    /// TenantMembership for the resolved tenant before any data access is permitted.
    /// </summary>
    /// <remarks>
    /// This list is the single source of truth for scope classification. It is intentionally
    /// conservative: a permission is only platform-scoped when it unambiguously operates on
    /// platform-level resources rather than a single tenant's partitioned data. Unknown or
    /// missing permissions default to tenant-scoped (fail-closed) in the guard.
    /// </remarks>
    public static class PlatformScope
    {
        public static IReadOnlySet<string> PermissionCodes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Platform staff and RBAC (cross-tenant, managed by the platform itself)
            PlatformUsers.Create, PlatformUsers.Read, PlatformUsers.Update, PlatformUsers.Delete,
            PlatformRoles.Create, PlatformRoles.Read, PlatformRoles.Update, PlatformRoles.Delete,
            PlatformPermissions.Read,

            // Tenant registry management: provisioning, suspension and cancellation of tenants
            Tenants.Create, Tenants.Read, Tenants.Update, Tenants.Delete,

            // Global catalogs shared across every tenant (not a tenant's own data)
            Plans.Create, Plans.Read, Plans.Update, Plans.Delete,
            Features.Create, Features.Read, Features.Update, Features.Delete,
            AddOnCatalogs.Create, AddOnCatalogs.Read, AddOnCatalogs.Update,
        };

        /// <summary>
        /// Permission codes that are intentionally <b>not</b> in <see cref="PermissionCodes"/> even
        /// though their names suggest platform-level work. These operate on <see cref="IHasTenantId"/>
        /// (tenant-partitioned) entities — Invoices, TenantCredits, TenantReferrals,
        /// TenantReferralCodes, TenantProvisioningJobs, TenantPlans, TenantCRMLeads, TenantAddOns,
        /// etc. — so they MUST run inside a verified tenant context and therefore remain
        /// TENANT-scoped in the guard (an active TenantMembership is required). A global role never
        /// grants cross-tenant access to these; that would only be possible via an explicitly
        /// approved platform capability that reads them with the tenant filter bypassed.
        /// </summary>
        public static bool IsPlatformScoped(string? permissionCode) =>
            permissionCode is not null && PermissionCodes.Contains(permissionCode);
    }
}
