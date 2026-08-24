using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

[Collection("Integration")]
public class InvitationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private const string TenantA = "inv-tenant-a";
    private const string TenantB = "inv-tenant-b";

    public InvitationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedTestDataAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var scopedProvider = scope.ServiceProvider;

        var userManager = scopedProvider.GetRequiredService<UserManager<IdentityUser>>();
        var appDbContext = scopedProvider.GetRequiredService<AppDbContext>();
        var tenantStore = scopedProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        await CreateTenantIfNotExistsAsync(tenantStore, TenantA, "Invitation Tenant A", true);
        await CreateTenantIfNotExistsAsync(tenantStore, TenantB, "Invitation Tenant B", true);
        await ResetTenantStatusAsync(tenantStore, TenantA, true);
        await ResetTenantStatusAsync(tenantStore, TenantB, true);

        var adminUser = await CreateUserIfNotExistsAsync(userManager, "admin-inv@test.com", "Admin@12345");
        await ResetMembershipStatusAsync(appDbContext, adminUser.Id, TenantA, "TenantAdmin", TenantMembershipStatus.Active);

        await SeedRolesAndPermissionsAsync(appDbContext, userManager);
    }

    private async Task ResetTenantStatusAsync(IMultiTenantStore<CenterixTenantInfo> store, string tenantId, bool isActive)
    {
        var tenant = await store.TryGetAsync(tenantId);
        if (tenant != null && tenant.IsActive != isActive)
        {
            tenant.IsActive = isActive;
            tenant.ValidUpTo = DateTime.UtcNow.AddYears(1);
            await store.TryUpdateAsync(tenant);
        }
    }

    private async Task ResetMembershipStatusAsync(AppDbContext context, string userId, string tenantId, string roleName, TenantMembershipStatus desiredStatus)
    {
        var existing = context.TenantMemberships
            .FirstOrDefault(m => m.UserId == userId && m.TenantId == tenantId);
        if (existing == null)
        {
            var membership = TenantMembership.Create(userId, tenantId, roleName, desiredStatus);
            if (membership.IsSuccess)
            {
                context.TenantMemberships.Add(membership.Value);
                await context.SaveChangesAsync();
            }
        }
        else if (existing.Status != desiredStatus && desiredStatus == TenantMembershipStatus.Active)
        {
            existing.Activate();
            await context.SaveChangesAsync();
        }
    }

    private async Task SeedRolesAndPermissionsAsync(AppDbContext context, UserManager<IdentityUser> userManager)
    {
        // Seed permissions
        foreach (var entry in PermissionCatalog.All)
        {
            if (!context.Permissions.Any(p => p.Code == entry.Code))
            {
                var permission = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (permission.IsSuccess)
                    context.Permissions.Add(permission.Value);
            }
        }
        await context.SaveChangesAsync();

        // Ensure TenantAdmin role exists
        var roleManager = _factory.Services.GetRequiredService<RoleManager<ApplicationRole>>();
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

        // Assign permissions to roles
        var tenantAdminRole = await roleManager.FindByNameAsync("TenantAdmin");
        if (tenantAdminRole != null)
        {
            var existingRolePermissions = context.RolePermissions.Where(rp => rp.RoleId == tenantAdminRole.Id).ToList();
            var existingPermissionIds = new HashSet<int>(existingRolePermissions.Select(rp => rp.PermissionId));

            var allPermissions = context.Permissions.ToList();
            foreach (var permission in allPermissions)
            {
                if (!existingPermissionIds.Contains(permission.Id))
                {
                    context.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, permission.Id).Value);
                }
            }
            await context.SaveChangesAsync();
        }
    }

    private async Task CreateTenantIfNotExistsAsync(IMultiTenantStore<CenterixTenantInfo> store, string id, string name, bool isActive)
    {
        var existing = await store.TryGetAsync(id);
        if (existing == null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = id,
                Identifier = id,
                Name = name,
                Email = $"{id}@test.com",
                IsActive = isActive,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task<IdentityUser> CreateUserIfNotExistsAsync(UserManager<IdentityUser> userManager, string email, string password)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user == null)
        {
            user = new IdentityUser
            {
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                PhoneNumberConfirmed = true,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant()
            };

            var passwordHasher = new PasswordHasher<IdentityUser>();
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await userManager.CreateAsync(user);
        }
        return user;
    }

    private async Task CreateMembershipIfNotExistsAsync(AppDbContext context, string userId, string tenantId, string roleName, TenantMembershipStatus status)
    {
        if (!context.TenantMemberships.Any(m => m.UserId == userId && m.TenantId == tenantId))
        {
            var membership = TenantMembership.Create(userId, tenantId, roleName, status);
            if (membership.IsSuccess)
            {
                context.TenantMemberships.Add(membership.Value);
                await context.SaveChangesAsync();
            }
        }
    }

    private string GenerateTokenForUser(string userId, string email, IList<string> roles)
    {
        return _factory.GenerateTestToken(userId, email, roles);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, string tenantHeader, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(tenantHeader))
        {
            request.Headers.Add("tenant", tenantHeader);
        }
        return request;
    }

    // ============================================================
    // TEST 1: Create invitation succeeds for authorized user
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test1_CreateInvitation_SucceedsForAuthorizedUser()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("admin-inv@test.com");

        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["TenantAdmin"]);
        var request = CreateRequest(HttpMethod.Post, "/api/invitations", TenantA, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email = "newuser@test.com", roleName = "TenantUser", expirationDays = 7 }),
            Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ============================================================
    // TEST 2: Cannot create duplicate active invitation
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test2_CreateInvitation_DuplicateActiveInvitationRejected()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("admin-inv@test.com");

        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["TenantAdmin"]);

        // Create first invitation
        var request1 = CreateRequest(HttpMethod.Post, "/api/invitations", TenantA, token);
        request1.Content = new StringContent(
            JsonSerializer.Serialize(new { email = "duplicate@test.com", roleName = "TenantUser" }),
            Encoding.UTF8, "application/json");
        await _client.SendAsync(request1);

        // Try to create duplicate
        var request2 = CreateRequest(HttpMethod.Post, "/api/invitations", TenantA, token);
        request2.Content = new StringContent(
            JsonSerializer.Serialize(new { email = "duplicate@test.com", roleName = "TenantUser" }),
            Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ============================================================
    // TEST 3: Cannot invite already active member
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test3_CreateInvitation_AlreadyActiveMemberRejected()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("admin-inv@test.com");

        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["TenantAdmin"]);

        // Try to invite the admin who is already a member
        var request = CreateRequest(HttpMethod.Post, "/api/invitations", TenantA, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email = "admin-inv@test.com", roleName = "TenantUser" }),
            Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ============================================================
    // TEST 4: Get invitations returns list
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test4_GetInvitations_ReturnsList()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("admin-inv@test.com");

        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["TenantAdmin"]);
        var request = CreateRequest(HttpMethod.Get, "/api/invitations", TenantA, token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ============================================================
    // TEST 5: Accept invitation with invalid token fails
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test5_AcceptInvitation_InvalidTokenFails()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("admin-inv@test.com");

        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["TenantAdmin"]);
        var request = CreateRequest(HttpMethod.Post, "/api/invitations/invalid-token-abc/accept", TenantA, token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ============================================================
    // TEST 6: Revoke invitation succeeds
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test6_RevokeInvitation_Succeeds()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("admin-inv@test.com");

        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["TenantAdmin"]);

        // Create invitation
        var createRequest = CreateRequest(HttpMethod.Post, "/api/invitations", TenantA, token);
        createRequest.Content = new StringContent(
            JsonSerializer.Serialize(new { email = "revoke@test.com", roleName = "TenantUser" }),
            Encoding.UTF8, "application/json");
        var createResponse = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Get invitation ID from the database
        using var scope2 = _factory.Services.CreateScope();
        var dbContext = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var invitation = dbContext.TenantInvitations
            .FirstOrDefault(i => i.Email == "revoke@test.com" && i.TenantId == TenantA);
        Assert.NotNull(invitation);

        // Revoke invitation
        var revokeRequest = CreateRequest(HttpMethod.Post, $"/api/invitations/{invitation!.Id}/revoke", TenantA, token);
        var revokeResponse = await _client.SendAsync(revokeRequest);

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
    }

    // ============================================================
    // TEST 7: Unauthenticated user cannot create invitation
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test7_Unauthenticated_CannotCreateInvitation()
    {
        await SeedTestDataAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        request.Headers.Add("tenant", TenantA);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email = "test@test.com", roleName = "TenantUser" }),
            Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ============================================================
    // TEST 8: Cross-tenant invitation is rejected
    // ============================================================
    [Fact]
    [Trait("Category", "Invitation")]
    public async Task Test8_CrossTenant_InvitationRejected()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("admin-inv@test.com");

        // Admin has membership in TenantA, try to invite to TenantB
        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["TenantAdmin"]);
        var request = CreateRequest(HttpMethod.Post, "/api/invitations", TenantB, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email = "cross@test.com", roleName = "TenantUser" }),
            Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
