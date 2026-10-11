using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.Teachers;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

// REV-011: quota counters stay correct across failed creates, deletes and Users;
// reconcile exists and is platform-guarded. Runs on the InMemory factory (fast);
// true multi-writer concurrency is proven by the SQL Server suites.
[Collection("Integration")]
public class Rev011QuotaAccountingTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Rev011QuotaAccountingTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.EmailSender.Clear();
    }

    // ============================================================
    // (a) Failed student creates never burn a slot
    // ============================================================
    [Fact]
    [Trait("Category", "Rev011")]
    public async Task StudentCreate_UnknownBranchStageYear_Returns404_CounterUnchanged()
    {
        var seed = await SeedActiveTenantAsync();
        var branchId = await SeedBranchAsync(seed);
        var (stageId, yearId) = await SeedStageYearAsync(seed);

        // Unknown branch / stage / year → 404 (validated BEFORE reserving).
        Assert.Equal(HttpStatusCode.NotFound, (await PostStudentAsync(
            seed, branchId: Guid.NewGuid(), stageId, yearId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostStudentAsync(
            seed, branchId, stageId: 9973, yearId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostStudentAsync(
            seed, branchId, stageId, yearId: 9941)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.Students.IgnoreQueryFilters()
            .Where(s => s.TenantId == seed.TenantId.ToString()));
        var counter = await db.TenantUsageCounters.SingleAsync(c => c.Id == seed.TenantId);
        Assert.Equal(0, counter.StudentsCount);
    }

    // ============================================================
    // (b) Create/delete cycles + branch/teacher delete release
    // ============================================================
    [Fact]
    [Trait("Category", "Rev011")]
    public async Task StudentCreateDeleteCycle_CanRecreateAfterDelete()
    {
        var seed = await SeedActiveTenantAsync();
        var branchId = await SeedBranchAsync(seed);
        var (stageId, yearId) = await SeedStageYearAsync(seed);

        Assert.Equal(HttpStatusCode.Created, (await PostStudentAsync(seed, branchId, stageId, yearId)).StatusCode);
        var studentId = await GetSingleStudentIdAsync(seed);
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/api/students/{studentId}", seed.TenantAdminToken, seed.Identifier)).StatusCode);

        // Re-create after delete succeeds (slot returned; release path does not corrupt state).
        Assert.Equal(HttpStatusCode.Created, (await PostStudentAsync(seed, branchId, stageId, yearId)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Students.IgnoreQueryFilters().CountAsync(
            s => s.TenantId == seed.TenantId.ToString() && s.DeletedAtUtc == null));
    }

    [Fact]
    [Trait("Category", "Rev011")]
    public async Task BranchDelete_Succeeds_SecondDeleteIs404()
    {
        var seed = await SeedActiveTenantAsync();
        var branchId = await SeedBranchAsync(seed);

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/api/branches/{branchId}", seed.TenantAdminToken, seed.Identifier)).StatusCode);

        // Already soft-deleted → NotFound, and the release path is not reached twice.
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Delete, $"/api/branches/{branchId}", seed.TenantAdminToken, seed.Identifier)).StatusCode);
    }

    [Fact]
    [Trait("Category", "Rev011")]
    public async Task TeacherDelete_StillReleases_Regression()
    {
        var seed = await SeedActiveTenantAsync();
        var branchId = await SeedBranchAsync(seed);
        var teacherId = await SeedTeacherRowAsync(seed, branchId);

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/api/teachers/{teacherId}", seed.TenantAdminToken, seed.Identifier)).StatusCode);
    }

    // ============================================================
    // (c) Users limit on register + accept; first-owner exemption
    // ============================================================
    [Fact]
    [Trait("Category", "Rev011")]
    public async Task Register_AtUsersLimit_Returns409_NothingCreated_InvitationStillUsable()
    {
        var seed = await SeedActiveTenantAsync();
        // Exhaust the Users quota: plan snapshot maxUsers = 5, counter already at 5,
        // while only the seeded admin holds an Active membership.
        await SetUsersCountAsync(seed.TenantId, usersCount: 5);

        var email = $"limited_{Guid.NewGuid():N}@rev011.test";
        var rawToken = await CreateInvitationViaApiAsync(seed, email);

        var response = await SendAsync(HttpMethod.Post, "/api/invitations/register",
            token: null, tenantHeader: null,
            payload: new { token = rawToken, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.Null(await userManager.FindByEmailAsync(email));
            Assert.Empty(db.TenantMemberships.Where(m =>
                m.TenantId == seed.TenantId.ToString() && m.UserId != seed.TenantAdminUserId));
            var invitation = db.TenantInvitations.Single(i => i.TokenHash == TestInviteTokens.Sha256Hex(rawToken));
            Assert.Equal(InvitationStatus.Pending, invitation.Status);
        }

        // The invitation is still usable once quota frees up.
        await SetUsersCountAsync(seed.TenantId, usersCount: 0);
        var retry = await SendAsync(HttpMethod.Post, "/api/invitations/register",
            token: null, tenantHeader: null,
            payload: new { token = rawToken, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    [Trait("Category", "Rev011")]
    public async Task Accept_AtUsersLimit_Returns409_MembershipUnchanged_InvitationStaysPending()
    {
        var seed = await SeedActiveTenantAsync();
        await SetUsersCountAsync(seed.TenantId, usersCount: 5);

        var email = $"acceptor_{Guid.NewGuid():N}@rev011.test";
        var user = await CreateUserAsync(email);
        var rawToken = await CreateInvitationViaApiAsync(seed, email);

        var response = await SendAsync(HttpMethod.Post, $"/api/invitations/{rawToken}/accept",
            _factory.GenerateTestToken(user.Id, email, []), seed.Identifier);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.TenantMemberships.Where(m =>
            m.UserId == user.Id && m.TenantId == seed.TenantId.ToString()));
        var invitation = db.TenantInvitations.Single(i => i.TokenHash == TestInviteTokens.Sha256Hex(rawToken));
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    [Trait("Category", "Rev011")]
    public async Task Register_FirstOwner_OnboardsWithNoSubscriptionOrCounter()
    {
        // A tenant with zero Active memberships and NO subscription/counter rows at all:
        // the first owner must never be blocked by limit state.
        var tenantId = Guid.NewGuid();
        var identifier = $"r11-first-{Guid.NewGuid():N}";
        await SeedBareTenantAsync(tenantId, identifier);
        var inviter = await CreateUserAsync($"inviter_{Guid.NewGuid():N}@rev011.test");
        var email = $"firstowner_{Guid.NewGuid():N}@rev011.test";
        var rawToken = await SeedInvitationRowAsync(tenantId.ToString(), email, inviter.Id);

        var response = await SendAsync(HttpMethod.Post, "/api/invitations/register",
            token: null, tenantHeader: null,
            payload: new { token = rawToken, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ============================================================
    // (d) Reconcile fixes corrupted counters; platform-guarded
    // ============================================================
    [Fact]
    [Trait("Category", "Rev011")]
    public async Task Reconcile_FixesCorruptedCounter_PlatformOnly()
    {
        var seed = await SeedActiveTenantAsync();
        var platformToken = await CreatePlatformAdminAsync();

        // Real rows: 2 students (1 soft-deleted), 1 branch, 1 teacher,
        // memberships: 1 active + 1 revoked.
        var branchId = await SeedBranchAsync(seed);
        var (stageId, yearId) = await SeedStageYearAsync(seed);
        var s1 = await SeedStudentRowAsync(seed, branchId, stageId, yearId);
        var s2 = await SeedStudentRowAsync(seed, branchId, stageId, yearId);
        await SoftDeleteStudentRowAsync(s1);
        await SeedTeacherRowAsync(seed, branchId);
        await SeedRevokedMembershipAsync(seed);

        // Corrupt the counter deliberately.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counter = await db.TenantUsageCounters.SingleAsync(c => c.Id == seed.TenantId);
            counter.UpdateCounts(99, 99, 99, 99, counter.StorageUsedMB, counter.SMSUsedThisCycle);
            await db.SaveChangesAsync();
        }

        // Non-platform caller → 403 and the corruption is untouched.
        var denied = await SendAsync(HttpMethod.Post,
            $"/api/tenantplans/{seed.TenantId}/reconcile-usage",
            seed.TenantAdminToken, seed.Identifier);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Unknown tenant → 404.
        var unknown = await SendAsync(HttpMethod.Post,
            $"/api/tenantplans/{Guid.NewGuid()}/reconcile-usage",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Platform reconcile → 204 and exact counts.
        var ok = await SendAsync(HttpMethod.Post,
            $"/api/tenantplans/{seed.TenantId}/reconcile-usage",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var fixed_ = await vdb.TenantUsageCounters.SingleAsync(c => c.Id == seed.TenantId);
        Assert.Equal(1, fixed_.StudentsCount); // s2 only; soft-deleted s1 excluded
        Assert.Equal(1, fixed_.TeachersCount);
        Assert.Equal(1, fixed_.BranchesCount);
        Assert.Equal(1, fixed_.UsersCount); // admin only; revoked excluded
    }

    // ============================================================
    // Seeding helpers
    // ============================================================
    private sealed record Seed(
        string PlatformToken, string TenantAdminToken, string TenantAdminUserId,
        Guid TenantId, string Identifier);

    private async Task<Seed> SeedActiveTenantAsync()
    {
        var identifier = $"r11-{Guid.NewGuid():N}";
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
        await EnsureRoleAsync("TenantUser");

        foreach (var code in new[]
        {
            Permissions.Students.Create, Permissions.Students.Read, Permissions.Students.Delete,
            Permissions.Branches.Create, Permissions.Branches.Read, Permissions.Branches.Delete,
            Permissions.Teachers.Create, Permissions.Teachers.Read, Permissions.Teachers.Delete,
            Permissions.Invitations.Create, Permissions.Invitations.Read
        })
        {
            var pid = db.Permissions.Single(p => p.Code == code).Id;
            if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole.Id && rp.PermissionId == pid))
                db.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, pid).Value);
        }
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@rev011.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r11@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r11@test.com", IsActive = false,
                ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
            });
            await tenantDb.SaveChangesAsync();
        }

        async Task<IdentityUser> EnsureUserAsync(string email, string role)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new IdentityUser
                {
                    Email = email, UserName = email, EmailConfirmed = true,
                    NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
                };
                user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
                await userManager.CreateAsync(user);
                await userManager.AddToRoleAsync(user, role);
            }
            return user;
        }

        var platformUser = await EnsureUserAsync($"platform_{Guid.NewGuid():N}@rev011.test", "PlatformAdmin");
        var tenantAdmin = await EnsureUserAsync($"tadmin_{Guid.NewGuid():N}@rev011.test", "TenantAdmin");

        if (!db.TenantMemberships.Any(m => m.UserId == tenantAdmin.Id && m.TenantId == tenant.Id.ToString()))
        {
            db.TenantMemberships.Add(TenantMembership.Create(
                tenantAdmin.Id, tenant.Id.ToString(), "TenantAdmin", TenantMembershipStatus.Active).Value);
        }
        await db.SaveChangesAsync();

        var seed = new Seed(
            _factory.GenerateTestToken(platformUser.Id, platformUser.Email!, ["PlatformAdmin"]),
            _factory.GenerateTestToken(tenantAdmin.Id, tenantAdmin.Email!, ["TenantAdmin"]),
            tenantAdmin.Id,
            tenant.Id,
            identifier);

        var planId = await CreatePlanAsync(seed.PlatformToken);
        await EnsureFeatureOnPlanAsync(planId, FeatureCodes.StudentManagement);
        await EnsureFeatureOnPlanAsync(planId, FeatureCodes.TeacherManagement);
        await ApproveAndActivateAsync(seed.PlatformToken, tenant.Id, planId);

        using var counterScope = _factory.Services.CreateScope();
        var counterDb = counterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        counterDb.TenantUsageCounters.Add(TenantUsageCounter.Create(
            tenant.Id, 0, 0, 0, 0, 0, 0, 50, 5, 2, 10, DateTime.UtcNow));
        await counterDb.SaveChangesAsync();

        return seed;
    }

    private async Task SeedBareTenantAsync(Guid tenantId, string identifier)
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        db.Tenants.Add(Tenant.Create(
            tenantId, identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@rev011.test", IsolationMode.Shared).Value);
        await db.SaveChangesAsync();

        if (await store.TryGetAsync(tenantId.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId.ToString(), Identifier = identifier, Name = identifier,
                Email = "r11@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenantId.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenantId.ToString(), Identifier = identifier, Name = identifier,
                Email = "r11@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
            await tenantDb.SaveChangesAsync();
        }
    }

    private async Task<int> CreatePlanAsync(string platformToken)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/plans",
            platformToken, tenantHeader: null,
            payload: new
            {
                code = $"R11{Guid.NewGuid():N}"[..24],
                displayName = "REV011 Plan",
                monthlyPrice = 199.99m,
                maxStudents = 50,
                maxUsers = 5,
                maxBranches = 2,
                maxTeachers = 10,
                storageGB = 20,
                smsQuota = 100,
                isActive = true,
                description = "REV-011 test plan",
                currencyCode = "USD",
                durationMonths = 12,
                bonusMonths = 1
            });
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
        var approved = await SendAsync(HttpMethod.Post, $"/api/tenants/{tenantId}/approve",
            platformToken, tenantHeader: null, payload: new { tenantId, planId });
        Assert.Equal(HttpStatusCode.Created, approved.StatusCode);

        var activated = await SendAsync(HttpMethod.Post, $"/api/tenants/{tenantId}/activate",
            platformToken, tenantHeader: null);
        Assert.Equal(HttpStatusCode.NoContent, activated.StatusCode);
    }

    private async Task<Guid> SeedBranchAsync(Seed seed)
    {
        var create = await SendAsync(HttpMethod.Post, "/api/branches",
            seed.TenantAdminToken, seed.Identifier,
            payload: new { name = "R11 Branch", address = "1 St", phone = "01000000000", isActive = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Branches.IgnoreQueryFilters()
            .Where(b => b.TenantId == seed.TenantId.ToString())
            .Select(b => b.Id).SingleAsync();
    }

    private async Task<(int StageId, int YearId)> SeedStageYearAsync(Seed seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var maxStageId = await db.AcademicStages.IgnoreQueryFilters().Select(x => (int?)x.Id).MaxAsync() ?? 0;
        var stageId = maxStageId + 1;
        var stage = AcademicStage.Create(stageId, $"S{stageId % 100000}", "Stage One", 1).Value;
        db.AcademicStages.Add(stage);
        db.StampAddedTenantIds(seed.TenantId.ToString());
        await db.SaveChangesAsync();

        var maxYearId = await db.AcademicYears.IgnoreQueryFilters().Select(x => (int?)x.Id).MaxAsync() ?? 0;
        var year = AcademicYear.Create(maxYearId + 1, stageId, $"Y{maxYearId + 1}", "Year One").Value;
        db.AcademicYears.Add(year);
        db.StampAddedTenantIds(seed.TenantId.ToString());
        await db.SaveChangesAsync();
        return (stageId, year.Id);
    }

    private async Task<Guid> SeedStudentRowAsync(Seed seed, Guid branchId, int stageId, int yearId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var student = Student.Create(
            Guid.NewGuid(), branchId, stageId, yearId,
            "طالب اختبار", "Test Student", null, Gender.Male, "01000000000",
            $"QR-{Guid.NewGuid():N}", null, null, StudentStatus.Active,
            DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Students.Add(student);
        db.StampAddedTenantIds(seed.TenantId.ToString());
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task SoftDeleteStudentRowAsync(Guid studentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var student = await db.Students.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == studentId);
        Assert.NotNull(student);
        Assert.True(student!.SoftDelete().IsSuccess);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTeacherRowAsync(Seed seed, Guid branchId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teacher = Teacher.Create(
            Guid.NewGuid(), $"user-{Guid.NewGuid():N}", branchId,
            "Ahmed Ali", "01000000000", "BSc", 5, TeacherStatus.Active,
            DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Teachers.Add(teacher);
        db.StampAddedTenantIds(seed.TenantId.ToString());
        await db.SaveChangesAsync();
        return teacher.Id;
    }

    private async Task SeedRevokedMembershipAsync(Seed seed)
    {
        var user = await CreateUserAsync($"revoked_{Guid.NewGuid():N}@rev011.test");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = TenantMembership.Create(
            user.Id, seed.TenantId.ToString(), "TenantUser", TenantMembershipStatus.Revoked);
        Assert.True(membership.IsSuccess);
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
    }

    private async Task SetUsersCountAsync(Guid tenantId, int usersCount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var counter = await db.TenantUsageCounters.SingleAsync(c => c.Id == tenantId);
        counter.UpdateCounts(counter.StudentsCount, usersCount, counter.BranchesCount, counter.TeachersCount,
            counter.StorageUsedMB, counter.SMSUsedThisCycle);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> GetSingleStudentIdAsync(Seed seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Students.IgnoreQueryFilters().Where(s => s.TenantId == seed.TenantId.ToString()).Select(s => s.Id).SingleAsync();
    }

    private async Task<string> CreateInvitationViaApiAsync(Seed seed, string email)
    {
        _factory.EmailSender.Clear();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.TenantAdminToken);
        request.Headers.Add("tenant", seed.Identifier);
        request.Content = JsonContent.Create(new { email, roleName = "TenantUser", expirationDays = 7 });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var sent = Assert.Single(_factory.EmailSender.Sent);
        Assert.Equal(email, sent.To);
        return TestInviteTokens.ExtractTokenFromEmailBody(sent.Body);
    }

    private async Task<string> SeedInvitationRowAsync(string tenantId, string email, string invitedByUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rawToken = TestInviteTokens.NewToken();
        var invitation = TenantInvitation.Create(
            Guid.NewGuid(), tenantId, email, invitedByUserId,
            "TenantUser", TestInviteTokens.Sha256Hex(rawToken),
            DateTimeOffset.UtcNow.AddDays(7)).Value;
        db.TenantInvitations.Add(invitation);
        await db.SaveChangesAsync();
        return rawToken;
    }

    private async Task<IdentityUser> CreateUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null) return existing;

        var user = new IdentityUser
        {
            Email = email, UserName = email, EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task<string> CreatePlatformAdminAsync()
    {
        var email = $"platform_{Guid.NewGuid():N}@rev011.test";
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roleManager.RoleExistsAsync("PlatformAdmin"))
        {
            await roleManager.CreateAsync(new ApplicationRole("PlatformAdmin")
            {
                Code = "PlatformAdmin", DisplayName = "Platform Administrator",
                IsSystem = true, NormalizedName = "PLATFORMADMIN"
            });
        }
        // Created and role-assigned in the SAME scope: passing an instance tracked by a
        // disposed scope into UserManager.AddToRoleAsync trips an identity-map conflict.
        var user = new IdentityUser
        {
            Email = email, UserName = email, EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, "PlatformAdmin");
        return _factory.GenerateTestToken(user.Id, email, ["PlatformAdmin"]);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? token, string? tenantHeader, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null)
            request.Headers.Add("tenant", tenantHeader);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostStudentAsync(Seed seed, Guid branchId, int stageId, int yearId)
    {
        return await SendAsync(HttpMethod.Post, "/api/students",
            seed.TenantAdminToken, seed.Identifier,
            payload: new
            {
                branchId,
                stageId,
                yearId,
                fullNameAr = "طالب اختبار",
                fullNameEn = "Test Student",
                gender = 1,
                phone = "01000000000",
                qrCode = $"QR-{Guid.NewGuid():N}",
                status = 0,
                enrolledAt = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
            });
    }
}
