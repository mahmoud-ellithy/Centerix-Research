namespace Centerix.Infrastructure.Auth;

using System.Security.Cryptography;
using System.Text;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Authentication;
using Centerix.Domain.Common.Results;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Implements refresh token issuance, rotation (with reuse detection), and revocation.
/// Tokens are stored hashed (SHA-256) so a DB leak never exposes live tokens.
/// </summary>
public class RefreshTokenService(
    AppDbContext dbContext,
    ITokenService tokenService,
    UserManager<IdentityUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<JwtSettings> jwtSettings,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    public async Task<string> IssueAsync(
        string userId,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var (token, expiresAt) = tokenService.GenerateRefreshToken();
        var hash = HashToken(token);

        var result = RefreshToken.Create(
            id: Guid.NewGuid(),
            userId: userId,
            tokenHash: hash,
            expiresAtUtc: expiresAt,
            deviceInfo: deviceInfo,
            ipAddress: ipAddress);

        if (!result.IsSuccess)
        {
            logger.LogError("Failed to issue refresh token for user {UserId}: {Errors}", userId, string.Join(", ", result.Errors!.Select(e => e.Code)));
            throw new InvalidOperationException("Failed to issue refresh token.");
        }

        dbContext.RefreshTokens.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        return token;
    }

    public async Task<Result<TokenPair>> RotateAsync(
        string refreshToken,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return RefreshTokenErrors.NotFound;

        var hash = HashToken(refreshToken);
        var stored = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(rt => rt.TokenHash == hash, cancellationToken);

        if (stored is null)
            return RefreshTokenErrors.NotFound;

        // Reuse detection: a token that was already revoked but is being presented again
        // is a replay attempt. Revoke the entire chain for this user.
        if (stored.IsRevoked)
        {
            logger.LogWarning("Reuse of revoked refresh token detected for user {UserId}. Revoking all tokens.", stored.UserId);
            await RevokeAllAsync(stored.UserId, cancellationToken);
            return RefreshTokenErrors.Revoked;
        }

        if (stored.IsExpired)
            return RefreshTokenErrors.Expired;

        var user = await userManager.FindByIdAsync(stored.UserId);
        if (user is null)
            return RefreshTokenErrors.NotFound;

        var roles = await userManager.GetRolesAsync(user);
        var accessToken = tokenService.GenerateAccessToken(user, roles);
        var accessExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationInMinutes);

        // Issue the new refresh token.
        var (newToken, newExpiresAt) = tokenService.GenerateRefreshToken();
        var newHash = HashToken(newToken);

        var newRefresh = RefreshToken.Create(
            id: Guid.NewGuid(),
            userId: stored.UserId,
            tokenHash: newHash,
            expiresAtUtc: newExpiresAt,
            deviceInfo: deviceInfo,
            ipAddress: ipAddress);

        if (!newRefresh.IsSuccess)
        {
            logger.LogError("Failed to mint rotated refresh token for user {UserId}: {Errors}", stored.UserId, string.Join(", ", newRefresh.Errors!.Select(e => e.Code)));
            return newRefresh.Errors!;
        }

        // Mark the old token as replaced (this also revokes it).
        var replaceResult = stored.ReplaceWith(newHash);
        if (!replaceResult.IsSuccess)
        {
            logger.LogWarning("Failed to mark old refresh token as replaced for user {UserId}: {Errors}", stored.UserId, string.Join(", ", replaceResult.Errors!.Select(e => e.Code)));
        }

        dbContext.RefreshTokens.Add(newRefresh.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new TokenPair(accessToken, newToken, accessExpiresAt, newExpiresAt);
    }

    public async Task<Result<Updated>> RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return RefreshTokenErrors.NotFound;

        var hash = HashToken(refreshToken);
        var stored = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(rt => rt.TokenHash == hash, cancellationToken);

        if (stored is null)
            return RefreshTokenErrors.NotFound;

        if (stored.IsRevoked)
            return RefreshTokenErrors.AlreadyRevoked;

        var result = stored.Revoke();
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Updated;
    }

    public async Task<Result<Updated>> RevokeAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Error.Validation("RefreshToken.UserId_Required", "User ID is required");

        var activeTokens = await dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.Revoke();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Updated;
    }

    private async Task<List<string>> ResolvePermissionsForRolesAsync(IList<string> roles, CancellationToken cancellationToken)
    {
        if (roles.Count == 0)
            return [];

        var roleIds = new List<string>();
        foreach (var name in roles)
        {
            var role = await roleManager.FindByNameAsync(name);
            if (role != null)
                roleIds.Add(role.Id);
        }

        if (roleIds.Count == 0)
            return [];

        return await (
            from rp in dbContext.RolePermissions.AsNoTracking()
            join p in dbContext.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
            where roleIds.Contains(rp.RoleId)
            select p.Code).Distinct().ToListAsync(cancellationToken);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
