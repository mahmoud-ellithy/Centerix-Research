namespace Centerix.Application.Common.Interfaces;

public interface ICurrentUser
{
    string UserId { get; }
    string UserName { get; }
    bool IsAuthenticated { get; }
    bool IsPlatformAdmin { get; }
    IEnumerable<string> Roles { get; }

    /// <summary>
    /// Permissions resolved for the current tenant context. Empty until tenant is authorized.
    /// Resolved from: Membership → TenantRole → RolePermission → Permission.
    /// </summary>
    IEnumerable<string> TenantPermissions { get; }

    /// <summary>
    /// Loads tenant-scoped permissions for the current user and tenant context.
    /// Must be called after the tenant context has been authorized.
    /// </summary>
    Task LoadTenantPermissionsAsync(CancellationToken cancellationToken = default);
}