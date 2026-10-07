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
/// Batch 2 targeted proof suite: NEW-1 (bootstrap credential hardening +
/// password.change_required enforcement), CFG-001 (production SMTP sender +
/// invitation delivery failure handling), NEW-2 (production migration / schema
/// validation / required seeding contract).
/// </summary>
[Collection("Integration")]
public class Batch2ProductionReadinessTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";
    private const string NewStrongPassword = "N3w!Str0ng#Pass2";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Batch2ProductionReadinessTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ================================================================
    // NEW-1: self-only change-password + cross-user proof
    // ================================================================

    /// <summary>
    /// MANDATORY cross-user security test (NEW-1): the change-password endpoint accepts
    /// NO target UserId (body/query/route/headers are all ignored) and identity comes
    /// exclusively from the authenticated server-side principal. User A rotating their
    /// password — even while submitting User B's id in the payload — changes ONLY A's
    /// credential; B's password and sessions are untouched.
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

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        // A rotated; B untouched.
        Assert.True(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(userA.Id)!, NewStrongPassword));
        Assert.False(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(userA.Id)!, StrongPassword));
        Assert.True(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(userB.Id)!, StrongPassword));

        // B's session survived A's rotation.
        var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var reuse = await refreshService.RotateAsync(refreshB);
        Assert.True(reuse.IsSuccess, "User B's refresh session must survive user A's password change.");
    }

    /// <summary>
    /// NEW-1: password.change_required is cleared ONLY after a successful change and refresh
    /// sessions are revoked ONLY after a successful change. A failed attempt leaves both
    /// exactly as they were.
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

            // Session still live: rotation succeeds.
            var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            var rotated = await refreshService.RotateAsync(refresh);
            Assert.True(rotated.IsSuccess, "Failed password change must not revoke sessions.");
            refresh = rotated.Value.RefreshToken;
        }

        // Correct password → success clears the requirement and kills sessions.
        var ok = await PostChangePasswordAsync(
            user.Id, email,
            new { currentPassword = StrongPassword, newPassword = NewStrongPassword });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            var claims = await userManager.GetClaimsAsync(dbUser!);
            Assert.DoesNotContain(claims, c => c.Type == "password.change_required");

            var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            var replay = await refreshService.RotateAsync(refresh);
            Assert.False(replay.IsSuccess, "Successful password change must revoke all refresh sessions.");
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

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// NEW-1 enforcement: the authoritative requirement state is loaded from the DATABASE,
    /// never trusted from the JWT. A principal whose DB row carries
    /// password.change_required=true is gated (403) on tenant endpoints even when the JWT
    /// carries NO such claim; a JWT forging password.change_required=true for a user whose
    /// DB row lacks it is IGNORED (no gate).
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
    }

    // ================================================================
    // NEW-1: bootstrap credential hardening (unit-level)
    // ================================================================

    [Fact]
    [Trait("Category", "Batch2")]
    public void GenerateTemporaryPassword_IsRandomAndNeverStaticDefault()
    {
        var first = TenancyConstants.GenerateTemporaryPassword();
        var second = TenancyConstants.GenerateTemporaryPassword();

        Assert.NotEqual("Admin@123", first);
        Assert.NotEqual("Admin@123", second);
        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 8, "Generated password must satisfy Identity length rules.");
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

    private async Task<string> IssueRefreshAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var refreshService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        return await refreshService.IssueAsync(userId, "batch2-test", "127.0.0.1");
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
