using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

// REV-010: attendance logs cannot reference a foreign or soft-deleted student.
// (Same-day duplicate → 409 is proven against SQL Server, where the unique index is
// enforced; the EF InMemory provider does not enforce unique constraints.)
[Collection("Integration")]
public class Rev010AttendanceIsolationTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Rev010AttendanceIsolationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Rev010")]
    public async Task CreateAttendanceLog_ForeignTenantStudent_Returns404_NoRowWritten()
    {
        var seed = await SeedTenantAsync();
        var foreignStudentId = Guid.NewGuid();

        var response = await PostAttendanceAsync(seed, studentId: foreignStudentId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.AttendanceLogs.IgnoreQueryFilters()
            .CountAsync(a => a.StudentId == foreignStudentId));
    }

    [Fact]
    [Trait("Category", "Rev010")]
    public async Task CreateAttendanceLog_CrossTenantStudent_Returns404()
    {
        var seedA = await SeedTenantAsync();
        var seedB = await SeedTenantAsync();
        var studentInB = await SeedStudentRowAsync(seedB.TenantId.ToString());

        // Caller in tenant A references tenant B's student → 404, nothing written.
        var response = await PostAttendanceAsync(seedA, studentInB);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.AttendanceLogs.IgnoreQueryFilters()
            .CountAsync(a => a.StudentId == studentInB));
    }

    [Fact]
    [Trait("Category", "Rev010")]
    public async Task CreateAttendanceLog_OwnStudent_Returns201()
    {
        var seed = await SeedTenantAsync();
        var studentId = await SeedStudentRowAsync(seed.TenantId.ToString());

        var response = await PostAttendanceAsync(seed, studentId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Rev010")]
    public async Task CreateAttendanceLog_SoftDeletedStudent_Returns404()
    {
        var seed = await SeedTenantAsync();
        var studentId = await SeedStudentRowAsync(seed.TenantId.ToString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = await db.Students.IgnoreQueryFilters().SingleAsync(s => s.Id == studentId);
            Assert.True(student.SoftDelete().IsSuccess);
            await db.SaveChangesAsync();
        }

        var response = await PostAttendanceAsync(seed, studentId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await vdb.AttendanceLogs.IgnoreQueryFilters()
            .CountAsync(a => a.StudentId == studentId));
    }

    // ============================================================
    // Seeding helpers
    // ============================================================
    private sealed record Seed(string AdminToken, Guid TenantId, string Identifier);

    private async Task<Seed> SeedTenantAsync()
    {
        var identifier = $"r10-{Guid.NewGuid():N}";
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

        var pid = db.Permissions.Single(p => p.Code == Permissions.AttendanceLogs.Create).Id;
        if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole.Id && rp.PermissionId == pid))
            db.RolePermissions.Add(RolePermission.Create(tenantAdminRole.Id, pid).Value);
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(
            Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP", "Africa/Cairo",
            "O", "W", $"owner_{Guid.NewGuid():N}@rev010.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        if (await store.TryGetAsync(tenant.Id.ToString()) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r10@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
        }

        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        if (await tenantDb.TenantInfo.FindAsync(tenant.Id.ToString()) is null)
        {
            tenantDb.TenantInfo.Add(new CenterixTenantInfo
            {
                Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
                Email = "r10@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
            await tenantDb.SaveChangesAsync();
        }

        var adminEmail = $"tadmin_{Guid.NewGuid():N}@rev010.test";
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

        return new Seed(
            _factory.GenerateTestToken(admin.Id, adminEmail, ["TenantAdmin"]),
            tenant.Id, identifier);
    }

    private async Task<Guid> SeedStudentRowAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var student = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1,
            "طالب اختبار", "Test Student", null, Gender.Male, "01000000000",
            $"QR-{Guid.NewGuid():N}", null, null, StudentStatus.Active,
            DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Students.Add(student);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task<HttpResponseMessage> PostAttendanceAsync(Seed seed, Guid studentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/attendancelogs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        request.Headers.Add("tenant", seed.Identifier);
        request.Content = JsonContent.Create(new
        {
            studentId,
            groupId = Guid.NewGuid(),
            sessionDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            status = 1, // Absent (Present/Late would require checkInTime per the validator)
            isOffline = false
        });
        return await _client.SendAsync(request);
    }
}
