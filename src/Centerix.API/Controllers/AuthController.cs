using Centerix.API.Infrastructure;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Authentication;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class AuthController(
    UserManager<IdentityUser> userManager,
    ITokenService tokenService,
    IRefreshTokenService refreshTokenService,
    ILocalizer localizer) : ApiController(localizer)
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("LoginPolicy")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            // Timing / user-enumeration hardening. A miss used to answer 401 immediately while a
            // hit paid for a PBKDF2 verification first - a gap of tens of milliseconds, which is
            // more than enough to enumerate registered addresses from outside. Run the SAME
            // password-hashing work (same hasher, same options) on the miss path so both branches
            // cost the same, then return the identical response.
            _ = userManager.PasswordHasher.HashPassword(
                new IdentityUser(), request.Password ?? string.Empty);

            // Don't reveal that user doesn't exist
            return Unauthorized(new
            {
                error = localizer.Translate("Auth:InvalidCredentials")
            });
        }

        // F2: lockout enforcement WITHOUT lock-state disclosure. The response for a locked
        // account is byte-identical to every other credential failure, so login can never be
        // used to confirm that an address exists, to learn that an account is locked, or to
        // observe how close it is to its threshold (the previous 429 + lockoutRemainingMinutes
        // answers did all three).
        //
        // Order matters:
        //  1. CheckPasswordAsync first so a locked account pays the SAME PBKDF2 cost as an
        //     unknown-email dummy hash (constant-cost response, no timing oracle), and because
        //     it never mutates lockout state.
        //  2. The lockout gate runs BEFORE the failure branch, so a wrong password against an
        //     already-locked account never reaches AccessFailedAsync — a locked account's
        //     remaining lock time can NOT be extended by hammering it.
        //  3. A failure that TRIGGERS the lockout (6th failure) still returns the plain 401:
        //     revealing the threshold by switching to 429 mid-sequence is itself enumeration.
        var passwordValid = await userManager.CheckPasswordAsync(user, request.Password);

        if (await userManager.IsLockedOutAsync(user))
        {
            return Unauthorized(new
            {
                error = localizer.Translate("Auth:InvalidCredentials")
            });
        }

        if (!passwordValid)
        {
            // Increment failed access count (no-op when the store lacks lockout support).
            await userManager.AccessFailedAsync(user);

            return Unauthorized(new
            {
                error = localizer.Translate("Auth:InvalidCredentials")
            });
        }

        // Reset failed access count on successful login
        await userManager.ResetAccessFailedCountAsync(user);

        // NEW-1 correction (A): enforce the password-change requirement AT LOGIN. The
        // authoritative state is loaded from the database (Identity claims) — the JWT
        // carries nothing yet and is never trusted for this decision. When the
        // requirement is set, NO refresh token is issued, NO normal session is created,
        // and NO normal login token pair is returned. The caller gets the controlled
        // password-change flow contract: a short-lived purpose-restricted token that is
        // valid ONLY for POST /api/auth/change-password and contains NO refresh token.
        var requirementClaims = await userManager.GetClaimsAsync(user);
        var changeRequired = requirementClaims.Any(c =>
            string.Equals(c.Type, PasswordChangeRequiredClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase));

        if (changeRequired)
        {
            var changePasswordToken = tokenService.GeneratePasswordChangeToken(user);
            return StatusCode(StatusCodes.Status403Forbidden, new PasswordChangeRequiredResponse
            {
                Error = localizer.Translate("Auth:PasswordChangeRequired"),
                ChangePasswordToken = changePasswordToken
            });
        }

        var roles = await userManager.GetRolesAsync(user);

        // Tenant-specific permissions are now resolved per-request via TenantPermissionResolver,
        // NOT embedded in the JWT. This ensures permissions cannot leak between tenants.
        var accessToken = tokenService.GenerateAccessToken(user, roles);
        var refreshToken = await refreshTokenService.IssueAsync(
            userId: user.Id,
            deviceInfo: Request.Headers.UserAgent.ToString(),
            ipAddress: ClientIp.Normalize(Request.HttpContext.Connection.RemoteIpAddress));

        return Ok(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            Email = user.Email,
            Roles = roles.ToList()
        });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("RefreshPolicy")]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        var result = await refreshTokenService.RotateAsync(
            refreshToken: request.RefreshToken,
            deviceInfo: Request.Headers.UserAgent.ToString(),
            ipAddress: ClientIp.Normalize(Request.HttpContext.Connection.RemoteIpAddress));

        if (!result.IsSuccess)
        {
            // A conflict means the rotation was either lost to a concurrent request or rolled
            // back, and in BOTH cases the server refused to issue anything. The client must not
            // treat this as "my token is dead": the successor it needs may already be in shared
            // storage (two-tab browser), or the conflict may be transient (deadlock). 409 is the
            // honest signal - re-sync / retry - whereas 401 would tell the client to discard a
            // credential it may still hold and force a needless re-login.
            var isConflict = result.Errors?.Any(
                e => e.Code == RefreshTokenErrors.RotationConflict.Code) ?? false;

            if (isConflict)
            {
                return Conflict(new { error = localizer.Translate("Auth:RefreshConflict") });
            }

            // NEW-1 correction (B): the authoritative password-change requirement still stands.
            // Nothing was minted. 403 directs the client into the controlled flow instead of
            // discarding credentials (401) or retrying a rotation that can never succeed.
            var isChangeRequired = result.Errors?.Any(
                e => e.Code == RefreshTokenErrors.PasswordChangeRequired.Code) ?? false;

            if (isChangeRequired)
            {
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = localizer.Translate("Auth:PasswordChangeRequired") });
            }

            return Unauthorized(new { error = localizer.Translate("Auth:InvalidRefreshToken") });
        }

        return Ok(new RefreshResponse
        {
            AccessToken = result.Value.AccessToken,
            RefreshToken = result.Value.RefreshToken
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(LogoutRequest request)
    {
        await refreshTokenService.RevokeAsync(request.RefreshToken);
        return NoContent();
    }

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await refreshTokenService.RevokeAllAsync(userId);
        return NoContent();
    }

    /// <summary>
    /// NEW-1 self-only password rotation (correction contract).
    /// 1. The caller is authenticated (Authorize) and ONLY the caller's own UserId —
    ///    resolved server-side from <see cref="ClaimTypes.NameIdentifier"/> — is ever used.
    ///    No target UserId is accepted from body, query, route, or headers.
    /// 2. The current password is validated; on credential failure the existing Identity
    ///    lockout policy applies (<c>AccessFailedAsync</c>) and NOTHING else happens: no
    ///    claim cleared, no sessions revoked, no tokens issued.
    /// 3. ONLY after a successful change is <c>password.change_required</c> cleared in the
    ///    database and are existing refresh sessions revoked.
    /// 4. The response is the fresh NORMAL token pair so the user continues normally.
    /// Tenant-independent: reachable by the bootstrap/root Platform user without any
    /// tenant membership (see TenantGuardMiddleware bypass).
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        // Strictly self-only: server-resolved identity, nothing from the request identifies
        // the target user.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { error = localizer.Translate("Auth:InvalidPasswordChangeRequest") });

        // NEW-1 correction (D): a locked-out account cannot rotate even with a live access
        // token. Answered indistinguishably (401) so lockout state is not disclosed.
        if (userManager.SupportsUserLockout && await userManager.IsLockedOutAsync(user))
            return Unauthorized(new { error = localizer.Translate("Auth:InvalidCredentials") });

        var changeResult = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!changeResult.Succeeded)
        {
            // NEW-1 correction (D): credential failure feeds the existing Identity lockout
            // policy. A failed attempt changes NOTHING else: the requirement claim and all
            // refresh sessions stay exactly as they were, and no tokens are issued.
            await userManager.AccessFailedAsync(user);
            return BadRequest(new
            {
                error = localizer.Translate("Auth:PasswordChangeFailed"),
                details = changeResult.Errors.Select(e => e.Description).ToList()
            });
        }

        // Successful rotation only: clear the authoritative requirement claim from the DB...
        var claims = await userManager.GetClaimsAsync(user);
        var requirementClaims = claims
            .Where(c => string.Equals(c.Type, PasswordChangeRequiredClaimType, StringComparison.Ordinal))
            .ToList();
        foreach (var claim in requirementClaims)
            await userManager.RemoveClaimAsync(user, claim);

        // ...revoke every pre-existing refresh session...
        await refreshTokenService.RevokeAllAsync(userId);

        // ...reset the lockout counter so a previously-failing user starts clean...
        await userManager.ResetAccessFailedCountAsync(user);

        // ...and issue the fresh NORMAL token pair so the user can continue normally.
        var roles = await userManager.GetRolesAsync(user);
        var accessToken = tokenService.GenerateAccessToken(user, roles);
        var refreshToken = await refreshTokenService.IssueAsync(
            userId: user.Id,
            deviceInfo: Request.Headers.UserAgent.ToString(),
            ipAddress: ClientIp.Normalize(Request.HttpContext.Connection.RemoteIpAddress));

        return Ok(new ChangePasswordResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken
        });
    }

    private const string PasswordChangeRequiredClaimType = "password.change_required";
}

public record LoginRequest(string Email, string Password);

public record LoginResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public string? Email { get; init; }
    public List<string> Roles { get; init; } = [];
}

public record RefreshRequest(string RefreshToken);

public record RefreshResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
}

public record LogoutRequest(string RefreshToken);

/// <summary>Self-only rotation payload. Carries NO user identifier by design.</summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// NEW-1 controlled password-change flow contract: returned (HTTP 403) by login when the
/// authoritative database state requires a password change. Contains NO refresh token and
/// NO normal session — only a short-lived purpose-restricted token for the change endpoint.
/// </summary>
public record PasswordChangeRequiredResponse
{
    public string Error { get; init; } = string.Empty;
    public string ChangePasswordToken { get; init; } = string.Empty;
}

/// <summary>
/// NEW-1 successful rotation contract: the fresh normal token pair issued ONLY after a
/// successful password change, so the user can continue normally.
/// </summary>
public record ChangePasswordResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
}
