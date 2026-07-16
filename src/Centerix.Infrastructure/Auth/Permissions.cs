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

    public static class TenantBillings
    {
        public const string Create = "TenantBillings.Create";
        public const string Read = "TenantBillings.Read";
        public const string Update = "TenantBillings.Update";
        public const string Delete = "TenantBillings.Delete";
    }

    public static class TenantCRMLeads
    {
        public const string Create = "TenantCRMLeads.Create";
        public const string Read = "TenantCRMLeads.Read";
        public const string Update = "TenantCRMLeads.Update";
        public const string Delete = "TenantCRMLeads.Delete";
    }

    public static string[] GetAll() =>
    [
        Plans.Create, Plans.Read, Plans.Update, Plans.Delete,
        Features.Create, Features.Read, Features.Update, Features.Delete,
        Tenants.Create, Tenants.Read, Tenants.Update, Tenants.Delete,
        TenantPlans.Create, TenantPlans.Read, TenantPlans.Update, TenantPlans.Delete,
        TenantBillings.Create, TenantBillings.Read, TenantBillings.Update, TenantBillings.Delete,
        TenantCRMLeads.Create, TenantCRMLeads.Read, TenantCRMLeads.Update, TenantCRMLeads.Delete,
    ];

    public static string[] GetPlatformAdminPermissions() => GetAll();

    public static string[] GetTenantAdminPermissions() =>
    [
        TenantPlans.Create, TenantPlans.Read, TenantPlans.Update, TenantPlans.Delete,
        TenantBillings.Create, TenantBillings.Read, TenantBillings.Update, TenantBillings.Delete,
        TenantCRMLeads.Create, TenantCRMLeads.Read, TenantCRMLeads.Update, TenantCRMLeads.Delete,
    ];

    public static string[] GetTenantUserPermissions() =>
    [
        TenantPlans.Read,
        TenantBillings.Read,
        TenantCRMLeads.Read,
    ];
}
