using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.API.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;

namespace Centerix.API.Infrastructure;

public class TenantGuardMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> PlatformAdminRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/plans",
        "/api/features",
        "/api/tenants"
    };

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, ICurrentTenant currentTenant)
    {
        if (currentUser.IsPlatformAdmin || !currentTenant.IsResolved)
        {
            await next(context);
            return;
        }

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
}