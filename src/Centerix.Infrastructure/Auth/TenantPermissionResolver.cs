namespace Centerix.Infrastructure.Auth;

using System.Security.Claims;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves tenant-scoped permissions for the current user by querying:
/// TenantMembership → RoleName → ApplicationRole → RolePermission → Permission.
/// This replaces the previous JWT-embedded permission model with a per-request
/// resolution that respects tenant isolation.
/// </summary>
public class TenantPermissionResolver(
    AppDbContext dbContext,
    ICurrentTenant currentTenant,
    IHttpContextAccessor httpContextAccessor,
    ILogger<TenantPermissionResolver> logger) : ITenantPermissionResolver
{
    public async Task<IReadOnlyList<string>> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAuthorized || string.IsNullOrEmpty(currentTenant.TenantId))
        {
            return Array.Empty<string>();
        }

        // Extract user ID from HTTP context claims directly to avoid circular dependency on ICurrentUser
        var userId = httpContextAccessor.HttpContext?.User?
            .FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Array.Empty<string>();
        }

        // Find the user's active membership in the current tenant
        var membership = await dbContext.TenantMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.UserId == userId
                  && m.TenantId == currentTenant.TenantId
                  && m.Status == TenantMembershipStatus.Active,
                cancellationToken);

        if (membership is null)
        {
            logger.LogDebug("No active membership found for user {UserId} in tenant {TenantId}",
                userId, currentTenant.TenantId);
            return Array.Empty<string>();
        }

        // Find the role by name
        var role = await dbContext.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name == membership.RoleName, cancellationToken);

        if (role is null)
        {
            logger.LogWarning("Role {RoleName} not found for membership of user {UserId} in tenant {TenantId}",
                membership.RoleName, userId, currentTenant.TenantId);
            return Array.Empty<string>();
        }

        // Resolve permissions from RolePermission → Permission
        var permissions = await (
            from rp in dbContext.RolePermissions.AsNoTracking()
            join p in dbContext.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
            where rp.RoleId == role.Id
            select p.Code
        ).Distinct().ToListAsync(cancellationToken);

        return permissions;
    }
}

/// <summary>
/// Interface for resolving tenant-scoped permissions.
/// </summary>
public interface ITenantPermissionResolver
{
    Task<IReadOnlyList<string>> ResolveAsync(CancellationToken cancellationToken = default);
}
