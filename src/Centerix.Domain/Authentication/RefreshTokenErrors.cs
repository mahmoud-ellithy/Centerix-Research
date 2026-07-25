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

    public static Error AlreadyRevoked =>
        Error.Validation("RefreshToken.AlreadyRevoked", "Refresh token is already revoked");
}
