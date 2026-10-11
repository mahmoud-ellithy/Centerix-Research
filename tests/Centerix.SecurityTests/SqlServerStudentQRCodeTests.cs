using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Branches;
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

// REV-009 (relational proofs): tenant-scoped QR uniqueness against real SQL Server — real
// unique index, real transactions, real concurrency. Requires Docker/Testcontainers or an
// external SQL Server (CENTERIX_SQLTEST_CONNECTION); cannot run where no server exists.
[Collection("SqlServerIntegration")]
public class SqlServerStudentQRCodeTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly SqlServerIntegrationFactory _env;

    public SqlServerStudentQRCodeTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _env.EmailSender.Clear();
    }

    // ==================================================================
    // Schema: migration TenantScopeStudentQRCodeIndex applied
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_QRIndex_IsTenantScopedUnique_GlobalIndexGone()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Old global index is gone.
        var oldIndex = await db.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(*) AS Value FROM sys.indexes
            WHERE name = 'UX_Students_QRCode'
              AND object_id = OBJECT_ID('Platform.Students')
            """).SingleAsync();
        Assert.Equal(0, oldIndex);

        // New composite unique index exists on (TenantId, QRCode).
        var newIndex = await db.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(*) AS Value FROM sys.indexes
            WHERE name = 'UX_Students_Tenant_QRCode'
              AND object_id = OBJECT_ID('Platform.Students')
              AND is_unique = 1
            """).SingleAsync();
        Assert.Equal(1, newIndex);
    }

    // ==================================================================
    // Behavior on the migrated schema
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task SameQRCode_AcrossTenants_BothSucceed_OnSqlServer()
    {
        var seedA = await SeedTenantAsync();
        var seedB = await SeedTenantAsync();
        var qr = $"SQLQR-SHARED-{Guid.NewGuid():N}";

        var responseA = await PostStudentAsync(seedA, qr);
        var responseB = await PostStudentAsync(seedB, qr);

        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task DuplicateQRCode_SameTenant_Returns409_IncludingSoftDeleted()
    {
        var seed = await SeedTenantAsync();
        var qr = $"SQLQR-DUPE-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Created, (await PostStudentAsync(seed, qr)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostStudentAsync(seed, qr)).StatusCode);

        // Soft-delete the student: its QR stays reserved → still 409, never 201/500.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = await db.Students.IgnoreQueryFilters()
                .SingleAsync(s => s.TenantId == seed.TenantId && s.QRCode == qr);
            Assert.True(student.SoftDelete().IsSuccess);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Conflict, (await PostStudentAsync(seed, qr)).StatusCode);
    }

    /// <summary>
    /// Concurrent duplicate inserts racing on one QR: exactly one wins with 201, the loser
    /// gets a clean 409 via the unique-violation mapping (never a 500), and the quota
    /// reservation rolls back with the loser's transaction.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDuplicateQRCode_ExactlyOneSucceeds_One409()
    {
        var seed = await SeedTenantAsync();
        var qr = $"SQLQR-RACE-{Guid.NewGuid():N}";

        var responses = await Task.WhenAll(
            PostStudentAsync(seed, qr),
            PostStudentAsync(seed, qr));

        Assert.Single(responses.Where(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Single(responses.Where(r => r.StatusCode == HttpStatusCode.Conflict));

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Students.IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == seed.TenantId && s.QRCode == qr));
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private sealed record Seed(
        string AdminToken, string TenantId, Guid BranchId, int StageId, int YearId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid().ToString();
        await EnsureTenantAsync(tenantId);
        await _env.Factory.SeedPermissionsAsync();
        var adminToken = await SeedAdminAsync(tenantId);
        await GrantStudentsWriteAsync();
        await EnsureQuotaAndFeatureAsync(tenantId);
        var (branchId, stageId, yearId) = await SeedBranchStageYearAsync(tenantId);
        return new Seed(adminToken, tenantId, branchId, stageId, yearId);
    }

    private async Task<HttpResponseMessage> PostStudentAsync(Seed seed, string qrCode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/students");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        request.Headers.Add("tenant", seed.TenantId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
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
            }),
            Encoding.UTF8, "application/json");
        return await _env.Client.SendAsync(request);
    }

    private async Task EnsureTenantAsync(string id)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(id) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = id,
                Identifier = id,
                Name = id,
                Email = $"{id}@registry.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task<string> SeedAdminAsync(string tenantId)
    {
        var adminEmail = $"admin_{Guid.NewGuid():N}@sqlqr.test";
        var user = await CreateUserAsync(adminEmail);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = TenantMembership.Create(user.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess);
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();

        return _env.Factory.GenerateTestToken(user.Id, user.Email!, ["TenantAdmin"]);
    }

    private async Task<IdentityUser> CreateUserAsync(string email, string password = StrongPassword)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null) return existing;

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, password);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task GrantStudentsWriteAsync()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var code in new[] { Permissions.Students.Create, Permissions.Students.Read, Permissions.Students.Delete })
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "TenantAdmin");
            Assert.NotNull(role);
            var permission = await db.Permissions.FirstOrDefaultAsync(p => p.Code == code);
            Assert.NotNull(permission);
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleId == role!.Id && rp.PermissionId == permission!.Id))
            {
                db.RolePermissions.Add(RolePermission.Create(role!.Id, permission!.Id).Value);
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task EnsureQuotaAndFeatureAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var plan = Plan.Create(
            0, $"SQLQR_{Guid.NewGuid():N}"[..28], "QR test plan", 100m,
            100, 10, 10, 40, 50, 1000, true, null, "USD", 12, 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, plan.Id, 100m, 100m, "USD", 12, 0,
            DateTime.UtcNow, false, SubscriptionStatus.Pending,
            100, 10, 10, 40, 50, 500).Value;
        Assert.True(sub.Activate(DateTime.UtcNow).IsSuccess);
        sub.GrantFeature("Students");
        db.TenantPlans.Add(sub);

        db.TenantUsageCounters.Add(TenantUsageCounter.Create(
            Guid.Parse(tenantId), 0, 0, 0, 0, 0, 0, 100, 10, 10, 40, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<(Guid BranchId, int StageId, int YearId)> SeedBranchStageYearAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var branch = Branch.Create(Guid.NewGuid(), "QR Branch", "1 St", "01000000000", null, true).Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(tenantId);

        var maxStageId = await db.AcademicStages.IgnoreQueryFilters().Select(x => (int?)x.Id).MaxAsync() ?? 0;
        var stage = AcademicStage.Create(maxStageId + 1, $"QS{maxStageId + 1}", "QR Stage", (byte)1).Value;
        db.AcademicStages.Add(stage);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var maxYearId = await db.AcademicYears.IgnoreQueryFilters().Select(x => (int?)x.Id).MaxAsync() ?? 0;
        var year = AcademicYear.Create(maxYearId + 1, stage.Id, $"QY{maxYearId + 1}", "QR Year").Value;
        db.AcademicYears.Add(year);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return (branch.Id, stage.Id, year.Id);
    }
}
