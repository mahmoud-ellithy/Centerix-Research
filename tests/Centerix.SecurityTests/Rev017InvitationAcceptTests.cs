using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
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

// REV-017: invitation acceptance can never escalate a returning member's role, never
// reactivates a Suspended member, and is atomic (single-use).
[Collection("Integration")]
public class Rev017InvitationAcceptTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Rev017InvitationAcceptTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.EmailSender.Clear();
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public async Task RevokedTenantAdmin_ReinvitedAsTenantUser_EndsAsTenantUser_SingleRow()
    {
        var seed = await SeedTenantAsync();
        var email = $"returning_{Guid.NewGuid():N}@rev017.test";
        var user = await CreateUserAsync(email);
        await SeedMembershipAsync(user.Id, seed.TenantId.ToString(), "TenantAdmin", TenantMembershipStatus.Revoked);

        var rawToken = await CreateInvitationViaApiAsync(seed, email, "TenantUser");
        var response = await AcceptAsync(seed, rawToken, user, email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = Assert.Single(db.TenantMemberships
            .Where(m => m.UserId == user.Id && m.TenantId == seed.TenantId.ToString()));
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public async Task RevokedTenantUser_ReinvitedAsTenantAdmin_EndsAsTenantAdmin()
    {
        var seed = await SeedTenantAsync();
        var email = $"returning_{Guid.NewGuid():N}@rev017.test";
        var user = await CreateUserAsync(email);
        await SeedMembershipAsync(user.Id, seed.TenantId.ToString(), "TenantUser", TenantMembershipStatus.Revoked);

        var rawToken = await CreateInvitationViaApiAsync(seed, email, "TenantAdmin");
        var response = await AcceptAsync(seed, rawToken, user, email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = Assert.Single(db.TenantMemberships
            .Where(m => m.UserId == user.Id && m.TenantId == seed.TenantId.ToString()));
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantAdmin", membership.RoleName);
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public async Task SuspendedMember_Accept_Returns403_MembershipUnchanged_InvitationStaysPending()
    {
        var seed = await SeedTenantAsync();
        var email = $"suspended_{Guid.NewGuid():N}@rev017.test";
        var user = await CreateUserAsync(email);
        await SeedMembershipAsync(user.Id, seed.TenantId.ToString(), "TenantUser", TenantMembershipStatus.Suspended);

        var rawToken = await CreateInvitationViaApiAsync(seed, email, "TenantUser");
        var response = await AcceptAsync(seed, rawToken, user, email);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = Assert.Single(db.TenantMemberships
            .Where(m => m.UserId == user.Id && m.TenantId == seed.TenantId.ToString()));
        Assert.Equal(TenantMembershipStatus.Suspended, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);

        var invitation = db.TenantInvitations.Single(i => i.TokenHash == TestInviteTokens.Sha256Hex(rawToken));
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public async Task DoubleAccept_SecondReturns409_SingleMembership()
    {
        var seed = await SeedTenantAsync();
        var email = $"double_{Guid.NewGuid():N}@rev017.test";
        var user = await CreateUserAsync(email);

        var rawToken = await CreateInvitationViaApiAsync(seed, email, "TenantUser");
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(seed, rawToken, user, email)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AcceptAsync(seed, rawToken, user, email)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(db.TenantMemberships
            .Where(m => m.UserId == user.Id && m.TenantId == seed.TenantId.ToString()));
    }

    // ============================================================
    // Seeding helpers
    // ============================================================
    private sealed record Seed(string AdminToken, Guid TenantId, string Identifier);

    private async Task<Seed> SeedTenantAsync()
    {
        var identifier = $"r17-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        foreach (var entry in PermissionCatalog.All)
        {
            if (!db.Permissions.Any(p => p.Code == entry.Code))
            {
                var permission = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (permission.IsSuccess) db.Permissions.Add(permission.Value);
            }
        }
        await db.SaveChangesAsync();

        async Task<ApplicationRole> EnsureRoleAsync(string name)
        {
            var role = await roleManager.FindByNameAsync(name);
            if (role is not null) return role;
            var created = new ApplicationRole(name)
            {
                Code = name, DisplayName = name, IsSystem = true, NormalizedName = name.ToUpperInvariant()
            };
            await roleManager.CreateAsync(created);
            return created;
        }

        await EnsureRoleAsync("PlatformAdmin");
        var tenantAdminRole = await EnsureRoleAsync("TenantAdmin");
        await EnsureRoleAsync("TenantUser");

        var pid = db.Permissions.Single(p => p.Code == Permissions.Invitations.Create).Id;
        if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole.Id && rp.PermissionId == pid))
            db.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, pid).Value);
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@rev017.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r17@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r17@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
            await tenantDb.SaveChangesAsync();
        }

        var adminEmail = $"tadmin_{Guid.NewGuid():N}@rev017.test";
        var admin = new IdentityUser
        {
            Email = adminEmail, UserName = adminEmail, EmailConfirmed = true,
            NormalizedEmail = adminEmail.ToUpperInvariant(), NormalizedUserName = adminEmail.ToUpperInvariant()
        };
        admin.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(admin, StrongPassword);
        await userManager.CreateAsync(admin);
        await userManager.AddToRoleAsync(admin, "TenantAdmin");

        db.TenantMemberships.Add(TenantMembership.Create(
            admin.Id, tenant.Id.ToString(), "TenantAdmin", TenantMembershipStatus.Active).Value);
        await db.SaveChangesAsync();

        // REV-011 Users reservation needs an active subscription + counter row (the seeded
        // admin is an Active member, so the first-owner exemption does not apply here).
        var plan = Plan.Create(
            0, $"R17{Guid.NewGuid():N}"[..24], "REV017 Plan", 100m,
            100, 10, 10, 40, 50, 1000, true, null, "USD", 12, 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenant.Id.ToString(), plan.Id, 100m, 100m, "USD", 12, 0,
            DateTime.UtcNow, false, SubscriptionStatus.Pending,
            100, 10, 10, 40, 50, 500).Value;
        Assert.True(sub.Activate(DateTime.UtcNow).IsSuccess);
        db.TenantPlans.Add(sub);

        db.TenantUsageCounters.Add(TenantUsageCounter.Create(
            tenant.Id, 0, 1, 0, 0, 0, 0, 100, 10, 10, 40, DateTime.UtcNow));
        await db.SaveChangesAsync();

        return new Seed(
            _factory.GenerateTestToken(admin.Id, adminEmail, ["TenantAdmin"]),
            tenant.Id, identifier);
    }

    private async Task SeedMembershipAsync(
        string userId, string tenantId, string role, TenantMembershipStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = TenantMembership.Create(userId, tenantId, role, status);
        Assert.True(membership.IsSuccess);
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
    }

    private async Task<IdentityUser> CreateUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null) return existing;

        var user = new IdentityUser
        {
            Email = email, UserName = email, EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task<string> CreateInvitationViaApiAsync(Seed seed, string email, string roleName)
    {
        _factory.EmailSender.Clear();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        request.Headers.Add("tenant", seed.Identifier);
        request.Content = JsonContent.Create(new { email, roleName, expirationDays = 7 });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var sent = Assert.Single(_factory.EmailSender.Sent);
        Assert.Equal(email, sent.To);
        return TestInviteTokens.ExtractTokenFromEmailBody(sent.Body);
    }

    private async Task<HttpResponseMessage> AcceptAsync(
        Seed seed, string rawToken, IdentityUser user, string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invitations/{rawToken}/accept");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.GenerateTestToken(user.Id, email, []));
        request.Headers.Add("tenant", seed.Identifier);
        return await _client.SendAsync(request);
    }
}
