namespace Centerix.Application.Common.Interfaces;

using System.Security.Claims;

/// <summary>
/// The SINGLE authoritative answer to "is this authenticated principal currently a PlatformAdmin?".
/// The JWT role claim alone is never sufficient: the effective decision is verified against the
/// server-side identity store (role membership + account state) on every request.
/// All PlatformAdmin authorization decisions (permission bypass, feature bypass, platform guard)
/// MUST go through this service — never through <c>ClaimsPrincipal.IsInRole</c> directly.
/// </summary>
public interface IPlatformAdminVerifier
{
    /// <summary>
    /// Returns true only when the principal carries the PlatformAdmin role claim AND the
    /// authoritative identity store still confirms the account exists, is not locked out,
    /// and still holds the PlatformAdmin role. Any uncertainty (missing claim, unknown user,
    /// revoked role, locked account, lookup failure) returns false (fail-closed).
    /// </summary>
    Task<bool> IsPlatformAdminAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}
