using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Centerix.Infrastructure.Common;

public class CurrentUser(
    IHttpContextAccessor httpContextAccessor,
    ITenantPermissionResolver permissionResolver) : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ITenantPermissionResolver _permissionResolver = permissionResolver;

    private List<string>? _tenantPermissions;

    public string UserId => _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    public string UserName => _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? string.Empty;

    public bool IsAuthenticated => _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;

    public bool IsPlatformAdmin => _httpContextAccessor.HttpContext?.User?.IsInRole("PlatformAdmin") ?? false;

    public IEnumerable<string> Roles => _httpContextAccessor.HttpContext?.User?.Claims
        .Where(c => c.Type == ClaimTypes.Role)
        .Select(c => c.Value) ?? [];

    /// <summary>
    /// Lazily resolves tenant-scoped permissions from the database.
    /// Returns cached permissions for the request lifetime once resolved.
    /// </summary>
    public IEnumerable<string> TenantPermissions
    {
        get
        {
            if (_tenantPermissions is not null)
                return _tenantPermissions;

            if (_httpContextAccessor.HttpContext?.Items["TenantPermissions"] is IEnumerable<string> items)
                return items;

            return [];
        }
    }

    /// <summary>
    /// Loads tenant-scoped permissions for the current user and tenant context.
    /// Must be called after AuthorizeTenant() has been invoked.
    /// </summary>
    public async Task LoadTenantPermissionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _tenantPermissions = (await _permissionResolver.ResolveAsync(cancellationToken)).ToList();
        }
        catch
        {
            // If permission resolution fails, use empty permissions.
            // The PermissionAuthorizationHandler will deny access if permissions are missing.
            _tenantPermissions = [];
        }
    }
}
