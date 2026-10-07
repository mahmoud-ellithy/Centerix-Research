using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Email;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Batch 2 targeted proof suite: NEW-1 (login/refresh/change-password enforcement of the
/// database-authoritative password.change_required requirement), CFG-001 (production SMTP
/// sender + invitation delivery failure handling), NEW-2 (production migration / schema
/// validation / required seeding contract). Relational/security verification additionally
/// runs on real SQL Server in <c>Batch2SqlServerTests</c>; these fast HTTP/unit tests pin
/// the endpoint contracts.
/// </summary>
[Collection("Integration")]
public class Batch2ProductionReadinessTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";
    private const string NewStrongPassword = "N3w!Str0ng#Pass2";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Batch2ProductionReadinessTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ================================================================
    // NEW-1 A: login enforces password-change requirement
    // ================================================================

    /// <summary>
    /// NEW-1 A: correct password + change_required=true → 403 flow contract, NO refresh
    /// token issued, NO normal session established; the flow token completes rotation and
    /// normal login works afterwards.
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task Login_ChangeRequired_ReturnsFlowContractWithoutSession()
    {
        var email = UniqueEmail("b2-login-req");
        var user = await CreateUserAsync(email);
        await AddChangeRequiredClaimAsync(user.Id);

        var response = await PostLoginAsync(email, StrongPassword);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("changePasswordToken", body);
        Assert.DoesNotContain("refreshToken", body);

        var flowToken = JsonSerializer.Deserialize<JsonElement>(body, JsonOptions)
            .GetProperty("changePasswordToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(flowToken));

        // No session was established: zero refresh rows for this user.
        Assert.Equal(0, await CountRefreshRowsAsync(user.Id));

        // The controlled flow remains available: rotate with the flow token.
        var change = await PostChangePasswordWithTokenAsync(flowToken!,
            new { currentPassword = StrongPassword, newPassword = NewStrongPassword });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var pair = JsonSerializer.Deserialize<JsonElement>(await change.Content.ReadAsStringAsync(), JsonOptions);
        Assert.False(string.IsNullOrWhiteSpace(pair.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(pair.GetProperty("refreshToken").GetString()));

        // Normal login works after rotation.
        var login = await PostLoginAsync(email, NewStrongPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("refreshToken", await login.Content.ReadAsStringAsync());
    }

    // ================================================================
    // NEW-1 B: refresh enforces password-change requirement
    // ================================================================

    /// <summary>
    /// NEW-1 B: a live refresh token whose owner is subsequently flagged → refresh fails
    /// (403), no access token minted, no new refresh token minted, row count unchanged.
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task Refresh_ChangeRequired_MintsNothing()
    {
        var email = UniqueEmail("b2-refresh-req");
        var user = await CreateUserAsync(email);

        var login = await PostLoginAsync(email, StrongPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions);
        var accessToken = loginBody.GetProperty("accessToken").GetString()!;
        var refreshToken = loginBody.GetProperty("refreshToken").GetString()!;
        Assert.Equal(1, await CountRefreshRowsAsync(user.Id));

        await AddChangeRequiredClaimAsync(user.Id);

        var refresh = await PostRefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        var refreshBody = await refresh.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", refreshBody);
        Assert.DoesNotContain("refreshToken", refreshBody);
        Assert.Equal(1, await CountRefreshRowsAsync(user.Id));

        // Recovery through the controlled flow still works with the pre-existing token.
        var change = await PostChangePasswordWithTokenAsync(accessToken,
            new { currentPassword = StrongPassword, newPassword = NewStrongPassword });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
    }

    // ================================================================
    // NEW-1 C+D: change-password contract
    // ================================================================

    /// <summary>
    /// MANDATORY cross-user security test (NEW-1): the change-password endpoint accepts
    /// NO target UserId (body/query/route/headers are all ignored) and identity comes
    /// exclusively from the authenticated server-side principal. User A rotating their
    /// password — even while submitting User B's id in the payload — changes ONLY A's
    /// credential; B's password and sessions are untouched. Success returns the fresh
    /// NORMAL pair, which is proven usable.
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task ChangePassword_CrossUser_SubmittingAnotherUserId_ChangesOnlySelf()
    {
        var emailA = UniqueEmail("b2-self-a");
        var emailB = UniqueEmail("b2-self-b");
        var userA = await CreateUserAsync(emailA);
        var userB = await CreateUserAsync(emailB);

        var refreshB = await IssueRefreshAsync(userB.Id);

        // Attack-shaped payload: tries to name user B as the target. The endpoint has no
        // such field — model binding drops it — and identity is server-resolved as A.
        var response = await PostChangePasswordAsync(
            userA.Id, emailA,
            new { currentPassword = StrongPassword, newPassword = NewStrongPassword, userId = userB.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pair = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
        var newRefreshA = pair.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(pair.GetProperty("accessToken").GetString()));

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        // A rotated; B untouched.
        Assert.True(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(userA.Id)!, NewStrongPassword));
        Assert.False(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(userA.Id)!, StrongPassword));
        Assert.True(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(userB.Id)!, StrongPassword));

        // A's fresh pair is usable; B's session survived A's rotation.
        var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        Assert.True((await refreshService.RotateAsync(newRefreshA)).IsSuccess);
        Assert.True((await refreshService.RotateAsync(refreshB)).IsSuccess);
    }

    /// <summary>
    /// NEW-1 C+D: password.change_required is cleared ONLY after a successful change and
    /// refresh sessions are revoked ONLY after a successful change. A failed attempt feeds
    /// the lockout counter (AccessFailedAsync) but leaves the requirement and sessions
    /// unchanged and issues nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task ChangePassword_FailedAttempt_LeavesRequirementAndSessionsUnchanged()
    {
        var email = UniqueEmail("b2-fail");
        var user = await CreateUserAsync(email);
        await AddChangeRequiredClaimAsync(user.Id);
        var refresh = await IssueRefreshAsync(user.Id);

        // Wrong current password → failure.
        var failed = await PostChangePasswordAsync(
            user.Id, email,
            new { currentPassword = "Wr0ng!Pass9", newPassword = NewStrongPassword });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            var claims = await userManager.GetClaimsAsync(dbUser!);
            Assert.Contains(claims, c => c.Type == "password.change_required" && c.Value == "true");

            // Lockout policy observed the credential failure...
            Assert.Equal(1, await userManager.GetAccessFailedCountAsync(dbUser!));

            // ...but the session row is untouched: still present, still not revoked.
            // (Rotation itself is refused while the requirement stands — NEW-1 B — which
            // is exactly why the row must be inspected rather than rotated here.)
            var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            var refused = await refreshService.RotateAsync(refresh);
            Assert.False(refused.IsSuccess, "Refresh must be refused while the requirement stands.");
            Assert.Equal("RefreshToken.PasswordChangeRequired", refused.Errors![0].Code);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.RefreshTokens.SingleAsync(rt => rt.UserId == user.Id);
            Assert.Null(row.RevokedAtUtc);
        }

        // Correct password → success clears the requirement, kills sessions, returns pair.
        var ok = await PostChangePasswordAsync(
            user.Id, email,
            new { currentPassword = StrongPassword, newPassword = NewStrongPassword });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var pair = JsonSerializer.Deserialize<JsonElement>(await ok.Content.ReadAsStringAsync(), JsonOptions);
        Assert.False(string.IsNullOrWhiteSpace(pair.GetProperty("accessToken").GetString()));
        var freshRefresh = pair.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(freshRefresh));

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            var claims = await userManager.GetClaimsAsync(dbUser!);
            Assert.DoesNotContain(claims, c => c.Type == "password.change_required");

            var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();

            // The fresh pair from the success response is fully usable...
            var fresh = await refreshService.RotateAsync(freshRefresh);
            Assert.True(fresh.IsSuccess, "The fresh pair returned by change-password must work.");

            // ...while the pre-change session is dead (its replay is confirmed reuse).
            var replay = await refreshService.RotateAsync(refresh);
            Assert.False(replay.IsSuccess, "Successful password change must revoke all refresh sessions.");
        }
    }

    /// <summary>
    /// NEW-1 D: a locked-out account cannot rotate even with a live access token (401,
    /// indistinguishable, no state change).
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task ChangePassword_LockedOutAccount_IsRejected()
    {
        var email = UniqueEmail("b2-locked");
        var user = await CreateUserAsync(email);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            await userManager.SetLockoutEndDateAsync(dbUser!, DateTimeOffset.UtcNow.AddMinutes(15));
        }

        var response = await PostChangePasswordAsync(
            user.Id, email,
            new { currentPassword = StrongPassword, newPassword = NewStrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(user.Id)!, StrongPassword));
        }
    }

    /// <summary>
    /// NEW-1: POST /api/auth/change-password is tenant-independent — usable by a user with
    /// NO tenant membership (the bootstrap/root Platform case) by sending no tenant header.
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task ChangePassword_WithoutTenantMembership_Succeeds()
    {
        var email = UniqueEmail("b2-notenant");
        var user = await CreateUserAsync(email);

        var response = await PostChangePasswordAsync(
            user.Id, email,
            new { currentPassword = StrongPassword, newPassword = NewStrongPassword },
            tenantHeader: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// NEW-1 enforcement: the authoritative requirement state is loaded from the DATABASE,
    /// never trusted from the JWT. A principal whose DB row carries
    /// password.change_required=true is gated (403) on tenant endpoints even when the JWT
    /// carries NO such claim; a JWT forging password.change_required=true for a user whose
    /// DB row lacks it is IGNORED (no gate). A purpose-restricted flow token outside the
    /// change-password endpoint is rejected even with a clean database row.
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task PasswordChangeRequired_EnforcedFromDatabase_NotFromJwt()
    {
        const string tenantId = "b2-enforce-tenant";
        await SeedTenantWithAdminAsync(tenantId, "b2-enforce-admin@test.com");

        string gatedEmail;
        string gatedUserId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            gatedEmail = UniqueEmail("b2-gated");
            var gated = await CreateUserInScopeAsync(scope, gatedEmail);
            gatedUserId = gated.Id;
            await userManager.AddClaimAsync(gated, new Claim("password.change_required", "true"));
            await EnsureMembershipAsync(tenantId, gated.Id, "TenantAdmin");
        }

        // DB says change-required; JWT says nothing → still gated.
        var gatedToken = _factory.GenerateTestToken(gatedUserId, gatedEmail, ["TenantAdmin"]);
        var gatedResponse = await SendAsync(HttpMethod.Get, "/api/invitations", tenantId, gatedToken);
        Assert.Equal(HttpStatusCode.Forbidden, gatedResponse.StatusCode);
        var gatedBody = await gatedResponse.Content.ReadAsStringAsync();
        Assert.Contains("PasswordChangeRequired", gatedBody);

        // DB says nothing; JWT forges password.change_required=true → NOT gated (JWT ignored).
        string cleanEmail;
        string cleanUserId;
        using (var scope = _factory.Services.CreateScope())
        {
            cleanEmail = UniqueEmail("b2-clean");
            var clean = await CreateUserInScopeAsync(scope, cleanEmail);
            cleanUserId = clean.Id;
            await EnsureMembershipAsync(tenantId, clean.Id, "TenantAdmin");
        }

        var forgedToken = GenerateTokenWithClaim(cleanUserId, cleanEmail, "password.change_required", "true");
        var forgedResponse = await SendAsync(HttpMethod.Get, "/api/invitations", tenantId, forgedToken);
        Assert.NotEqual(HttpStatusCode.Forbidden, forgedResponse.StatusCode);
        var forgedBody = await forgedResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PasswordChangeRequired", forgedBody);

        // Purpose-restricted flow token outside its endpoint → rejected even with clean DB.
        var flowToken = GenerateTokenWithClaim(cleanUserId, cleanEmail, "pwd_change_only", "true");
        var flowResponse = await SendAsync(HttpMethod.Get, "/api/invitations", tenantId, flowToken);
        Assert.Equal(HttpStatusCode.Forbidden, flowResponse.StatusCode);
    }

    // ================================================================
    // NEW-1 E: no bootstrap password generator (unit-level)
    // ================================================================

    [Fact]
    [Trait("Category", "Batch2")]
    public void BootstrapCredential_RequiresExplicitConfiguration_NoDefault()
    {
        // There is no generator, no static password, and no fallback: the options
        // contract ships EMPTY and the seeder throws without an explicit value.
        Assert.True(string.IsNullOrWhiteSpace(new BootstrapAdminOptions().TemporaryPassword));
        Assert.Equal("BootstrapAdmin", BootstrapAdminOptions.SectionName);
    }

    // ================================================================
    // CFG-001: invitation delivery failure handling
    // ================================================================

    /// <summary>
    /// CFG-001: persist → commit → send → compensate. When e-mail delivery throws, the
    /// request returns FAILURE and the invitation does NOT remain Pending (compensating
    /// revoke succeeds). A control invitation with working delivery still succeeds.
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public async Task CreateInvitation_DeliveryFailure_ReturnsFailureAndLeavesNoPending()
    {
        const string tenantId = "b2-inv-tenant";
        const string adminEmail = "b2-inv-admin@test.com";
        await SeedTenantWithAdminAsync(tenantId, adminEmail);

        string adminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            adminId = (await userManager.FindByEmailAsync(adminEmail))!.Id;
        }

        var failingEmail = UniqueEmail("b2-inv-fail") + ".invalid";
        var okEmail = UniqueEmail("b2-inv-ok");

        _factory.EmailSender.Clear();
        _factory.EmailSender.ShouldFail = to => to.Contains("fail", StringComparison.OrdinalIgnoreCase);
        try
        {
            var adminToken = _factory.GenerateTestToken(adminId, adminEmail, ["TenantAdmin"]);

            var failResponse = await SendJsonAsync(HttpMethod.Post, "/api/invitations", tenantId, adminToken,
                new { email = failingEmail, roleName = "TenantUser", expirationDays = 7 });
            Assert.NotEqual(HttpStatusCode.Created, failResponse.StatusCode);
            Assert.True((int)failResponse.StatusCode >= 400, "Delivery failure must return failure, never success.");

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pending = await db.TenantInvitations.FirstOrDefaultAsync(i =>
                    i.TenantId == tenantId && i.NormalizedEmail == failingEmail.Trim().ToUpperInvariant()
                    && i.Status == InvitationStatus.Pending);
                Assert.Null(pending);
            }

            var okResponse = await SendJsonAsync(HttpMethod.Post, "/api/invitations", tenantId, adminToken,
                new { email = okEmail, roleName = "TenantUser", expirationDays = 7 });
            Assert.Equal(HttpStatusCode.Created, okResponse.StatusCode);
        }
        finally
        {
            _factory.EmailSender.Clear();
        }
    }

    /// <summary>
    /// CFG-001: production SMTP configuration is validated and fails clearly when required
    /// values are missing/invalid. The supported path is MailKit (SmtpEmailSender); the
    /// development sender is never a silent production fallback (covered by DI policy +
    /// Program startup gate).
    /// </summary>
    [Fact]
    [Trait("Category", "Batch2")]
    public void SmtpOptions_MissingOrInvalid_FailsClearly_ValidPasses()
    {
        Assert.Throws<InvalidOperationException>(() => new SmtpOptions().Validate());
        Assert.Throws<InvalidOperationException>(() => new SmtpOptions { Host = "smtp.example.com" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new SmtpOptions
        {
            Host = "smtp.example.com",
            Port = 99999,
            FromAddress = "noreply@example.com"
        }.Validate());

        var valid = new SmtpOptions
        {
            Host = "smtp.example.com",
            Port = 587,
            FromAddress = "noreply@example.com"
        };
        valid.Validate();
    }

    // ================================================================
    // NEW-2: database initialization contract (unit-level)
    // ================================================================

    [Fact]
    [Trait("Category", "Batch2")]
    public void DatabaseInitializationOptions_SafeProductionDefaults()
    {
        var options = new DatabaseInitializationOptions();

        Assert.True(options.ApplyMigrations);
        Assert.True(options.Seed);
        Assert.True(options.ValidateSchema);
        Assert.False(options.SeedDevelopmentData);
        Assert.Equal("DatabaseInitialization", DatabaseInitializationOptions.SectionName);
        Assert.Equal("BootstrapAdmin", BootstrapAdminOptions.SectionName);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static string UniqueEmail(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}@test.com";

    private static string UniqueIp() =>
        $"10.{Random.Shared.Next(1, 200)}.{Random.Shared.Next(1, 200)}.{Random.Shared.Next(1, 200)}";

    private async Task<IdentityUser> CreateUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return await CreateUserInScopeAsync(scope, email);
    }

    private static async Task<IdentityUser> CreateUserInScopeAsync(IServiceScope scope, string email)
    {
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
        var result = await userManager.CreateAsync(user, StrongPassword);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task AddChangeRequiredClaimAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByIdAsync(userId);
        await userManager.AddClaimAsync(user!, new Claim("password.change_required", "true"));
    }

    private async Task<int> CountRefreshRowsAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.CountAsync(rt => rt.UserId == userId);
    }

    private async Task<string> IssueRefreshAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        return await refreshService.IssueAsync(userId, "batch2-test", "127.0.0.1");
    }

    private async Task<HttpResponseMessage> PostLoginAsync(string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, UniqueIp());
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, password }), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostRefreshAsync(string refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, UniqueIp());
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken }), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostChangePasswordWithTokenAsync(string token, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostChangePasswordAsync(
        string userId, string email, object payload, string? tenantHeader = "unused-no-membership")
    {
        var token = _factory.GenerateTestToken(userId, email, []);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null && tenantHeader != "unused-no-membership")
            request.Headers.Add("tenant", tenantHeader);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string tenantHeader, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("tenant", tenantHeader);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string url, string tenantHeader, string token, object payload)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("tenant", tenantHeader);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    private string GenerateTokenWithClaim(string userId, string email, string claimType, string claimValue)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Email, email),
            new(claimType, claimValue),
        };
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("ThisIsATestSecretKeyThatIsAtLeast32CharsLong!!"));
        var token = new JwtSecurityToken(
            issuer: "TestIssuer",
            audience: "TestAudience",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task SeedTenantWithAdminAsync(string tenantId, string adminEmail)
    {
        await _factory.SeedPermissionsAsync();

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var db = sp.GetRequiredService<AppDbContext>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();

        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@test.com",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }

        // Test convenience: TenantAdmin holds every permission so invitation endpoints
        // are reachable; authorization matrices themselves are covered by Batch 1 suites.
        var tenantAdminRole = await roleManager.FindByNameAsync("TenantAdmin");
        var allPermissionIds = db.Permissions.Select(p => p.Id).ToList();
        var existing = db.RolePermissions.Where(rp => rp.RoleId == tenantAdminRole!.Id).Select(rp => rp.PermissionId).ToHashSet();
        foreach (var pid in allPermissionIds)
            if (!existing.Contains(pid))
                db.RolePermissions.Add(RolePermission.Create(tenantAdminRole!.Id, pid).Value);
        await db.SaveChangesAsync();

        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new IdentityUser
            {
                Email = adminEmail,
                UserName = adminEmail,
                EmailConfirmed = true,
                PhoneNumberConfirmed = true,
                NormalizedEmail = adminEmail.ToUpperInvariant(),
                NormalizedUserName = adminEmail.ToUpperInvariant()
            };
            var created = await userManager.CreateAsync(admin, StrongPassword);
            Assert.True(created.Succeeded, string.Join(";", created.Errors.Select(e => e.Description)));
        }

        await EnsureMembershipAsync(tenantId, admin.Id, "TenantAdmin");
    }

    private async Task EnsureMembershipAsync(string tenantId, string userId, string roleName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.TenantMemberships
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);
        if (existing is null)
        {
            var membership = TenantMembership.Create(userId, tenantId, roleName, TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess,
                "Membership creation failed: " + (membership.Errors is null
                    ? "unknown"
                    : string.Join(",", membership.Errors.Select(e => e.Code))));
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }
        else if (existing.Status != TenantMembershipStatus.Active)
        {
            existing.Activate();
            await db.SaveChangesAsync();
        }
    }
}
