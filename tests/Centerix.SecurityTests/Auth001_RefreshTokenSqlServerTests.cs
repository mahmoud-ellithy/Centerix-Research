using System.Net;
using System.Net.Http.Headers;
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
/// AUTH-001 regression proof against a REAL SQL Server database (real migrations applied).
/// <para>
/// Refresh tokens used to derive from <c>AuditableEntity&lt;Guid&gt;</c>, which implements
/// <c>IHasTenantId</c>. That gave <c>Platform.RefreshTokens.TenantId</c> a NOT NULL mapping and put
/// every refresh-token row behind the global tenant query filter. Nothing stamps the column on an
/// anonymous login (no tenant is resolved yet, and the interceptor deliberately stamps nothing
/// when <c>ICurrentTenant.TenantId</c> is empty), so the INSERT violated NOT NULL and the whole
/// login endpoint answered HTTP 500.
/// </para>
/// <para>
/// The invariant under test: refresh tokens are authentication artifacts, owned by a USER and
/// addressed by their SHA-256 hash - never by a tenant. Issuance, rotation, replay detection and
/// revocation must all work with no tenant context whatsoever, and the tenant column must be gone
/// from the deployed schema.
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class Auth001_RefreshTokenSqlServerTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SqlServerIntegrationFactory _env;

    public Auth001_RefreshTokenSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // Schema invariants
    // ==================================================================

    /// <summary>The migration chain must be fully applied and the tenant column must be gone.</summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Migrations_NoPending_And_PlatformRefreshTokens_HasNoTenantIdColumn()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var tenantIdColumnExists = await db.Database.SqlQuery<bool>(
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'Platform'
                  AND TABLE_NAME = 'RefreshTokens'
                  AND COLUMN_NAME = 'TenantId') THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Value
            """).SingleAsync();

        Assert.False(
            tenantIdColumnExists,
            "Platform.RefreshTokens.TenantId still exists. Refresh tokens must not be tenant-owned: " +
            "they are created before any tenant context exists and are addressed by TokenHash/UserId.");
    }

    // ==================================================================
    // AUTH-001: anonymous login with no tenant context
    // ==================================================================

    /// <summary>
    /// The core AUTH-001 proof: a login that resolves NO tenant still mints a token pair.
    /// Before the fix this returned HTTP 500 (NOT NULL violation on RefreshTokens.TenantId).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Login_WithoutTenantContext_IssuesAccessTokenAndRefreshToken()
    {
        var email = UniqueEmail("login");
        await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        // No `tenant` header: the pipeline never resolves/authorizes a tenant for this request.
        var response = await _env.Client.SendAsync(LoginRequest(email, remoteIp));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /api/auth/login but got {(int)response.StatusCode}. Body: {body}");

        var payload = JsonSerializer.Deserialize<LoginResponse>(body, JsonOptions);
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.AccessToken), "access token missing");
        Assert.False(string.IsNullOrWhiteSpace(payload.RefreshToken), "refresh token missing");
        Assert.Equal(email, payload.Email);

        // The row really is persisted and retrievable with NO tenant context applied.
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = db.Users.Single(u => u.NormalizedEmail == email.ToUpperInvariant());
        var stored = await db.RefreshTokens.SingleAsync(rt => rt.UserId == user.Id);
        Assert.False(string.IsNullOrWhiteSpace(stored.TokenHash));
        Assert.False(stored.IsRevoked);
    }

    /// <summary>
    /// The full AUTH-001 lifecycle over HTTP: login -> refresh (rotation) -> logout -> replay of the
    /// revoked token is rejected. Every hop runs without a tenant header.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Login_Refresh_Logout_RoundTrip_Works_WithoutTenantContext()
    {
        var email = UniqueEmail("roundtrip");
        await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        var login = await LoginAsync(email, remoteIp);
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        // Rotation mints a NEW refresh token and revokes the presented one.
        var refreshResponse = await _env.Client.SendAsync(
            RefreshRequest(login.RefreshToken, remoteIp));
        var refreshBody = await refreshResponse.Content.ReadAsStringAsync();
        Assert.True(
            refreshResponse.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /api/auth/refresh but got {(int)refreshResponse.StatusCode}. Body: {refreshBody}");

        var rotated = JsonSerializer.Deserialize<RefreshResponse>(refreshBody, JsonOptions);
        Assert.NotNull(rotated);
        Assert.NotEqual(login.RefreshToken, rotated!.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(rotated.AccessToken));

        // Replaying the ORIGINAL (already rotated) refresh token must fail.
        //
        // POLICY: a presentation of a just-rotated token inside the server's bounded rotation
        // race window is answered with 409 and issues nothing (it is indistinguishable from a
        // concurrent retry of the same rotation). Genuine replay is what this asserts, so the
        // replay is issued AFTER that window has closed.
        await Task.Delay(RotationGrace());
        var replay = await _env.Client.SendAsync(
            RefreshRequest(login.RefreshToken, remoteIp));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Logout with the freshly issued access token revokes the current refresh token.
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        logout.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        logout.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken = rotated.RefreshToken }),
            Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.NoContent, (await _env.Client.SendAsync(logout)).StatusCode);

        var afterLogout = await _env.Client.SendAsync(
            RefreshRequest(rotated.RefreshToken, remoteIp));
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    /// <summary>
    /// logout-all must also work with no tenant context: it revokes EVERY refresh token the
    /// caller holds, across all tenants, so it is inherently tenant-independent.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task LogoutAll_RevokesEveryToken_WithoutTenantContext()
    {
        var email = UniqueEmail("logoutall");
        await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        var login = await LoginAsync(email, remoteIp);
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout-all");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        logout.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        Assert.Equal(HttpStatusCode.NoContent, (await _env.Client.SendAsync(logout)).StatusCode);

        var afterLogout = await _env.Client.SendAsync(RefreshRequest(login.RefreshToken, remoteIp));
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    /// <summary>
    /// Rotation must be able to SEE the stored row. Because the entity used to sit behind the
    /// global tenant query filter, an unauthenticated (empty/foreign tenant) context made the row
    /// invisible and every refresh silently answered 401 even when the row existed.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task RefreshTokenRow_IsVisible_WithoutAnyTenantContext()
    {
        var email = UniqueEmail("visible");
        await CreateUserAsync(email);
        var remoteIp = RemoteIp();

        var login = await LoginAsync(email, remoteIp);
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Read with the SAME DbContext the production request pipeline would build: no tenant
        // header was sent, so ICurrentTenant carries no authorized tenant for this flow.
        var userId = await db.Users
            .Where(u => u.NormalizedEmail == email.ToUpperInvariant())
            .Select(u => u.Id)
            .SingleAsync();

        var rows = await db.RefreshTokens.CountAsync(rt => rt.UserId == userId);

        Assert.True(rows >= 1, "Refresh token rows are invisible without a tenant context (AUTH-001).");
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@auth001.test";

    /// <summary>Distinct per-call client IP so each test gets its own rate-limit partition.</summary>
    private static string RemoteIp()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return $"10.{(bytes[0] % 200) + 1}.{(bytes[1] % 200) + 1}.{(bytes[2] % 200) + 1}";
    }

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
