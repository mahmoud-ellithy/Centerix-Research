namespace Centerix.SecurityTests;

using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.SalaryPayments;
using Centerix.Domain.Teachers.Teachers;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task 15.1 — H-03 real SQL Server concurrency tests for SalaryPayment state transitions.
/// Proves that concurrent mutations on the same Pending row are guarded by RowVersion:
/// exactly one SaveChanges succeeds; the other receives DbUpdateConcurrencyException.
/// Final database state is always valid (Paid or Cancelled, never stale/invalid).
/// Uses Testcontainers SQL Server via SqlServerIntegrationFactory.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task15_1ConcurrencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public Task15_1ConcurrencySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // Helpers (same pattern as Phase5TeachersConcurrencySqlServerTests)
    // ==================================================================

    private async Task<string> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Finbuckle.MultiTenant.Abstractions.IMultiTenantStore<Centerix.Infrastructure.Tenancy.CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new Centerix.Infrastructure.Tenancy.CenterixTenantInfo
            {
                Id = tenantId, Identifier = tenantId, Name = tenantId,
                Email = $"{tenantId}@t151.test", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
        }
        return tenantId;
    }

    private async Task<(Guid branchId, Guid teacherId)> SeedTeacherAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var branch = Domain.Students.Branches.Branch.Create(Guid.NewGuid(), $"T151B-{tenantId[..8]}").Value;
        db.Branches.Add(branch);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var teacher = Teacher.Create(Guid.NewGuid(), $"uid-t151", branch.Id,
            "T151 Teacher", "01000000000", "BSc", 1,
            TeacherStatus.Active, DateOnly.FromDateTime(DateTime.UtcNow)).Value;
        db.Teachers.Add(teacher);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return (branch.Id, teacher.Id);
    }

    private async Task<Guid> SeedPendingPaymentAsync(string tenantId, Guid teacherId, byte month, short year)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = SalaryPayment.Create(Guid.NewGuid(), teacherId, month, year, 5000m, 4500m).Value;
        db.SalaryPayments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    // ==================================================================
    // Test C — Cancel vs Cancel (sequential)
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_ConcurrentCancelVsCancel_OnlyOneSucceeds_BecauseOfRowVersion()
    {
        var tenantId = await SeedTenantAsync();
        var (_, teacherId) = await SeedTeacherAsync(tenantId);
        var paymentId = await SeedPendingPaymentAsync(tenantId, teacherId, 1, 2027);

        using (var scopeA = _env.Factory.Services.CreateScope())
        using (var scopeB = _env.Factory.Services.CreateScope())
        {
            var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

            var payA = await dbA.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);
            var payB = await dbB.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);

            var resultA = payA.Cancel();
            Assert.True(resultA.IsSuccess, "First Cancel should succeed at domain level");

            var resultB = payB.Cancel();
            Assert.True(resultB.IsSuccess, "Second Cancel should succeed at domain level (both read Pending)");

            await dbA.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                async () => await dbB.SaveChangesAsync());
        }

        using (var verify = _env.Factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var final = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);
            Assert.Equal(SalaryPaymentStatus.Cancelled, final.Status);
            Assert.Null(final.PaidAt);
        }
    }

    // ==================================================================
    // Test C variant — Cancel vs Cancel with Barrier (true parallel)
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_ParallelCancelVsCancel_ExactlyOneWins()
    {
        var tenantId = await SeedTenantAsync();
        var (_, teacherId) = await SeedTeacherAsync(tenantId);
        var paymentId = await SeedPendingPaymentAsync(tenantId, teacherId, 2, 2027);

        using var barrier = new Barrier(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var taskA = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pay = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId, cts.Token);
            barrier.SignalAndWait(cts.Token);
            pay.Cancel();
            try
            {
                await db.SaveChangesAsync(cts.Token);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }, cts.Token);

        var taskB = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pay = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId, cts.Token);
            barrier.SignalAndWait(cts.Token);
            pay.Cancel();
            try
            {
                await db.SaveChangesAsync(cts.Token);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }, cts.Token);

        var results = await Task.WhenAll(taskA, taskB);
        var successCount = results.Count(r => r);

        Assert.Equal(1, successCount);

        using (var verify = _env.Factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var final = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);
            Assert.Equal(SalaryPaymentStatus.Cancelled, final.Status);
            Assert.Null(final.PaidAt);
        }
    }

    // ==================================================================
    // Test A — MarkPaid vs Cancel with Barrier (true parallel)
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_ParallelMarkPaidVsCancel_ExactlyOneWins()
    {
        var tenantId = await SeedTenantAsync();
        var (_, teacherId) = await SeedTeacherAsync(tenantId);
        var paymentId = await SeedPendingPaymentAsync(tenantId, teacherId, 3, 2027);

        using var barrier = new Barrier(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var taskMarkPaid = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pay = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId, cts.Token);
            barrier.SignalAndWait(cts.Token);
            pay.MarkPaid(DateTime.UtcNow);
            try
            {
                await db.SaveChangesAsync(cts.Token);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }, cts.Token);

        var taskCancel = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pay = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId, cts.Token);
            barrier.SignalAndWait(cts.Token);
            pay.Cancel();
            try
            {
                await db.SaveChangesAsync(cts.Token);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }, cts.Token);

        var results = await Task.WhenAll(taskMarkPaid, taskCancel);
        var successCount = results.Count(r => r);

        Assert.Equal(1, successCount);

        using (var verify = _env.Factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var final = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);
            Assert.NotEqual(SalaryPaymentStatus.Pending, final.Status);
            if (final.Status == SalaryPaymentStatus.Paid)
            {
                Assert.NotNull(final.PaidAt);
            }
            else
            {
                Assert.Null(final.PaidAt);
            }
        }
    }

    // ==================================================================
    // Test B — MarkPaid vs MarkPaid with Barrier (true parallel)
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task15_1")]
    public async Task SalaryPayment_ParallelMarkPaidVsMarkPaid_ExactlyOneWins()
    {
        var tenantId = await SeedTenantAsync();
        var (_, teacherId) = await SeedTeacherAsync(tenantId);
        var paymentId = await SeedPendingPaymentAsync(tenantId, teacherId, 4, 2027);

        using var barrier = new Barrier(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var taskA = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pay = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId, cts.Token);
            barrier.SignalAndWait(cts.Token);
            pay.MarkPaid(DateTime.UtcNow);
            try
            {
                await db.SaveChangesAsync(cts.Token);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }, cts.Token);

        var taskB = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pay = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId, cts.Token);
            barrier.SignalAndWait(cts.Token);
            pay.MarkPaid(DateTime.UtcNow);
            try
            {
                await db.SaveChangesAsync(cts.Token);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }, cts.Token);

        var results = await Task.WhenAll(taskA, taskB);
        var successCount = results.Count(r => r);

        Assert.Equal(1, successCount);

        using (var verify = _env.Factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var final = await db.SalaryPayments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);
            Assert.Equal(SalaryPaymentStatus.Paid, final.Status);
            Assert.NotNull(final.PaidAt);
        }
    }
}
