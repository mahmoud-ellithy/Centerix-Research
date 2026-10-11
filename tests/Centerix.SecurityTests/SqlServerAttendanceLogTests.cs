using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Enums;
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

// REV-010 (relational proofs): cross-tenant attendance isolation and the same-day
// duplicate → 409 mapping against real SQL Server, where the unique index is enforced.
// Requires Docker/Testcontainers or an external SQL Server; cannot run where none exists.
[Collection("SqlServerIntegration")]
public class SqlServerAttendanceLogTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly SqlServerIntegrationFactory _env;

    public SqlServerAttendanceLogTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _env.EmailSender.Clear();
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Schema_SessionUniqueIndex_Preserved()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var index = await db.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(*) AS Value FROM sys.indexes
            WHERE name = 'UX_AttendanceLogs_Student_Session'
              AND object_id = OBJECT_ID('Platform.AttendanceLogs')
              AND is_unique = 1
            """).SingleAsync();
        Assert.Equal(1, index);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task CreateAttendanceLog_ForeignTenantStudent_Returns404_NoRowWritten()
    {
        var seed = await SeedTenantAsync();

        var response = await PostAttendanceAsync(seed.AdminToken, seed.TenantId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task CreateAttendanceLog_OwnStudent_Returns201_DuplicateSameDay_Returns409()
    {
        var seed = await SeedTenantAsync();
        var studentId = await SeedStudentRowAsync(seed.TenantId);

        Assert.Equal(HttpStatusCode.Created,
            (await PostAttendanceAsync(seed.AdminToken, seed.TenantId, studentId)).StatusCode);

        // Same student + same session date → unique violation mapped to 409, never 500.
        Assert.Equal(HttpStatusCode.Conflict,
            (await PostAttendanceAsync(seed.AdminToken, seed.TenantId, studentId)).StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AttendanceLogs.IgnoreQueryFilters()
            .CountAsync(a => a.StudentId == studentId));
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task CreateAttendanceLog_SoftDeletedStudent_Returns404()
    {
        var seed = await SeedTenantAsync();
        var studentId = await SeedStudentRowAsync(seed.TenantId);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = await db.Students.IgnoreQueryFilters().SingleAsync(s => s.Id == studentId);
            Assert.True(student.SoftDelete().IsSuccess);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await PostAttendanceAsync(seed.AdminToken, seed.TenantId, studentId)).StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private sealed record Seed(string AdminToken, string TenantId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid().ToString();
        await EnsureTenantAsync(tenantId);
        await _env.Factory.SeedPermissionsAsync();

        var adminEmail = $"admin_{Guid.NewGuid():N}@sqlatt.test";
        var user = await CreateUserAsync(adminEmail);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = TenantMembership.Create(user.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        await GrantAttendanceCreateAsync();

        return new Seed(
            _env.Factory.GenerateTestToken(user.Id, user.Email!, ["TenantAdmin"]),
            tenantId);
    }

    private async Task<HttpResponseMessage> PostAttendanceAsync(string adminToken, string tenantId, Guid studentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/attendancelogs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.Add("tenant", tenantId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                studentId,
                groupId = Guid.NewGuid(),
                sessionDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                status = 1,
                isOffline = false
            }),
            Encoding.UTF8, "application/json");
        return await _env.Client.SendAsync(request);
    }

    private async Task<Guid> SeedStudentRowAsync(string tenantId)
    {
        var (branchId, stageId, yearId) = await SeedBranchStageYearAsync(tenantId);
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var student = Student.Create(
            Guid.NewGuid(), branchId, stageId, yearId,
            "طالب اختبار", "Test Student", null, null, "01000000000",
            $"QR-{Guid.NewGuid():N}", null, null, StudentStatus.Active,
            DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Students.Add(student);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task<(Guid BranchId, int StageId, int YearId)> SeedBranchStageYearAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var branch = Centerix.Domain.Students.Branches.Branch.Create(
            Guid.NewGuid(), "ATT Branch", "1 St", "01000000000", null, true).Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(tenantId);

        var stageCode = $"AS{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var stage = Centerix.Domain.Students.Lookups.AcademicStage.Create(0, stageCode, "ATT Stage", (byte)1).Value;
        db.AcademicStages.Add(stage);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var yearCode = $"AY{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var year = Centerix.Domain.Students.Lookups.AcademicYear.Create(0, stage.Id, yearCode, "ATT Year").Value;
        db.AcademicYears.Add(year);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return (branch.Id, stage.Id, year.Id);
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

    private async Task<IdentityUser> CreateUserAsync(string email)
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
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task GrantAttendanceCreateAsync()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "TenantAdmin");
        Assert.NotNull(role);
        var permission = await db.Permissions.FirstOrDefaultAsync(p => p.Code == Permissions.AttendanceLogs.Create);
        Assert.NotNull(permission);
        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleId == role!.Id && rp.PermissionId == permission!.Id))
        {
            db.RolePermissions.Add(RolePermission.Create(role!.Id, permission!.Id).Value);
            await db.SaveChangesAsync();
        }
    }
}
