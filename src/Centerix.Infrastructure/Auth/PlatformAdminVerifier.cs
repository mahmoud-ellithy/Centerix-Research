namespace Centerix.Infrastructure.Auth;

using System.Security.Claims;
using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

/// <summary>
/// Database-backed <see cref="IPlatformAdminVerifier"/>. The PlatformAdmin role claim in the JWT
/// is treated as a NECESSARY but NOT SUFFICIENT hint: the authoritative decision re-validates the
/// principal against the Identity store (AspNetUsers / AspNetUserRoles) so that
/// (a) a forged or tampered token cannot confer PlatformAdmin privileges on its own,
/// (b) revoking the role or locking/disabling the account takes effect immediately, even while
/// an unexpired access token is still circulating, and
/// (c) tenant role/permission assignments can never escalate into PlatformAdmin status, because
/// TenantMembership.RoleName is never consulted here.
/// Scoped: the result is cached once per user per request scope. Failures are NOT cached and are
/// always fail-closed (deny).
/// </summary>
public class PlatformAdminVerifier(
    UserManager<IdentityUser> userManager,
    ILogger<PlatformAdminVerifier> logger) : IPlatformAdminVerifier
{
    private string? _cachedUserId;
    private bool _cachedResult;

    public async Task<bool> IsPlatformAdminAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return false;

        if (!principal.IsInRole(RoleConstants.PlatformAdmin))
            return false;

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return false;

        if (_cachedUserId == userId)
            return _cachedResult;

        try
        {
            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
                return Cache(userId, false);

            if (await userManager.IsLockedOutAsync(user))
                return Cache(userId, false);

            return Cache(userId, await userManager.IsInRoleAsync(user, RoleConstants.PlatformAdmin));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "PlatformAdmin verification failed for user {UserId}; access denied (fail-closed)", userId);
            return false;
        }
    }

    private bool Cache(string userId, bool result)
    {
        _cachedUserId = userId;
        _cachedResult = result;
        return result;
    }
}
