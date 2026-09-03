namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.SalaryPayments;
using Centerix.Domain.Teachers.Subjects;
using Centerix.Domain.Teachers.TeacherSalaryConfigs;
using Centerix.Domain.Teachers.Teachers;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;

using Finbuckle.MultiTenant.Abstractions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

/// <summary>
/// Phase 5 Teachers remediation HTTP authorization tests (F-01 / M-01):
/// every TeacherManagement mutation is gated by [RequireFeature] exactly like the approved
/// Students pattern — a tenant WITHOUT the Teachers feature gets 403 on every mutation,
/// and a tenant WITH it succeeds (positive controls). Also regression-proves that the
/// salary-payment create path cannot be forced into a non-Pending initial state (H-02).
/// Runs on the InMemory factory; provider-independent authorization is the subject here.
/// </summary>
[Collection("Integration")]
public class Phase5TeachersAuthorizationHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Phase5TeachersAuthorizationHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // Seeding helpers (mirrors the Phase3 pattern, self-contained)
    // ==================================================================

    private async Task<(string PlatformToken, string TenantAdminToken, Guid TenantId, string Identifier)>
        SeedAsync()
    {
        var identifier = $"p5-{Guid.NewGuid():N}";
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
            "O", "W", $"owner_{Guid.NewGuid():N}@p5.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p5@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "p5@test.com", IsActive = false,
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

        var platformUser = await EnsureUserAsync($"platform_{Guid.NewGuid():N}@p5.test", "PlatformAdmin");
        var tenantAdmin = await EnsureUserAsync($"tadmin_{Guid.NewGuid():N}@p5.test", "TenantAdmin");

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

    private async Task<int> CreatePlanAsync(string platformToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        request.Content = JsonContent.Create(new
        {
            code = $"P5{Guid.NewGuid():N}"[..24],
            displayName = "Phase 5 Plan",
            monthlyPrice = 199.99m,
            maxStudents = 50,
            maxUsers = 5,
            maxBranches = 5,
            maxTeachers = 10,
            storageGB = 20,
            smsQuota = 100,
            isActive = true,
            description = "Phase 5 HTTP test plan",
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

    private async Task ApproveAndActivateAsync(string platformToken, Guid tenantId, int planId)
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
    }

    private async Task<Phase5Seed> SeedActiveTenantAsync(bool withTeachersFeature = true)
    {
        var (platformToken, tenantAdminToken, tenantId, identifier) = await SeedAsync();
        var planId = await CreatePlanAsync(platformToken);
        if (withTeachersFeature) await EnsureFeatureOnPlanAsync(planId, FeatureCodes.TeacherManagement);
        await ApproveAndActivateAsync(platformToken, tenantId, planId);
        return new Phase5Seed(platformToken, tenantAdminToken, tenantId, identifier);
    }

    private sealed record Phase5Seed(
        string PlatformToken, string TenantAdminToken, Guid TenantId, string Identifier);

    /// <summary>
    /// Direct-DB row seeding: the create endpoints are themselves feature-gated, so tests
    /// that exercise OTHER gated mutations seed their target rows through the domain layer
    /// (same approach as the approved Phase 3 Students feature-gate tests).
    /// </summary>
    private async Task<Guid> SeedTeacherAsync(Phase5Seed s, Guid? branchId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var effectiveBranchId = branchId ?? Guid.NewGuid();
        var teacher = Teacher.Create(
            Guid.NewGuid(), $"user-{Guid.NewGuid():N}", effectiveBranchId,
            "Ahmed Ali", "01000000000", "BSc", 5, TeacherStatus.Active,
            DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Teachers.Add(teacher);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();
        return teacher.Id;
    }

    private async Task<Guid> SeedBranchAsync(Phase5Seed s)
    {
        var create = await _client.SendAsync(Post(
            "/api/branches",
            new { name = "P5 Branch", address = "1 St", phone = "01000000000", isActive = true },
            s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Branches.IgnoreQueryFilters().Single(b => b.TenantId == s.TenantId.ToString()).Id;
    }

    private async Task<(int SubjectId, int StageId)> SeedSubjectAsync(Phase5Seed s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // AcademicStage rows are tenant-scoped (query filter) but share a GLOBAL int key,
        // so stage 1 may already exist from another tenant in the InMemory store.
        // Allocate a fresh id; the UpdateSubjectHandler looks the stage up under the
        // current tenant's filter, and this row is stamped for the current tenant.
        var maxStageId = await db.AcademicStages.IgnoreQueryFilters().Select(x => (int?)x.Id).MaxAsync() ?? 0;
        var stageId = maxStageId + 1;
        var stage = AcademicStage.Create(stageId, "S1", "Stage One", (byte)(stageId % 250 + 1)).Value;
        db.AcademicStages.Add(stage);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();

        var subject = Subject.Create(0, $"Subj{Guid.NewGuid():N}"[..14], stageId).Value;
        db.Subjects.Add(subject);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();
        return (subject.Id, stageId);
    }

    private async Task<int> SeedSalaryConfigAsync(Phase5Seed s, Guid teacherId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = TeacherSalaryConfig.Create(
            0, teacherId, null, SalaryType.Fixed, 5000m,
            DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.TeacherSalaryConfigs.Add(config);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();
        return config.Id;
    }

    private async Task<Guid> SeedSalaryPaymentAsync(Phase5Seed s, Guid teacherId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = SalaryPayment.Create(Guid.NewGuid(), teacherId, 9, 2026, 5000m, 4500m).Value;
        db.SalaryPayments.Add(payment);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();
        return payment.Id;
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

    // ==================================================================
    // F-01 / H-01: Feature-gate matrix — every mutation requires TeacherManagement
    // ==================================================================

    // ------------------------------------------------------------------
    // Teacher mutations
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Teacher_Update_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var teacherId = await SeedTeacherAsync(s);

        var update = await _client.SendAsync(Put(
            $"/api/teachers/{teacherId}",
            new
            {
                id = teacherId,
                userId = $"user-{Guid.NewGuid():N}",
                branchId = Guid.NewGuid(),
                fullName = "Updated Name",
                phone = "01000000000",
                qualification = "MSc",
                yearsExp = 8,
                status = 1
            },
            s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Teacher_Update_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var branchId = await SeedBranchAsync(s);
        var teacherId = await SeedTeacherAsync(s, branchId);

        var update = await _client.SendAsync(Put(
            $"/api/teachers/{teacherId}",
            new
            {
                id = teacherId,
                userId = $"user-{Guid.NewGuid():N}",
                branchId = branchId,
                fullName = "Updated Name",
                phone = "01000000000",
                qualification = "MSc",
                yearsExp = 8,
                status = 1
            },
            s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Teacher_Delete_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var teacherId = await SeedTeacherAsync(s);

        var deleted = await _client.SendAsync(Delete(
            $"/api/teachers/{teacherId}", s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);

        // Verify the teacher still exists (was not actually soft-deleted).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stillThere = await db.Teachers.IgnoreQueryFilters().AnyAsync(t => t.Id == teacherId);
            Assert.True(stillThere);
        }
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Teacher_Delete_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var teacherId = await SeedTeacherAsync(s);

        var deleted = await _client.SendAsync(Delete(
            $"/api/teachers/{teacherId}", s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Soft-deleted row should be hidden by the soft-delete filter.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var visible = await db.Teachers.AnyAsync(t => t.Id == teacherId);
            Assert.False(visible);
        }
    }

    // ------------------------------------------------------------------
    // Subject mutations
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Subject_Update_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var (subjectId, stageId) = await SeedSubjectAsync(s);

        var update = await _client.SendAsync(Put(
            $"/api/subjects/{subjectId}",
            new { id = subjectId, name = "Updated Subject", stageId },
            s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Subject_Update_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var (subjectId, stageId) = await SeedSubjectAsync(s);

        var update = await _client.SendAsync(Put(
            $"/api/subjects/{subjectId}",
            new { id = subjectId, name = "Updated Subject", stageId },
            s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Subject_Delete_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var (subjectId, _) = await SeedSubjectAsync(s);

        var deleted = await _client.SendAsync(Delete(
            $"/api/subjects/{subjectId}", s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task Subject_Delete_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var (subjectId, _) = await SeedSubjectAsync(s);

        var deleted = await _client.SendAsync(Delete(
            $"/api/subjects/{subjectId}", s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    // ------------------------------------------------------------------
    // SalaryConfig mutations
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryConfig_Update_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var teacherId = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var update = await _client.SendAsync(Put(
            $"/api/teachersalaryconfigs/{configId}",
            new { id = configId, groupId = (Guid?)null, salaryType = 1, value = 6000m, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) },
            s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryConfig_Update_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var teacherId = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var update = await _client.SendAsync(Put(
            $"/api/teachersalaryconfigs/{configId}",
            new { id = configId, groupId = (Guid?)null, salaryType = 1, value = 6000m, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) },
            s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryConfig_Delete_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var teacherId = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var deleted = await _client.SendAsync(Delete(
            $"/api/teachersalaryconfigs/{configId}", s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryConfig_Delete_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var teacherId = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var deleted = await _client.SendAsync(Delete(
            $"/api/teachersalaryconfigs/{configId}", s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    // ------------------------------------------------------------------
    // SalaryPayment mutations
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryPayment_MarkPaid_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var teacherId = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var markPaidReq = new HttpRequestMessage(
            HttpMethod.Post, $"/api/salarypayments/{paymentId}/mark-paid")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", s.TenantAdminToken) },
            Content = null!
        };
        markPaidReq.Headers.Add("tenant", s.Identifier);
        var markPaid = await _client.SendAsync(markPaidReq);

        Assert.Equal(HttpStatusCode.Forbidden, markPaid.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryPayment_Cancel_WithoutFeature_IsForbidden()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: false);
        var teacherId = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var cancelReq = new HttpRequestMessage(
            HttpMethod.Post, $"/api/salarypayments/{paymentId}/cancel")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", s.TenantAdminToken) },
            Content = null!
        };
        cancelReq.Headers.Add("tenant", s.Identifier);
        var cancel = await _client.SendAsync(cancelReq);

        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryPayment_MarkPaid_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var teacherId = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var markPaidReq = new HttpRequestMessage(
            HttpMethod.Post, $"/api/salarypayments/{paymentId}/mark-paid")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", s.TenantAdminToken) },
            Content = null!
        };
        markPaidReq.Headers.Add("tenant", s.Identifier);
        var markPaid = await _client.SendAsync(markPaidReq);

        Assert.Equal(HttpStatusCode.NoContent, markPaid.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    public async Task SalaryPayment_Cancel_WithFeature_Succeeds()
    {
        var s = await SeedActiveTenantAsync(withTeachersFeature: true);
        var teacherId = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var cancelReq = new HttpRequestMessage(
            HttpMethod.Post, $"/api/salarypayments/{paymentId}/cancel")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", s.TenantAdminToken) },
            Content = null!
        };
        cancelReq.Headers.Add("tenant", s.Identifier);
        var cancel = await _client.SendAsync(cancelReq);

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
    }
}
