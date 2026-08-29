namespace Centerix.Infrastructure.Common;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

/// <summary>
/// Explicit platform authorization boundary. Backed by the PlatformAdmin role claim resolved by
/// authentication; handlers call this so tenant-side permission grants can never reach
/// commercial/platform workflows even if a tenant role is misconfigured with broad permissions.
/// </summary>
public class PlatformAdminGuard(ICurrentUser currentUser) : IPlatformAdminGuard
{
    public Result<Updated> EnsurePlatformAdmin()
    {
        if (!currentUser.IsAuthenticated)
            return Error.Unauthorized("Platform.AdminRequired", "Authentication is required.");

        if (!currentUser.IsPlatformAdmin)
            return Error.Forbidden("Platform.AdminRequired",
                "This operation is restricted to platform administrators.");

        return Result.Updated;
    }
}

