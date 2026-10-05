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

        // Feature policies ("Feature:{code}") gate TENANT commercial entitlements; everything
        // else is treated as a permission code (existing behavior).
        if (policyName.StartsWith("Feature:", StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new FeatureRequirement(policyName["Feature:".Length..]))
                .Build();
        }

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
/// Authorization handler that decides a <see cref="PermissionRequirement"/> across the two
/// authorization domains of the system.
/// <para>
/// SECURITY BOUNDARY (T22 correction). The permission SCOPE is resolved first, through the single
/// authoritative <see cref="PermissionScopes"/> classifier, and the scope then decides which — and
/// only which — grant sources may authorize the request:
/// </para>
/// <list type="bullet">
///   <item><description><b>Platform scope</b>: the ONLY acceptable grant is
///   <see cref="IPlatformAdminVerifier"/>, whose decision is re-validated against the server-side
///   Identity store. There is NO tenant fallback for platform permissions: a
///   <c>TenantMembership</c>, a tenant role, a tenant <c>RolePermission</c> row or a pre-computed
///   permission list can never authorize them, even if the database is misconfigured or corrupted.</description></item>
///   <item><description><b>Tenant scope</b>: requires an AUTHORIZED tenant context
///   (<see cref="ICurrentTenant.IsAuthorized"/>), never merely a resolved one, and then evaluates
///   TenantMembership → Role → RolePermission.</description></item>
///   <item><description><b>Unknown scope</b>: DENIED. An unclassifiable permission is a
///   configuration fault and must never widen access.</description></item>
/// </list>
/// </summary>
public class PermissionAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    IPlatformAdminVerifier platformAdminVerifier,
    ILogger<PermissionAuthorizationHandler> logger) : AuthorizationHandler<PermissionRequirement>
{
    private readonly ILogger<PermissionAuthorizationHandler> _logger = logger;
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var cancellationToken = httpContext?.RequestAborted ?? CancellationToken.None;

        // STEP 1 — the single authoritative scope decision. Resolved BEFORE any grant source is
        // consulted, so no tenant-derived data can influence a platform authorization decision.
        var scope = PermissionScopes.Resolve(requirement.Permission);

        // PlatformAdmin bypass is DB-VERIFIED (T22): the JWT role claim alone is never sufficient.
        // Revocation/lockout takes effect immediately; a forged claim without a matching identity
        // store role is denied. This is the single authoritative PlatformAdmin decision.
        var isPlatformAdmin = await platformAdminVerifier.IsPlatformAdminAsync(
            context.User, cancellationToken);

        if (isPlatformAdmin)
        {
            context.Succeed(requirement);
            return;
        }

        // STEP 2 — PLATFORM scope without an authoritative PlatformAdmin decision: DENY.
        // This is the corrected trust boundary. It holds even when the request carries a tenant
        // header, an authorized tenant context, or a deliberately injected platform RolePermission
        // row: none of those may ever produce platform authorization.
        if (scope == PermissionScope.Platform)
        {
            _logger.LogWarning(
                "Denied platform-scoped permission '{Permission}' for user {UserId}: " +
                "tenant-derived permission sources are not valid for platform scope.",
                requirement.Permission,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return;
        }

        // STEP 3 — UNKNOWN scope: fail closed. An unclassifiable permission (not in the canonical
        // catalog and not explicitly platform-scoped) is never authorized from any source.
        if (scope == PermissionScope.Unknown)
        {
            _logger.LogWarning(
                "Denied permission '{Permission}' for user {UserId}: unknown permission scope (fail-closed).",
                requirement.Permission,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return;
        }

        // ---- TENANT scope from here on. Only authorized tenant context may grant it. ----

        if (httpContext is null)
            return;

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return;

        var scopedServices = httpContext.RequestServices;
        var currentTenant = scopedServices.GetRequiredService<ICurrentTenant>();

        // IsAuthorized — NOT IsResolved. A resolved tenant is only a client-selected input
        // (Finbuckle read it from a request header/host); it proves nothing about whether the
        // authenticated principal may act in that tenant. TenantId stays empty until
        // AuthorizeTenant() is called, so both conditions are required.
        //
        // This is checked for EVERY tenant-scope grant source, including the pre-computed
        // HttpContext.Items list: that list is an optimization populated by the guard, not an
        // independent proof of authorization, so it is never honoured outside an authorized
        // tenant context.
        if (!currentTenant.IsAuthorized || string.IsNullOrEmpty(currentTenant.TenantId))
            return;

        var dbContext = scopedServices.GetRequiredService<IAppDbContext>();

        // Primary path: read permissions resolved by TenantGuardMiddleware from HttpContext.Items.
        if (httpContext.Items["TenantPermissions"] is IEnumerable<string> permissions)
        {
            if (permissions.Any(p => string.Equals(p, requirement.Permission, StringComparison.OrdinalIgnoreCase)))
            {
                context.Succeed(requirement);
                return;
            }
        }

        // Fallback: DB lookup
        try
        {
            var tenantId = currentTenant.TenantId;

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
