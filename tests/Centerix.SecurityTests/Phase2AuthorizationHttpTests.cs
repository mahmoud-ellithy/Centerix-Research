using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

/// <summary>
/// Phase 2 HTTP authorization matrix: platform admins perform commercial workflows; tenant
/// admins are denied every one of them; feature/limit/subscription gates block business
/// operations regardless of permissions. Runs on the InMemory factory (fast); relational
/// guarantees behind these gates are proven by Phase2SqlServerTests.
/// </summary>
[Collection("Integration")]
public class Phase2AuthorizationHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Phase2AuthorizationHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // Seeding
    // ==================================================================

    /// <returns>(platformToken, tenantAdminToken, tenantId, tenantIdentifier)</returns>
    private async Task<(string PlatformToken, string TenantAdminToken, Guid TenantId, string TenantIdentifier)>
        SeedAsync()
    {
        var identifier = $"p2-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        // Permission catalog + roles.
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

        // TenantAdmin: student create/read (for gate tests) + own-subscription visibility.
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

        // PendingApproval domain tenant + registry projection.
        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@p2.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p2@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
        }

        // CRITICAL: the REGISTRY CONTEXT must contain the row too — ITenantRegistrySync
        // persists dual-context changes only when the projection row already exists.
        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p2@test.com", IsActive = false,
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

        var platformUser = await EnsureUserAsync($"platform_{Guid.NewGuid():N}@p2.test", "PlatformAdmin");
        var tenantAdmin = await EnsureUserAsync($"tadmin_{Guid.NewGuid():N}@p2.test", "TenantAdmin");

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

    private async Task<int> CreatePlanAsync(string platformToken, int durationMonths = 12, int bonusMonths = 1)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        request.Content = JsonContent.Create(new
        {
            code = $"P2{Guid.NewGuid():N}"[..24],
            displayName = "Phase 2 Plan",
            monthlyPrice = 199.99m,
            maxStudents = 50,
            maxUsers = 5,
            maxBranches = 2,
            maxTeachers = 10,
            storageGB = 20,
            smsQuota = 100,
            isActive = true,
            description = "HTTP test plan",
            currencyCode = "USD",
            durationMonths,
            bonusMonths
        });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Plans.OrderByDescending(p => p.Id).Select(p => p.Id).FirstAsync();
    }

    private static HttpRequestMessage Post(string url, object payload, string? token = null, string? tenantHeader = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null)
            request.Headers.Add("tenant", tenantHeader);
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private static HttpRequestMessage Get(string url, string token, string tenantHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("tenant", tenantHeader);
        return request;
    }

    private async Task<(Guid SubscriptionId, DateTime EffectiveEnd)> ApproveAndActivateAsync(
        string platformToken, Guid tenantId, int planId)
    {
        var approved = await _client.SendAsync(Post(
            $"/api/tenants/{tenantId}/approve", new { tenantId, planId }, platformToken));
        Assert.Equal(HttpStatusCode.Created, approved.StatusCode);

        var activated = await _client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"/api/tenants/{tenantId}/activate")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", platformToken) }
        });
        Assert.Equal(HttpStatusCode.NoContent, activated.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sub = await db.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.TenantId == tenantId.ToString());
        return (sub.Id, sub.EffectiveEndsAtUtc);
    }

    // ==================================================================
    // Onboarding workflow + boundary
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task Approve_PlatformAdmin_CreatesActiveSubscription_ProvisioningTenant_ValidUpToSynced()
    {
        var (platformToken, _, tenantId, _) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);

        var response = await _client.SendAsync(Post(
            $"/api/tenants/{tenantId}/approve", new { tenantId, planId }, platformToken));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        Assert.Equal(LifecycleStatus.Provisioning, tenant.LifecycleStatus);

        var subscription = await db.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.TenantId == tenantId.ToString());
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.NotNull(subscription.ActivatedAtUtc);
        Assert.Equal(subscription.EffectiveEndsAtUtc, tenant.ValidUpTo); // single source of truth mirrored
    }

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task Approve_TenantAdmin_IsForbidden()
    {
        var (_, tenantAdminToken, tenantId, identifier) = await SeedAsync();

        var response = await _client.SendAsync(Post(
            $"/api/tenants/{tenantId}/approve", new { tenantId, planId = 99999 },
            tenantAdminToken, identifier));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task Reject_PlatformAdmin_Works_TenantAdmin_Denied()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();

        var denied = await _client.SendAsync(Post(
            $"/api/tenants/{tenantId}/reject", new { tenantId, reason = "self-reject" },
            tenantAdminToken, identifier));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var ok = await _client.SendAsync(Post(
            $"/api/tenants/{tenantId}/reject", new { tenantId, reason = "incomplete docs" }, platformToken));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(LifecycleStatus.Rejected, (await db.Tenants.SingleAsync(t => t.Id == tenantId)).LifecycleStatus);
    }

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task Activate_PendingCannotSkipCommercialGate_ThenPlatformCompletesOnboarding()
    {
        var (platformToken, tenantAdminToken, tenantId, _) = await SeedAsync();

        // Premature activation BEFORE approval fails even for the platform admin.
        var premature = await _client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"/api/tenants/{tenantId}/activate")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", platformToken) }
        });
        Assert.False(premature.IsSuccessStatusCode);

        var planId = await CreatePlanAsync(platformToken);
        await ApproveAndActivateAsync(platformToken, tenantId, planId); // includes provisioning completion

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(LifecycleStatus.Active, (await db.Tenants.SingleAsync(t => t.Id == tenantId)).LifecycleStatus);

        // And a tenant admin can NEVER reach this workflow.
        var denied = await _client.SendAsync(Post(
            $"/api/tenants/{tenantId}/activate", new { tenantId }, tenantAdminToken));
        Assert.False(denied.IsSuccessStatusCode);
    }

    // ==================================================================
    // Subscription workflow boundary + lifecycle effects
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task Renew_PlatformAdmin_ExtendsTerm_TenantAdmin_Denied_AndNothingChanges()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);
        var (subscriptionId, effectiveBefore) = await ApproveAndActivateAsync(platformToken, tenantId, planId);

        var denied = await _client.SendAsync(Post("/api/tenantplans/renew",
            new { tenantId, additionalMonths = 6 }, tenantAdminToken, identifier));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using (var check = _factory.Services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
            var unchanged = await db.TenantPlans.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(tp => tp.Id == subscriptionId);
            Assert.Equal(effectiveBefore, unchanged.EffectiveEndsAtUtc);
            Assert.Equal(12, unchanged.DurationMonths);
        }

        var ok = await _client.SendAsync(Post("/api/tenantplans/renew",
            new { tenantId, additionalMonths = 6, additionalBonusMonths = 1 }, platformToken));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await vdb.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.Id == subscriptionId);
        Assert.Equal(18, after.DurationMonths);
        Assert.Equal(2, after.BonusMonths);
        Assert.True(after.EffectiveEndsAtUtc > effectiveBefore);
    }

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task SuspendSubscription_BlocksImmediately_Reactivate_Restores()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);
        var (subscriptionId, _) = await ApproveAndActivateAsync(platformToken, tenantId, planId);

        var suspend = await _client.SendAsync(Post("/api/tenantplans/suspend",
            new { tenantId, reason = "non-payment" }, platformToken));
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);

        var stateDuringSuspension = await GetMySubscriptionAsync(tenantAdminToken, identifier);
        Assert.Equal("Suspended", stateDuringSuspension.Status);
        Assert.False(stateDuringSuspension.IsActiveNow);

        var reactivate = await _client.SendAsync(Post("/api/tenantplans/activate",
            new { tenantId }, platformToken));
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);

        var restored = await GetMySubscriptionAsync(tenantAdminToken, identifier);
        Assert.Equal("Active", restored.Status);
        Assert.True(restored.IsActiveNow);
    }

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task CancelSubscription_EndsCommercialAccess_TenantAdmin_Denied()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);
        var (subscriptionId, _) = await ApproveAndActivateAsync(platformToken, tenantId, planId);

        var denied = await _client.SendAsync(Post("/api/tenantplans/cancel",
            new { tenantId, reason = "self-cancel" }, tenantAdminToken, identifier));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var ok = await _client.SendAsync(Post("/api/tenantplans/cancel",
            new { tenantId, reason = "churn" }, platformToken));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SubscriptionStatus.Cancelled,
            (await db.TenantPlans.IgnoreQueryFilters().AsNoTracking().SingleAsync(tp => tp.Id == subscriptionId)).Status);
    }

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task MySubscription_ReturnsOwnSnapshot_IncludingBonus()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken, durationMonths: 3, bonusMonths: 2);
        await ApproveAndActivateAsync(platformToken, tenantId, planId);

        var sub = await GetMySubscriptionAsync(tenantAdminToken, identifier);
        Assert.Equal("Active", sub.Status);
        Assert.True(sub.IsActiveNow);
        Assert.Equal(3, sub.DurationMonths);
        Assert.Equal(2, sub.BonusMonths); // bonus auditable, not hidden inside a computed date
    }

    /// <summary>IDOR: tenant A's admin requesting tenant B's context is rejected by the guard.</summary>
    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task MySubscription_CrossTenantContext_IsRejectedByGuard()
    {
        var (platformTokenA, tenantAdminTokenA, tenantIdA, identifierA) = await SeedAsync();
        var planIdA = await CreatePlanAsync(platformTokenA);
        await ApproveAndActivateAsync(platformTokenA, tenantIdA, planIdA);

        var (platformTokenB, tenantAdminTokenB, tenantIdB, identifierB) = await SeedAsync();
        var planIdB = await CreatePlanAsync(platformTokenB);
        await ApproveAndActivateAsync(platformTokenB, tenantIdB, planIdB);

        // Tenant B's admin claims TENANT A's context: guard denies (no membership in A).
        var crossTenant = await _client.SendAsync(Get("/api/tenantplans/me", tenantAdminTokenB, identifierA));
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);

        // Their own context works and never leaks tenant A's data.
        var own = await GetMySubscriptionAsync(tenantAdminTokenB, identifierB);
        Assert.True(own.IsActiveNow);
    }

    // ==================================================================
    // Feature / limit / expiration gating on a REAL business operation
    // ==================================================================

    private async Task SeedUsageCounterAsync(Guid tenantId, int studentsUsed, int studentsMax)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TenantUsageCounters.Add(TenantUsageCounter.Create(
            tenantId, studentsUsed, 0, 0, 0, 0, 0,
            studentsMax, studentsMax, studentsMax, studentsMax, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

private static object StudentPayload() => new
    {
        branchId = Guid.NewGuid(),
        stageId = 1,
        yearId = 1,
        fullNameAr = "Ø·Ø§Ù„Ø¨ Ø§Ø®ØªØ¨Ø§Ø±",
        fullNameEn = (string?)"Test Student",
        gender = 1, // Gender.Male
        phone = "01000000000",
        qrCode = $"QR-{Guid.NewGuid():N}",
        status = 0, // StudentStatus.Active
        enrolledAt = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
    };

private async Task EnsureFeatureOnPlanAsync(int planId, string featureCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.Features.FirstOrDefaultAsync(f => f.Code == featureCode);
        if (feature is null)
        {
            var result = Centerix.Domain.Platform.Features.Feature.Create(0, featureCode.Trim(), "Seeded for test", "Core");
            feature = result.Value;
            db.Features.Add(feature);
            await db.SaveChangesAsync();
        }
        var existing = await db.PlanFeatures.FirstOrDefaultAsync(pf => pf.PlanId == planId && pf.FeatureId == feature.Id);
        if (existing is null)
        {
            var pfResult = Centerix.Domain.Platform.Plans.PlanFeature.Create(0, planId, feature.Id, true);
            db.PlanFeatures.Add(pfResult.Value);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
[Trait("Category", "Phase2Http")]
public async Task BusinessWrite_FeatureGranted_LimitExhausted_DeniedByLimit()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);
        await EnsureFeatureOnPlanAsync(planId, FeatureCodes.StudentManagement);
        var (subscriptionId, _) = await ApproveAndActivateAsync(platformToken, tenantId, planId);
        await SeedUsageCounterAsync(tenantId, studentsUsed: 50, studentsMax: 1); // quota exhausted (plan snapshot maxStudents = 50)

        var response = await _client.SendAsync(Post(
            "/api/students", StudentPayload(), tenantAdminToken, identifier));

        var body = await response.Content.ReadAsStringAsync();
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("limit", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Phase2Http")]
    public async Task BusinessWrite_FeatureMissing_PermissionPresent_IsDenied()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);
        await ApproveAndActivateAsync(platformToken, tenantId, planId);
        // NO Students entitlement snapshot.

        var response = await _client.SendAsync(Post(
            "/api/students", StudentPayload(), tenantAdminToken, identifier));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
[Trait("Category", "Phase2Http")]
    public async Task BusinessWrite_ExpiredSubscription_BlockedDespitePermissionAndFeature()
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);
        await EnsureFeatureOnPlanAsync(planId, FeatureCodes.StudentManagement);
        var (subscriptionId, _) = await ApproveAndActivateAsync(platformToken, tenantId, planId);

        using (var expire = _factory.Services.CreateScope())
        {
            var db = expire.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = await db.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.Id == subscriptionId);
            typeof(TenantPlan).GetProperty(nameof(TenantPlan.EffectiveEndsAtUtc))!
                .SetValue(sub, DateTime.UtcNow.AddDays(-1));
            await db.SaveChangesAsync();
        }

        var response = await _client.SendAsync(Post(
            "/api/students", StudentPayload(), tenantAdminToken, identifier));

        // Fail-closed: expired â‡’ feature gate cannot succeed â‡’ 403 (never a silent pass).
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private sealed record MySubView(string Status, bool IsActiveNow, int DurationMonths, int BonusMonths);

    private async Task<MySubView> GetMySubscriptionAsync(string tenantAdminToken, string identifier)
    {
        var response = await _client.SendAsync(Get("/api/tenantplans/me", tenantAdminToken, identifier));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new MySubView(
            root.GetProperty("status").GetString()!,
            root.GetProperty("isActiveAsOfNow").GetBoolean(),
            root.GetProperty("durationMonths").GetInt32(),
            root.GetProperty("bonusMonths").GetInt32());
    }
}


