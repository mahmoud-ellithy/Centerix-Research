using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Centerix.Infrastructure.Auth;

/// <summary>
/// Custom authorization policy provider that resolves permission-based policies.
/// Instead of requiring permission claims in the JWT, this provider creates policies
/// with a custom requirement that is handled by <see cref="PermissionAuthorizationHandler"/>.
/// This ensures permissions are resolved per-request from the tenant context, not from the token.
/// </summary>
public class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var policy = await _fallback.GetPolicyAsync(policyName);
        if (policy != null)
            return policy;

        return new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();
    }

    public Task<AuthorizationPolicy?> GetDefaultPolicyAsync()
        => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        => Task.FromResult<AuthorizationPolicy?>(null);
}

/// <summary>
/// Authorization requirement that represents a required permission code.
/// Handled by <see cref="PermissionAuthorizationHandler"/>.
/// </summary>
public class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Authorization handler that checks if the current user has the required permission
/// in the current tenant context. Resolves permissions on-demand from the DB via
/// TenantMembership → RoleName → Role → RolePermission → Permission, reading
/// ICurrentTenant for the current tenant context.
/// </summary>
public class PermissionAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<PermissionAuthorizationHandler> logger) : AuthorizationHandler<PermissionRequirement>
{
    private readonly ILogger<PermissionAuthorizationHandler> _logger = logger;
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var isPlatformAdmin = context.User.IsInRole("PlatformAdmin");
        if (isPlatformAdmin)
        {
            context.Succeed(requirement);
            return;
        }

        // Primary path: read permissions resolved by TenantGuardMiddleware from HttpContext.Items.
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.Items["TenantPermissions"] is IEnumerable<string> permissions)
        {
            if (permissions.Any(p => string.Equals(p, requirement.Permission, StringComparison.OrdinalIgnoreCase)))
            {
                context.Succeed(requirement);
                return;
            }
        }

        // Fallback: DB lookup
        if (httpContext is null)
            return;

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return;

        try
        {
            var scopedServices = httpContext.RequestServices;
            var currentTenant = scopedServices.GetRequiredService<ICurrentTenant>();

            if (!currentTenant.IsResolved || string.IsNullOrEmpty(currentTenant.TenantId))
                return;

            var dbContext = scopedServices.GetRequiredService<IAppDbContext>();
            var tenantId = currentTenant.TenantId;
            var cancellationToken = httpContext.RequestAborted;

            var membership = await dbContext.TenantMemberships
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    m => m.UserId == userId
                      && m.TenantId == tenantId
                      && m.Status == TenantMembershipStatus.Active,
                    cancellationToken);

            if (membership is null)
                return;

            var permissionId = await dbContext.Permissions
                .AsNoTracking()
                .Where(p => p.Code == requirement.Permission)
                .Select(p => p.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (permissionId == 0)
                return;

            var roleManager = scopedServices.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roleManager.FindByNameAsync(membership.RoleName);

            if (role is null)
                return;

            var hasPermission = await dbContext.RolePermissions
                .AsNoTracking()
                .AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permissionId, cancellationToken);

            if (hasPermission)
            {
                context.Succeed(requirement);
            }
        }
        catch (Exception ex)
        {
            // Fail-closed: any error DENIES the permission. It is logged so transient DB faults or
            // authorization misconfiguration are visible in operations instead of being silently
            // swallowed as anonymous denials.
            _logger.LogWarning(ex,
                "Permission resolution failed for '{Permission}' for user {UserId}; access denied (fail-closed)",
                requirement.Permission,
                userId);
        }
    }
}
