using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.API.Localization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;

namespace Centerix.API.Infrastructure;

public class TenantGuardMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> BypassPathPrefixes =
        ["/scalar", "/openapi", "/swagger"];

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, ICurrentTenant currentTenant, IAppDbContext dbContext)
    {
        if (IsBypassPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        // Platform administrators are authorized across tenants by role. Establish the verified
        // tenant context for the resolved tenant (if any) so downstream data access is scoped.
        if (currentUser.IsPlatformAdmin)
        {
            currentTenant.AuthorizeTenant();
            await next(context);
            return;
        }

        if (!currentTenant.IsResolved)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                title = "Forbidden",
                status = StatusCodes.Status403Forbidden,
                detail = "Tenant context is required for this request."
            }));
            return;
        }

        // C1 fix: the RESOLVED tenant (from a client-supplied header/host) must correspond to a
        // tenant the authenticated user is an ACTIVE member of. This check uses ResolvedTenantId
        // (the selection input). TenantMembership is intentionally not IHasTenantId, so the query is
        // not scoped by the resolved tenant and returns the user's true memberships.
        if (currentUser.IsAuthenticated && !string.IsNullOrEmpty(currentUser.UserId))
        {
            var isActiveMember = await dbContext.TenantMemberships
                .AnyAsync(m => m.UserId == currentUser.UserId
                            && m.TenantId == currentTenant.ResolvedTenantId
                            && m.Status == TenantMembershipStatus.Active,
                    context.RequestAborted);

            if (!isActiveMember)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                    title = "Forbidden",
                    status = StatusCodes.Status403Forbidden,
                    detail = "You are not a member of the requested tenant."
                }));
                return;
            }
        }

        // Establish the VERIFIED tenant context. From this point on, AppDbContext filtering,
        // TenantInterceptor, tenant-aware authorization and services all read ICurrentTenant.TenantId
        // (the authorized tenant), never the raw client-resolved value.
        currentTenant.AuthorizeTenant();

        var localizer = context.RequestServices.GetRequiredService<ILocalizer>();

        if (!currentTenant.IsActive)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                title = localizer.Translate("Error:TenantDeactivated"),
                status = StatusCodes.Status403Forbidden,
                detail = localizer.Translate("Error:TenantDeactivatedDetail")
            }));
            return;
        }

        if (currentTenant.ValidUpTo < DateTime.UtcNow)
        {
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
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