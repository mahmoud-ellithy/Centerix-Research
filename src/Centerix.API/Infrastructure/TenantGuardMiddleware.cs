using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Centerix.API.Infrastructure;

public class TenantGuardMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> BypassPathPrefixes =
        ["/scalar", "/openapi", "/swagger"];

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser currentUser,
        ICurrentTenant currentTenant,
        IAppDbContext dbContext)
    {
        // Documentation / tooling endpoints are not part of the authenticated API surface.
        if (IsBypassPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        // The authorization middleware (UseAuthorization) already rejected unauthenticated
        // requests to protected endpoints. If we still see an unauthenticated caller here, the
        // endpoint was explicitly anonymous (e.g. /api/auth/login, /api/auth/refresh). Those
        // endpoints bootstrap identity and have no tenant to authorize, so let them through.
        if (!currentUser.IsAuthenticated)
        {
            await next(context);
            return;
        }

        // Classify the request by its required permission. Platform-scoped operations act on
        // cross-tenant platform resources and are authorized through platform permissions alone;
        // they must NOT require a tenant membership and must NOT establish a tenant-scoped
        // data context. Everything else is tenant-scoped.
        var isPlatformScoped = IsPlatformScopedRequest(context);

        if (isPlatformScoped)
        {
            // No tenant context is established: tenant-partitioned queries must not be silently
            // filtered (or widened) by a stray tenant header. Platform handlers operate on
            // platform-level entities that are not tenant-scoped. The required platform permission
            // itself is enforced downstream by the [HasPermission] authorization policy.
            await next(context);
            return;
        }

        // ---- TENANT-SCOPED REQUEST ----

        // A tenant-scoped request requires an explicitly resolved tenant. Selection (a client
        // supplied header/host) is NOT authorization. Having a resolved tenant is necessary but
        // not sufficient — the membership and lifecycle checks below are what authorize access.
        if (!currentTenant.IsResolved)
        {
            await WriteForbidden(context,
                "Tenant context is required for this request.");
            return;
        }

        // C1 guard: the resolved tenant must correspond to a tenant the authenticated user is an
        // ACTIVE member of. TenantMembership is intentionally not IHasTenantId, so this query is
        // not filtered by the resolved tenant and reflects the user's true memberships. This check
        // applies to every caller — including platform administrators — so a global role never
        // becomes an unrestricted cross-tenant bypass.
        if (!string.IsNullOrEmpty(currentUser.UserId))
        {
            var isActiveMember = await dbContext.TenantMemberships
                .AnyAsync(m => m.UserId == currentUser.UserId
                            && m.TenantId == currentTenant.ResolvedTenantId
                            && m.Status == TenantMembershipStatus.Active,
                    context.RequestAborted);

            if (!isActiveMember)
            {
                await WriteForbidden(context,
                    "You are not an active member of the requested tenant.");
                return;
            }
        }

        // Establish the VERIFIED tenant context. From this point on, AppDbContext filtering, the
        // TenantInterceptor, tenant-aware authorization and services all read ICurrentTenant.TenantId
        // (the authorized tenant), never the raw client-resolved value.
        currentTenant.AuthorizeTenant();

        // Tenant operational status. Resolved + a valid membership still do not grant access if the
        // tenant itself is not operational. These checks are an additional gate, NOT a substitute for
        // the membership verification above.
        var localizer = context.RequestServices.GetRequiredService<ILocalizer>();

        if (!currentTenant.IsActive)
        {
            await WriteForbidden(context,
                localizer.Translate("Error:TenantDeactivated"),
                localizer.Translate("Error:TenantDeactivatedDetail"));
            return;
        }

        if (currentTenant.ValidUpTo < DateTime.UtcNow)
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
