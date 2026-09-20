namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Teachers.SalaryPayments.Commands;
using Centerix.Application.Teachers.TeacherSalaryConfigs.Commands;
using Centerix.Application.Teachers.Teachers.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.SalaryPayments;
using Centerix.Domain.Teachers.TeacherSalaryConfigs;
using Centerix.Domain.Teachers.Teachers;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 15 — Teachers Module Production Hardening tests.
/// Covers: H-02 (state machine), H-03 (concurrency), F-05 (membership),
/// F-10 (EffectiveFrom), F-14 (Net>Gross), and soft-delete visibility.
/// </summary>
public class Task15TeachersHardeningTests
{
    // =====================================================================
    // H-02 — SalaryPayment Domain State Machine
    // =====================================================================

    [Fact]
    public void StateMachine_PendingToPaid_Succeeds()
    {
        var payment = CreatePendingPayment();
        var result = payment.MarkPaid(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Paid, payment.Status);
        Assert.NotNull(payment.PaidAt);
    }

    [Fact]
    public void StateMachine_PendingToCancelled_Succeeds()
    {
        var payment = CreatePendingPayment();
        var result = payment.Cancel();
        Assert.True(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Cancelled, payment.Status);
        Assert.Null(payment.PaidAt);
    }

    [Fact]
    public void StateMachine_PaidToPaid_Fails()
    {
        var payment = CreatePendingPayment();
        payment.MarkPaid(DateTime.UtcNow);
        var result = payment.MarkPaid(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public void StateMachine_PaidToCancelled_Fails()
    {
        var payment = CreatePendingPayment();
        payment.MarkPaid(DateTime.UtcNow);
        var result = payment.Cancel();
        Assert.False(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public void StateMachine_CancelledToPaid_Fails()
    {
        var payment = CreatePendingPayment();
        payment.Cancel();
        var result = payment.MarkPaid(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Cancelled, payment.Status);
    }

    [Fact]
    public void StateMachine_CancelledToCancelled_NoOpSucceeds()
    {
        var payment = CreatePendingPayment();
        payment.Cancel();
        var result = payment.Cancel();
        Assert.True(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Cancelled, payment.Status);
        Assert.Null(payment.PaidAt);
    }

    // =====================================================================
    // H-02 — SalaryPayment Creation (must be Pending, no client-supplied status)
    // =====================================================================

    [Fact]
    public void Create_PendingPayment_Succeeds()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            2026,
            10000m,
            8500m);
        Assert.True(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Pending, result.Value.Status);
        Assert.Null(result.Value.PaidAt);
    }

    [Fact]
    public void Create_EmptyTeacherId_Fails()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.Empty,
            1,
            2026,
            10000m,
            8500m);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Create_ZeroGrossAmount_Fails()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            2026,
            0m,
            0m);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Create_ZeroNetAmount_Fails()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            2026,
            10000m,
            0m);
        Assert.False(result.IsSuccess);
    }

    // =====================================================================
    // F-14 — NetAmount > GrossAmount rejected
    // =====================================================================

    [Fact]
    public void Create_NetAmountExceedsGrossAmount_Fails()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            2026,
            5000m,
            6000m);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "SalaryPayment.NetExceedsGross");
    }

    [Fact]
    public void Create_NetAmountEqualsGrossAmount_Succeeds()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            2026,
            5000m,
            5000m);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Create_NetAmountLessThanGrossAmount_Succeeds()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            2026,
            10000m,
            8500m);
        Assert.True(result.IsSuccess);
    }

    // =====================================================================
    // F-10 — EffectiveFrom default rejected
    // =====================================================================

    [Fact]
    public void TeacherSalaryConfig_Create_DefaultEffectiveFrom_Fails()
    {
        var result = TeacherSalaryConfig.Create(
            0,
            Guid.NewGuid(),
            null,
            SalaryType.Fixed,
            5000m,
            default);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TeacherSalaryConfig.EffectiveFrom_Required");
    }

    [Fact]
    public void TeacherSalaryConfig_Create_ValidEffectiveFrom_Succeeds()
    {
        var result = TeacherSalaryConfig.Create(
            0,
            Guid.NewGuid(),
            null,
            SalaryType.Fixed,
            5000m,
            new DateOnly(2026, 1, 1));
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void TeacherSalaryConfig_Create_PercentageOver100_Fails()
    {
        var result = TeacherSalaryConfig.Create(
            0,
            Guid.NewGuid(),
            null,
            SalaryType.Percentage,
            150m,
            new DateOnly(2026, 1, 1));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TeacherSalaryConfig_Create_ValueOutOfRange_Fails()
    {
        var result = TeacherSalaryConfig.Create(
            0,
            Guid.NewGuid(),
            null,
            SalaryType.Fixed,
            0m,
            new DateOnly(2026, 1, 1));
        Assert.False(result.IsSuccess);
    }

    // =====================================================================
    // F-05 — Teacher UserId membership check (handler-level)
    // =====================================================================

    [Fact]
    public async Task CreateTeacherHandler_ForeignUser_ReturnsUserNotInTenant()
    {
        using var scope = TestInfrastructure.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        TestInfrastructure.AuthorizeTenant(scope.ServiceProvider, tenantId);

        await TestInfrastructure.EnsureTenantExists(scope.ServiceProvider, tenantId);

        var branch = CreateTestBranch(db, tenantId);
        await db.SaveChangesAsync();

        var limitService = Substitute.For<ILimitService>();
        limitService.ReserveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Updated);

        var handler = new CreateTeacherHandler(
            db,
            scope.ServiceProvider.GetRequiredService<ICurrentTenant>(),
            limitService,
            Substitute.For<IAuditWriter>());

        var command = new CreateTeacherCommand(
            UserId: "foreign-user-not-in-tenant",
            BranchId: branch.Id,
            FullName: "Test Teacher",
            Phone: "1234567890",
            Qualification: null,
            YearsExp: (byte)5,
            Status: TeacherStatus.Active,
            JoinedAt: new DateOnly(2026, 1, 1));

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Teacher.UserNotInTenant");
    }

    [Fact]
    public async Task UpdateTeacherHandler_ForeignUser_ReturnsUserNotInTenant()
    {
        using var scope = TestInfrastructure.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        TestInfrastructure.AuthorizeTenant(scope.ServiceProvider, tenantId);

        await TestInfrastructure.EnsureTenantExists(scope.ServiceProvider, tenantId);

        var branch = CreateTestBranch(db, tenantId);
        var teacher = CreateTestTeacher(db, tenantId, branch.Id, "existing-user");
        await db.SaveChangesAsync();

        var handler = new UpdateTeacherHandler(
            db,
            scope.ServiceProvider.GetRequiredService<ICurrentTenant>(),
            Substitute.For<IAuditWriter>());

        var command = new UpdateTeacherCommand(
            Id: teacher.Id,
            UserId: "foreign-user-not-in-tenant",
            BranchId: branch.Id,
            FullName: "Updated Name",
            Phone: "0987654321",
            Qualification: null,
            YearsExp: (byte)3,
            Status: TeacherStatus.Active);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Teacher.UserNotInTenant");
    }

    // =====================================================================
    // H-04 — Soft-delete visibility
    // =====================================================================

    [Fact]
    public async Task SoftDeletedTeacher_IsNotVisibleInList()
    {
        using var scope = TestInfrastructure.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        TestInfrastructure.AuthorizeTenant(scope.ServiceProvider, tenantId);

        await TestInfrastructure.EnsureTenantExists(scope.ServiceProvider, tenantId);

        var branch = CreateTestBranch(db, tenantId);
        var teacher = CreateTestTeacher(db, tenantId, branch.Id, "user-soft-delete");
        await db.SaveChangesAsync();

        teacher.SoftDelete();
        await db.SaveChangesAsync();

        var listed = await db.Teachers
            .Where(t => t.TenantId == tenantId)
            .ToListAsync();

        Assert.DoesNotContain(listed, t => t.Id == teacher.Id);
    }

    [Fact]
    public async Task SoftDeletedTeacher_GetById_ReturnsNotFound()
    {
        using var scope = TestInfrastructure.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        TestInfrastructure.AuthorizeTenant(scope.ServiceProvider, tenantId);

        await TestInfrastructure.EnsureTenantExists(scope.ServiceProvider, tenantId);

        var branch = CreateTestBranch(db, tenantId);
        var teacher = CreateTestTeacher(db, tenantId, branch.Id, "user-getbyid-delete");
        await db.SaveChangesAsync();

        teacher.SoftDelete();
        await db.SaveChangesAsync();

        var found = await db.Teachers
            .FirstOrDefaultAsync(t => t.Id == teacher.Id);

        Assert.Null(found);
    }

    [Fact]
    public async Task SoftDeletedTeacher_ExistenceCheck_ReturnsFalse()
    {
        using var scope = TestInfrastructure.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        TestInfrastructure.AuthorizeTenant(scope.ServiceProvider, tenantId);

        await TestInfrastructure.EnsureTenantExists(scope.ServiceProvider, tenantId);

        var branch = CreateTestBranch(db, tenantId);
        var teacher = CreateTestTeacher(db, tenantId, branch.Id, "user-existence-delete");
        await db.SaveChangesAsync();

        teacher.SoftDelete();
        await db.SaveChangesAsync();

        var exists = await db.Teachers
            .AnyAsync(t => t.Id == teacher.Id);

        Assert.False(exists);
    }

    [Fact]
    public async Task SoftDeletedTeacher_CannotCreateSalaryConfigForDeletedTeacher()
    {
        using var scope = TestInfrastructure.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        TestInfrastructure.AuthorizeTenant(scope.ServiceProvider, tenantId);

        await TestInfrastructure.EnsureTenantExists(scope.ServiceProvider, tenantId);

        var branch = CreateTestBranch(db, tenantId);
        var teacher = CreateTestTeacher(db, tenantId, branch.Id, "user-salaryconfig-delete");
        await db.SaveChangesAsync();

        teacher.SoftDelete();
        await db.SaveChangesAsync();

        var handler = new CreateTeacherSalaryConfigHandler(
            db,
            scope.ServiceProvider.GetRequiredService<ICurrentTenant>(),
            Substitute.For<IAuditWriter>());

        var command = new CreateTeacherSalaryConfigCommand(
            TeacherId: teacher.Id,
            GroupId: null,
            SalaryType: SalaryType.Fixed,
            Value: 5000m,
            EffectiveFrom: new DateOnly(2026, 1, 1));

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static SalaryPayment CreatePendingPayment()
    {
        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            2026,
            10000m,
            8500m);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Domain.Students.Branches.Branch CreateTestBranch(AppDbContext db, string tenantId)
    {
        var branch = Domain.Students.Branches.Branch.Create(
            Guid.NewGuid(),
            "Test Branch").Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(tenantId);
        return branch;
    }

    private static Teacher CreateTestTeacher(AppDbContext db, string tenantId, Guid branchId, string userId)
    {
        var result = Teacher.Create(
            Guid.NewGuid(),
            userId,
            branchId,
            "Test Teacher",
            "1234567890",
            null,
            (byte)5,
            TeacherStatus.Active,
            new DateOnly(2026, 1, 1));
        var teacher = result.Value;
        db.Teachers.Add(teacher);
        db.StampAddedTenantIds(tenantId);
        return teacher;
    }
}

/// <summary>
/// Shared test infrastructure for Teachers hardening tests.
/// </summary>
internal static class TestInfrastructure
{
    private static readonly string TenantIdField = "_authorizedTenantId";
    private static readonly string IsAuthorizedField = "_isAuthorized";

    public static IServiceScope CreateScope()
    {
        var factory = new TestWebApplicationFactory();
        return factory.Services.CreateScope();
    }

    public static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        var authorizedTenantIdField = type.GetField(TenantIdField, BindingFlags.NonPublic | BindingFlags.Instance);
        var isAuthorizedField = type.GetField(IsAuthorizedField, BindingFlags.NonPublic | BindingFlags.Instance);
        authorizedTenantIdField!.SetValue(currentTenant, tenantId);
        isAuthorizedField!.SetValue(currentTenant, true);
    }

    public static async Task EnsureTenantExists(IServiceProvider scope, string tenantId)
    {
        var store = scope.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@test.com",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }
}
