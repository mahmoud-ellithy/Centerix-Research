namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Branches;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// T22 — End-to-end HTTP authorization proof over the REAL application pipeline
/// (no mocked authorization handler). The test factory mirrors the PRODUCTION role matrix
/// (Permissions.GetTenantAdminPermissions/GetTenantUserPermissions), so every expectation
/// below is the real, seeded production behavior.
/// Actors: Anonymous, TenantUser, TenantAdmin, PlatformAdmin, WrongTenantAdmin.
/// </summary>
[Trait("Category", "T22Http")]
public class Task22_AuthorizationHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private string _tenantA = null!;
    private string _tenantB = null!;
    private IdentityUser _adminA = null!;
    private IdentityUser _userA = null!;
    private IdentityUser _adminB = null!;
    private IdentityUser _platformAdmin = null!;
    private IdentityUser _forgedUser = null!;

    public Task22_AuthorizationHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // A. PlatformAdmin verification over HTTP
    // ==================================================================

    [Fact]
    public async Task PlatformAdmin_PlatformRead_Allowed()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/plans", null, Token(_platformAdmin, "PlatformAdmin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_PlatformTenantsRead_Allowed()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/tenants", null, Token(_platformAdmin, "PlatformAdmin"));
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_TenantScopedEndpoint_WithoutMembership_Denied()
    {
        // PlatformAdmin is NOT a magic cross-tenant bypass: tenant-scoped endpoints still
        // require an active TenantMembership in the resolved tenant (endpoint semantics preserved).
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/students", _tenantA, Token(_platformAdmin, "PlatformAdmin"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForgedPlatformAdminClaim_WithoutDbRole_PlatformRead_Denied()
    {
        // The JWT claims PlatformAdmin, but the identity store never granted the role.
        // Before T22 the claim alone authorized the request.
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/plans", null, Token(_forgedUser, "PlatformAdmin"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RevokedPlatformAdmin_StaleToken_PlatformRead_Denied()
    {
        // Token legitimately minted as PlatformAdmin; role revoked afterwards in the store.
        await SeedAsync();
        var token = Token(_platformAdmin, "PlatformAdmin");

        var before = await SendAsync(HttpMethod.Get, "/api/plans", null, token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var stored = await userManager.FindByIdAsync(_platformAdmin.Id);
            await userManager.RemoveFromRoleAsync(stored!, "PlatformAdmin");
        }

        var after = await SendAsync(HttpMethod.Get, "/api/plans", null, token);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task LockedOutPlatformAdmin_Denied()
    {
        await SeedAsync();
        var token = Token(_platformAdmin, "PlatformAdmin");

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var stored = await userManager.FindByIdAsync(_platformAdmin.Id);
            await userManager.SetLockoutEnabledAsync(stored!, true);
            await userManager.SetLockoutEndDateAsync(stored!, DateTimeOffset.UtcNow.AddHours(1));
        }

        var response = await SendAsync(HttpMethod.Get, "/api/plans", null, token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_CannotBecomePlatformAdmin_ViaTenantMembership()
    {
        // TenantAdmin holds the maximum tenant-side grants; platform endpoints must still deny.
        await SeedAsync();
        var read = await SendAsync(HttpMethod.Get, "/api/plans", _tenantA, Token(_adminA, "TenantAdmin"));
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var write = await SendAsync(HttpMethod.Post, "/api/plans", _tenantA, Token(_adminA, "TenantAdmin"),
            new { name = "X", description = "X", durationMonths = 1, price = 1m, currency = "EGP" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task TenantUser_PlatformRead_Denied()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/plans", _tenantA, Token(_userA, "TenantUser"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlatformPermission_GrantedToTenantRole_StillCannotAuthorizePlatformEndpoint()
    {
        // Scope integrity: even if a tenant role is misconfigured with a PLATFORM permission row,
        // the tenant membership path can never authorize a platform-scoped endpoint.
        await SeedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var tenantAdminRole = await roleManager.FindByNameAsync("TenantAdmin");
            var plansRead = db.Permissions.Single(p => p.Code == Permissions.Plans.Read);
            if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole!.Id && rp.PermissionId == plansRead.Id))
            {
                db.RolePermissions.Add(RolePermission.Create(tenantAdminRole!.Id, plansRead.Id).Value);
                await db.SaveChangesAsync();
            }
        }

        var response = await SendAsync(HttpMethod.Get, "/api/plans", _tenantA, Token(_adminA, "TenantAdmin"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // B. Tenant endpoints under the production role matrix
    // ==================================================================

    [Fact]
    public async Task Anonymous_TenantEndpoint_Unauthorized()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/students", _tenantA, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_PlatformEndpoint_Unauthorized()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/plans", null, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_TenantRead_Allowed()
    {
        // Students.Read is part of the PRODUCTION TenantAdmin matrix (fixed by T22).
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/students", _tenantA, Token(_adminA, "TenantAdmin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_TenantWrite_PermissionGranted_CommercialGateMayApply()
    {
        // Branches.Create is part of the PRODUCTION TenantAdmin matrix (fixed by T22).
        // The request must NOT fail at the authorization layer (401/403); without an active
        // subscription the commercial limit gate answers Conflict instead.
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Post, "/api/branches", _tenantA, Token(_adminA, "TenantAdmin"),
            new { name = "Main", address = "A", phone = (string?)null, managerId = (Guid?)null, isActive = true });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_TenantRead_Allowed()
    {
        // Students.Read is part of the PRODUCTION TenantUser matrix (fixed by T22).
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/students", _tenantA, Token(_userA, "TenantUser"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_TenantWrite_Denied()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Post, "/api/students", _tenantA, Token(_userA, "TenantUser"),
            new { fullNameAr = "طالب", fullNameEn = "Student", branchId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_FeatureGate_StillApplies_EvenWithPermission()
    {
        // Objective H: permission and subscription-feature are independent dimensions.
        // TenantAdmin HAS Students.Create, but the tenant has no subscription feature → 403.
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Post, "/api/students", _tenantA, Token(_adminA, "TenantAdmin"),
            new { fullNameAr = "طالب", fullNameEn = "Student", branchId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // C. Sensitive financial + tenant lifecycle endpoints
    // ==================================================================

    [Fact]
    public async Task TenantAdmin_RefundApprove_Denied()
    {
        // Refunds.Approve is platform-only (D-01); TenantAdmin must receive 403.
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Post, $"/api/refunds/{Guid.NewGuid()}/approve", _tenantA,
            Token(_adminA, "TenantAdmin"), null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_RefundCreate_Denied()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Post, "/api/refunds", _tenantA, Token(_userA, "TenantUser"),
            new { paymentId = Guid.NewGuid(), amount = 10m, reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_TenantLifecycleSuspend_Denied()
    {
        // Tenant lifecycle is platform-scoped; TenantAdmin must never suspend a tenant.
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Post, $"/api/tenants/{Guid.NewGuid()}/suspend", null,
            Token(_adminA, "TenantAdmin"), new { reason = "abuse" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForgedPlatformAdminClaim_TenantLifecycleSuspend_Denied()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Post, $"/api/tenants/{Guid.NewGuid()}/suspend", null,
            Token(_forgedUser, "PlatformAdmin"), new { reason = "abuse" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // D. Cross-tenant isolation
    // ==================================================================

    [Fact]
    public async Task TenantAdmin_CrossTenantHeader_Denied()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/students", _tenantB, Token(_adminA, "TenantAdmin"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdminB_CrossTenantHeader_Denied()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/students", _tenantA, Token(_adminB, "TenantAdmin"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_CrossTenantHeader_Denied()
    {
        await SeedAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/students", _tenantB, Token(_userA, "TenantUser"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_CrossTenantResourceId_NotFound()
    {
        // A branch that exists in tenant B is invisible (404) to tenant A through the query filter.
        await SeedAsync();

        Guid branchId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var branch = Branch.Create(Guid.NewGuid(), "Tenant B Branch").Value;
            branchId = branch.Id;
            db.Branches.Add(branch);
            db.StampAddedTenantIds(_tenantB);
            await db.SaveChangesAsync();
        }

        var response = await SendAsync(HttpMethod.Get, $"/api/branches/{branchId}", _tenantA, Token(_adminA, "TenantAdmin"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ==================================================================
    // Seeding / helpers
    // ==================================================================

    private async Task SeedAsync()
    {
        _tenantA = $"t22a-{Guid.NewGuid():N}";
        _tenantB = $"t22b-{Guid.NewGuid():N}";

        await _factory.SeedPermissionsAsync();

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        await AddTenantAsync(store, _tenantA);
        await AddTenantAsync(store, _tenantB);

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

        _adminA = await CreateUserAsync(userManager, $"admin_a_{Guid.NewGuid():N}@t22.test");
        _userA = await CreateUserAsync(userManager, $"user_a_{Guid.NewGuid():N}@t22.test");
        _adminB = await CreateUserAsync(userManager, $"admin_b_{Guid.NewGuid():N}@t22.test");
        _platformAdmin = await CreateUserAsync(userManager, $"pa_{Guid.NewGuid():N}@t22.test");
        _forgedUser = await CreateUserAsync(userManager, $"forged_{Guid.NewGuid():N}@t22.test");

        // The forged user holds a legitimate TENANT identity — only the JWT claim is a lie.
        await userManager.AddToRoleAsync(_adminA, "TenantAdmin");
        await userManager.AddToRoleAsync(_userA, "TenantUser");
        await userManager.AddToRoleAsync(_adminB, "TenantAdmin");
        await userManager.AddToRoleAsync(_platformAdmin, "PlatformAdmin");
        await userManager.AddToRoleAsync(_forgedUser, "TenantAdmin");

        AddMembership(db, _adminA.Id, _tenantA, "TenantAdmin");
        AddMembership(db, _userA.Id, _tenantA, "TenantUser");
        AddMembership(db, _adminB.Id, _tenantB, "TenantAdmin");
        await db.SaveChangesAsync();
    }

    private static async Task AddTenantAsync(IMultiTenantStore<CenterixTenantInfo> store, string tenantId)
    {
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@t22.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private static void AddMembership(AppDbContext db, string userId, string tenantId, string roleName)
    {
        if (!db.TenantMemberships.Any(m => m.UserId == userId && m.TenantId == tenantId))
        {
            db.TenantMemberships.Add(
                TenantMembership.Create(userId, tenantId, roleName, TenantMembershipStatus.Active).Value);
        }
    }

    private static async Task<IdentityUser> CreateUserAsync(UserManager<IdentityUser> userManager, string email)
    {
        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private string Token(IdentityUser user, params string[] roles) =>
        _factory.GenerateTestToken(user.Id, user.Email!, roles);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? tenantHeader, string? token, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(tenantHeader))
            request.Headers.Add("tenant", tenantHeader);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        else if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request);
    }
}
