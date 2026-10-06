using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Centerix.API.Controllers;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// a) a burst of concurrent refreshes with the SAME token yields exactly ONE successor row, the
/// losers are answered with a conflict instead of a logout, and the successor the winner minted
/// stays alive and usable;
/// b) replaying a rotated token OUTSIDE the rotation race window revokes the entire chain
/// (including the successor that the winning refresh minted), leaving zero active tokens;
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
    // (a) Atomic rotation under concurrency - and the winner SURVIVES
    // ==================================================================

    /// <summary>
    /// Five refreshes of the SAME token fired simultaneously. Exactly one may succeed and exactly
    /// one successor row may exist. Without the UPDLOCK/HOLDLOCK read every request observes a
    /// live row and each mints its own successor.
    /// <para>
    /// The losers must NOT be allowed to log the winner out. They used to take the reuse path and
    /// call <c>RevokeAllAsync</c>, which revoked the successor the winner had just minted - so a
    /// two-tab browser, or a client retrying after a timeout, ended up with ZERO live tokens.
    /// A loser is now answered with 409 (no credentials, no revocation) and the winner's
    /// replacement survives and stays usable.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRefresh_WithSameToken_ProducesExactlyOneSuccessor_AndTheWinnerSurvives()
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
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.True(
            successCount == 1,
            $"Exactly one concurrent refresh may win, but {successCount} succeeded " +
            $"(statuses: {string.Join(", ", responses.Select(r => (int)r.StatusCode))}). " +
            "AUTH-002 requires rotation to be atomic.");
        Assert.True(
            successCount + conflictCount == responses.Length,
            $"A losing concurrent refresh must be answered with 409 Conflict, but got: " +
            $"{string.Join(", ", responses.Select(r => (int)r.StatusCode))}.");

        // No loser may ever receive a token pair.
        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                !body.Contains("accessToken", StringComparison.OrdinalIgnoreCase),
                $"A losing request must not receive credentials, but the 409 body was: {body}");
        }

        var (total, active) = await TokenCountsAsync(user.Id);
        Assert.True(
            total == 2,
            $"Exactly one successor row must exist (original + 1), but found {total}. " +
            "A second row means two requests rotated the same presented token.");
        Assert.True(
            active == 1,
            "The successor minted by the winning refresh must survive the losing requests, " +
            $"but {active} token(s) are active after the race.");

        // The winning replacement is the one whose RefreshToken came back with HTTP 200.
        var winningResponse = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var winner = JsonSerializer.Deserialize<RefreshResponse>(
            await winningResponse.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(winner);

        // ...and it is genuinely usable, not just present in the table.
        var followUp = await _env.Client.SendAsync(RefreshRequest(winner!.RefreshToken, RemoteIp()));
        Assert.True(
            followUp.StatusCode == HttpStatusCode.OK,
            $"The winning replacement must remain usable, but refreshing it returned " +
            $"{(int)followUp.StatusCode}.");
    }

    /// <summary>
    /// Presenting the rotated token again AFTER the race/retry window has closed must still be
    /// treated as reuse - the grace must never become a permanent alternative path.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ReplayingTheConstituentTokenAfterTheRaceWindow_IsStillReuse()
    {
        var email = UniqueEmail("postrace");
        var user = await CreateUserAsync(email);

        var login = await LoginAsync(email, RemoteIp());

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()))));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        await Task.Delay(RotationGrace());

        var replay = await _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var (_, active) = await TokenCountsAsync(user.Id);
        Assert.True(
            active == 0,
            $"Replaying a rotated token after the race window must revoke the chain, but " +
            $"{active} token(s) are still active.");
    }

    // ==================================================================
    // (b) Reuse detection revokes the whole chain
    // ==================================================================

    /// <summary>
    /// login → refresh → refresh (two successors deep) → replay the ORIGINAL token.
    /// The replay must return 401 AND leave the user with zero active tokens: presenting a
    /// rotated-out token is treated as theft, so every descendant of that chain dies with it.
    /// <para>
    /// This replay happens immediately - inside the rotation race window - and must STILL be
    /// detected, because the discriminator is not time alone: the successor the original token
    /// points at has itself already been rotated away, so nothing about this presentation can be a
    /// request racing that first rotation. A race loser, by contrast, always points at a
    /// successor that is still live.
    /// </para>
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

    /// <summary>
    /// The server-configured rotation race/retry window, padded by one second so a test that
    /// wants to be OUTSIDE it cannot land on the boundary.
    /// </summary>
    private TimeSpan RotationGrace()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IOptions<JwtSettings>>().Value;
        return TimeSpan.FromSeconds(Math.Max(0, settings.RefreshRotationGraceSeconds) + 1);
    }

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
