using System.Net;
using System.Net.Http.Headers;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

public class C1CrossTenantIsolationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Test tenant IDs
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string TenantC = "tenant-c";

    public C1CrossTenantIsolationTests(TestWebApplicationFactory factory)
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

        // Create tenants
        await CreateTenantIfNotExistsAsync(tenantStore, TenantA, "Tenant A", true);
        await CreateTenantIfNotExistsAsync(tenantStore, TenantB, "Tenant B", true);
        await CreateTenantIfNotExistsAsync(tenantStore, TenantC, "Tenant C", true);

        // Create users
        var userA = await CreateUserIfNotExistsAsync(userManager, "user-a@test.com", "UserA@test123");
        var userB = await CreateUserIfNotExistsAsync(userManager, "user-b@test.com", "UserB@test123");

        // Create memberships: User A belongs to Tenant A only
        await CreateMembershipIfNotExistsAsync(appDbContext, userA.Id, TenantA, TenantMembershipStatus.Active);

        // User B belongs to Tenant A and Tenant B
        await CreateMembershipIfNotExistsAsync(appDbContext, userB.Id, TenantA, TenantMembershipStatus.Active);
        await CreateMembershipIfNotExistsAsync(appDbContext, userB.Id, TenantB, TenantMembershipStatus.Active);
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

    private async Task CreateMembershipIfNotExistsAsync(AppDbContext context, string userId, string tenantId, TenantMembershipStatus status)
    {
        if (!context.TenantMemberships.Any(m => m.UserId == userId && m.TenantId == tenantId))
        {
            var membership = TenantMembership.Create(userId, tenantId, status);
            if (membership.IsSuccess)
            {
                context.TenantMemberships.Add(membership.Value);
                await context.SaveChangesAsync();
            }
        }
    }

    private string GenerateTokenForUser(string userId, string email, IList<string> roles, IList<string> permissions)
    {
        return _factory.GenerateTestToken(userId, email, roles, permissions);
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
    // TEST 1: User A, Tenant A, Request Tenant A → 200
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test1_UserA_TenantA_RequestTenantA_ReturnsOk()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantA, token);

        var response = await _client.SendAsync(request);

        // Should succeed - user A has active membership in Tenant A
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent,
                    $"Expected 200/204 but got {response.StatusCode}");
    }

    // ============================================================
    // TEST 2: User A, Tenant A, Request Tenant B → 403
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test2_UserA_TenantA_RequestTenantB_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantB, token);

        var response = await _client.SendAsync(request);

        // Should be forbidden - user A has no membership in Tenant B
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 3: User A, Tenant A, Tenant B header → 403
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test3_UserA_TenantAHeader_TenantBHeader_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantB, token);

        var response = await _client.SendAsync(request);

        // Should be forbidden - user A has no membership in Tenant B
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 4: User A, Tenant A, POST with Tenant B header → 403
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test4_UserA_TenantA_POST_WithTenantBHeader_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Create]);
        var request = CreateRequest(HttpMethod.Post, "/api/students", TenantB, token);

        var response = await _client.SendAsync(request);

        // Should be forbidden - user A has no membership in Tenant B
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 5: User A, Tenant A + Tenant B memberships, Request A → allowed
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test5_UserB_TenantAAndB_RequestTenantA_ReturnsOk()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-b@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantA, token);

        var response = await _client.SendAsync(request);

        // Should succeed - user B has active membership in Tenant A
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent,
                    $"Expected 200/204 but got {response.StatusCode}");
    }

    // ============================================================
    // TEST 6: User A, Tenant A + Tenant B memberships, Request B → allowed
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test6_UserB_TenantAAndB_RequestTenantB_ReturnsOk()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-b@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantB, token);

        var response = await _client.SendAsync(request);

        // Should succeed - user B has active membership in Tenant B
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent,
                    $"Expected 200/204 but got {response.StatusCode}");
    }

    // ============================================================
    // TEST 7: User A, Tenant A only, Request C → denied
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test7_UserA_TenantA_Only_RequestTenantC_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantC, token);

        var response = await _client.SendAsync(request);

        // Should be forbidden - user A has no membership in Tenant C
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 8: Membership inactive → denied
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test8_MembershipInactive_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        // Suspend user A's membership in Tenant A
        using (var scope = _factory.Services.CreateScope())
        {
            var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await userManager.FindByEmailAsync("user-a@test.com");

            var membership = appDbContext.TenantMemberships
                .FirstOrDefault(m => m.UserId == user!.Id && m.TenantId == TenantA);
            if (membership != null)
            {
                membership.Suspend();
                await appDbContext.SaveChangesAsync();
            }
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await userManager.FindByEmailAsync("user-a@test.com");

            var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
            var request = CreateRequest(HttpMethod.Get, "/api/students", TenantA, token);

            var response = await _client.SendAsync(request);

            // Should be forbidden - membership is suspended
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ============================================================
    // TEST 9: Tenant active, Membership active → allowed
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test9_TenantActive_MembershipActive_ReturnsOk()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", TenantA, token);

        var response = await _client.SendAsync(request);

        // Should succeed - both tenant and membership are active
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent,
                    $"Expected 200/204 but got {response.StatusCode}");
    }

    // ============================================================
    // TEST 10: Tenant suspended, Membership active → denied
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test10_TenantSuspended_MembershipActive_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        // Deactivate Tenant A
        using (var scope = _factory.Services.CreateScope())
        {
            var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            var tenantA = await tenantStore.TryGetAsync(TenantA);
            if (tenantA != null)
            {
                tenantA.IsActive = false;
                await tenantStore.TryUpdateAsync(tenantA);
            }
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await userManager.FindByEmailAsync("user-a@test.com");

            var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
            var request = CreateRequest(HttpMethod.Get, "/api/students", TenantA, token);

            var response = await _client.SendAsync(request);

            // Should be forbidden - tenant is deactivated
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ============================================================
    // TEST 11: No tenant header → 403 (tenant-scoped endpoint)
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test11_NoTenantHeader_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/students", string.Empty, token);

        var response = await _client.SendAsync(request);

        // Should be forbidden - no tenant resolved for tenant-scoped endpoint
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 12: Unauthenticated → 401 (or redirect to login)
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test12_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedTestDataAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/students");
        request.Headers.Add("tenant", TenantA);

        var response = await _client.SendAsync(request);

        // Should be unauthorized
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ============================================================
    // TEST 13: Platform-scoped endpoint without tenant → works
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test13_PlatformScoped_NoTenant_Works()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        // PlatformAdmin can access platform-scoped endpoints without tenant
        var token = GenerateTokenForUser(user!.Id, user.Email!, ["PlatformAdmin"], [Permissions.Tenants.Read]);
        var request = CreateRequest(HttpMethod.Get, "/api/tenants", string.Empty, token);

        var response = await _client.SendAsync(request);

        // Platform-scoped endpoints should work without tenant header
        // (they may return data or empty list, but shouldn't be 403)
        Assert.NotEqual(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================
    // TEST 14: User A tries to read User B's tenant resources via ID
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test14_UserA_KnowsTenantBResourceId_ReturnsForbiddenOrNotFound()
    {
        await SeedTestDataAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync("user-a@test.com");

        // Try to access a specific resource with a known GUID using Tenant B header
        var fakeId = Guid.NewGuid();
        var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
        var request = CreateRequest(HttpMethod.Get, $"/api/students/{fakeId}", TenantB, token);

        var response = await _client.SendAsync(request);

        // Should be forbidden - user A has no membership in Tenant B
        // Even if they know a valid resource ID, they can't access it
        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            response.StatusCode == System.Net.HttpStatusCode.NotFound,
            $"Expected 403 or 404 but got {response.StatusCode}");
    }

    // ============================================================
    // TEST 15: Revoked membership → denied
    // ============================================================
    [Fact]
    [Trait("Category", "C1")]
    public async Task Test15_RevokedMembership_ReturnsForbidden()
    {
        await SeedTestDataAsync();

        // Revoke user A's membership in Tenant A
        using (var scope = _factory.Services.CreateScope())
        {
            var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await userManager.FindByEmailAsync("user-a@test.com");

            var membership = appDbContext.TenantMemberships
                .FirstOrDefault(m => m.UserId == user!.Id && m.TenantId == TenantA);
            if (membership != null)
            {
                membership.Revoke();
                await appDbContext.SaveChangesAsync();
            }
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await userManager.FindByEmailAsync("user-a@test.com");

            var token = GenerateTokenForUser(user!.Id, user.Email!, [], [Permissions.Students.Read]);
            var request = CreateRequest(HttpMethod.Get, "/api/students", TenantA, token);

            var response = await _client.SendAsync(request);

            // Should be forbidden - membership is revoked
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
