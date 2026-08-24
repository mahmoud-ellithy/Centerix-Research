using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// HTTP-level tests for the two invitation consumption paths:
///  - POST /api/invitations/register      (brand-new users, anonymous, token = capability)
///  - POST /api/invitations/{token}/accept(existing users, authenticated, e-mail must match)
/// Runs on the EF InMemory factory for fast feedback; relational behavior is covered by the
/// SQL Server integration suite.
/// </summary>
[Collection("Integration")]
public class InvitationRegistrationHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private const string TenantA = "reg-tenant-a";
    public const string TestBaseUrl = "https://app.securitytests.local";

    public InvitationRegistrationHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.EmailSender.Clear();
    }

    // ------------------------------------------------------------------
    // Seeding helpers
    // ------------------------------------------------------------------

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var scopedProvider = scope.ServiceProvider;

        var userManager = scopedProvider.GetRequiredService<UserManager<IdentityUser>>();
        var appDbContext = scopedProvider.GetRequiredService<AppDbContext>();
        var tenantStore = scopedProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        await EnsureTenantAsync(tenantStore, TenantA);

        var roleManager = scopedProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        if (!await roleManager.RoleExistsAsync("TenantAdmin"))
        {
            await roleManager.CreateAsync(new ApplicationRole("TenantAdmin")
            {
                Code = "TenantAdmin",
                DisplayName = "Tenant Administrator",
                IsSystem = true,
                NormalizedName = "TENANTADMIN"
            });
        }

        if (!await roleManager.RoleExistsAsync("TenantUser"))
        {
            await roleManager.CreateAsync(new ApplicationRole("TenantUser")
            {
                Code = "TenantUser",
                DisplayName = "Tenant User",
                IsSystem = true,
                NormalizedName = "TENANTUSER"
            });
        }

        foreach (var entry in PermissionCatalog.All)
        {
            if (!appDbContext.Permissions.Any(p => p.Code == entry.Code))
            {
                var permission = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (permission.IsSuccess)
                {
                    appDbContext.Permissions.Add(permission.Value);
                }
            }
        }

        await appDbContext.SaveChangesAsync();

        var adminRole = await roleManager.FindByNameAsync("TenantAdmin");
        if (adminRole is not null)
        {
            var existingPermissionIds = appDbContext.RolePermissions
                .Where(rp => rp.RoleId == adminRole.Id)
                .Select(rp => rp.PermissionId)
                .ToHashSet();

            foreach (var permission in appDbContext.Permissions.ToList())
            {
                if (existingPermissionIds.Contains(permission.Id)) continue;
                appDbContext.RolePermissions.Add(RolePermission.Create(adminRole.Id, permission.Id).Value);
            }

            await appDbContext.SaveChangesAsync();
        }
    }

    private static async Task EnsureTenantAsync(IMultiTenantStore<CenterixTenantInfo> store, string id)
    {
        if (await store.TryGetAsync(id) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = id,
                Identifier = id,
                Name = id,
                Email = $"{id}@test.com",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task<IdentityUser> EnsureUserAsync(string email, string password = "Admin@12345")
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null) return user;

        user = new IdentityUser
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

    private async Task<(string Token, Guid InvitationId)> CreateInvitationViaApiAsync(
        string adminToken, string email, string roleName = "TenantUser", int expirationDays = 7)
    {
        _factory.EmailSender.Clear();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.Add("tenant", TenantA);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, roleName, expirationDays }),
            Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var invitationId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

        var sentEmail = Assert.Single(_factory.EmailSender.Sent);
        Assert.Equal(email, sentEmail.To);
        return (TestInviteTokens.ExtractTokenFromEmailBody(sentEmail.Body), invitationId);
    }

    private async Task SeedInvitationDirectlyAsync(
        string email, string token, InvitationStatus status, int expiresInDays = 7, string? tenantId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inviter = await db.TenantMemberships.FirstAsync(m => m.TenantId == TenantA);

        // Domain validation requires a FUTURE expiry at creation; a past expiry is applied
        // afterwards via reflection so tests can exercise the runtime expiry-check path.
        var createdAtExpiry = DateTimeOffset.UtcNow.AddDays(expiresInDays > 0 ? expiresInDays : 7);

        var invitationResult = TenantInvitation.Create(
            Guid.NewGuid(),
            tenantId ?? TenantA,
            email,
            inviter.UserId,
            "TenantUser",
            TestInviteTokens.Sha256Hex(token),
            createdAtExpiry);
        Assert.True(invitationResult.IsSuccess);
        var invitation = invitationResult.Value;

        switch (status)
        {
            case InvitationStatus.Revoked:
                invitation.Revoke(inviter.UserId);
                break;
            case InvitationStatus.Accepted:
                invitation.Accept(inviter.UserId);
                break;
            case InvitationStatus.Expired:
                invitation.MarkExpired();
                break;
            case InvitationStatus.Pending when expiresInDays <= 0:
                typeof(TenantInvitation).GetProperty(nameof(TenantInvitation.ExpiresAtUtc))!
                    .SetValue(invitation, DateTimeOffset.UtcNow.AddDays(expiresInDays));
                break;
        }

        db.TenantInvitations.Add(invitation);
        await db.SaveChangesAsync();
    }

    private async Task<string> LoginAsAdminAsync()
    {
        var admin = await EnsureUserAsync("admin-reg@test.com");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.TenantMemberships.Any(m => m.UserId == admin.Id && m.TenantId == TenantA))
        {
            var membership = TenantMembership.Create(admin.Id, TenantA, "TenantAdmin", TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        return _factory.GenerateTestToken(admin.Id, admin.Email!, ["TenantAdmin"]);
    }

    private static HttpRequestMessage AnonymousPost(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    // ------------------------------------------------------------------
    // Registration from invitation (new users, anonymous endpoint)
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_NewUser_ValidToken_CreatesAccountMembershipAndAccepts()
    {
        await SeedAsync();
        var adminToken = await LoginAsAdminAsync();
        var (token, _) = await CreateInvitationViaApiAsync(adminToken, "newuser1@test.com");

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token, password = "Str0ng!Pass1" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Side effects persisted: user exists, membership Active with invited role, invitation Accepted.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var user = await userManager.FindByEmailAsync("newuser1@test.com");
        Assert.NotNull(user);

        var membership = db.TenantMemberships.Single(m => m.UserId == user!.Id && m.TenantId == TenantA);
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);

        var invitation = db.TenantInvitations.Single(i => i.Id != Guid.Empty && i.NormalizedEmail == "NEWUSER1@TEST.COM" && i.TenantId == TenantA && i.Status == InvitationStatus.Accepted);
        Assert.Equal(user!.Id, invitation.AcceptedByUserId);
        Assert.NotNull(invitation.AcceptedAtUtc);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_InvalidToken_Returns401_AndPersistsNothing()
    {
        await SeedAsync();

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token = "definitely-not-a-real-token", password = "Str0ng!Pass1" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_ExpiredPendingInvitation_MarksExpired_Returns409_InvitationUnusable()
    {
        await SeedAsync();
        await LoginAsAdminAsync(); // seeds the TenantA admin membership used as InvitedByUserId
        var token = TestInviteTokens.NewToken();
        await SeedInvitationDirectlyAsync("expired@test.com", token, InvitationStatus.Pending, expiresInDays: -1);

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token, password = "Str0ng!Pass1" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invitation = db.TenantInvitations.Single(i => i.TokenHash == TestInviteTokens.Sha256Hex(token));
        Assert.Equal(InvitationStatus.Expired, invitation.Status);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_RevokedInvitation_Returns409()
    {
        await SeedAsync();
        await LoginAsAdminAsync();
        var token = TestInviteTokens.NewToken();
        await SeedInvitationDirectlyAsync("revoked@test.com", token, InvitationStatus.Revoked);

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token, password = "Str0ng!Pass1" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_AlreadyAcceptedInvitation_Returns409()
    {
        await SeedAsync();
        await LoginAsAdminAsync();
        var token = TestInviteTokens.NewToken();
        await SeedInvitationDirectlyAsync("accepted@test.com", token, InvitationStatus.Accepted);

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token, password = "Str0ng!Pass1" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_EmailAlreadyExists_Returns409_NoDuplicateAccount()
    {
        await SeedAsync();
        await EnsureUserAsync("alreadyexists@test.com");

        var adminToken = await LoginAsAdminAsync();
        var (token, _) = await CreateInvitationViaApiAsync(adminToken, "alreadyexists@test.com");

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token, password = "Str0ng!Pass1" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Still exactly one account for the e-mail and invitation remains Pending (usable via login+accept).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, db.Users.Count(u => u.Email == "alreadyexists@test.com"));
        var invitation = db.TenantInvitations.Single(i => i.NormalizedEmail == "ALREADYEXISTS@TEST.COM");
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_WeakPassword_FailsValidation_NoPartialState()
    {
        await SeedAsync();
        var adminToken = await LoginAsAdminAsync();
        var (token, _) = await CreateInvitationViaApiAsync(adminToken, "weakpw@test.com");

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token, password = "weak" }));

        Assert.False(response.IsSuccessStatusCode);

        // Atomicity: no account was created and the invitation stays usable (Pending).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, db.Users.Count(u => u.Email == "weakpw@test.com"));
        var invitation = db.TenantInvitations.Single(i => i.NormalizedEmail == "WEAKPW@TEST.COM");
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Register_Anonymous_IsAllowed_ByDesign()
    {
        // Explicit regression test for the fallback-policy defect: a brand-new invited person has no
        // credentials, so the endpoint MUST be reachable anonymously ([AllowAnonymous]).
        await SeedAsync();
        var adminToken = await LoginAsAdminAsync();
        var (token, _) = await CreateInvitationViaApiAsync(adminToken, "anonflow@test.com");

        var response = await _client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token, password = "Str0ng!Pass1" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Environment-aware invitation links (H1)
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task InvitationLink_UsesConfiguredApplicationBaseUrl_NotHardcodedLocalhost()
    {
        await SeedAsync();
        var adminToken = await LoginAsAdminAsync();

        await CreateInvitationViaApiAsync(adminToken, "urlcheck@test.com");

        var sentEmail = Assert.Single(_factory.EmailSender.Sent);
        Assert.Contains(TestBaseUrl, sentEmail.Body);
        Assert.DoesNotContain("localhost:5000", sentEmail.Body);

        // The extracted token must round-trip through the generated absolute link.
        var token = TestInviteTokens.ExtractTokenFromEmailBody(sentEmail.Body);
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    // ------------------------------------------------------------------
    // Accepting an invitation as an EXISTING authenticated user
    // ------------------------------------------------------------------

    private async Task<(HttpRequestMessage Request, IdentityUser User)> AcceptRequestForAsync(
        string rawToken, string userEmail)
    {
        var user = await EnsureUserAsync(userEmail);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invitations/{rawToken}/accept");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.GenerateTestToken(user.Id, user.Email!, []));
        request.Headers.Add("tenant", TenantA);
        return (request, user);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Accept_ExistingUser_ValidToken_CreatesActiveMembership()
    {
        await SeedAsync();
        var adminToken = await LoginAsAdminAsync();
        var (token, _) = await CreateInvitationViaApiAsync(adminToken, "existingaccept@test.com");

        var (request, user) = await AcceptRequestForAsync(token, "existingaccept@test.com");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = db.TenantMemberships.Single(m => m.UserId == user.Id && m.TenantId == TenantA);
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Accept_DifferentAuthenticatedUser_Returns403_InvitationStaysPending()
    {
        await SeedAsync();
        var adminToken = await LoginAsAdminAsync();
        var (token, _) = await CreateInvitationViaApiAsync(adminToken, "intended@test.com");

        // Both accounts exist; the invitation is addressed to intended@test.com but consumed by
        // another authenticated user — the e-mail/principal binding must reject this.
        await EnsureUserAsync("intended@test.com");
        var other = await EnsureUserAsync("otheruser@test.com");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invitations/{token}/accept");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.GenerateTestToken(other.Id, other.Email!, []));
        request.Headers.Add("tenant", TenantA);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invitation = db.TenantInvitations.Single(i => i.NormalizedEmail == "INTENDED@TEST.COM");
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
        Assert.Empty(db.TenantMemberships.Where(m => m.TenantId == TenantA && m.UserId == other.Id));
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Accept_ReactivatesRevokedMembership()
    {
        await SeedAsync();
        var adminToken = await LoginAsAdminAsync();

        // Pre-existing REVOKED membership for the invited e-mail.
        var member = await EnsureUserAsync("reactivate@test.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = TenantMembership.Create(member.Id, TenantA, "TenantUser", TenantMembershipStatus.Revoked);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        var (token, _) = await CreateInvitationViaApiAsync(adminToken, "reactivate@test.com");
        var (request, _) = await AcceptRequestForAsync(token, "reactivate@test.com");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var reactivated = db2.TenantMemberships.Single(m => m.UserId == member.Id && m.TenantId == TenantA);
        Assert.Equal(TenantMembershipStatus.Active, reactivated.Status);
    }

    [Fact]
    [Trait("Category", "InvitationRegistration")]
    public async Task Accept_Unauthenticated_Returns401_ByDesign()
    {
        await SeedAsync();
        await LoginAsAdminAsync();
        var token = TestInviteTokens.NewToken();
        await SeedInvitationDirectlyAsync("anonaccept@test.com", token, InvitationStatus.Pending);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invitations/{token}/accept");
        request.Headers.Add("tenant", TenantA);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
