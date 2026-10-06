namespace Centerix.Infrastructure.Auth;

using System.Data;
using System.Security.Cryptography;
using System.Text;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Authentication;
using Centerix.Domain.Common.Results;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
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

    /// <summary>
    /// AUTH-002 — Rotate a presented refresh token atomically.
    /// <para>
    /// Rotation is a read → validate → mutate sequence over a single row. Without a lock, two
    /// concurrent refreshes carrying the same token both observe "not revoked", both mint a new
    /// active token and both commit: one presented token then has two live successors and reuse
    /// detection is defeated (an attacker replaying a stolen token in the same window would not
    /// be caught).
    /// </para>
    /// <para>
    /// Serializable isolation plus an UPDLOCK/HOLDLOCK read of the presented row makes the second
    /// request wait for the first to commit, then re-read the committed state and see the row is
    /// already revoked. Exactly ONE request can therefore ever take the mint path.
    /// </para>
    /// <para>
    /// A request that loses the race is NOT reuse: it presented a token it already legitimately
    /// held, it just arrived a few milliseconds behind the winner. Revoking the whole chain for it
    /// would let any loser log the winner out of every session (a two-tab browser, or a mobile
    /// client retrying after a timeout, would be destroyed by its own retry). Such a loser is
    /// answered with <see cref="RefreshTokenErrors.RotationConflict"/> (HTTP 409) and NOTHING is
    /// revoked. Only a presentation outside that bounded window - or one whose successor chain has
    /// already moved on - is confirmed reuse and takes the family-revocation path.
    /// </para>
    /// </summary>
    public async Task<Result<TokenPair>> RotateAsync(
        string refreshToken,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return RefreshTokenErrors.NotFound;

        var hash = HashToken(refreshToken);

        // Captured BEFORE the transaction opens, so a request that is about to block on the
        // winner's lock is still measured by when it ARRIVED rather than by when it finally got
        // to run. This is what lets a slow-but-legitimate loser stay inside the race window while
        // a genuinely later replay stays outside it.
        var presentedAtUtc = DateTime.UtcNow;

        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            var result = await TryRotateAsync(hash, deviceInfo, ipAddress, presentedAtUtc, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning(ex, "Refresh token rotation conflicted with a concurrent request.");
            return RefreshTokenErrors.RotationConflict;
        }
    }

    private async Task<Result<TokenPair>> TryRotateAsync(
        string hash,
        string? deviceInfo,
        string? ipAddress,
        DateTime presentedAtUtc,
        CancellationToken cancellationToken)
    {
        // UPDLOCK + HOLDLOCK (SQL Server): hold an update lock on the presented row for the
        // whole transaction so a concurrent rotation of the same token blocks here instead of
        // reading a stale "still active" row. The plain LINQ read below then observes the
        // winner's committed state. InMemory has no concurrency, so the fallback read applies.
        RefreshToken? stored;
        if (dbContext.IsRelational)
        {
            stored = await dbContext.RefreshTokens
                .FromSqlRaw(
                    "SELECT * FROM [Platform].[RefreshTokens] WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE [TokenHash] = @p0",
                    hash)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            stored = await dbContext.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == hash, cancellationToken);
        }

        if (stored is null)
            return RefreshTokenErrors.NotFound;

        if (stored.IsRevoked)
        {
            // Race loser vs. genuine replay. Both present a token that is already revoked; only
            // the database state plus the bounded window below can tell them apart.
            if (await IsLostRotationRaceAsync(stored, presentedAtUtc, cancellationToken))
            {
                logger.LogInformation(
                    "Refresh token already rotated by a concurrent request for user {UserId}; " +
                    "answering conflict and leaving the winning successor untouched.",
                    stored.UserId);
                return RefreshTokenErrors.RotationConflict;
            }

            // Reuse detection: a token that was already rotated (or revoked) and is presented
            // again outside the race window is a replay attempt. Revoke the entire chain.
            logger.LogWarning("Reuse of revoked refresh token detected for user {UserId}. Revoking all tokens.", stored.UserId);
            await RevokeAllAsync(stored.UserId, cancellationToken);
            return RefreshTokenErrors.Revoked;
        }

        if (stored.IsExpired)
            return RefreshTokenErrors.Expired;

        var user = await userManager.FindByIdAsync(stored.UserId);
        if (user is null)
            return RefreshTokenErrors.NotFound;

        // AUTH-003 — a refresh token must not outlive an account lockout. /api/auth/login re-checks
        // lockout on every attempt, but a refresh token minted BEFORE the lock is a bearer
        // credential: without this check a locked-out account would keep exchanging it for fresh
        // access tokens for as long as the refresh token remains valid (up to the full refresh
        // lifetime), silently defeating the lockout. Treat the presented token as compromised and
        // kill the whole chain so the account really stops authenticating.
        if (userManager.SupportsUserLockout && await userManager.IsLockedOutAsync(user))
        {
            logger.LogWarning(
                "Refresh attempted for locked-out user {UserId}. Revoking all refresh tokens.",
                user.Id);
            await RevokeAllAsync(user.Id, cancellationToken);
            return RefreshTokenErrors.AccountLocked;
        }

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
            if (dbContext is DbContext failedDb)
                failedDb.ChangeTracker.Clear();
            return newRefresh.Errors!;
        }

        // Mark the old token as replaced (this also revokes it).
        var replaceResult = stored.ReplaceWith(newHash);
        if (!replaceResult.IsSuccess)
        {
            logger.LogWarning("Failed to mark old refresh token as replaced for user {UserId}: {Errors}", stored.UserId, string.Join(", ", replaceResult.Errors!.Select(e => e.Code)));
        }

        // The revoke of the old row and the insert of its successor are saved in a single
        // SaveChanges inside the transaction, so the pair is all-or-nothing.
        dbContext.RefreshTokens.Add(newRefresh.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new TokenPair(accessToken, newToken, accessExpiresAt, newExpiresAt);
    }

    /// <summary>
    /// Decides whether an already-revoked presentation is a LOSER OF A CONCURRENT ROTATION rather
    /// than a genuine replay of a consumed token.
    /// <para>
    /// All three conditions are required, and every one of them is read from committed database
    /// state inside the same serializable transaction that holds the UPDLOCK on the presented row,
    /// so the decision is made by SQL Server's view of the world and is identical on every
    /// application instance:
    /// </para>
    /// <list type="number">
    ///   <item><description>the token went through ROTATION (it carries a successor link). A plain
    ///   revoke - logout, logout-all, account lockout - has no successor and always stays on the
    ///   reuse path;</description></item>
    ///   <item><description>the successor is STILL ACTIVE. If the chain has already moved past the
    ///   successor, the presented token is an old link of a completed chain, not a request racing
    ///   that rotation;</description></item>
    ///   <item><description>the presentation ARRIVED inside the bounded, server-controlled grace
    ///   window measured from the winner's commit. Outside it the presentation is a later replay,
    ///   no matter what the successor looks like.</description></item>
    /// </list>
    /// <para>
    /// The window is deliberately narrow and NEVER mints credentials: a loser (or an attacker who
    /// replays inside the window) only ever receives HTTP 409 with an empty body, so the grace can
    /// never become an authentication path. What it buys is that a legitimate race - two tabs, or a
    /// retry after a timeout - can no longer destroy the successor the winner just minted.
    /// </para>
    /// </summary>
    private async Task<bool> IsLostRotationRaceAsync(
        RefreshToken stored,
        DateTime presentedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(stored.ReplacedByTokenHash))
            return false;

        if (stored.RevokedAtUtc is not { } revokedAtUtc)
            return false;

        if (presentedAtUtc - revokedAtUtc > RotationGrace)
            return false;

        var successor = await dbContext.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(rt => rt.TokenHash == stored.ReplacedByTokenHash, cancellationToken);

        return successor is { IsActive: true };
    }

    private TimeSpan RotationGrace =>
        TimeSpan.FromSeconds(Math.Max(0, _jwtSettings.RefreshRotationGraceSeconds));

    public async Task<Result<Updated>> RevokeAsync(
        string refreshToken, CancellationToken cancellationToken = default)
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

    /// <summary>SQL Server deadlock victim (error 1205). Transient under Serializable isolation.</summary>
    private static bool IsDeadlockException(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is SqlException { Number: 1205 })
                return true;
            current = current.InnerException;
        }

        return false;
    }
}
