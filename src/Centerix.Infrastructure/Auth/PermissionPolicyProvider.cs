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
/// SECURITY BOUNDARY (T22 final correction). The permission SCOPE is resolved FIRST, through the
/// single authoritative <see cref="PermissionScopes"/> classifier, and the scope then decides which
/// — and only which — grant sources may authorize the request:
/// </para>
/// <list type="bullet">
///   <item><description><b>Platform scope</b>: the ONLY acceptable grant is
///   <see cref="IPlatformAdminVerifier"/>, whose decision is re-validated against the server-side
///   Identity store. The PlatformAdmin verifier is consulted INSIDE the platform-scope branch —
///   never before scope is known — so the bypass can never apply to a Tenant or Unknown scope.
///   There is NO tenant fallback for platform permissions: a <c>TenantMembership</c>, a tenant
///   role, a tenant <c>RolePermission</c> row or a pre-computed permission list can never
///   authorize them, even if the database is misconfigured or corrupted.</description></item>
///   <item><description><b>Tenant scope</b>: requires an AUTHORIZED tenant context
///   (<see cref="ICurrentTenant.IsAuthorized"/>), never merely a resolved one, and then evaluates
///   TenantMembership → Role → RolePermission. <b>The PlatformAdmin bypass does not apply to tenant
///   scope</b>: a verified PlatformAdmin who has no active membership in the resolved tenant is
///   denied for any tenant-scoped permission, exactly as any other unauthenticated-for-this-tenant
///   principal would be.</description></item>
///   <item><description><b>Unknown scope</b>: DENIED. An unclassifiable permission is a
///   configuration fault and must never widen access — neither the PlatformAdmin verifier nor any
///   tenant-derived source is consulted.</description></item>
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
        // consulted. From this point on, no PlatformAdmin check, no tenant fallback, no DB read
        // may produce a different scope. This ordering is the corrected trust boundary; the
        // PlatformAdmin bypass used to live here (before scope was known) and would therefore widen
        // access across BOTH platform and tenant scopes.
        var scope = PermissionScopes.Resolve(requirement.Permission);

        // STEP 2 — PLATFORM scope: the only acceptable grant is the authoritative PlatformAdmin
        // decision. The verifier is consulted HERE — inside the platform branch — so a Tenant or
        // Unknown scope cannot be widened by a verified PlatformAdmin.
        if (scope == PermissionScope.Platform)
        {
            // PlatformAdmin bypass is DB-VERIFIED (T22): the JWT role claim alone is never sufficient.
            // Revocation/lockout takes effect immediately; a forged claim without a matching
            // identity-store role is denied.
            var isPlatformAdmin = await platformAdminVerifier.IsPlatformAdminAsync(
                context.User, cancellationToken);

            if (isPlatformAdmin)
            {
                context.Succeed(requirement);
                return;
            }

            _logger.LogWarning(
                "Denied platform-scoped permission '{Permission}' for user {UserId}: " +
                "tenant-derived permission sources are not valid for platform scope.",
                requirement.Permission,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return;
        }

        // STEP 3 — UNKNOWN scope: fail closed. An unclassifiable permission (not in the canonical
        // catalog and not explicitly platform-scoped, OR an entry whose explicit scope is Unknown)
        // is never authorized from any source. The PlatformAdmin verifier is intentionally NOT
        // called here: scope must not be widened by an unrelated grant decision.
        if (scope == PermissionScope.Unknown)
        {
            _logger.LogWarning(
                "Denied permission '{Permission}' for user {UserId}: unknown permission scope (fail-closed).",
                requirement.Permission,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return;
        }

        // ---- TENANT scope from here on. The PlatformAdmin bypass DOES NOT APPLY. ----

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
        // tenant context. A verified PlatformAdmin with IsAuthorized == false is therefore
        // denied at this exact step — the corrected trust boundary.
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
