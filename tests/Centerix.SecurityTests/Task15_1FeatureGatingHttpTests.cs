namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Students;
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
/// Task 15.1 — H-01 HTTP feature-gating matrix for Teachers module mutations.
/// For each gated endpoint, proves:
///   Case 1: Feature present + permission present  → allowed
///   Case 2: Feature missing + permission present  → 403
///   Case 3: Feature expired + permission present  → 403
///   Case 4: Feature present + permission missing  → 403
/// Endpoints tested:
///   Teacher PUT, Teacher DELETE
///   Subject PUT, Subject DELETE
///   TeacherSalaryConfig PUT, TeacherSalaryConfig DELETE
///   SalaryPayment MarkPaid, SalaryPayment Cancel
/// </summary>
[Collection("Integration")]
public class Task15_1FeatureGatingHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Task15_1FeatureGatingHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // Seed infrastructure
    // ==================================================================

    private sealed record Seed(
        string PlatformToken, string TenantAdminToken, string LimitedToken,
        Guid TenantId, string Identifier, int PlanId, string AdminUserId);

    private async Task<Seed> SeedTenantAsync(bool withFeature, bool grantAllPermissions)
    {
        var identifier = $"t151fg-{Guid.NewGuid():N}";
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
                var p = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (p.IsSuccess) db.Permissions.Add(p.Value);
            }
        }
        await db.SaveChangesAsync();

        async Task<ApplicationRole> EnsureRoleAsync(string name)
        {
            var role = await roleManager.FindByNameAsync(name);
            if (role is not null) return role;
            var created = new ApplicationRole(name)
            {
                Code = name, DisplayName = name, IsSystem = true,
                NormalizedName = name.ToUpperInvariant()
            };
            await roleManager.CreateAsync(created);
            return created;
        }

        var platformRole = await EnsureRoleAsync("PlatformAdmin");
        var tenantAdminRole = await EnsureRoleAsync("TenantAdmin");
        var limitedRole = await EnsureRoleAsync("LimitedUser");

        foreach (var p in db.Permissions.ToList())
        {
            if (!db.RolePermissions.Any(rp => rp.RoleId == platformRole.Id && rp.PermissionId == p.Id))
                db.RolePermissions.Add(RolePermission.Create(platformRole.Id, p.Id).Value);
        }

        if (grantAllPermissions)
        {
            foreach (var p in db.Permissions.ToList())
            {
                if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole.Id && rp.PermissionId == p.Id))
                    db.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, p.Id).Value);
            }
        }
        else
        {
            var readPerms = db.Permissions.Where(p => p.Action == "Read").ToList();
            foreach (var p in readPerms)
            {
                if (!db.RolePermissions.Any(rp => rp.RoleId == limitedRole.Id && rp.PermissionId == p.Id))
                    db.RolePermissions.Add(RolePermission.Create(limitedRole.Id, p.Id).Value);
            }
        }
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@t151fg.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = $"{identifier}@t151fg.test", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = $"{identifier}@t151fg.test", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
            await tenantDb.SaveChangesAsync();
        }
        await db.SaveChangesAsync();

        async Task<IdentityUser> EnsureUserAsync(string email, string role)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new IdentityUser
                {
                    Email = email, UserName = email, EmailConfirmed = true,
                    NormalizedEmail = email.ToUpperInvariant(),
                    NormalizedUserName = email.ToUpperInvariant()
                };
                user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
                await userManager.CreateAsync(user);
            }
            if (!await userManager.IsInRoleAsync(user, role))
                await userManager.AddToRoleAsync(user, role);
            return user;
        }

        var adminUser = await EnsureUserAsync($"tadmin_{Guid.NewGuid():N}@t151fg.test", "TenantAdmin");
        if (!db.TenantMemberships.Any(m => m.UserId == adminUser.Id && m.TenantId == tenant.Id.ToString()))
            db.TenantMemberships.Add(TenantMembership.Create(
                adminUser.Id, tenant.Id.ToString(), "TenantAdmin", TenantMembershipStatus.Active).Value);

        string limitedToken = "";
        if (!grantAllPermissions)
        {
            var limitedUser = await EnsureUserAsync($"limited_{Guid.NewGuid():N}@t151fg.test", "LimitedUser");
            if (!db.TenantMemberships.Any(m => m.UserId == limitedUser.Id && m.TenantId == tenant.Id.ToString()))
                db.TenantMemberships.Add(TenantMembership.Create(
                    limitedUser.Id, tenant.Id.ToString(), "LimitedUser", TenantMembershipStatus.Active).Value);
            limitedToken = _factory.GenerateTestToken(limitedUser.Id, limitedUser.Email!, ["LimitedUser"]);
        }
        await db.SaveChangesAsync();

        var planCode = $"T151{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, planCode, "T151 Plan", 100m,
            10, 5, 5, 10, 10, 1000, true, null, "USD", 12, 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        if (withFeature)
        {
            var feature = await db.Features.FirstOrDefaultAsync(f => f.Code == FeatureCodes.TeacherManagement);
            if (feature is null)
            {
                feature = Feature.Create(0, FeatureCodes.TeacherManagement, "Teachers", "Core").Value;
                db.Features.Add(feature);
                await db.SaveChangesAsync();
            }
            var pf = PlanFeature.Create(0, plan.Id, feature.Id, true).Value;
            db.PlanFeatures.Add(pf);
            await db.SaveChangesAsync();
        }

        var platformUser = await EnsureUserAsync($"platform_{Guid.NewGuid():N}@t151fg.test", "PlatformAdmin");
        var platformToken = _factory.GenerateTestToken(platformUser.Id, platformUser.Email!, ["PlatformAdmin"]);
        var tenantAdminToken = _factory.GenerateTestToken(adminUser.Id, adminUser.Email!, ["TenantAdmin"]);

        return new Seed(platformToken, tenantAdminToken, limitedToken, tenant.Id, identifier, plan.Id, adminUser.Id);
    }

    private async Task ApproveAndActivateAsync(Seed s)
    {
        var approved = await _client.SendAsync(Post(
            $"/api/tenants/{s.TenantId}/approve",
            new { tenantId = s.TenantId, planId = s.PlanId },
            s.PlatformToken));
        Assert.Equal(HttpStatusCode.Created, approved.StatusCode);

        var activated = await _client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"/api/tenants/{s.TenantId}/activate")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", s.PlatformToken) }
        });
        Assert.Equal(HttpStatusCode.NoContent, activated.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sub = await db.TenantPlans.IgnoreQueryFilters()
            .SingleAsync(tp => tp.TenantId == s.TenantId.ToString());
        db.TenantUsageCounters.Add(TenantUsageCounter.Create(
            s.TenantId,
            studentsCount: 0, usersCount: 0, branchesCount: 0, teachersCount: 0,
            storageUsedMB: 0, smsUsedThisCycle: 0,
            effectiveMaxStudents: sub.SnapshotMaxStudents,
            effectiveMaxUsers: sub.SnapshotMaxUsers,
            effectiveMaxBranches: sub.SnapshotMaxBranches,
            effectiveMaxTeachers: sub.SnapshotMaxTeachers,
            calculatedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task ExpireSubscriptionAsync(Seed s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sub = await db.TenantPlans.IgnoreQueryFilters()
            .SingleAsync(tp => tp.TenantId == s.TenantId.ToString());
        typeof(TenantPlan).GetProperty(nameof(TenantPlan.EffectiveEndsAtUtc))!
            .SetValue(sub, DateTime.UtcNow.AddDays(-1));
        await db.SaveChangesAsync();
    }

    private async Task<(Guid TeacherId, Guid BranchId)> SeedTeacherAsync(Seed s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var branch = Branch.Create(Guid.NewGuid(), $"T151fgB-{s.Identifier[..8]}").Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(s.TenantId.ToString());

        var teacher = Teacher.Create(Guid.NewGuid(), $"uid-fg-{Guid.NewGuid():N}"[..20], branch.Id,
            "FG Teacher", "01000000000", "BSc", 1,
            TeacherStatus.Active, DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Teachers.Add(teacher);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();
        return (teacher.Id, branch.Id);
    }

    private async Task<(int SubjectId, int StageId)> SeedSubjectAsync(Seed s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stage = Centerix.Domain.Students.Lookups.AcademicStage.Create(0, $"S{s.Identifier[..8]}", "Stage 1", 1).Value;
        db.AcademicStages.Add(stage);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();

        var subject = Subject.Create(0, $"FGSubj{Guid.NewGuid():N}"[..14], stage.Id).Value;
        db.Subjects.Add(subject);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();
        return (subject.Id, stage.Id);
    }

    private async Task<int> SeedSalaryConfigAsync(Seed s, Guid teacherId)
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

    private async Task<Guid> SeedSalaryPaymentAsync(Seed s, Guid teacherId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = SalaryPayment.Create(Guid.NewGuid(), teacherId, 6, 2026, 5000m, 4500m).Value;
        db.SalaryPayments.Add(payment);
        db.StampAddedTenantIds(s.TenantId.ToString());
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task MarkPaidAsync(Seed s, Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pay = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);
        pay.MarkPaid(DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    // ==================================================================
    // HTTP helpers
    // ==================================================================

    private static HttpRequestMessage Post(string url, object payload, string? token = null, string? tenantHeader = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null) req.Headers.Add("tenant", tenantHeader);
        req.Content = JsonContent.Create(payload);
        return req;
    }

    private static HttpRequestMessage Put(string url, object payload, string? token = null, string? tenantHeader = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null) req.Headers.Add("tenant", tenantHeader);
        req.Content = JsonContent.Create(payload);
        return req;
    }

    private static HttpRequestMessage Delete(string url, string? token = null, string? tenantHeader = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null) req.Headers.Add("tenant", tenantHeader);
        return req;
    }

    private static HttpRequestMessage PostAction(string url, string? token = null, string? tenantHeader = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null) req.Headers.Add("tenant", tenantHeader);
        return req;
    }

    // ==================================================================
    // Teacher PUT — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Put_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, branchId) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Put($"/api/teachers/{teacherId}", new
        {
            id = teacherId, userId = s.AdminUserId, branchId,
            fullName = "Updated", phone = "01000000000", qualification = "MSc",
            yearsExp = 5, status = 1
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Put_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, branchId) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Put($"/api/teachers/{teacherId}", new
        {
            id = teacherId, userId = s.AdminUserId, branchId,
            fullName = "Updated", phone = "01000000000", qualification = "MSc",
            yearsExp = 5, status = 1
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Put_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (teacherId, branchId) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Put($"/api/teachers/{teacherId}", new
        {
            id = teacherId, userId = s.AdminUserId, branchId,
            fullName = "Updated", phone = "01000000000", qualification = "MSc",
            yearsExp = 5, status = 1
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Put_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (teacherId, branchId) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Put($"/api/teachers/{teacherId}", new
        {
            id = teacherId, userId = s.AdminUserId, branchId,
            fullName = "Updated", phone = "01000000000", qualification = "MSc",
            yearsExp = 5, status = 1
        }, s.LimitedToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ==================================================================
    // Teacher DELETE — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Delete_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/teachers/{teacherId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Delete_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/teachers/{teacherId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Delete_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/teachers/{teacherId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Teacher_Delete_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/teachers/{teacherId}", s.LimitedToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ==================================================================
    // Subject PUT — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Put_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (subjectId, stageId) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Put($"/api/subjects/{subjectId}", new
        {
            id = subjectId, name = "Updated Subject", stageId
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Put_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (subjectId, stageId) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Put($"/api/subjects/{subjectId}", new
        {
            id = subjectId, name = "Updated Subject", stageId
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Put_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (subjectId, stageId) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Put($"/api/subjects/{subjectId}", new
        {
            id = subjectId, name = "Updated Subject", stageId
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Put_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (subjectId, stageId) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Put($"/api/subjects/{subjectId}", new
        {
            id = subjectId, name = "Updated Subject", stageId
        }, s.LimitedToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ==================================================================
    // Subject DELETE — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Delete_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (subjectId, _) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/subjects/{subjectId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Delete_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (subjectId, _) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/subjects/{subjectId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Delete_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (subjectId, _) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/subjects/{subjectId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task Subject_Delete_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (subjectId, _) = await SeedSubjectAsync(s);

        var resp = await _client.SendAsync(Delete($"/api/subjects/{subjectId}", s.LimitedToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ==================================================================
    // TeacherSalaryConfig PUT — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Put_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Put($"/api/teachersalaryconfigs/{configId}", new
        {
            id = configId, groupId = (Guid?)null, salaryType = 1,
            value = 6000m, effectiveFrom = new DateOnly(2027, 1, 1)
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Put_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Put($"/api/teachersalaryconfigs/{configId}", new
        {
            id = configId, groupId = (Guid?)null, salaryType = 1,
            value = 6000m, effectiveFrom = new DateOnly(2027, 1, 1)
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Put_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Put($"/api/teachersalaryconfigs/{configId}", new
        {
            id = configId, groupId = (Guid?)null, salaryType = 1,
            value = 6000m, effectiveFrom = new DateOnly(2027, 1, 1)
        }, s.TenantAdminToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Put_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Put($"/api/teachersalaryconfigs/{configId}", new
        {
            id = configId, groupId = (Guid?)null, salaryType = 1,
            value = 6000m, effectiveFrom = new DateOnly(2027, 1, 1)
        }, s.LimitedToken, s.Identifier));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ==================================================================
    // TeacherSalaryConfig DELETE — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Delete_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Delete($"/api/teachersalaryconfigs/{configId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Delete_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Delete($"/api/teachersalaryconfigs/{configId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Delete_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Delete($"/api/teachersalaryconfigs/{configId}", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task TeacherSalaryConfig_Delete_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var configId = await SeedSalaryConfigAsync(s, teacherId);

        var resp = await _client.SendAsync(Delete($"/api/teachersalaryconfigs/{configId}", s.LimitedToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ==================================================================
    // SalaryPayment MarkPaid — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_MarkPaid_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/mark-paid", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_MarkPaid_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/mark-paid", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_MarkPaid_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/mark-paid", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_MarkPaid_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/mark-paid", s.LimitedToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ==================================================================
    // SalaryPayment Cancel — Cases 1-4
    // ==================================================================

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_Cancel_FeaturePresent_Allowed()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/cancel", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_Cancel_FeatureMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: false, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/cancel", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_Cancel_FeatureExpired_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: true);
        await ApproveAndActivateAsync(s);
        await ExpireSubscriptionAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/cancel", s.TenantAdminToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    [Trait("Category", "Phase5Http")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_Cancel_PermissionMissing_403()
    {
        var s = await SeedTenantAsync(withFeature: true, grantAllPermissions: false);
        await ApproveAndActivateAsync(s);
        var (teacherId, _) = await SeedTeacherAsync(s);
        var paymentId = await SeedSalaryPaymentAsync(s, teacherId);

        var resp = await _client.SendAsync(PostAction(
            $"/api/salarypayments/{paymentId}/cancel", s.LimitedToken, s.Identifier));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
