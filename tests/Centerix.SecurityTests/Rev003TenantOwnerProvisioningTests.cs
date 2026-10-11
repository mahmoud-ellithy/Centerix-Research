using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

// REV-003: a new tenant can get its first TenantAdmin using only platform-authorized HTTP calls.
[Collection("Integration")]
public class Rev003TenantOwnerProvisioningTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Rev003TenantOwnerProvisioningTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.EmailSender.Clear();
    }

    // ============================================================
    // E2E: create → approve → activate → provision-owner → register → login → tenant call
    // ============================================================
    [Fact]
    [Trait("Category", "Rev003")]
    public async Task ProvisionOwner_EndToEnd_NewTenantGetsWorkingFirstTenantAdmin()
    {
        await _factory.SeedPermissionsAsync();
        var platformToken = await CreatePlatformAdminAsync("rev003e2e");
        var ownerEmail = $"owner_{Guid.NewGuid():N}@rev003.test";

        var tenantId = await CreateTenantViaApiAsync(platformToken, ownerEmail);
        var planId = await CreatePlanViaApiAsync(platformToken);
        await ApproveAndActivateViaApiAsync(platformToken, tenantId, planId);

        var provisionResponse = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.Created, provisionResponse.StatusCode);

        var rawToken = TestInviteTokens.ExtractTokenFromEmailBody(
            Assert.Single(_factory.EmailSender.Sent).Body);

        // New account registers from the owner invitation.
        var registerResponse = await SendAsync(HttpMethod.Post, "/api/invitations/register",
            token: null, tenantHeader: null,
            payload: new { token = rawToken, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        // Owner logs in and calls a tenant endpoint with the tenant header → 200.
        var accessToken = await LoginAsync(ownerEmail, StrongPassword);
        var tenantCall = await SendAsync(
            HttpMethod.Get, "/api/invitations", accessToken, tenantId.ToString());
        Assert.Equal(HttpStatusCode.OK, tenantCall.StatusCode);

        // The membership row is a TenantAdmin.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var owner = await userManager.FindByEmailAsync(ownerEmail);
        Assert.NotNull(owner);
        var membership = db.TenantMemberships
            .Single(m => m.UserId == owner!.Id && m.TenantId == tenantId.ToString());
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantAdmin", membership.RoleName);
    }

    // ============================================================
    // Authorization: tenant roles denied, anonymous denied
    // ============================================================
    [Fact]
    [Trait("Category", "Rev003")]
    public async Task ProvisionOwner_TenantAdminAndTenantUser_AreForbidden_Anonymous_IsUnauthorized()
    {
        await _factory.SeedPermissionsAsync();
        var platformToken = await CreatePlatformAdminAsync("rev003auth");
        var tenantId = await CreateTenantViaApiAsync(platformToken, $"owner_{Guid.NewGuid():N}@rev003.test");
        var planId = await CreatePlanViaApiAsync(platformToken);
        await ApproveAndActivateViaApiAsync(platformToken, tenantId, planId);

        // Seed a tenant admin + tenant user with memberships in the new tenant.
        var (tenantAdminToken, tenantUserToken) = await SeedTenantMembersAsync(tenantId.ToString());

        var adminCall = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            tenantAdminToken, tenantId.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, adminCall.StatusCode);

        var userCall = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            tenantUserToken, tenantId.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, userCall.StatusCode);

        var anonymous = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            token: null, tenantHeader: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ============================================================
    // Lifecycle / existence guards
    // ============================================================
    [Fact]
    [Trait("Category", "Rev003")]
    public async Task ProvisionOwner_NonApprovedTenant_Returns409_UnknownTenant_Returns404()
    {
        await _factory.SeedPermissionsAsync();
        var platformToken = await CreatePlatformAdminAsync("rev003life");

        // PendingApproval tenant (never approved) → 409.
        var pendingId = await CreateTenantViaApiAsync(platformToken, $"owner_{Guid.NewGuid():N}@rev003.test");
        var pendingCall = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{pendingId}/provision-owner",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.Conflict, pendingCall.StatusCode);

        // Unknown tenant → 404.
        var unknownCall = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{Guid.NewGuid()}/provision-owner",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.NotFound, unknownCall.StatusCode);
    }

    [Fact]
    [Trait("Category", "Rev003")]
    public async Task ProvisionOwner_SecondCall_Resends_OldInvitationRevoked_NewPending()
    {
        await _factory.SeedPermissionsAsync();
        var platformToken = await CreatePlatformAdminAsync("rev003resend");
        var tenantId = await CreateTenantViaApiAsync(platformToken, $"owner_{Guid.NewGuid():N}@rev003.test");
        var planId = await CreatePlanViaApiAsync(platformToken);
        await ApproveAndActivateViaApiAsync(platformToken, tenantId, planId);

        var first = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstToken = TestInviteTokens.ExtractTokenFromEmailBody(
            Assert.Single(_factory.EmailSender.Sent).Body);
        var firstHash = TestInviteTokens.Sha256Hex(firstToken);

        _factory.EmailSender.Clear();
        var second = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invitations = db.TenantInvitations
            .Where(i => i.TenantId == tenantId.ToString())
            .ToList();
        Assert.Equal(2, invitations.Count);
        Assert.Equal(InvitationStatus.Revoked, invitations.Single(i => i.TokenHash == firstHash).Status);
        Assert.Single(invitations.Where(i => i.Status == InvitationStatus.Pending));

        // The fresh invitation is delivered and usable.
        Assert.Single(_factory.EmailSender.Sent);
    }

    [Fact]
    [Trait("Category", "Rev003")]
    public async Task ProvisionOwner_OwnerAlreadyMember_Returns409()
    {
        await _factory.SeedPermissionsAsync();
        var platformToken = await CreatePlatformAdminAsync("rev003member");
        var ownerEmail = $"owner_{Guid.NewGuid():N}@rev003.test";
        var tenantId = await CreateTenantViaApiAsync(platformToken, ownerEmail);
        var planId = await CreatePlanViaApiAsync(platformToken);
        await ApproveAndActivateViaApiAsync(platformToken, tenantId, planId);

        // Complete onboarding once.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            platformToken, tenantHeader: null)).StatusCode);
        var rawToken = TestInviteTokens.ExtractTokenFromEmailBody(
            Assert.Single(_factory.EmailSender.Sent).Body);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post,
            "/api/invitations/register", token: null, tenantHeader: null,
            payload: new { token = rawToken, password = StrongPassword })).StatusCode);

        // Provisioning again now that the owner is an Active member → 409.
        var repeat = await SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
    }

    // ============================================================
    // Delivery failure: compensation, no Pending left behind
    // ============================================================
    [Fact]
    [Trait("Category", "Rev003")]
    public async Task ProvisionOwner_EmailSendFailure_Compensated_NoPendingInvitation()
    {
        await _factory.SeedPermissionsAsync();
        var platformToken = await CreatePlatformAdminAsync("rev003fail");
        var ownerEmail = $"owner_{Guid.NewGuid():N}@rev003.test";
        var tenantId = await CreateTenantViaApiAsync(platformToken, ownerEmail);
        var planId = await CreatePlanViaApiAsync(platformToken);
        await ApproveAndActivateViaApiAsync(platformToken, tenantId, planId);

        _factory.EmailSender.Clear();
        _factory.EmailSender.ShouldFail = to =>
            string.Equals(to, ownerEmail, StringComparison.OrdinalIgnoreCase);
        try
        {
            var response = await SendAsync(
                HttpMethod.Post, $"/api/tenants/{tenantId}/provision-owner",
                platformToken, tenantHeader: null);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            _factory.EmailSender.Clear();
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.TenantInvitations
            .Where(i => i.TenantId == tenantId.ToString()
                && i.Status == InvitationStatus.Pending));
    }

    // ============================================================
    // Helpers
    // ============================================================
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? token, string? tenantHeader, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null)
            request.Headers.Add("tenant", tenantHeader);
        // Pin a unique client IP per request so LoginPolicy rate limiting never flakes.
        request.Headers.Add("X-Test-Remote-IP", $"10.3.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        return await _client.SendAsync(request);
    }

    private async Task<string> CreatePlatformAdminAsync(string prefix)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        if (!await roleManager.RoleExistsAsync("PlatformAdmin"))
        {
            await roleManager.CreateAsync(new ApplicationRole("PlatformAdmin")
            {
                Code = "PlatformAdmin",
                DisplayName = "Platform Administrator",
                IsSystem = true,
                NormalizedName = "PLATFORMADMIN"
            });
        }

        var email = $"{prefix}_{Guid.NewGuid():N}@rev003.test";
        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, "PlatformAdmin");

        return _factory.GenerateTestToken(user.Id, email, ["PlatformAdmin"]);
    }

    private async Task<Guid> CreateTenantViaApiAsync(string platformToken, string ownerEmail)
    {
        var slug = $"r3{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/tenants",
            platformToken, tenantHeader: null,
            payload: new
            {
                slug,
                subdomain = slug,
                displayName = $"REV003 {slug}",
                country = "EG",
                currency = "EGP",
                timezone = "Africa/Cairo",
                ownerFirstName = "Owner",
                ownerLastName = "Three",
                ownerEmail,
                isolationMode = 0
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tenants.Where(t => t.Slug == slug).Select(t => t.Id).SingleAsync();
    }

    private async Task<int> CreatePlanViaApiAsync(string platformToken)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/plans",
            platformToken, tenantHeader: null,
            payload: new
            {
                code = $"R3{Guid.NewGuid():N}"[..20],
                displayName = "REV003 Plan",
                monthlyPrice = 99.0m,
                maxStudents = 100,
                maxUsers = 10,
                maxBranches = 5,
                maxTeachers = 20,
                storageGB = 10,
                smsQuota = 100,
                isActive = true,
                description = "REV-003 test plan",
                currencyCode = "USD",
                durationMonths = 12,
                bonusMonths = 0
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Plans.OrderByDescending(p => p.Id).Select(p => p.Id).FirstAsync();
    }

    private async Task ApproveAndActivateViaApiAsync(string platformToken, Guid tenantId, int planId)
    {
        var approve = await SendAsync(HttpMethod.Post, $"/api/tenants/{tenantId}/approve",
            platformToken, tenantHeader: null,
            payload: new { tenantId, planId });
        Assert.Equal(HttpStatusCode.Created, approve.StatusCode);

        var activate = await SendAsync(HttpMethod.Post, $"/api/tenants/{tenantId}/activate",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
    }

    private async Task<(string TenantAdminToken, string TenantUserToken)> SeedTenantMembersAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        async Task<string> SeedMemberAsync(string role)
        {
            var email = $"{role.ToLowerInvariant()}_{Guid.NewGuid():N}@rev003.test";
            var user = new IdentityUser
            {
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant()
            };
            user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
            Assert.True((await userManager.CreateAsync(user)).Succeeded);
            var membership = Centerix.Domain.Platform.Tenants.TenantMembership.Create(
                user.Id, tenantId, role, TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
            return _factory.GenerateTestToken(user.Id, email, [role]);
        }

        return (await SeedMemberAsync("TenantAdmin"), await SeedMemberAsync("TenantUser"));
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/auth/login",
            token: null, tenantHeader: null,
            payload: new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }
}
