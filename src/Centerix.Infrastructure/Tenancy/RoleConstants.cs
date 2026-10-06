namespace Centerix.Infrastructure.Tenancy;

using Centerix.Domain.Platform.Tenants;

/// <summary>
/// Well-known ASP.NET Identity role names. The values delegate to
/// <see cref="TenantRoleNames"/> so the Identity role catalog and the domain's tenant/platform
/// role classification can never drift apart (SEC-001).
/// </summary>
public static class RoleConstants
{
    public const string PlatformAdmin = TenantRoleNames.PlatformAdmin;
    public const string TenantAdmin = TenantRoleNames.TenantAdmin;
    public const string TenantUser = TenantRoleNames.TenantUser;
}
