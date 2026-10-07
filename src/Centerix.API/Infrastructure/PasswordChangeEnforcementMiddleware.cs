using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace Centerix.API.Infrastructure;

/// <summary>
/// NEW-1 password.change_required enforcement (correction).
/// Two independent, server-authoritative gates evaluated in strict order:
/// Gate 1 (evaluated FIRST, before any exemption): purpose-restricted flow tokens
/// (JWT claim <c>pwd_change_only=true</c>, issued ONLY by the login gate while the
/// requirement stands) are valid for exactly one endpoint: POST /api/auth/change-password.
/// Anywhere else they are rejected with 403. This check takes precedence over AllowAnonymous
/// so a flow token can never reach login, refresh, register, invitation, or any other
/// anonymous endpoint.
/// Gate 2 (evaluated after exemptions): when the authoritative database state (Identity
/// user claims, loaded per request via <see cref="UserManager{TUser}"/> — NEVER trusted
/// from the JWT) still carries <c>password.change_required=true</c>, every authenticated
/// request except the allow-list below is rejected with 403 <c>Auth:PasswordChangeRequired</c>.
/// The allow-list keeps the credential-rotation flow usable: login/refresh (anonymous),
/// POST /api/auth/change-password (to clear the requirement), logout endpoints (to abandon
/// sessions), anonymous invitation registration, and docs.
/// </summary>
public sealed class PasswordChangeEnforcementMiddleware(RequestDelegate next)
{
    public const string ChangeRequiredClaimType = "password.change_required";

    public async Task InvokeAsync(HttpContext context, UserManager<IdentityUser> userManager)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Gate 1: a purpose-restricted flow token outside its single endpoint is rejected
        // BEFORE any exemption (including AllowAnonymous) is evaluated. The purpose-token
        // restriction takes precedence over the generic AllowAnonymous exemption so that
        // a password-change-only token can never reach login, refresh, register, invitation,
        // or any other anonymous endpoint.
        if (IsPasswordChangeOnlyToken(context.User) && !IsChangePasswordEndpoint(context))
        {
            await WriteForbidden(context,
                "This token is restricted to POST /api/auth/change-password.");
            return;
        }

        if (IsExempt(context))
        {
            await next(context);
            return;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            await next(context);
            return;
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            await next(context);
            return;
        }

        // Gate 2: authoritative state comes from the database, never from JWT claims.
        var claims = await userManager.GetClaimsAsync(user);
        var changeRequired = claims.Any(c =>
            string.Equals(c.Type, ChangeRequiredClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase));

        if (!changeRequired)
        {
            await next(context);
            return;
        }

        await WriteForbidden(context,
            "Password change is required before this operation. Use POST /api/auth/change-password.");
    }

    private static bool IsPasswordChangeOnlyToken(ClaimsPrincipal principal) =>
        principal.FindFirst(Centerix.Infrastructure.Auth.JwtTokenService.PasswordChangeOnlyClaimType)?.Value == "true";

    private static bool IsChangePasswordEndpoint(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteForbidden(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
            title = "Auth:PasswordChangeRequired",
            status = StatusCodes.Status403Forbidden,
            detail
        }));
    }

    private static bool IsExempt(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AllowAnonymousAttribute>() is not null)
            return true;

        if (IsBypassPath(context.Request.Path))
            return true;

        // POST /api/auth/change-password — the rotation endpoint itself.
        if (HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase))
            return true;

        // Session teardown must stay usable while the requirement is set.
        if (HttpMethods.IsPost(context.Request.Method) &&
            (context.Request.Path.StartsWithSegments("/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/api/auth/logout-all", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static bool IsBypassPath(PathString path)
    {
        return path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase);
    }
}
