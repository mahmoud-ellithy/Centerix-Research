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
using Xunit.Abstractions;

namespace Centerix.SecurityTests;

/// <summary>
/// Refresh-session regression proof against a REAL SQL Server database with production wiring.
/// <para>
/// The AUTH-002 locking fix made rotation atomic, but the LOSERS of a legitimate race were then
/// routed into the reuse path: every losing request called <c>RevokeAllAsync</c> and destroyed the
/// successor the winner had just minted. Eight concurrent refreshes therefore left ZERO live
/// refresh tokens - a two-tab browser, or a mobile client retrying after a timeout, was logged out
/// of every session by its own retry.
/// </para>
/// <para>
/// The invariants this suite pins down:
/// </para>
/// <list type="bullet">
///   <item><description><b>Single winner</b> - for a given refresh token at most one concurrent
///   request rotates it successfully, and it never depends on the rate limiter to be true.</description></item>
///   <item><description><b>Winner survives</b> - a loser can neither destroy nor REPLACE the
///   winner's successor: exactly one live token remains and it is usable.</description></item>
///   <item><description><b>Loser cannot authenticate</b> - a loser gets 409 with no credentials,
///   never a token pair.</description></item>
///   <item><description><b>Genuine replay still detected</b> - outside the bounded race window a
///   replay of a consumed token is reuse: 401, no credentials, chain revoked (documented
///   policy: confirmed reuse revokes the whole user token family, because the model has no
///   per-chain family column).</description></item>
///   <item><description><b>No permanent alternative path</b> - replaying forever never yields a
///   token and never keeps the chain alive.</description></item>
///   <item><description><b>Unrelated sessions untouched</b> - racing one session leaves the
///   user's other sessions live.</description></item>
/// </list>
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class Auth004_RefreshConcurrentLegitimacyTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static int _ipCounter;

    private readonly SqlServerIntegrationFactory _env;
    private readonly ITestOutputHelper _output;

    public Auth004_RefreshConcurrentLegitimacyTests(SqlServerIntegrationFactory env, ITestOutputHelper output)
    {
        _env = env;
        _output = output;
    }

    // ==================================================================
    // Test 1 — 25 rounds × 8 concurrent refreshes
    // ==================================================================

    /// <summary>
    /// The headline regression: 25 independent rounds of 8 simultaneous refreshes carrying the
    /// SAME token. Every round must produce exactly one 200, seven 409s, exactly one live token,
    /// and a winner that is still usable as the input of the next round.
    /// </summary>
    [Fact]
    public async Task ConcurrentRefresh_25RoundsOf8_ExactlyOneWinnerPerRound_WinnerAlwaysSurvives()
    {
        const int rounds = 25;
        const int concurrency = 8;

        var email = UniqueEmail("race25");
        var user = await CreateUserAsync(email);

        var current = (await LoginAsync(email, RemoteIp())).RefreshToken;
        var summary = new List<string>(rounds);

        for (var round = 1; round <= rounds; round++)
        {
            // No artificial serialization: all 8 requests are in flight together, and each carries
            // its OWN remote IP so the refresh rate limiter can never be what makes one fail.
            var responses = await Task.WhenAll(Enumerable.Range(0, concurrency)
                .Select(_ => _env.Client.SendAsync(RefreshRequest(current, RemoteIp()))));

            var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
            var successCount = statuses.Count(s => s == (int)HttpStatusCode.OK);
            var conflictCount = statuses.Count(s => s == (int)HttpStatusCode.Conflict);

            Assert.True(
                successCount == 1,
                $"Round {round}: exactly one concurrent refresh may win, but {successCount} " +
                $"succeeded (statuses: {string.Join(", ", statuses)}).");

            Assert.True(
                successCount + conflictCount == responses.Length,
                $"Round {round}: every losing request must be answered with 409 Conflict, but " +
                $"statuses were: {string.Join(", ", statuses)}.");

            // A loser must never receive a credential.
            foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                var loserBody = await loser.Content.ReadAsStringAsync();
                Assert.True(
                    !loserBody.Contains("accessToken", StringComparison.OrdinalIgnoreCase),
                    $"Round {round}: a losing request must not receive credentials, but the body " +
                    $"was: {loserBody}");
            }

            var (total, active) = await TokenCountsAsync(user.Id);

            Assert.True(
                active == 1,
                $"Round {round}: exactly one live refresh token must remain after the race, but " +
                $"{active} are active (total rows: {total}).");

            Assert.True(
                total == round + 1,
                $"Round {round}: exactly one row per rotation must exist (1 login + {round} " +
                $"rotations), but found {total} rows.");

            var winner = JsonSerializer.Deserialize<RefreshResponse>(
                await responses.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadAsStringAsync(),
                JsonOptions);
            Assert.NotNull(winner);
            Assert.False(string.IsNullOrWhiteSpace(winner!.RefreshToken));

            summary.Add($"round {round}: 200={successCount} 409={conflictCount} live={active} rows={total}");

            // Feeding the winner's replacement into the next round doubles as the proof that the
            // winning replacement remains usable.
            current = winner.RefreshToken;
        }

        var (finalTotal, finalActive) = await TokenCountsAsync(user.Id);
        Assert.True(finalTotal == rounds + 1, $"Expected {rounds + 1} rows at the end, found {finalTotal}.");
        Assert.True(finalActive == 1, $"Expected exactly 1 live token at the end, found {finalActive}.");

        // Final explicit check: the last winner is a real, usable credential.
        var followUp = await _env.Client.SendAsync(RefreshRequest(current, RemoteIp()));
        Assert.True(
            followUp.StatusCode == HttpStatusCode.OK,
            $"The final winning replacement must be usable, but refreshing it returned " +
            $"{(int)followUp.StatusCode}.");

        _output.WriteLine($"{rounds} rounds × {concurrency} concurrent requests:");
        foreach (var line in summary)
            _output.WriteLine("  " + line);
    }

    // ==================================================================
    // Test 2 — two legitimate application requests
    // ==================================================================

    /// <summary>
    /// Request A and request B both start from the same session token (two tabs of one browser,
    /// or a client and its own retry). One wins; the loser must not be able to destroy the winner,
    /// so the user is able to keep using the session through the winner's replacement.
    /// </summary>
    [Fact]
    public async Task TwoLegitimateRequests_SharingOneToken_LoserDoesNotDestroyTheWinner()
    {
        var email = UniqueEmail("twotabs");
        var user = await CreateUserAsync(email);

        var login = await LoginAsync(email, RemoteIp());

        var responses = await Task.WhenAll(
            _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp())),
            _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp())));

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var losers = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();

        Assert.True(
            winners.Count == 1,
            $"Exactly one of the two legitimate requests may win, but {winners.Count} succeeded " +
            $"(statuses: {string.Join(", ", responses.Select(r => (int)r.StatusCode))}).");
        Assert.True(
            losers.Count == 1 && losers[0].StatusCode == HttpStatusCode.Conflict,
            $"The losing request must be answered with 409, but got {(int)losers[0].StatusCode}.");

        var (_, active) = await TokenCountsAsync(user.Id);
        Assert.True(
            active == 1,
            "The loser must not revoke the winner's replacement, but " +
            $"{active} token(s) are active after the two requests.");

        var winner = JsonSerializer.Deserialize<RefreshResponse>(
            await winners[0].Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(winner);

        // The user can continue the session with the winner's replacement.
        var continuation = await _env.Client.SendAsync(RefreshRequest(winner!.RefreshToken, RemoteIp()));
        Assert.True(
            continuation.StatusCode == HttpStatusCode.OK,
            "The user must be able to continue the session after the race, but refreshing the " +
            $"winner's replacement returned {(int)continuation.StatusCode}.");
    }

    // ==================================================================
    // Test 3 — genuine replay
    // ==================================================================

    /// <summary>
    /// T0 refreshes R to R2; once the race/retry window has closed, an attacker replays R.
    /// <para>
    /// <b>Documented policy:</b> replay of a consumed refresh token outside the race window is
    /// confirmed compromise. The replay is rejected with 401, it obtains no access token, and the
    /// user's whole refresh-token family is revoked - including the legitimate successor. The
    /// family here IS the user's token set, because the persistence model has no per-chain family
    /// column; revoking it is the existing AUTH-002 security model and is deliberately kept.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GenuineReplay_AfterTheRaceWindow_IsRejectedAndRevokesTheChain()
    {
        var email = UniqueEmail("genuinereplay");
        var user = await CreateUserAsync(email);

        var login = await LoginAsync(email, RemoteIp());
        var rotated = await RefreshAsync(login.RefreshToken, RemoteIp());

        var (totalBefore, activeBefore) = await TokenCountsAsync(user.Id);
        Assert.Equal(2, totalBefore);
        Assert.Equal(1, activeBefore);

        await Task.Delay(RotationGrace());

        var replay = await _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.True(
            !replayBody.Contains("accessToken", StringComparison.OrdinalIgnoreCase),
            $"A replay must never yield an access token, but the body was: {replayBody}");

        var (totalAfter, activeAfter) = await TokenCountsAsync(user.Id);
        Assert.Equal(totalBefore, totalAfter);
        Assert.True(
            activeAfter == 0,
            $"Confirmed reuse must revoke the chain (AUTH-002 policy), but {activeAfter} " +
            "token(s) are still active.");

        // The legitimate successor is dead too - that is the documented family-revocation policy.
        var successorAfter = await _env.Client.SendAsync(RefreshRequest(rotated.RefreshToken, RemoteIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, successorAfter.StatusCode);
    }

    // ==================================================================
    // Test 4 — repeated replay
    // ==================================================================

    /// <summary>
    /// Replaying a consumed token over and over must never turn into an alternative login.
    /// Inside the race window every presentation conflicts and issues nothing; outside it every
    /// presentation is rejected and re-revokes the (already dead) chain.
    /// </summary>
    [Fact]
    public async Task RepeatedReplay_NeverBecomesAnAlternativeAuthenticationPath()
    {
        var email = UniqueEmail("repeatreplay");
        var user = await CreateUserAsync(email);

        var login = await LoginAsync(email, RemoteIp());
        var rotated = await RefreshAsync(login.RefreshToken, RemoteIp());

        // --- inside the race/retry window: conflicts only, no credentials, winner intact ---
        for (var i = 0; i < 3; i++)
        {
            var inside = await _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()));
            Assert.True(
                inside.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized,
                $"Inside the race window a replay must never succeed, got {(int)inside.StatusCode}.");
            var insideBody = await inside.Content.ReadAsStringAsync();
            Assert.True(
                !insideBody.Contains("accessToken", StringComparison.OrdinalIgnoreCase),
                $"Inside the race window a replay must not receive credentials, body: {insideBody}");
        }

        var (_, activeInside) = await TokenCountsAsync(user.Id);
        Assert.True(
            activeInside == 1,
            "Replays inside the race window must leave the winner intact, but " +
            $"{activeInside} token(s) are active.");

        // --- outside the window: every replay is rejected, forever ---
        await Task.Delay(RotationGrace());

        for (var i = 0; i < 5; i++)
        {
            var outside = await _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()));
            Assert.Equal(HttpStatusCode.Unauthorized, outside.StatusCode);

            var outsideBody = await outside.Content.ReadAsStringAsync();
            Assert.True(
                !outsideBody.Contains("accessToken", StringComparison.OrdinalIgnoreCase),
                $"Outside the race window a replay must not receive credentials, body: {outsideBody}");

            var (_, active) = await TokenCountsAsync(user.Id);
            Assert.True(active == 0, $"After replay #{i + 1} the chain must be dead, but {active} are active.");
        }

        // The successor cannot be resurrected either.
        var successor = await _env.Client.SendAsync(RefreshRequest(rotated.RefreshToken, RemoteIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);
    }

    // ==================================================================
    // Test 5 — multiple sessions
    // ==================================================================

    /// <summary>
    /// One user holds three independent sessions. Racing a refresh of session A must only ever
    /// touch session A: sessions B and C stay live and stay usable.
    /// </summary>
    [Fact]
    public async Task ConcurrentRefreshOfOneSession_DoesNotRevokeUnrelatedSessions()
    {
        var email = UniqueEmail("multisession");
        var user = await CreateUserAsync(email);

        var sessionA = (await LoginAsync(email, RemoteIp())).RefreshToken;
        var sessionB = (await LoginAsync(email, RemoteIp())).RefreshToken;
        var sessionC = (await LoginAsync(email, RemoteIp())).RefreshToken;

        var (_, activeBefore) = await TokenCountsAsync(user.Id);
        Assert.Equal(3, activeBefore);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _env.Client.SendAsync(RefreshRequest(sessionA, RemoteIp()))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var (total, active) = await TokenCountsAsync(user.Id);
        Assert.Equal(4, total);   // A (revoked) + B + C + A's replacement
        Assert.Equal(3, active);  // B, C and A's replacement

        // B and C specifically are still alive.
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.RefreshTokens.Where(rt => rt.UserId == user.Id).ToListAsync();
        Assert.True(rows.Single(rt => rt.TokenHash == Hash(sessionB)).IsActive, "Session B must survive.");
        Assert.True(rows.Single(rt => rt.TokenHash == Hash(sessionC)).IsActive, "Session C must survive.");

        // ...and still usable.
        var useB = await _env.Client.SendAsync(RefreshRequest(sessionB, RemoteIp()));
        Assert.Equal(HttpStatusCode.OK, useB.StatusCode);
        var useC = await _env.Client.SendAsync(RefreshRequest(sessionC, RemoteIp()));
        Assert.Equal(HttpStatusCode.OK, useC.StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@auth004.test";

    /// <summary>
    /// Strictly incrementing client IPs: every single request in this suite needs its own
    /// rate-limit partition, so uniqueness (not just randomness) is what makes the concurrency
    /// assertions about rotation rather than about throttling.
    /// </summary>
    private static string RemoteIp()
    {
        var n = System.Threading.Interlocked.Increment(ref _ipCounter);
        return $"10.{1 + (n / (250 * 250)) % 250}.{1 + (n / 250) % 250}.{1 + n % 250}";
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>The configured rotation race window, padded so a test lands safely outside it.</summary>
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
