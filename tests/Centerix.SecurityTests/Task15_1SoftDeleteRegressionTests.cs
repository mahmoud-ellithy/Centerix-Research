namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Students;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.Teachers;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task 15.1 — H-04 shared soft-delete regression tests for Teacher, Student, Branch.
/// Proves that the composed query filter (TenantId == current && DeletedAtUtc == null)
/// correctly hides soft-deleted entities from List, GetById, and Any/Existence queries.
/// Also verifies cross-tenant combined isolation: Tenant B cannot see Tenant A's
/// soft-deleted entities.
/// Executes against the actual configured AppDbContext (InMemory provider via
/// TestWebApplicationFactory) — no manual .Where() bypass.
/// </summary>
public class Task15_1SoftDeleteRegressionTests
{
    // ==================================================================
    // Teacher — soft-delete regression
    // ==================================================================

    [Fact]
    public async Task Teacher_SoftDeleted_NotVisibleInList()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var teacher = SeedTeacher(db, tenantId, "sd-list-teacher");
        await db.SaveChangesAsync();

        teacher.SoftDelete();
        await db.SaveChangesAsync();

        var list = await db.Teachers.Where(t => t.TenantId == tenantId).ToListAsync();
        Assert.DoesNotContain(list, t => t.Id == teacher.Id);
    }

    [Fact]
    public async Task Teacher_SoftDeleted_GetById_ReturnsNull()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var teacher = SeedTeacher(db, tenantId, "sd-getbyid-teacher");
        await db.SaveChangesAsync();

        teacher.SoftDelete();
        await db.SaveChangesAsync();

        var found = await db.Teachers.FirstOrDefaultAsync(t => t.Id == teacher.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task Teacher_SoftDeleted_Any_ReturnsFalse()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var teacher = SeedTeacher(db, tenantId, "sd-any-teacher");
        await db.SaveChangesAsync();

        teacher.SoftDelete();
        await db.SaveChangesAsync();

        var exists = await db.Teachers.AnyAsync(t => t.Id == teacher.Id);
        Assert.False(exists);
    }

    // ==================================================================
    // Student — soft-delete regression
    // ==================================================================

    [Fact]
    public async Task Student_SoftDeleted_NotVisibleInList()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var student = SeedStudent(db, tenantId);
        await db.SaveChangesAsync();

        student.SoftDelete();
        await db.SaveChangesAsync();

        var list = await db.Students.Where(s => s.TenantId == tenantId).ToListAsync();
        Assert.DoesNotContain(list, s => s.Id == student.Id);
    }

    [Fact]
    public async Task Student_SoftDeleted_GetById_ReturnsNull()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var student = SeedStudent(db, tenantId);
        await db.SaveChangesAsync();

        student.SoftDelete();
        await db.SaveChangesAsync();

        var found = await db.Students.FirstOrDefaultAsync(s => s.Id == student.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task Student_SoftDeleted_Any_ReturnsFalse()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var student = SeedStudent(db, tenantId);
        await db.SaveChangesAsync();

        student.SoftDelete();
        await db.SaveChangesAsync();

        var exists = await db.Students.AnyAsync(s => s.Id == student.Id);
        Assert.False(exists);
    }

    // ==================================================================
    // Branch — soft-delete regression
    // ==================================================================

    [Fact]
    public async Task Branch_SoftDeleted_NotVisibleInList()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var branch = SeedBranch(db, tenantId);
        await db.SaveChangesAsync();

        branch.SoftDelete();
        await db.SaveChangesAsync();

        var list = await db.Branches.Where(b => b.TenantId == tenantId).ToListAsync();
        Assert.DoesNotContain(list, b => b.Id == branch.Id);
    }

    [Fact]
    public async Task Branch_SoftDeleted_GetById_ReturnsNull()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var branch = SeedBranch(db, tenantId);
        await db.SaveChangesAsync();

        branch.SoftDelete();
        await db.SaveChangesAsync();

        var found = await db.Branches.FirstOrDefaultAsync(b => b.Id == branch.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task Branch_SoftDeleted_Any_ReturnsFalse()
    {
        using var scope = CreateAuthorizedScope(out var tenantId, out var db);
        var branch = SeedBranch(db, tenantId);
        await db.SaveChangesAsync();

        branch.SoftDelete();
        await db.SaveChangesAsync();

        var exists = await db.Branches.AnyAsync(b => b.Id == branch.Id);
        Assert.False(exists);
    }

    // ==================================================================
    // Cross-Tenant + Soft-Delete combined
    // ==================================================================

    [Fact]
    public async Task CrossTenant_SoftDeletedEntity_NotVisibleInOtherTenant()
    {
        var factory = new TestWebApplicationFactory();
        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();

        var tenantIdA = $"tenant-a-{Guid.NewGuid():N}"[..20];
        var tenantIdB = $"tenant-b-{Guid.NewGuid():N}"[..20];

        AuthorizeTenant(scopeA.ServiceProvider, tenantIdA);
        AuthorizeTenant(scopeB.ServiceProvider, tenantIdB);
        await EnsureTenantExistsAsync(scopeA.ServiceProvider, tenantIdA);
        await EnsureTenantExistsAsync(scopeB.ServiceProvider, tenantIdB);

        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

        var branchA = SeedBranch(dbA, tenantIdA);
        await dbA.SaveChangesAsync();

        var teacherA = SeedTeacher(dbA, tenantIdA, "cross-tenant-teacher");
        await dbA.SaveChangesAsync();

        teacherA.SoftDelete();
        await dbA.SaveChangesAsync();

        var visibleInB = await dbB.Teachers.AnyAsync(t => t.Id == teacherA.Id);
        Assert.False(visibleInB, "Tenant B must not see Tenant A's soft-deleted teacher");

        var branchVisibleInB = await dbB.Branches.AnyAsync(b => b.Id == branchA.Id);
        Assert.False(branchVisibleInB, "Tenant B must not see Tenant A's branch (different tenant)");
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static readonly string TenantIdField = "_authorizedTenantId";
    private static readonly string IsAuthorizedField = "_isAuthorized";

    private IServiceScope CreateAuthorizedScope(out string tenantId, out AppDbContext db)
    {
        var factory = new TestWebApplicationFactory();
        var scope = factory.Services.CreateScope();
        tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        EnsureTenantExistsAsync(scope.ServiceProvider, tenantId).GetAwaiter().GetResult();
        db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return scope;
    }

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField(TenantIdField, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, tenantId);
        type.GetField(IsAuthorizedField, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, true);
    }

    private static async Task EnsureTenantExistsAsync(IServiceProvider scope, string tenantId)
    {
        var store = scope.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId, Identifier = tenantId, Name = tenantId,
                Email = $"{tenantId}@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
        }
    }

    private static Teacher SeedTeacher(AppDbContext db, string tenantId, string userId)
    {
        var branch = Branch.Create(Guid.NewGuid(), $"Branch-{userId}").Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(tenantId);

        var teacher = Teacher.Create(Guid.NewGuid(), userId, branch.Id,
            $"Teacher {userId}", "01000000000", null, (byte)5,
            TeacherStatus.Active, new DateOnly(2026, 1, 1)).Value;
        db.Teachers.Add(teacher);
        db.StampAddedTenantIds(tenantId);
        return teacher;
    }

    private static Student SeedStudent(AppDbContext db, string tenantId)
    {
        var branch = Branch.Create(Guid.NewGuid(), $"Branch-sd-{tenantId[..8]}").Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(tenantId);

        var student = Student.Create(
            Guid.NewGuid(), branch.Id, 1, 1,
            "طالب اختبار", "Test Student",
            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-10)),
            Gender.Male, "01000000000", $"QR-{Guid.NewGuid():N}"[..12],
            null, null, StudentStatus.Active,
            DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Students.Add(student);
        db.StampAddedTenantIds(tenantId);
        return student;
    }

    private static Branch SeedBranch(AppDbContext db, string tenantId)
    {
        var branch = Branch.Create(Guid.NewGuid(), $"Branch-{tenantId[..8]}").Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(tenantId);
        return branch;
    }
}
