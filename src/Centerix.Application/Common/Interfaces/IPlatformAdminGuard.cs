namespace Centerix.Application.Common.Interfaces;

using Centerix.Domain.Common.Results;

/// <summary>
/// Explicit PLATFORM authorization boundary for commercial operations (tenant approval,
/// plan assignment, subscription management, overrides). Tenant-side permission codes alone are
/// NOT sufficient: every platform workflow handler must call this guard so a tenant admin holding
/// an over-broad tenant permission can never reach platform operations.
/// </summary>
public interface IPlatformAdminGuard
{
    /// <summary>Success (Updated) when the caller is an authenticated Platform Admin; otherwise Forbidden/Unauthorized.</summary>
    Result<Updated> EnsurePlatformAdmin();
}
