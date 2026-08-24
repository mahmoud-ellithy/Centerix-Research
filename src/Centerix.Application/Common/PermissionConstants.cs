namespace Centerix.Application.Common;

/// <summary>
/// Permission code constants used by the Application layer for authorization checks.
/// These mirror the Infrastructure.Auth.Permissions class but are accessible from the Application layer.
/// </summary>
public static class PermissionConstants
{
    public const string ClaimType = "Permission";

    public static class Invitations
    {
        public const string Create = "Invitations.Create";
        public const string Read = "Invitations.Read";
        public const string Revoke = "Invitations.Revoke";
    }

    public static class Memberships
    {
        public const string Read = "Memberships.Read";
        public const string Manage = "Memberships.Manage";
    }

    public static class Students
    {
        public const string Create = "Students.Create";
        public const string Read = "Students.Read";
        public const string Update = "Students.Update";
        public const string Delete = "Students.Delete";
    }

    public static class PlatformScope
    {
        public const string TenantsRead = "Tenants.Read";
        public const string TenantsCreate = "Tenants.Create";
        public const string TenantsUpdate = "Tenants.Update";
        public const string TenantsDelete = "Tenants.Delete";
    }
}
