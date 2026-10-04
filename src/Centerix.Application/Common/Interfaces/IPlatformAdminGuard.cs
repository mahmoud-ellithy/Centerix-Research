namespace Centerix.Application.Common.Interfaces;

using Centerix.Domain.Common.Results;

/// <summary>
/// Explicit PLATFORM authorization boundary for commercial operations (tenant approval,
/// plan assignment, subscription management, overrides). Tenant-side permission codes alone are
/// NOT sufficient: every platform workflow handler must call this guard so a tenant admin holding
/// an over-broad tenant permission can never reach platform operations.
/// The decision is delegated to <see cref="IPlatformAdminVerifier"/>, which re-validates the
/// principal against the authoritative identity store — a JWT role claim alone is never enough.
/// </summary>
public interface IPlatformAdminGuard
{
    /// <summary>Success (Updated) when the caller is an authenticated, DB-verified Platform Admin; otherwise Forbidden/Unauthorized.</summary>
    Task<Result<Updated>> EnsurePlatformAdminAsync(CancellationToken cancellationToken = default);
}
