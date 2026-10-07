namespace Centerix.Domain.Authentication;

using Centerix.Domain.Common.Results;

public static class RefreshTokenErrors
{
    public static Error UserIdRequired =>
        Error.Validation("RefreshToken.UserId_Required", "User ID is required");

    public static Error TokenHashRequired =>
        Error.Validation("RefreshToken.TokenHash_Required", "Token hash is required");

    public static Error ExpiryInPast =>
        Error.Validation("RefreshToken.Expiry_InPast", "Expiry must be in the future");

    public static Error NotFound =>
        Error.NotFound("RefreshToken.NotFound", "Refresh token was not found or is invalid");

    public static Error Expired =>
        Error.Unauthorized("RefreshToken.Expired", "Refresh token has expired");

    public static Error Revoked =>
        Error.Unauthorized("RefreshToken.Revoked", "Refresh token has been revoked");

    /// <summary>
    /// AUTH-003: the account was locked out after this refresh token was minted. Login re-checks
    /// lockout on every attempt; a refresh must too, otherwise a locked-out account keeps minting
    /// fresh access tokens until the refresh token itself expires.
    /// </summary>
    public static Error AccountLocked =>
        Error.Unauthorized("RefreshToken.AccountLocked", "The account has been locked out");

    public static Error AlreadyRevoked =>
        Error.Validation("RefreshToken.AlreadyRevoked", "Refresh token is already revoked");

    /// <summary>
    /// AUTH-002 race/conflict answer (HTTP 409). Returned when the presented refresh token was
    /// rotated by a concurrent request, or when the rotation transaction lost a lock race.
    /// Nothing is issued and nothing is revoked: the losing request must not be able to destroy
    /// the successor the winner just minted, and it must not be handed that successor either.
    /// </summary>
    public static Error RotationConflict =>
        Error.Conflict(
            "RefreshToken.RotationConflict",
            "This refresh request conflicted with a concurrent request. Please retry.");

    /// <summary>
    /// NEW-1 correction: the account still carries the authoritative
    /// <c>password.change_required</c> requirement (loaded from the database, never the
    /// JWT). Refresh must not bypass it: nothing is minted. Answered as HTTP 403; the
    /// caller must complete POST /api/auth/change-password first.
    /// </summary>
    public static Error PasswordChangeRequired =>
        Error.Forbidden("RefreshToken.PasswordChangeRequired", "Password change is required before refreshing this session");
}
