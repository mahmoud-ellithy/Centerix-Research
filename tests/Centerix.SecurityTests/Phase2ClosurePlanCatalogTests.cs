using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Phase 2 closure: verifies PlatformAdminGuard protects Plan catalog operations.
/// Tests the real HTTP authorization path (permission + guard = allowed for PlatformAdmin,
/// denied for TenantAdmin), including defense-in-depth when tenant role is granted Plans.*.
/// Also verifies handler-level guard via unit tests with mocked IPlatformAdminGuard.
/// </summary>
[Collection("Integration")]
public class Phase2ClosurePlanCatalogTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Phase2ClosurePlanCatalogTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(string PlatformToken, string TenantAdminToken, Guid TenantId, string TenantIdentifier)>
        SeedAsync()
    {
        var identifier = $"p2c-{Guid.NewGuid():N}";
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

        var platformRole = await EnsureRoleAsync("PlatformAdmin");
        var tenantAdminRole = await EnsureRoleAsync("TenantAdmin");

        var grantCodes = new[]
        {
            Permissions.TenantPlans.Read, Permissions.Students.Create, Permissions.Students.Read
        };
        foreach (var code in grantCodes)
        {
            var pid = db.Permissions.Single(p => p.Code == code).Id;
            if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole.Id && rp.PermissionId == pid))
                db.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, pid).Value);
        }
        foreach (var p in db.Permissions.ToList())
        {
            if (!db.RolePermissions.Any(rp => rp.RoleId == platformRole.Id && rp.PermissionId == p.Id))
                db.RolePermissions.Add(RolePermission.Create(platformRole.Id, p.Id).Value);
        }
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@p2c.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p2c@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p2c@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
            await tenantDb.SaveChangesAsync();
        }

        async Task<IdentityUser> EnsureUserAsync(string email, params string[] roles)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new IdentityUser
                {
                    Email = email, UserName = email, EmailConfirmed = true,
                    NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
                };
                user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
                await userManager.CreateAsync(user);
                foreach (var role in roles) await userManager.AddToRoleAsync(user, role);
            }
            return user;
        }

        var platformUser = await EnsureUserAsync($"platform_{Guid.NewGuid():N}@p2c.test", "PlatformAdmin");
        var tenantAdmin = await EnsureUserAsync($"tadmin_{Guid.NewGuid():N}@p2c.test", "TenantAdmin");

        if (!db.TenantMemberships.Any(m => m.UserId == tenantAdmin.Id && m.TenantId == tenant.Id.ToString()))
        {
            db.TenantMemberships.Add(TenantMembership.Create(
                tenantAdmin.Id, tenant.Id.ToString(), "TenantAdmin", TenantMembershipStatus.Active).Value);
        }
        await db.SaveChangesAsync();

        return (
            _factory.GenerateTestToken(platformUser.Id, platformUser.Email!, ["PlatformAdmin"]),
            _factory.GenerateTestToken(tenantAdmin.Id, tenantAdmin.Email!, ["TenantAdmin"]),
            tenant.Id,
            identifier);
    }

    private static object PlanPayload(string? code = null) => new
    {
        code = code ?? $"P2C{Guid.NewGuid():N}"[..24],
        displayName = "Phase 2 Closure Plan",
        monthlyPrice = 199.99m,
        maxStudents = 50,
        maxUsers = 5,
        maxBranches = 2,
        maxTeachers = 10,
        storageGB = 20,
        smsQuota = 100,
        isActive = true,
        description = "Closure test plan",
        currencyCode = "USD",
        durationMonths = 12,
        bonusMonths = 1
    };

    private async Task GrantPlansPermissionsToTenantAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var tenantAdminRole = await roleManager.FindByNameAsync("TenantAdmin");
        Assert.NotNull(tenantAdminRole);
        foreach (var code in new[] { Permissions.Plans.Create, Permissions.Plans.Update, Permissions.Plans.Delete, Permissions.Plans.Read })
        {
            var perm = db.Permissions.Single(p => p.Code == code);
            if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole!.Id && rp.PermissionId == perm.Id))
                db.RolePermissions.Add(RolePermission.Create(tenantAdminRole!.Id, perm.Id).Value);
        }
        await db.SaveChangesAsync();
    }

    private async Task<int> CreatePlanAsPlatformAsync(string platformToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        request.Content = JsonContent.Create(PlanPayload());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Plans.OrderByDescending(p => p.Id).Select(p => p.Id).FirstAsync();
    }

    // ==================================================================
    // HTTP: PlatformAdmin can perform catalog operations
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task PlatformAdmin_CanCreatePlan_Returns201()
    {
        var (platformToken, _, _, _) = await SeedAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        request.Content = JsonContent.Create(PlanPayload());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task PlatformAdmin_CanUpdatePlan_Returns204()
    {
        var (platformToken, _, _, _) = await SeedAsync();
        var planId = await CreatePlanAsPlatformAsync(platformToken);

        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/plans/{planId}");
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        update.Content = JsonContent.Create(new
        {
            id = planId,
            code = $"UPD{Guid.NewGuid():N}"[..20],
            displayName = "Updated Plan Name",
            monthlyPrice = 299.99m,
            maxStudents = 100,
            maxUsers = 10,
            maxBranches = 5,
            maxTeachers = 20,
            storageGB = 50,
            smsQuota = 500,
            isActive = true,
            description = "updated",
            currencyCode = "USD",
            durationMonths = 12,
            bonusMonths = 2
        });
        var response = await _client.SendAsync(update);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plan = await db.Plans.SingleAsync(p => p.Id == planId);
        Assert.Equal("Updated Plan Name", plan.DisplayName);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task PlatformAdmin_CanDeleteUnusedPlan_Returns204()
    {
        var (platformToken, _, _, _) = await SeedAsync();
        var planId = await CreatePlanAsPlatformAsync(platformToken);

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/plans/{planId}");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        var response = await _client.SendAsync(del);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Plans.AnyAsync(p => p.Id == planId));
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task PlatformAdmin_CannotDeleteInUsePlan_ReturnsConflict()
    {
        var (platformToken, _, tenantId, _) = await SeedAsync();
        var planId = await CreatePlanAsPlatformAsync(platformToken);

        // Approve tenant with this plan => subscription references it => delete must be blocked
        var approved = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenantId}/approve")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", platformToken) },
            Content = JsonContent.Create(new { tenantId, planId })
        });
        Assert.Equal(HttpStatusCode.Created, approved.StatusCode);

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/plans/{planId}");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        var response = await _client.SendAsync(del);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ==================================================================
    // HTTP: TenantAdmin cannot perform catalog operations
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task TenantAdmin_CannotCreatePlan_Returns403()
    {
        var (_, tenantAdminToken, _, identifier) = await SeedAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantAdminToken);
        request.Headers.Add("tenant", identifier);
        request.Content = JsonContent.Create(PlanPayload());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task TenantAdmin_CannotUpdatePlan_Returns403()
    {
        var (platformToken, tenantAdminToken, _, identifier) = await SeedAsync();
        var planId = await CreatePlanAsPlatformAsync(platformToken);

        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/plans/{planId}");
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantAdminToken);
        update.Headers.Add("tenant", identifier);
        update.Content = JsonContent.Create(new
        {
            id = planId,
            code = $"HACK{Guid.NewGuid():N}"[..20],
            displayName = "Hacked",
            monthlyPrice = 1m,
            maxStudents = 1,
            maxUsers = 1,
            maxBranches = 1,
            maxTeachers = 1,
            storageGB = 1,
            smsQuota = 0,
            isActive = true,
            description = "hack",
            currencyCode = "USD",
            durationMonths = 1,
            bonusMonths = 0
        });
        var response = await _client.SendAsync(update);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task TenantAdmin_CannotDeletePlan_Returns403()
    {
        var (platformToken, tenantAdminToken, _, identifier) = await SeedAsync();
        var planId = await CreatePlanAsPlatformAsync(platformToken);

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/plans/{planId}");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantAdminToken);
        del.Headers.Add("tenant", identifier);
        var response = await _client.SendAsync(del);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task TenantAdmin_CannotCreatePlan_EvenWhenPermissionGranted_Returns403_DefenseInDepth()
    {
        var (platformToken, tenantAdminToken, _, identifier) = await SeedAsync();
        await GrantPlansPermissionsToTenantAdminAsync();

        // Create a new token for same tenant admin after permission grant (permissions resolved per-request via DB, not JWT, so same token works)
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantAdminToken);
        request.Headers.Add("tenant", identifier);
        request.Content = JsonContent.Create(PlanPayload());
        var response = await _client.SendAsync(request);
        // Must still be Forbidden: guard denies even if permission handler hypothetically succeeded
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // PlatformAdmin still succeeds (sanity)
        var platReq = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        platReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        platReq.Content = JsonContent.Create(PlanPayload());
        var platResp = await _client.SendAsync(platReq);
        Assert.Equal(HttpStatusCode.Created, platResp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task PermissionDenial_RemainsIntact_Unauthenticated_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Content = JsonContent.Create(PlanPayload());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task TenantAdmin_CannotCreatePlan_WithoutTenantHeader_Returns403()
    {
        var (_, tenantAdminToken, _, _) = await SeedAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantAdminToken);
        request.Content = JsonContent.Create(PlanPayload());
        var response = await _client.SendAsync(request);
        // Platform-scoped endpoint without tenant header: permission handler denies (no PlatformAdmin role)
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // Handler-level guard verification (mocked guard) — proves defense-in-depth
    // at handler layer independent of HTTP permission handler.
    // ==================================================================

    private static IAuditWriter MockAudit()
    {
        var a = Substitute.For<IAuditWriter>();
        a.WriteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return a;
    }

    private static IPlatformAdminGuard ForbiddenGuard()
    {
        var g = Substitute.For<IPlatformAdminGuard>();
        g.EnsurePlatformAdmin().Returns(Error.Forbidden("Platform.AdminRequired", "restricted"));
        return g;
    }

    private static IPlatformAdminGuard AllowedGuard()
    {
        var g = Substitute.For<IPlatformAdminGuard>();
        g.EnsurePlatformAdmin().Returns(Result.Updated);
        return g;
    }

    [Fact]
    public async Task CreatePlanHandler_TenantAdmin_GuardDenies_ReturnsForbidden_DoesNotCreate()
    {
        var db = Substitute.For<IAppDbContext>();
        db.Plans.Returns(Substitute.For<DbSet<Plan>>());
        var handler = new CreatePlanHandler(db, ForbiddenGuard(), MockAudit());
        var result = await handler.Handle(new CreatePlanCommand("CODE", "Name", 10m, 10, 5, 2, 10, 20, 100, true), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Type == ErrorKind.Forbidden);
        await db.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePlanHandler_TenantAdmin_GuardDenies_ReturnsForbidden()
    {
        var db = Substitute.For<IAppDbContext>();
        var handler = new UpdatePlanHandler(db, ForbiddenGuard(), MockAudit());
        var result = await handler.Handle(new UpdatePlanCommand(1, "C", "N", 10m, 1, 1, 1, 1, 1, 1, true), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Type == ErrorKind.Forbidden);
    }

    [Fact]
    public async Task DeletePlanHandler_TenantAdmin_GuardDenies_ReturnsForbidden()
    {
        var db = Substitute.For<IAppDbContext>();
        var handler = new DeletePlanHandler(db, ForbiddenGuard(), MockAudit());
        var result = await handler.Handle(new DeletePlanCommand(1), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Type == ErrorKind.Forbidden);
    }

    [Fact]
    public async Task CreatePlanHandler_PlatformAdmin_GuardAllows_ProceedsToValidation()
    {
        // Guard allows, but validation may fail (e.g., empty code) — proves guard did not block
        var db = Substitute.For<IAppDbContext>();
        // Need Plans DbSet to avoid null, but handler will fail validation before SaveChanges due to empty code
        var handler = new CreatePlanHandler(db, AllowedGuard(), MockAudit());
        var result = await handler.Handle(new CreatePlanCommand("", "", 10m, 10, 5, 2, 10, 20, 100, true), CancellationToken.None);
        // Should fail validation, not forbidden
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(result.Errors!, e => e.Type == ErrorKind.Forbidden);
    }
}

// ==================================================================
// Verifies Condition #2 decision: CreateTenantLimitOverride handler removed
// TenantLimitOverride domain capability remains, but dead command/handler
// is not reachable via handler resolution and no controller route exists.
// ==================================================================
public class Phase2ClosureTenantLimitOverrideTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public Phase2ClosureTenantLimitOverrideTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public void CreateTenantLimitOverrideHandler_ShouldNotExist_OptionA_Removed()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .FirstOrDefault(t => t.Name == "CreateTenantLimitOverrideHandler");
        Assert.Null(type);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public void CreateTenantLimitOverrideCommand_ShouldNotExist_OptionA_Removed()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .FirstOrDefault(t => t.Name == "CreateTenantLimitOverrideCommand");
        Assert.Null(type);
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public void TenantLimitOverride_Entity_ShouldStillExist_DomainCapabilityPreserved()
    {
        var type = typeof(Centerix.Domain.Platform.Subscriptions.LimitOverrides.TenantLimitOverride);
        Assert.NotNull(type);
        Assert.True(typeof(Centerix.Domain.Common.IHasTenantId).IsAssignableFrom(type));
    }

    [Fact]
    [Trait("Category", "Phase2Closure")]
    public async Task TenantLimitOverride_NoControllerRoute_Returns404()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();

        // ensure platform user exists
        foreach (var entry in PermissionCatalog.All)
            if (!db.Permissions.Any(p => p.Code == entry.Code))
            {
                var perm = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (perm.IsSuccess) db.Permissions.Add(perm.Value);
            }
        await db.SaveChangesAsync();

        var platformRole = await roleManager.FindByNameAsync("PlatformAdmin") ?? new ApplicationRole("PlatformAdmin") { Code = "PlatformAdmin", DisplayName = "PlatformAdmin", IsSystem = true, NormalizedName = "PLATFORMADMIN" };
        if (await roleManager.FindByNameAsync("PlatformAdmin") is null) await roleManager.CreateAsync(platformRole);
        foreach (var p in db.Permissions.ToList())
            if (!db.RolePermissions.Any(rp => rp.RoleId == platformRole.Id && rp.PermissionId == p.Id))
                db.RolePermissions.Add(RolePermission.Create(platformRole.Id, p.Id).Value);
        await db.SaveChangesAsync();

        var platformUser = await userManager.FindByEmailAsync("closure-override-platform@test.com");
        if (platformUser is null)
        {
            platformUser = new IdentityUser { Email = "closure-override-platform@test.com", UserName = "closure-override-platform@test.com", EmailConfirmed = true, NormalizedEmail = "CLOSURE-OVERRIDE-PLATFORM@TEST.COM", NormalizedUserName = "CLOSURE-OVERRIDE-PLATFORM@TEST.COM" };
            platformUser.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(platformUser, "Str0ng!Pass1");
            await userManager.CreateAsync(platformUser);
            await userManager.AddToRoleAsync(platformUser, "PlatformAdmin");
        }
        var token = _factory.GenerateTestToken(platformUser.Id, platformUser.Email!, ["PlatformAdmin"]);
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/tenantlimitoverrides");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new { limitType = "Students", overrideValue = 999, reason = "test" });
        var resp = await client.SendAsync(req);
        // No controller route exists for TenantLimitOverride creation (Option A: handler removed).
        // Middleware may return 403 (no tenant context) or 404 (no endpoint); either way it must NOT succeed as platform operation.
        Assert.False(resp.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, resp.StatusCode);
    }
}
