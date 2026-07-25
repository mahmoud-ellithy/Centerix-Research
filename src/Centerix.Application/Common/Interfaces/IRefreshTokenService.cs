namespace Centerix.Application.Common.Interfaces;

using Centerix.Domain.Common.Results;

/// <summary>Access + refresh token pair returned to clients on login/refresh.</summary>
public record TokenPair(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAtUtc, DateTime RefreshTokenExpiresAtUtc);

/// <summary>
/// Issues, rotates, and revokes refresh tokens. A refresh consumes the presented
/// token and issues a new access+refresh pair (rotation with reuse detection).
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>Issue a brand-new refresh token for a user (e.g. on login).</summary>
    Task<string> IssueAsync(
        string userId,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Consume <paramref name="refreshToken"/> and issue a new access+refresh pair.
    /// Reuse of the consumed token will revoke the entire chain (reuse detection).
    /// </summary>
    Task<Result<TokenPair>> RotateAsync(
        string refreshToken,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Revoke a specific refresh token (logout this device).</summary>
    Task<Result<Updated>> RevokeAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revoke all refresh tokens for a user (logout everywhere).</summary>
    Task<Result<Updated>> RevokeAllAsync(string userId, CancellationToken cancellationToken = default);
}
