using System.Net;
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
/// AUTH-003 regression proof against a REAL SQL Server database.
/// <para>
/// Login re-checks <c>IsLockedOutAsync</c> on every attempt, but rotation did not. A refresh token
/// minted BEFORE the account was locked stayed a live bearer credential: the locked-out user (or
/// anyone holding that token) could keep exchanging it for fresh access tokens for the entire
/// refresh lifetime, so the lockout only ever stopped the <c>/login</c> endpoint.
/// </para>
/// <para>
/// The invariant under test: once the account is locked out, every refresh token the account holds
/// is rejected AND revoked - the chain dies with the lockout, so the account really stops
/// authenticating until an administrator lets it back in.
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class Auth003_RefreshLockoutTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SqlServerIntegrationFactory _env;

    public Auth003_RefreshLockoutTests(SqlServerIntegrationFactory env) => _env = env;

    /// <summary>
    /// Control: with no lockout in place a live refresh token still rotates. This is what makes the
    /// lockout test meaningful - a 401 below is attributable to the lockout and nothing else.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_WhileAccountIsNotLockedOut_Succeeds()
    {
        var email = UniqueEmail("unlocked");
        await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        var login = await LoginAsync(email, remoteIp);
        var rotated = await RefreshAsync(login.RefreshToken, RemoteIp());

        Assert.False(string.IsNullOrWhiteSpace(rotated.RefreshToken));
        Assert.Equal(1, await ActiveTokenCountAsync(email));
    }

    /// <summary>
    /// The AUTH-003 proof: an account locked out AFTER a refresh token was issued can no longer use
    /// it. The response is 401 and - critically - the whole refresh chain is revoked, so there is
    /// nothing left to replay even if the lockout expires before the token does.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_AfterAccountIsLockedOut_IsRejected_AndRevokesEveryToken()
    {
        var email = UniqueEmail("locked");
        await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        var login = await LoginAsync(email, remoteIp);
        var rotated = await RefreshAsync(login.RefreshToken, RemoteIp());
        Assert.Equal(1, await ActiveTokenCountAsync(email));

        await LockAccountAsync(email);

        // The account is locked out; login now reports it.
        var loginAfterLock = await _env.Client.SendAsync(LoginRequest(email, RemoteIp()));
        Assert.Equal(HttpStatusCode.TooManyRequests, loginAfterLock.StatusCode);

        // ...but the refresh token issued before the lock must NOT still work.
        var refreshAfterLock = await _env.Client.SendAsync(
            RefreshRequest(rotated.RefreshToken, RemoteIp()));
        Assert.True(
            refreshAfterLock.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected 401 for a refresh on a locked-out account but got " +
            $"{(int)refreshAfterLock.StatusCode}.");

        // The chain must be dead: nothing is left to replay, not even the token that was just
        // rejected, and not the one issued by the successful rotation that preceded the lock.
        Assert.Equal(0, await ActiveTokenCountAsync(email));

        // And it stays dead on a second attempt.
        var secondAttempt = await _env.Client.SendAsync(
            RefreshRequest(login.RefreshToken, RemoteIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, secondAttempt.StatusCode);
        Assert.Equal(0, await ActiveTokenCountAsync(email));
    }

    /// <summary>
    /// After the lockout window expires the account authenticates normally again - the fix must
    /// reject during the lock, not permanently poison the account's sessions.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_SucceedsAgain_AfterTheLockoutExpires()
    {
        var email = UniqueEmail("relapsed");
        await CreateUserAsync(email);

        var login = await LoginAsync(email, RemoteIp());
        await LockAccountAsync(email);

        var blocked = await _env.Client.SendAsync(RefreshRequest(login.RefreshToken, RemoteIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        Assert.Equal(0, await ActiveTokenCountAsync(email));

        await ExpireLockoutAsync(email);

        var relogin = await LoginAsync(email, RemoteIp());
        var rotated = await RefreshAsync(relogin.RefreshToken, RemoteIp());
        Assert.False(string.IsNullOrWhiteSpace(rotated.RefreshToken));
        Assert.Equal(1, await ActiveTokenCountAsync(email));
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@auth003.test";

    /// <summary>Distinct per-call client IP so each test (and each call) gets its own rate-limit partition.</summary>
    private static string RemoteIp()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return $"10.{(bytes[0] % 200) + 1}.{(bytes[1] % 200) + 1}.{(bytes[2] % 200) + 1}";
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

    private async Task<int> ActiveTokenCountAsync(string email)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users
            .Where(u => u.NormalizedEmail == email.ToUpperInvariant())
            .Select(u => u.Id)
            .SingleAsync();
        // IsActive is a computed member and cannot be translated server-side, so materialise first.
        var rows = await db.RefreshTokens
            .Where(rt => rt.UserId == userId)
            .ToListAsync();
        return rows.Count(rt => rt.IsActive);
    }

    // Identity's UserManager attaches the instance you hand it, so every helper re-fetches the
    // user inside its own scope instead of sharing a detached instance across DbContexts.
    private async Task LockAccountAsync(string email)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.SetLockoutEnabledAsync(user!, true)).Succeeded);
        Assert.True((await userManager.SetLockoutEndDateAsync(
            user!, DateTimeOffset.UtcNow.AddMinutes(15))).Succeeded);
    }

    private async Task ExpireLockoutAsync(string email)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.SetLockoutEndDateAsync(
            user!, DateTimeOffset.UtcNow.AddMinutes(-1))).Succeeded);
        Assert.True((await userManager.ResetAccessFailedCountAsync(user!)).Succeeded);
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
