namespace Centerix.Infrastructure.Common;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Explicit platform authorization boundary. Delegates to <see cref="IPlatformAdminVerifier"/>,
/// which backs the decision with the authoritative identity store (role membership + account
/// state), so a stale or forged PlatformAdmin role claim cannot reach commercial/platform
/// workflows — even if a tenant role is misconfigured with broad permissions.
/// </summary>
public class PlatformAdminGuard(
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor,
    IPlatformAdminVerifier platformAdminVerifier) : IPlatformAdminGuard
{
    public async Task<Result<Updated>> EnsurePlatformAdminAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated)
            return Error.Unauthorized("Platform.AdminRequired", "Authentication is required.");

        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is null || !await platformAdminVerifier.IsPlatformAdminAsync(principal, cancellationToken))
        {
            return Error.Forbidden("Platform.AdminRequired",
                "This operation is restricted to platform administrators.");
        }

        return Result.Updated;
    }
}
