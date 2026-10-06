using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Centerix.API.Controllers;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// AUTH-002 regression proof against a REAL SQL Server database (row locks are only meaningful there).
/// <para>
/// Rotation used to be a read → validate → mutate sequence with no lock: two concurrent refreshes
/// carrying the same token could both observe "not revoked", both mint a new active token and both
/// commit. One presented token then had two live successors, and reuse detection - which only
/// triggers on an already-revoked row - never fired for the replay that raced the original.
/// </para>
/// <para>
/// The invariants under test:
/// a) a burst of concurrent refreshes with the SAME token yields exactly ONE successor row;
/// b) replaying a rotated token revokes the entire chain (including the successor that the winning
/// refresh just minted), leaving zero active tokens;
/// c) rotation records the old row as revoked AND points it at the hash of its successor, so the
/// chain is auditable end-to-end.
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class Auth002_RefreshTokenRotationTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SqlServerIntegrationFactory _env;

    public Auth002_RefreshTokenRotationTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // (a) Atomic rotation under concurrency
    // ==================================================================

    /// <summary>
    /// Five refreshes of the SAME token fired simultaneously. Exactly one may succeed and exactly
    /// one successor row may exist. Without the UPDLOCK/HOLDLOCK read every request observes a
    /// live row and each mints its own successor.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRefresh_WithSameToken_ProducesExactlyOneSuccessor()
    {
        var email = UniqueEmail("concurrent");
        var user = await CreateUserAsync(email);

        var login = await LoginAsync(email, RemoteIp());
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        // Fire the same refresh token at the endpoint simultaneously. Each request carries its
        // own remote IP so a per-IP rate limit can never be the reason one of them fails.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()))));

        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var unauthorizedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized);

        Assert.True(
            successCount == 1,
            $"Exactly one concurrent refresh may win, but {successCount} succeeded " +
            $"(statuses: {string.Join(", ", responses.Select(r => (int)r.StatusCode))}). " +
            "AUTH-002 requires rotation to be atomic.");
        Assert.True(
            successCount + unauthorizedCount == responses.Length,
            $"Every concurrent refresh must be either 200 or 401, but got: " +
            $"{string.Join(", ", responses.Select(r => (int)r.StatusCode))}.");

        var (total, active) = await TokenCountsAsync(user.Id);
        Assert.True(
            total == 2,
            $"Exactly one successor row must exist (original + 1), but found {total}. " +
            "A second row means two requests rotated the same presented token.");
        Assert.True(
            active == 0,
            "After the losing requests replay the rotated token, reuse detection must have " +
            $"revoked the whole chain, but {active} token(s) are still active.");
    }

    // ==================================================================
    // (b) Reuse detection revokes the whole chain
    // ==================================================================

    /// <summary>
    /// login → refresh → refresh (two successors deep) → replay the ORIGINAL token.
    /// The replay must return 401 AND leave the user with zero active tokens: presenting a
    /// rotated-out token is treated as theft, so every descendant of that chain dies with it.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ReplayOfOriginalToken_RevokesTheEntireChain()
    {
        var email = UniqueEmail("replay");
        var user = await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        var login = await LoginAsync(email, remoteIp);
        var first = await RefreshAsync(login.RefreshToken, remoteIp);
        var second = await RefreshAsync(first.RefreshToken, remoteIp);

        var (totalBefore, activeBefore) = await TokenCountsAsync(user.Id);
        Assert.True(totalBefore == 3, $"Expected original + 2 successors, found {totalBefore} rows.");
        Assert.True(activeBefore == 1, $"Expected exactly 1 active token before replay, found {activeBefore}.");

        var replay = await _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var (totalAfter, activeAfter) = await TokenCountsAsync(user.Id);
        Assert.Equal(totalAfter, totalBefore);
        Assert.True(
            activeAfter == 0,
            "Replaying a rotated-out refresh token must revoke every descendant in the chain " +
            $"(AUTH-002 reuse detection), but {activeAfter} token(s) are still active.");
    }

    // ==================================================================
    // (c) Rotation bookkeeping
    // ==================================================================

    /// <summary>
    /// Rotation must be recorded, not just performed: the presented row is revoked AND stamped with
    /// the hash of the successor it minted. That link is what makes a replay detectable at all -
    /// a plain revoke with no successor link would make every later replay look like a first use.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Rotation_RevokesPresentedToken_AndPointsItAtItsSuccessor()
    {
        var email = UniqueEmail("bookkeeping");
        var user = await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        var login = await LoginAsync(email, remoteIp);
        var rotated = await RefreshAsync(login.RefreshToken, remoteIp);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.RefreshTokens
            .Where(rt => rt.UserId == user.Id)
            .OrderBy(rt => rt.CreatedAtUtc)
            .ToListAsync();

        Assert.Equal(2, rows.Count);

        var presented = rows.Single(rt => rt.TokenHash == Hash(login.RefreshToken));
        var successor = rows.Single(rt => rt.TokenHash == Hash(rotated.RefreshToken));

        Assert.NotNull(presented.RevokedAtUtc);
        Assert.NotNull(presented.ReplacedByTokenHash);
        Assert.Equal(successor.TokenHash, presented.ReplacedByTokenHash);

        Assert.Null(successor.RevokedAtUtc);
        Assert.Null(successor.ReplacedByTokenHash);
        Assert.True(successor.IsActive, "The successor issued by a successful rotation must be active.");
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@auth002.test";

    /// <summary>Distinct per-call client IP so each test (and each concurrent call) gets its own rate-limit partition.</summary>
    private static string RemoteIp()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return $"10.{(bytes[0] % 200) + 1}.{(bytes[1] % 200) + 1}.{(bytes[2] % 200) + 1}";
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static HttpRequestMessage LoginRequest(string email, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, password = StrongPassword }),
            Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage RefreshRequest(string refreshToken, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken }),
            Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<LoginResponse> LoginAsync(string email, string remoteIp)
    {
        var response = await _env.Client.SendAsync(LoginRequest(email, remoteIp));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /api/auth/login but got {(int)response.StatusCode}. Body: {body}");

        var payload = JsonSerializer.Deserialize<LoginResponse>(body, JsonOptions);
        Assert.NotNull(payload);
        return payload!;
    }

    private async Task<RefreshResponse> RefreshAsync(string refreshToken, string remoteIp)
    {
        var response = await _env.Client.SendAsync(RefreshRequest(refreshToken, remoteIp));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /api/auth/refresh but got {(int)response.StatusCode}. Body: {body}");

        var payload = JsonSerializer.Deserialize<RefreshResponse>(body, JsonOptions);
        Assert.NotNull(payload);
        return payload!;
    }

    private async Task<(int Total, int Active)> TokenCountsAsync(string userId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.RefreshTokens.Where(rt => rt.UserId == userId).ToListAsync();
        return (rows.Count, rows.Count(rt => rt.IsActive));
    }

    private async Task<IdentityUser> CreateUserAsync(string email, string password = StrongPassword)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, password);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }
}
