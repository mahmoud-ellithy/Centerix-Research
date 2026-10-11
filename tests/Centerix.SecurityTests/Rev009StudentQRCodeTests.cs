using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Lookups;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

// REV-009: the same QR code can exist in different tenants; same-tenant duplicates
// (including soft-deleted rows) return 409, never 500.
[Collection("Integration")]
public class Rev009StudentQRCodeTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Rev009StudentQRCodeTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Rev009")]
    public async Task SameQRCode_InTwoTenants_BothSucceed()
    {
        var seedA = await SeedActiveTenantAsync();
        var seedB = await SeedActiveTenantAsync();
        const string qr = "REV009-SHARED-QR";

        var responseA = await PostStudentAsync(seedA, qr);
        var responseB = await PostStudentAsync(seedB, qr);

        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
    }

    [Fact]
    [Trait("Category", "Rev009")]
    public async Task DuplicateQRCode_SameTenant_Returns409_Not500()
    {
        var seed = await SeedActiveTenantAsync();
        const string qr = "REV009-DUPE-QR";

        Assert.Equal(HttpStatusCode.Created, (await PostStudentAsync(seed, qr)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostStudentAsync(seed, qr)).StatusCode);
    }

    [Fact]
    [Trait("Category", "Rev009")]
    public async Task DuplicateQRCode_SoftDeletedStudent_Returns409_QRStaysReserved()
    {
        var seed = await SeedActiveTenantAsync();
        const string qr = "REV009-RESERVED-QR";

        Assert.Equal(HttpStatusCode.Created, (await PostStudentAsync(seed, qr)).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = await db.Students.IgnoreQueryFilters()
                .SingleAsync(s => s.TenantId == seed.TenantId.ToString() && s.QRCode == qr);
            Assert.True(student.SoftDelete().IsSuccess);
            await db.SaveChangesAsync();
        }

        // The soft-deleted student's QR stays reserved within the tenant → 409, not 201/500.
        Assert.Equal(HttpStatusCode.Conflict, (await PostStudentAsync(seed, qr)).StatusCode);
    }

    // ============================================================
    // Seeding helpers (compact active-tenant setup with Students feature)
    // ============================================================
    private sealed record Seed(
        string TenantAdminToken, Guid TenantId, string Identifier,
        Guid BranchId, int StageId, int YearId);

    private async Task<Seed> SeedActiveTenantAsync()
    {
        var identifier = $"r09-{Guid.NewGuid():N}";
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

        foreach (var code in new[] { Permissions.Students.Create, Permissions.Students.Read, Permissions.Students.Delete })
        {
            var pid = db.Permissions.Single(p => p.Code == code).Id;
            if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole.Id && rp.PermissionId == pid))
                db.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, pid).Value);
        }
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@rev009.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r09@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r09@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
            await tenantDb.SaveChangesAsync();
        }

        var adminEmail = $"tadmin_{Guid.NewGuid():N}@rev009.test";
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

        var platformUser = await EnsurePlatformAdminAsync(userManager, roleManager);
        var platformToken = _factory.GenerateTestToken(platformUser.Id, platformUser.Email!, ["PlatformAdmin"]);

        var planId = await CreatePlanAsync(platformToken);
        await EnsureFeatureOnPlanAsync(planId, FeatureCodes.StudentManagement);
        await ApproveAndActivateAsync(platformToken, tenant.Id, planId);

        using var counterScope = _factory.Services.CreateScope();
        var counterDb = counterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        counterDb.TenantUsageCounters.Add(TenantUsageCounter.Create(
            tenant.Id, 0, 0, 0, 0, 0, 0, 50, 5, 2, 10, DateTime.UtcNow));
        await counterDb.SaveChangesAsync();

        var branchId = await SeedBranchAsync(tenant.Id.ToString(),
            _factory.GenerateTestToken(admin.Id, adminEmail, ["TenantAdmin"]), identifier);
        var (stageId, yearId) = await SeedStageYearAsync(tenant.Id.ToString());

        return new Seed(
            _factory.GenerateTestToken(admin.Id, adminEmail, ["TenantAdmin"]),
            tenant.Id, identifier, branchId, stageId, yearId);
    }

    private async Task<IdentityUser> EnsurePlatformAdminAsync(
        UserManager<IdentityUser> userManager, RoleManager<ApplicationRole> roleManager)
    {
        if (!await roleManager.RoleExistsAsync("PlatformAdmin"))
        {
            await roleManager.CreateAsync(new ApplicationRole("PlatformAdmin")
            {
                Code = "PlatformAdmin", DisplayName = "Platform Administrator",
                IsSystem = true, NormalizedName = "PLATFORMADMIN"
            });
        }

        var email = $"platform_{Guid.NewGuid():N}@rev009.test";
        var user = new IdentityUser
        {
            Email = email, UserName = email, EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
        await userManager.CreateAsync(user);
        await userManager.AddToRoleAsync(user, "PlatformAdmin");
        return user;
    }

    private async Task<int> CreatePlanAsync(string platformToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        request.Content = JsonContent.Create(new
        {
            code = $"R09{Guid.NewGuid():N}"[..24],
            displayName = "REV009 Plan",
            monthlyPrice = 99.0m,
            maxStudents = 50,
            maxUsers = 5,
            maxBranches = 2,
            maxTeachers = 10,
            storageGB = 10,
            smsQuota = 100,
            isActive = true,
            description = "REV-009 test plan",
            currencyCode = "USD",
            durationMonths = 12,
            bonusMonths = 0
        });
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(request)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Plans.OrderByDescending(p => p.Id).Select(p => p.Id).FirstAsync();
    }

    private async Task EnsureFeatureOnPlanAsync(int planId, string featureCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.Features.FirstOrDefaultAsync(f => f.Code == featureCode);
        if (feature is null)
        {
            feature = Centerix.Domain.Platform.Features.Feature.Create(0, featureCode.Trim(), "Seeded for test", "Core").Value;
            db.Features.Add(feature);
            await db.SaveChangesAsync();
        }
        if (!await db.PlanFeatures.AnyAsync(pf => pf.PlanId == planId && pf.FeatureId == feature.Id))
        {
            db.PlanFeatures.Add(Centerix.Domain.Platform.Plans.PlanFeature.Create(0, planId, feature.Id, true).Value);
            await db.SaveChangesAsync();
        }
    }

    private async Task ApproveAndActivateAsync(string platformToken, Guid tenantId, int planId)
    {
        var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenantId}/approve");
        approve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        approve.Content = JsonContent.Create(new { tenantId, planId });
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(approve)).StatusCode);

        var activate = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenantId}/activate");
        activate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(activate)).StatusCode);
    }

    private async Task<Guid> SeedBranchAsync(string tenantId, string adminToken, string identifier)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/branches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.Add("tenant", identifier);
        request.Content = JsonContent.Create(new
        {
            name = "R09 Branch", address = "1 St", phone = "01000000000", isActive = true
        });
        var response = await _client.SendAsync(request);
        // Branches endpoints are not feature-gated; only the Branches.Create grant is needed.
        // The TenantAdmin role in these seeds holds no Branches.* grant → 403 is acceptable;
        // fall back to direct-DB seeding so the QR tests stay independent of branch grants.
        if (response.StatusCode == HttpStatusCode.Created)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Branches.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId).Select(b => b.Id).SingleAsync();
        }

        using var direct = _factory.Services.CreateScope();
        var directDb = direct.ServiceProvider.GetRequiredService<AppDbContext>();
        var branch = Centerix.Domain.Students.Branches.Branch.Create(
            Guid.NewGuid(), "R09 Branch", "1 St", "01000000000", null, true).Value;
        directDb.Branches.Add(branch);
        directDb.StampAddedTenantIds(tenantId);
        await directDb.SaveChangesAsync();
        return branch.Id;
    }

    private async Task<(int StageId, int YearId)> SeedStageYearAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var maxStageId = await db.AcademicStages.IgnoreQueryFilters().Select(x => (int?)x.Id).MaxAsync() ?? 0;
        var stage = AcademicStage.Create(maxStageId + 1, $"R9S{maxStageId + 1}", "Stage One", (byte)1).Value;
        db.AcademicStages.Add(stage);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var maxYearId = await db.AcademicYears.IgnoreQueryFilters().Select(x => (int?)x.Id).MaxAsync() ?? 0;
        var year = AcademicYear.Create(maxYearId + 1, stage.Id, $"R9Y{maxYearId + 1}", "Year One").Value;
        db.AcademicYears.Add(year);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return (stage.Id, year.Id);
    }

    private async Task<HttpResponseMessage> PostStudentAsync(Seed seed, string qrCode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/students");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.TenantAdminToken);
        request.Headers.Add("tenant", seed.Identifier);
        request.Content = JsonContent.Create(new
        {
            branchId = seed.BranchId,
            stageId = seed.StageId,
            yearId = seed.YearId,
            fullNameAr = "طالب اختبار",
            fullNameEn = "Test Student",
            gender = 1,
            phone = "01000000000",
            qrCode,
            status = 0,
            enrolledAt = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
        });
        return await _client.SendAsync(request);
    }
}
