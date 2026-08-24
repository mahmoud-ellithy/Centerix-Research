using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Json;

namespace Centerix.API.Infrastructure;

public class TenantGuardMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> BypassPathPrefixes =
        ["/scalar", "/openapi", "/swagger"];

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentTenant currentTenant,
        IAppDbContext dbContext)
    {
        if (IsBypassPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            await next(context);
            return;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        var isPlatformScoped = IsPlatformScopedRequest(context);

        if (isPlatformScoped)
        {
            await next(context);
            return;
        }

        // Invitation consumption endpoints are token-capability flows: the invitee BY DEFINITION
        // holds no TenantMembership for the target tenant yet — acceptance is what CREATES it.
        // Authorization is enforced instead by (a) authentication + e-mail binding against the
        // authenticated principal (/accept) and (b) the SHA-256-hashed capability token with
        // status/expiry validation (both handlers). No tenant-scoped data is touched by these two
        // handlers, so waiving the membership precondition does not weaken tenant isolation.
        if (IsInvitationConsumptionEndpoint(context))
        {
            await next(context);
            return;
        }

        if (!currentTenant.IsResolved)
        {
            await WriteForbidden(context,
                $"[GUARD] Tenant not resolved. Path={context.Request.Path}");
            return;
        }

        if (!string.IsNullOrEmpty(userId))
        {
            var isActiveMember = await dbContext.TenantMemberships
                .AnyAsync(m => m.UserId == userId
                            && m.TenantId == currentTenant.ResolvedTenantId
                            && m.Status == TenantMembershipStatus.Active,
                    context.RequestAborted);

            if (!isActiveMember)
            {
                await WriteForbidden(context,
                    $"[GUARD] Not active member. userId={userId}, tenantId={currentTenant.ResolvedTenantId}");
                return;
            }
        }

        currentTenant.AuthorizeTenant();

        try
        {
            var permissions = await ResolveTenantPermissionsAsync(
                dbContext, userId, currentTenant.TenantId, context.RequestAborted);

            if (permissions.Count > 0)
            {
                context.Items["TenantPermissions"] = permissions;
            }
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<TenantGuardMiddleware>();
            logger?.LogWarning(ex, "Failed to load tenant permissions for user {UserId}", userId);
        }

        var localizer = context.RequestServices.GetRequiredService<ILocalizer>();

        if (!currentTenant.IsActive)
        {
            await WriteForbidden(context,
                localizer.Translate("Error:TenantDeactivated"),
                localizer.Translate("Error:TenantDeactivatedDetail"));
            return;
        }

        // Expiry rule (explicit): a tenant with no configured expiry (ValidUpTo == null) is never
        // blocked; a configured expiry in the past yields 402 Payment Required.
        if (currentTenant.ValidUpTo is { } validUpTo && validUpTo < DateTime.UtcNow)
        {
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.2",
                title = localizer.Translate("Error:TenantExpired"),
                status = StatusCodes.Status402PaymentRequired,
                detail = localizer.Translate("Error:TenantExpiredDetail")
            }));
            return;
        }

        await next(context);
    }

    private static async Task<List<string>> ResolveTenantPermissionsAsync(
        IAppDbContext dbContext,
        string userId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        // Find the user's active membership in the current tenant
        var membership = await dbContext.TenantMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.UserId == userId
                  && m.TenantId == tenantId
                  && m.Status == TenantMembershipStatus.Active,
                cancellationToken);

        if (membership is null)
            return [];

        // Find the role by name via Identity's Roles table
        // AppDbContext inherits IdentityDbContext which has the Roles DbSet
        var identityContext = (Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext)dbContext;
        var role = await identityContext.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.NormalizedName == membership.RoleName.ToUpperInvariant(), cancellationToken);

        if (role is null)
            return [];

        // Resolve permissions from RolePermission → Permission
        return await (
            from rp in dbContext.RolePermissions.AsNoTracking()
            join p in dbContext.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
            where rp.RoleId == role.Id
            select p.Code
        ).Distinct().ToListAsync(cancellationToken);
    }

    private static bool IsPlatformScopedRequest(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        var permission = endpoint.Metadata
            .GetOrderedMetadata<HasPermissionAttribute>()
            .FirstOrDefault()?
            .Permission;

        return Permissions.PlatformScope.IsPlatformScoped(permission);
    }

    /// <summary>
    /// Matches exactly the two invitation consumption endpoints:
    ///   POST /api/invitations/register           (anonymous; token = capability)
    ///   POST /api/invitations/{token}/accept     (authenticated; e-mail must match principal)
    /// Everything else under /api/invitations (create/list/revoke) still requires an active
    /// TenantMembership and permission, as before.
    /// </summary>
    private static bool IsInvitationConsumptionEndpoint(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/invitations/register", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            ?? [];

        return segments.Length == 4
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("invitations", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("accept", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteForbidden(HttpContext context, string title, string? detail = null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
            title,
            status = StatusCodes.Status403Forbidden,
            detail = detail ?? title
        }));
    }

    private static bool IsBypassPath(PathString path)
    {
        foreach (var prefix in BypassPathPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
