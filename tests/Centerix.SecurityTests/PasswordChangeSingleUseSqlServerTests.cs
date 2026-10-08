using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Blocker 3: the purpose-restricted <c>pwd_change_only=true</c> token is SINGLE-USE.
/// After a successful password change the same token must no longer be accepted at
/// POST /api/auth/change-password, while the normal access/refresh pair returned by that
/// change keeps working. All proofs run against REAL SQL Server (never InMemory).
/// </summary>
[Collection("SqlServerIntegration")]
public class PasswordChangeSingleUseSqlServerTests
{
    private const string StrongPassword = "Str0ng!Pass1";
    private const string NewStrongPassword = "N3w!Str0ng#Pass2";
    private const string ThirdStrongPassword = "Th1rd!Str0ng#Pass3";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SqlServerIntegrationFactory _env;

    public PasswordChangeSingleUseSqlServerTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _env.EmailSender.Clear();
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task PurposeToken_IsSingleUse_AfterSuccessfulPasswordChange()
    {
        var email = UniqueEmail("pwd-single");
        var user = await CreateUserAsync(email);
        await AddChangeRequiredClaimAsync(user.Id);

        // Login is gated into the controlled flow: 403 + purpose token, no session.
        var login = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var flowToken = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions)
            .GetProperty("changePasswordToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(flowToken));

        // 1. Valid purpose token -> change password -> 200 + normal token pair.
        var change = await ChangePasswordRequest(flowToken, StrongPassword, NewStrongPassword);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var pair = JsonSerializer.Deserialize<JsonElement>(
            await change.Content.ReadAsStringAsync(), JsonOptions);
        var newAccessToken = pair.GetProperty("accessToken").GetString()!;
        var newRefreshToken = pair.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(newAccessToken));
        Assert.False(string.IsNullOrWhiteSpace(newRefreshToken));

        // 2a. Same purpose token again (stale current password) -> rejected, not a 400.
        var replayStale = await ChangePasswordRequest(flowToken, StrongPassword, ThirdStrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, replayStale.StatusCode);

        // 2b. Same purpose token again (correct current password) -> still rejected.
        var replayFresh = await ChangePasswordRequest(flowToken, NewStrongPassword, ThirdStrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, replayFresh.StatusCode);

        // 4. Refresh token from the new normal session -> succeeds.
        var refresh = await _env.Client.SendAsync(RefreshRequest(newRefreshToken, UniqueIp()));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshed = JsonSerializer.Deserialize<JsonElement>(
            await refresh.Content.ReadAsStringAsync(), JsonOptions);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(refreshed.GetProperty("refreshToken").GetString()));

        // 3. New normal access token -> normal authenticated endpoint -> succeeds.
        // (A second rotation with the fresh session token proves the normal token works.)
        var secondChange = await ChangePasswordRequest(newAccessToken, NewStrongPassword, ThirdStrongPassword);
        Assert.Equal(HttpStatusCode.OK, secondChange.StatusCode);

        // Sanity: the latest password is the live credential.
        var finalLogin = await _env.Client.SendAsync(LoginRequest(email, ThirdStrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.OK, finalLogin.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task PurposeToken_ConsumptionIsPerRequirement_UnrelatedTokensUnaffected()
    {
        // User A: rotate via the NORMAL access-token path while a flow token is outstanding.
        var emailA = UniqueEmail("pwd-cross-a");
        var userA = await CreateUserAsync(emailA);
        var loginA = await _env.Client.SendAsync(LoginRequest(emailA, StrongPassword, UniqueIp()));
        var accessA = JsonSerializer.Deserialize<JsonElement>(
            await loginA.Content.ReadAsStringAsync(), JsonOptions).GetProperty("accessToken").GetString()!;

        await AddChangeRequiredClaimAsync(userA.Id);
        var gatedA = await _env.Client.SendAsync(LoginRequest(emailA, StrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.Forbidden, gatedA.StatusCode);
        var flowA = JsonSerializer.Deserialize<JsonElement>(
            await gatedA.Content.ReadAsStringAsync(), JsonOptions)
            .GetProperty("changePasswordToken").GetString()!;

        // The successful rotation (through the normal token) consumes the outstanding flow token.
        var changeA = await ChangePasswordRequest(accessA, StrongPassword, NewStrongPassword);
        Assert.Equal(HttpStatusCode.OK, changeA.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await ChangePasswordRequest(flowA, NewStrongPassword, ThirdStrongPassword)).StatusCode);

        // User B (unrelated): the flow token still completes its own rotation exactly once.
        var emailB = UniqueEmail("pwd-cross-b");
        var userB = await CreateUserAsync(emailB);
        await AddChangeRequiredClaimAsync(userB.Id);
        var gatedB = await _env.Client.SendAsync(LoginRequest(emailB, StrongPassword, UniqueIp()));
        var flowB = JsonSerializer.Deserialize<JsonElement>(
            await gatedB.Content.ReadAsStringAsync(), JsonOptions)
            .GetProperty("changePasswordToken").GetString()!;

        Assert.Equal(
            HttpStatusCode.OK,
            (await ChangePasswordRequest(flowB, StrongPassword, NewStrongPassword)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await ChangePasswordRequest(flowB, NewStrongPassword, ThirdStrongPassword)).StatusCode);

        // Endpoint binding is unchanged: a purpose token outside its endpoint is still 403.
        var refreshAttempt = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refreshAttempt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", flowA);
        refreshAttempt.Headers.Add(TestRemoteIpStartupFilter.HeaderName, UniqueIp());
        refreshAttempt.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken = "any-value" }), Encoding.UTF8, "application/json");
        var refreshResponse = await _env.Client.SendAsync(refreshAttempt);
        Assert.Equal(HttpStatusCode.Forbidden, refreshResponse.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task PurposeToken_FailedAttempt_DoesNotConsume_AllowsRetryThenSingleUse()
    {
        var email = UniqueEmail("pwd-retry");
        var user = await CreateUserAsync(email);
        await AddChangeRequiredClaimAsync(user.Id);

        var login = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        var flowToken = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions)
            .GetProperty("changePasswordToken").GetString()!;

        // Wrong current password -> 400, requirement (and therefore the token) survives.
        var failed = await ChangePasswordRequest(flowToken, "Wr0ng!Pass9", NewStrongPassword);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        // Retry with the SAME token and the correct password -> 200 + normal pair.
        var change = await ChangePasswordRequest(flowToken, StrongPassword, NewStrongPassword);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        // Now consumed: the same token is rejected.
        var replay = await ChangePasswordRequest(flowToken, NewStrongPassword, ThirdStrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    // ==================================================================
    // Helpers (mirroring Batch2SqlServerTests)
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@sql.test";

    private static string UniqueIp() =>
        $"10.{Random.Shared.Next(1, 200)}.{Random.Shared.Next(1, 200)}.{Random.Shared.Next(1, 200)}";

    private static HttpRequestMessage LoginRequest(string email, string password, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, password }), Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage RefreshRequest(string refreshToken, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken }), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<HttpResponseMessage> ChangePasswordRequest(
        string token, string currentPassword, string newPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { currentPassword, newPassword }), Encoding.UTF8, "application/json");
        return await _env.Client.SendAsync(request);
    }

    private async Task<IdentityUser> CreateUserAsync(string email, string password = StrongPassword)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
            return existing;

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task AddChangeRequiredClaimAsync(string userId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByIdAsync(userId);
        var result = await userManager.AddClaimAsync(user!, new Claim("password.change_required", "true"));
        Assert.True(result.Succeeded);
    }
}
