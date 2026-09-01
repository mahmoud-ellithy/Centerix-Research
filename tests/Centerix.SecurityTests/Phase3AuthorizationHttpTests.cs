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
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;
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
/// Phase 3 HTTP authorization matrix for the education module (M-01):
/// Branches, Students, AcademicStage, AcademicYear. Mirrors the Phase 2 commercial-gate
/// pattern but replaces the platform-admin surface with the tenant-admin business surface.
/// Runs on the InMemory factory (fast); relational guarantees (filtered unique indexes,
/// rowversion concurrency, atomic counter reservation) are proven by Phase3SqlServerTests.
/// </summary>
[Collection("Integration")]
public class Phase3AuthorizationHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Phase3AuthorizationHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // Seeding helpers
    // ==================================================================

    /// <summary>
    /// Seeds: permissions, both roles, a tenant (PendingApproval), an active platform user
    /// and tenant admin user, registers the tenant in both domain + Finbuckle store.
    /// Caller still needs to approve+activate the tenant to flip it Active and unlock
    /// business writes. Returns tokens scoped to the SEEDED tenant context.
    /// </summary>
    private async Task<(string PlatformToken, string TenantAdminToken, Guid TenantId, string TenantIdentifier)>
        SeedAsync()
    {
        var identifier = $"p3-{Guid.NewGuid():N}";
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

        // TenantAdmin holds ALL permissions for the business module so success/denial is
        // determined by commercial gates (feature / limit / expiry) and tenant isolation,
        // never by missing role permissions.
        foreach (var p in db.Permissions.ToList())
        {
            if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole.Id && rp.PermissionId == p.Id))
                db.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, p.Id).Value);
        }
        foreach (var p in db.Permissions.ToList())
        {
            if (!db.RolePermissions.Any(rp => rp.RoleId == platformRole.Id && rp.PermissionId == p.Id))
                db.RolePermissions.Add(RolePermission.Create(platformRole.Id, p.Id).Value);
        }
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@p3.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p3@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p3@test.com", IsActive = false,
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

        var platformUser = await EnsureUserAsync($"platform_{Guid.NewGuid():N}@p3.test", "PlatformAdmin");
        var tenantAdmin = await EnsureUserAsync($"tadmin_{Guid.NewGuid():N}@p3.test", "TenantAdmin");

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

    private async Task<int> CreatePlanAsync(string platformToken,
        int maxStudents = 50, int maxBranches = 5)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        request.Content = JsonContent.Create(new
        {
            code = $"P3{Guid.NewGuid():N}"[..24],
            displayName = "Phase 3 Plan",
            monthlyPrice = 199.99m,
            maxStudents,
            maxUsers = 5,
            maxBranches,
            maxTeachers = 10,
            storageGB = 20,
            smsQuota = 100,
            isActive = true,
            description = "Phase 3 HTTP test plan",
            currencyCode = "USD",
            durationMonths = 12,
            bonusMonths = 0
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

    private static HttpRequestMessage Put(string url, object payload, string? token = null, string? tenantHeader = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
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

    private static HttpRequestMessage Delete(string url, string token, string tenantHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("tenant", tenantHeader);
        return request;
    }

    private async Task<Guid> ApproveAndActivateAsync(
        string platformToken, string tenantAdminToken, Guid tenantId, string identifier, int planId)
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

        // Provision a usage counter with the snapshot limits copied in so limit gates
        // observe the same effective max a fresh tenant would.
        db.TenantUsageCounters.Add(TenantUsageCounter.Create(
            tenantId,
            studentsCount: 0, usersCount: 0, branchesCount: 0, teachersCount: 0,
            storageUsedMB: 0, smsUsedThisCycle: 0,
            effectiveMaxStudents: sub.SnapshotMaxStudents,
            effectiveMaxUsers: sub.SnapshotMaxUsers,
            effectiveMaxBranches: sub.SnapshotMaxBranches,
            effectiveMaxTeachers: sub.SnapshotMaxTeachers,
            calculatedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        return sub.Id;
    }

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

    /// <summary>
    /// Resolves a tenant the caller will use to read/write. Returns platform/admin tokens,
    /// tenant id, identifier, plan id, subscription id — a fully active subscription with
    /// the Students feature granted.
    /// </summary>
    private async Task<Phase3Seed> SeedActiveTenantAsync(
        int maxStudents = 50, int maxBranches = 5, bool withStudentsFeature = true)
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken, maxStudents, maxBranches);
        if (withStudentsFeature) await EnsureFeatureOnPlanAsync(planId, FeatureCodes.StudentManagement);
        var subId = await ApproveAndActivateAsync(platformToken, tenantAdminToken, tenantId, identifier, planId);
        return new Phase3Seed(platformToken, tenantAdminToken, tenantId, identifier, planId, subId);
    }

    private sealed record Phase3Seed(
        string PlatformToken, string TenantAdminToken, Guid TenantId, string Identifier, int PlanId, Guid SubscriptionId);

    private static object BranchPayload(string name = "Main Branch") => new
    {
        name,
        address = "1 Tahrir St",
        phone = "01000000000",
        isActive = true
    };

    private static object StudentPayload(Guid branchId, int stageId, int yearId, string? qr = null) => new
    {
        branchId,
        stageId,
        yearId,
        fullNameAr = "طالب اختبار",
        fullNameEn = (string?)"Test Student",
        gender = 1,
        phone = "01000000000",
        qrCode = qr ?? $"QR-{Guid.NewGuid():N}",
        status = 0,
        enrolledAt = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
    };

    // ==================================================================
    // Branches: full CRUD on the tenant-admin surface
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Branches_TenantAdmin_CanCreateReadUpdateAndDelete()
    {
        var s = await SeedActiveTenantAsync();

        var create = await _client.SendAsync(Post(
            "/api/branches", BranchPayload("Cairo"), s.TenantAdminToken, s.Identifier));
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"Create returned {(int)create.StatusCode}: {createBody}");

        Guid branchId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var branch = db.Branches.IgnoreQueryFilters().Single(b => b.TenantId == s.TenantId.ToString());
            branchId = branch.Id;
            Assert.Equal("Cairo", branch.Name);
            Assert.True(branch.IsActive);
        }

        var list = await _client.SendAsync(Get("/api/branches", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal("Cairo", doc.RootElement[0].GetProperty("name").GetString());
        }

        var single = await _client.SendAsync(Get($"/api/branches/{branchId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);

        var update = await _client.SendAsync(Put(
            $"/api/branches/{branchId}",
            new { id = branchId, name = "Cairo HQ", address = "new", phone = "01000000001", managerId = (Guid?)null },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var delete = await _client.SendAsync(Delete(
            $"/api/branches/{branchId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True((await db.Branches.IgnoreQueryFilters().SingleAsync(b => b.TenantId == s.TenantId.ToString())).IsDeleted());
        }
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Branches_TenantAdmin_CrossTenant_IsForbiddenByGuard()
    {
        var s = await SeedActiveTenantAsync();
        var create = await _client.SendAsync(Post(
            "/api/branches", BranchPayload(), s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var branchId = db.Branches.IgnoreQueryFilters().Single(b => b.TenantId == s.TenantId.ToString()).Id;

        // Build a SECOND tenant; its admin has no membership in tenant A.
        var (platformTokenB, tenantAdminTokenB, tenantIdB, identifierB) = await SeedAsync();
        var planB = await CreatePlanAsync(platformTokenB);
        // Activate tenant B independently (ApproveAndActivateAsync only needs the
        // identifiers — we don't read the Guid back here).
        await ApproveAndActivateAsync(
            platformTokenB, tenantAdminTokenB, tenantIdB, identifierB, planB);


        // Admin of tenant B trying to read tenant A's branch via tenant-A context
        // should be denied by the tenant guard (no membership in A).
        var cross = await _client.SendAsync(Get(
            $"/api/branches/{branchId}", tenantAdminTokenB, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
    }

    // ==================================================================
    // Lookups (AcademicStage / AcademicYear) — Tenant-isolated CRUD
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task AcademicStages_TenantAdmin_CanCreateAndList_AndStageIsIsolatedAcrossTenants()
    {
        var sA = await SeedActiveTenantAsync();
        var sB = await SeedActiveTenantAsync();

        var create = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = " PRIMARY ", displayName = "Primary", sortOrder = (byte)1 },
            sA.TenantAdminToken, sA.Identifier));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var listA = await _client.SendAsync(Get(
            "/api/academicstages", sA.TenantAdminToken, sA.Identifier));
        Assert.Equal(HttpStatusCode.OK, listA.StatusCode);
        using (var doc = JsonDocument.Parse(await listA.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            // Tenant interceptor normalizes code to UPPER.
            Assert.Equal("PRIMARY", doc.RootElement[0].GetProperty("code").GetString());
        }

        // Tenant B sees ZERO stages — the global query filter on IHasTenantId must
        // isolate the catalog row.
        var listB = await _client.SendAsync(Get(
            "/api/academicstages", sB.TenantAdminToken, sB.Identifier));
        Assert.Equal(HttpStatusCode.OK, listB.StatusCode);
        using (var doc = JsonDocument.Parse(await listB.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, doc.RootElement.GetArrayLength());
        }
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task AcademicStages_DuplicateCodeWithinTenant_IsRejected()
    {
        var s = await SeedActiveTenantAsync();

        var first = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var dup = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1 duplicate", sortOrder = (byte)2 },
            s.TenantAdminToken, s.Identifier));
        Assert.False(dup.IsSuccessStatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(db.AcademicStages.IgnoreQueryFilters().Where(x => x.TenantId == s.TenantId.ToString())); // only the first persisted
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task AcademicStages_SameCodeAcrossTenants_BothAllowed()
    {
        var sA = await SeedActiveTenantAsync();
        var sB = await SeedActiveTenantAsync();

        var first = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            sA.TenantAdminToken, sA.Identifier));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            sB.TenantAdminToken, sB.Identifier));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode); // tenant-scoped uniqueness
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task AcademicYears_RequiresStage_AndStageMustExistInSameTenant()
    {
        var s = await SeedActiveTenantAsync();

        // Seed a stage so the FK + tenant-scoped stage check has something to find.
        var createStage = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, createStage.StatusCode);

        int stageId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            stageId = db.AcademicStages.IgnoreQueryFilters().Single(x => x.TenantId == s.TenantId.ToString()).Id;
        }

        var ok = await _client.SendAsync(Post(
            "/api/academicyears",
            new { stageId, yearCode = "Y1", yearName = "Year 1" },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        // Bogus stage in own tenant → 404 (or 400) — never 500.
        var bogus = await _client.SendAsync(Post(
            "/api/academicyears",
            new { stageId = 99999, yearCode = "Y9", yearName = "Year 9" },
            s.TenantAdminToken, s.Identifier));
        Assert.False(bogus.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, bogus.StatusCode);
    }

    // ==================================================================
    // Students: tenant-admin happy path, referential integrity, limits
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Students_TenantAdmin_CanCreateReadUpdateSoftDelete()
    {
        var s = await SeedActiveTenantAsync();

        // Stage + year + branch must exist in this tenant.
        var branchCreate = await _client.SendAsync(Post(
            "/api/branches", BranchPayload("Cairo"), s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, branchCreate.StatusCode);

        var stageCreate = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, stageCreate.StatusCode);

        Guid branchId;
        int stageId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            branchId = db.Branches.IgnoreQueryFilters().Single(b => b.TenantId == s.TenantId.ToString()).Id;
            stageId = db.AcademicStages.IgnoreQueryFilters().Single(x => x.TenantId == s.TenantId.ToString()).Id;
        }

        var yearCreate = await _client.SendAsync(Post(
            "/api/academicyears",
            new { stageId, yearCode = "Y1", yearName = "Year 1" },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, yearCreate.StatusCode);

        int yearId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            yearId = db.AcademicYears.IgnoreQueryFilters().Single(y => y.TenantId == s.TenantId.ToString()).Id;
        }

        var create = await _client.SendAsync(Post(
            "/api/students", StudentPayload(branchId, stageId, yearId),
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        Guid studentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = db.Students.IgnoreQueryFilters().Include(x => x.Branch).Include(x => x.Stage).Include(x => x.Year).Single(x => x.TenantId == s.TenantId.ToString());
            studentId = student.Id;
            Assert.Equal("طالب اختبار", student.FullNameAr);
            Assert.NotNull(student.Branch);
            Assert.NotNull(student.Stage);
            Assert.NotNull(student.Year);
        }

        var list = await _client.SendAsync(Get(
            "/api/students", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal("Cairo", doc.RootElement[0].GetProperty("branchName").GetString());
            Assert.Equal("K1", doc.RootElement[0].GetProperty("stageName").GetString());
            Assert.Equal("Year 1", doc.RootElement[0].GetProperty("yearName").GetString());
        }

        var update = await _client.SendAsync(Put(
            $"/api/students/{studentId}",
            new
            {
                id = studentId,
                branchId,
                stageId,
                yearId,
                fullNameAr = "محمود",
                fullNameEn = (string?)"Mahmoud",
                gender = 1,
                phone = "01000000001",
                discountType = (int?)0,
                discountValue = (decimal?)10m,
                status = 1
            },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var delete = await _client.SendAsync(Delete(
            $"/api/students/{studentId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var soft = await db.Students.IgnoreQueryFilters().SingleAsync();
            Assert.True(soft.IsDeleted());
            Assert.Equal(StudentStatus.Inactive, soft.Status); // status flipped to Inactive
        }
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Students_CreateWithMissingBranch_ReturnsNotFound_NotFiveHundred()
    {
        var s = await SeedActiveTenantAsync();

        var bogus = await _client.SendAsync(Post(
            "/api/students",
            StudentPayload(Guid.NewGuid(), stageId: 1, yearId: 1),
            s.TenantAdminToken, s.Identifier));

        Assert.False(bogus.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, bogus.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bogus.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound()
    {
        var sA = await SeedActiveTenantAsync();
        var sB = await SeedActiveTenantAsync();

        // Tenant A creates a branch.
        var branchA = await _client.SendAsync(Post(
            "/api/branches", BranchPayload("A-Branch"),
            sA.TenantAdminToken, sA.Identifier));
        Assert.Equal(HttpStatusCode.Created, branchA.StatusCode);

        Guid branchAId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            branchAId = db.Branches.IgnoreQueryFilters().Single(b => b.Name == "A-Branch").Id;
        }

        // Tenant B also has its own stage/year so only the branch is the cross-tenant ref.
        await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            sB.TenantAdminToken, sB.Identifier));
        int bStage;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            bStage = db.AcademicStages.IgnoreQueryFilters()
                .Single(x => x.TenantId == sB.TenantId.ToString()).Id;
        }
        await _client.SendAsync(Post(
            "/api/academicyears",
            new { stageId = bStage, yearCode = "Y1", yearName = "Year 1" },
            sB.TenantAdminToken, sB.Identifier));
        int bYear;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            bYear = db.AcademicYears.IgnoreQueryFilters()
                .Single(x => x.TenantId == sB.TenantId.ToString()).Id;
        }

        // Tenant B tries to create a student pointing at Tenant A's branch — query filter
        // on Branches must hide it from B, handler returns BranchErrors.NotFound.
        var crossBranch = await _client.SendAsync(Post(
            "/api/students",
            StudentPayload(branchAId, bStage, bYear),
            sB.TenantAdminToken, sB.Identifier));
        Assert.Equal(HttpStatusCode.NotFound, crossBranch.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Students_FeatureMissing_PermissionPresent_IsDenied()
    {
        // No Students feature entitlement on plan.
        var s = await SeedActiveTenantAsync(withStudentsFeature: false);

        var branchCreate = await _client.SendAsync(Post(
            "/api/branches", BranchPayload(), s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, branchCreate.StatusCode);

        Guid branchId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            branchId = db.Branches.IgnoreQueryFilters().Single(b => b.TenantId == s.TenantId.ToString()).Id;
        }

        var stageCreate = await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, stageCreate.StatusCode);

        int stageId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            stageId = db.AcademicStages.IgnoreQueryFilters().Single(x => x.TenantId == s.TenantId.ToString()).Id;
        }

        var yearCreate = await _client.SendAsync(Post(
            "/api/academicyears",
            new { stageId, yearCode = "Y1", yearName = "Year 1" },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, yearCreate.StatusCode);

        int yearId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            yearId = db.AcademicYears.IgnoreQueryFilters().Single(y => y.TenantId == s.TenantId.ToString()).Id;
        }

        var denied = await _client.SendAsync(Post(
            "/api/students", StudentPayload(branchId, stageId, yearId),
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Students_LimitExhausted_IsDeniedEvenWithFeatureAndPermission()
    {
        var s = await SeedActiveTenantAsync(maxStudents: 50); // plan snapshot also 50

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counter = await db.TenantUsageCounters.SingleAsync(c => c.Id == s.TenantId);
            counter.UpdateCounts(
                studentsCount: 50, usersCount: 0, branchesCount: 0, teachersCount: 0,
                storageUsedMB: 0, smsUsedThisCycle: 0);
            await db.SaveChangesAsync();
        }

        var branchCreate = await _client.SendAsync(Post(
            "/api/branches", BranchPayload(), s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, branchCreate.StatusCode);

        Guid branchId;
        int stageId;
        int yearId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            branchId = db.Branches.IgnoreQueryFilters().Single(b => b.TenantId == s.TenantId.ToString()).Id;
        }
        await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            s.TenantAdminToken, s.Identifier));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            stageId = db.AcademicStages.IgnoreQueryFilters().Single(x => x.TenantId == s.TenantId.ToString()).Id;
        }
        await _client.SendAsync(Post(
            "/api/academicyears",
            new { stageId, yearCode = "Y1", yearName = "Year 1" },
            s.TenantAdminToken, s.Identifier));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            yearId = db.AcademicYears.IgnoreQueryFilters().Single(y => y.TenantId == s.TenantId.ToString()).Id;
        }

        var response = await _client.SendAsync(Post(
            "/api/students", StudentPayload(branchId, stageId, yearId),
            s.TenantAdminToken, s.Identifier));

        Assert.False(response.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("limit", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Students_ExpiredSubscription_BlocksCreate()
    {
        var s = await SeedActiveTenantAsync();

        // Supporting entities are created while the subscription is still ACTIVE. Only the
        // Students write is exercised under the expired state — the Students endpoint is the
        // one gated by [RequireFeature(StudentManagement)], and branch/stage/year are limit-gated
        // (not feature-gated), so expiring first would deny them with 409 instead of isolating
        // the expired-subscription feature block we want to assert here.
        var branchCreate = await _client.SendAsync(Post(
            "/api/branches", BranchPayload(), s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, branchCreate.StatusCode);

        Guid branchId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            branchId = db.Branches.IgnoreQueryFilters().Single(b => b.TenantId == s.TenantId.ToString()).Id;
        }
        await _client.SendAsync(Post(
            "/api/academicstages",
            new { code = "K1", displayName = "K1", sortOrder = (byte)1 },
            s.TenantAdminToken, s.Identifier));
        int stageId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            stageId = db.AcademicStages.IgnoreQueryFilters().Single(x => x.TenantId == s.TenantId.ToString()).Id;
        }
        await _client.SendAsync(Post(
            "/api/academicyears",
            new { stageId, yearCode = "Y1", yearName = "Year 1" },
            s.TenantAdminToken, s.Identifier));
        int yearId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            yearId = db.AcademicYears.IgnoreQueryFilters().Single(y => y.TenantId == s.TenantId.ToString()).Id;
        }

        // Force-expire the subscription now that the supporting catalog exists.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = await db.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.Id == s.SubscriptionId);
            typeof(TenantPlan).GetProperty(nameof(TenantPlan.EffectiveEndsAtUtc))!
                .SetValue(sub, DateTime.UtcNow.AddDays(-1));
            await db.SaveChangesAsync();
        }

        // The Students endpoint is gated by [RequireFeature(StudentManagement)] — which
        // fails closed when the subscription is expired. Expect 403.
        var denied = await _client.SendAsync(Post(
            "/api/students", StudentPayload(branchId, stageId, yearId),
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // ==================================================================
    // Branches: limit gate (complement to Students)
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase3Http")]
    public async Task Branches_LimitExhausted_IsDenied()
    {
        var s = await SeedActiveTenantAsync(maxBranches: 2);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counter = await db.TenantUsageCounters.SingleAsync(c => c.Id == s.TenantId);
            counter.UpdateCounts(
                studentsCount: 0, usersCount: 0, branchesCount: 2, teachersCount: 0,
                storageUsedMB: 0, smsUsedThisCycle: 0);
            await db.SaveChangesAsync();
        }

        var denied = await _client.SendAsync(Post(
            "/api/branches", BranchPayload(), s.TenantAdminToken, s.Identifier));
        Assert.False(denied.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, denied.StatusCode);
    }
}