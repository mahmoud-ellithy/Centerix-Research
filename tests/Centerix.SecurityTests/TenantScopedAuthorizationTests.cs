using System.Net;
using System.Net.Http.Headers;
using Centerix.Application.Common.Interfaces;
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
/// Tests that permissions are tenant-scoped and cannot leak between tenants.
/// Verifies that a user with different roles in different tenants gets
/// the correct permissions for the current tenant context.
/// </summary>
[Collection("Integration")]
public class TenantScopedAuthorizationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private const string TenantX = "auth-tenant-x";
    private const string TenantY = "auth-tenant-y";

    public TenantScopedAuthorizationTests(TestWebApplicationFactory factory)
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
        var roleManager = scopedProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        await CreateTenantIfNotExistsAsync(tenantStore, TenantX, "Auth Tenant X", true);
        await CreateTenantIfNotExistsAsync(tenantStore, TenantY, "Auth Tenant Y", true);

        await ResetTenantStatusAsync(tenantStore, TenantX, true);
        await ResetTenantStatusAsync(tenantStore, TenantY, true);

        foreach (var entry in PermissionCatalog.All)
        {
            if (!appDbContext.Permissions.Any(p => p.Code == entry.Code))
            {
                var permission = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (permission.IsSuccess)
                    appDbContext.Permissions.Add(permission.Value);
            }
        }
        await appDbContext.SaveChangesAsync();

        await EnsureRoleAsync(roleManager, "TenantAdmin", "Tenant Administrator");
        await EnsureRoleAsync(roleManager, "TenantUser", "Tenant User");

        var adminRole = await roleManager.FindByNameAsync("TenantAdmin");
        if (adminRole != null)
        {
            var allPermissions = appDbContext.Permissions.ToList();
            var existingRolePermissions = appDbContext.RolePermissions.Where(rp => rp.RoleId == adminRole.Id).ToList();
            var existingPermissionIds = new HashSet<int>(existingRolePermissions.Select(rp => rp.PermissionId));

            foreach (var permission in allPermissions)
            {
                if (!existingPermissionIds.Contains(permission.Id))
                {
                    appDbContext.RolePermissions.Add(RolePermission.Create(adminRole.Id, permission.Id).Value);
                }
            }
        }

        var userRole = await roleManager.FindByNameAsync("TenantUser");
        if (userRole != null)
        {
            var readPermissions = appDbContext.Permissions
                .Where(p => p.Action == "Read" || p.Action == "Manage")
                .ToList();
            var existingRolePermissions = appDbContext.RolePermissions.Where(rp => rp.RoleId == userRole.Id).ToList();
            var existingPermissionIds = new HashSet<int>(existingRolePermissions.Select(rp => rp.PermissionId));

            foreach (var permission in readPermissions)
            {
                if (!existingPermissionIds.Contains(permission.Id))
                {
                    appDbContext.RolePermissions.Add(RolePermission.Create(userRole.Id, permission.Id).Value);
                }
            }
        }
        await appDbContext.SaveChangesAsync();

        var multiUser = await CreateUserIfNotExistsAsync(userManager, "multi@test.com", "Multi@12345");

        await ResetMembershipStatusAsync(appDbContext, multiUser.Id, TenantX, "TenantAdmin", TenantMembershipStatus.Active);
        await ResetMembershipStatusAsync(appDbContext, multiUser.Id, TenantY, "TenantUser", TenantMembershipStatus.Active);

        var platformAdmin = await CreateUserIfNotExistsAsync(userManager, "platform@test.com", "Platform@12345");
        await ResetMembershipStatusAsync(appDbContext, platformAdmin.Id, TenantX, "TenantAdmin", TenantMembershipStatus.Active);
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

    private async Task EnsureRoleAsync(RoleManager<ApplicationRole> roleManager, string code, string displayName)
    {
        if (!await roleManager.RoleExistsAsync(code))
        {
            await roleManager.CreateAsync(new ApplicationRole(code)
            {
                Code = code,
                DisplayName = displayName,
                IsSystem = true,
                NormalizedName = code.ToUpperInvariant()
            });
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
    // TEST 1: User with TenantAdmin in X can access X
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test1_TenantAdminInX_CanAccessX()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("multi@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, []);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantX, token);

        var response = await _client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200/204 but got {response.StatusCode}");
    }

    // ============================================================
    // TEST 2: User with TenantUser in Y can access Y
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test2_TenantUserInY_CanAccessY()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("multi@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, []);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantY, token);

        var response = await _client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200/204 but got {response.StatusCode}");
    }

    // ============================================================
    // TEST 3: User without membership in a tenant is denied
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test3_NoMembership_Denied()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("multi@test.com");

        // User doesn't have membership in TenantX's "other" tenant
        var token = GenerateTokenForUser(user!.Id, user.Email!, []);
        var request = CreateRequest(HttpMethod.Get, "/api/students", "nonexistent-tenant", token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 4: Platform admin can access platform-scoped endpoints
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test4_PlatformAdmin_CanAccessPlatformScopedEndpoints()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync("platform@test.com");

        var token = GenerateTokenForUser(admin!.Id, admin.Email!, ["PlatformAdmin"]);
        var request = CreateRequest(HttpMethod.Get, "/api/tenants", string.Empty, token);

        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 5: Suspended membership is denied
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test5_SuspendedMembership_Denied()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await userManager.FindByEmailAsync("multi@test.com");

        // Suspend membership in TenantX
        var membership = appDbContext.TenantMemberships
            .FirstOrDefault(m => m.UserId == user!.Id && m.TenantId == TenantX);
        if (membership != null)
        {
            membership.Suspend();
            await appDbContext.SaveChangesAsync();
        }

        using var scope2 = _factory.Services.CreateScope();
        var userManager2 = scope2.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user2 = await userManager2.FindByEmailAsync("multi@test.com");

        var token = GenerateTokenForUser(user2!.Id, user2.Email!, []);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantX, token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 6: Revoked membership is denied
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test6_RevokedMembership_Denied()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await userManager.FindByEmailAsync("multi@test.com");

        // Revoke membership in TenantX
        var membership = appDbContext.TenantMemberships
            .FirstOrDefault(m => m.UserId == user!.Id && m.TenantId == TenantX);
        if (membership != null)
        {
            membership.Revoke();
            await appDbContext.SaveChangesAsync();
        }

        using var scope2 = _factory.Services.CreateScope();
        var userManager2 = scope2.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user2 = await userManager2.FindByEmailAsync("multi@test.com");

        var token = GenerateTokenForUser(user2!.Id, user2.Email!, []);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantX, token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 7: Deactivated tenant is denied
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test7_DeactivatedTenant_Denied()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        var tenantX = await tenantStore.TryGetAsync(TenantX);
        if (tenantX != null)
        {
            tenantX.IsActive = false;
            await tenantStore.TryUpdateAsync(tenantX);
        }

        using var scope2 = _factory.Services.CreateScope();
        var userManager = scope2.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("multi@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, []);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantX, token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 8: Same user can have different roles in different tenants
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test8_SameUser_DifferentRolesInDifferentTenants()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await userManager.FindByEmailAsync("multi@test.com");

        // Verify user has different roles in different tenants
        var membershipX = appDbContext.TenantMemberships
            .FirstOrDefault(m => m.UserId == user!.Id && m.TenantId == TenantX);
        var membershipY = appDbContext.TenantMemberships
            .FirstOrDefault(m => m.UserId == user!.Id && m.TenantId == TenantY);

        Assert.NotNull(membershipX);
        Assert.NotNull(membershipY);
        Assert.Equal("TenantAdmin", membershipX!.RoleName);
        Assert.Equal("TenantUser", membershipY!.RoleName);
    }

    // ============================================================
    // TEST 9: Membership creation prevents duplicates
    // ============================================================
    [Fact]
    [Trait("Category", "TenantScopedAuth")]
    public async Task Test9_DuplicateMembership_Prevented()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var uniqueEmail = $"dup-test-{Guid.NewGuid():N}@test.com";
        var user = new IdentityUser
        {
            Email = uniqueEmail,
            UserName = uniqueEmail,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = uniqueEmail.ToUpperInvariant(),
            NormalizedUserName = uniqueEmail.ToUpperInvariant()
        };
        var passwordHasher = new PasswordHasher<IdentityUser>();
        user.PasswordHash = passwordHasher.HashPassword(user, "Dup@12345");
        await userManager.CreateAsync(user);

        var membership1 = TenantMembership.Create(user.Id, TenantX, "TenantAdmin", TenantMembershipStatus.Active);
        Assert.True(membership1.IsSuccess);

        var membership2 = TenantMembership.Create(user.Id, TenantX, "TenantAdmin", TenantMembershipStatus.Active);
        Assert.True(membership2.IsSuccess);

        appDbContext.TenantMemberships.Add(membership1.Value);
        await appDbContext.SaveChangesAsync();

        appDbContext.Entry(membership1.Value).State = EntityState.Detached;

        appDbContext.TenantMemberships.Add(membership2.Value);
        await Assert.ThrowsAnyAsync<Exception>(
            () => appDbContext.SaveChangesAsync());
    }
}
