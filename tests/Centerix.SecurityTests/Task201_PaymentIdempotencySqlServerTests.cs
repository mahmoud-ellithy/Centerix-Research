namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 20.2 — SQL Server payment idempotency race-condition verification.
/// Verifies Case D: concurrent same-key + same-payload requests produce exactly one payment.
/// Uses SQL Server/Testcontainers with barrier synchronization.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task201_PaymentIdempotencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);

    public Task201_PaymentIdempotencySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(currentTenant, true);
    }

    private static async Task EnsureTenantExists(IServiceProvider scope, string tenantId)
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

    /// <summary>
    /// Case D — Concurrent same-key + same-payload requests.
    /// Two simultaneous CreatePayment requests with the same IdempotencyKey and same payload
    /// must result in exactly ONE payment being created. The losing request must resolve
    /// deterministically (returning the existing payment id) without creating a duplicate.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_ConcurrentSameKeySamePayload_ExactlyOneCreated()
    {
        var tenantId = $"tenant-payid-{Guid.NewGuid():N}"[..20];
        var idempotencyKey = $"race-key-{Guid.NewGuid():N}";
        var paymentNumber = $"PAY-RACE-{Guid.NewGuid():N}"[..16];

        // Setup: seed tenant
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        using var barrier = new Barrier(2);
        var tcs1 = new TaskCompletionSource<Result<Guid>>();
        var tcs2 = new TaskCompletionSource<Result<Guid>>();

        async Task<Result<Guid>> ExecutePayment()
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = new CreatePaymentHandler(db, Substitute.For<IAuditWriter>());

            barrier.SignalAndWait(BarrierTimeout);

            var cmd = new CreatePaymentCommand(paymentNumber, 1000m, "EGP", PaymentMethod.Cash, idempotencyKey);
            return await handler.Handle(cmd, CancellationToken.None);
        }

        var task1 = Task.Run(async () =>
        {
            try { tcs1.TrySetResult(await ExecutePayment()); }
            catch (Exception ex) { tcs1.TrySetException(ex); }
        });

        var task2 = Task.Run(async () =>
        {
            try { tcs2.TrySetResult(await ExecutePayment()); }
            catch (Exception ex) { tcs2.TrySetException(ex); }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(TestTimeout);

        try
        {
            await Task.WhenAll(task1, task2).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Concurrent operations did not complete within {TestTimeout.TotalSeconds}s");
        }

        var r1 = await tcs1.Task;
        var r2 = await tcs2.Task;

        // Both must succeed (one creates, one returns existing id)
        Assert.True(r1.IsSuccess, $"Request 1 failed: {string.Join(", ", r1.Errors?.Select(e => e.Code) ?? [])}");
        Assert.True(r2.IsSuccess, $"Request 2 failed: {string.Join(", ", r2.Errors?.Select(e => e.Code) ?? [])}");

        // Both must return the SAME payment id (idempotent behavior)
        Assert.Equal(r1.Value, r2.Value);

        // Exactly ONE payment with this key exists in the database
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var payments = await db.Payments
                .AsNoTracking()
                .Where(p => p.IdempotencyKey == idempotencyKey)
                .ToListAsync();

            Assert.Single(payments, "Exactly one payment must exist for this idempotency key");
            Assert.Equal(1000m, payments[0].Amount);
            Assert.Equal("EGP", payments[0].CurrencyCode);
        }
    }

    /// <summary>
    /// Case D variant — Same key + different payload (concurrent).
    /// Two simultaneous CreatePayment requests with the same IdempotencyKey but different
    /// amounts must result in one success (the winner) and one conflict error. No duplicate
    /// payment may be created regardless of timing.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_ConcurrentSameKeyDifferentPayload_ExactlyOneConflict()
    {
        var tenantId = $"tenant-payid2-{Guid.NewGuid():N}"[..20];
        var idempotencyKey = $"race-key-diff-{Guid.NewGuid():N}";
        var paymentNumber1 = $"PAY-RACE1-{Guid.NewGuid():N}"[..16];
        var paymentNumber2 = $"PAY-RACE2-{Guid.NewGuid():N}"[..16];

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        using var barrier = new Barrier(2);
        var tcs1 = new TaskCompletionSource<Result<Guid>>();
        var tcs2 = new TaskCompletionSource<Result<Guid>>();

        async Task<Result<Guid>> ExecutePayment1()
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = new CreatePaymentHandler(db, Substitute.For<IAuditWriter>());

            barrier.SignalAndWait(BarrierTimeout);

            var cmd = new CreatePaymentCommand(paymentNumber1, 1000m, "EGP", PaymentMethod.Cash, idempotencyKey);
            return await handler.Handle(cmd, CancellationToken.None);
        }

        async Task<Result<Guid>> ExecutePayment2()
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = new CreatePaymentHandler(db, Substitute.For<IAuditWriter>());

            barrier.SignalAndWait(BarrierTimeout);

            var cmd = new CreatePaymentCommand(paymentNumber2, 2000m, "EGP", PaymentMethod.Cash, idempotencyKey);
            return await handler.Handle(cmd, CancellationToken.None);
        }

        var task1 = Task.Run(async () =>
        {
            try { tcs1.TrySetResult(await ExecutePayment1()); }
            catch (Exception ex) { tcs1.TrySetException(ex); }
        });

        var task2 = Task.Run(async () =>
        {
            try { tcs2.TrySetResult(await ExecutePayment2()); }
            catch (Exception ex) { tcs2.TrySetException(ex); }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(TestTimeout);

        try
        {
            await Task.WhenAll(task1, task2).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Concurrent operations did not complete within {TestTimeout.TotalSeconds}s");
        }

        var r1 = await tcs1.Task;
        var r2 = await tcs2.Task;

        // Exactly one must succeed (the winner creates the payment)
        var successCount = (r1.IsSuccess ? 1 : 0) + (r2.IsSuccess ? 1 : 0);
        Assert.True(successCount == 1, $"Expected exactly 1 success, got {successCount}. R1: {(r1.IsSuccess ? "OK" : string.Join(", ", r1.Errors?.Select(e => e.Code) ?? []))}, R2: {(r2.IsSuccess ? "OK" : string.Join(", ", r2.Errors?.Select(e => e.Code) ?? []))}");

        // The failing one must be a conflict
        var failingResult = r1.IsSuccess ? r2 : r1;
        Assert.Contains(failingResult.Errors!, e => e.Code == "Payment.IdempotencyKeyConflict");

        // Exactly ONE payment with this key exists
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var payments = await db.Payments
                .AsNoTracking()
                .Where(p => p.IdempotencyKey == idempotencyKey)
                .ToListAsync();

            Assert.Single(payments, "Exactly one payment must exist for this idempotency key");
        }
    }

    /// <summary>
    /// Case C — Different keys + same payload.
    /// Two CreatePayment requests with different IdempotencyKeys but same payload
    /// must create TWO distinct payments (unless there's an explicit domain rule preventing it).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_DifferentKeysSamePayload_TwoPaymentsCreated()
    {
        var tenantId = $"tenant-payid3-{Guid.NewGuid():N}"[..20];
        var idempotencyKey1 = $"key-diff1-{Guid.NewGuid():N}";
        var idempotencyKey2 = $"key-diff2-{Guid.NewGuid():N}";

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid paymentId1;
        Guid paymentId2;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var handler = new CreatePaymentHandler(db, Substitute.For<IAuditWriter>());

            var cmd1 = new CreatePaymentCommand($"PAY-DIFF1-{Guid.NewGuid():N}"[..16], 500m, "USD", PaymentMethod.Card, idempotencyKey1);
            var result1 = await handler.Handle(cmd1, CancellationToken.None);
            Assert.True(result1.IsSuccess);
            paymentId1 = result1.Value;

            var cmd2 = new CreatePaymentCommand($"PAY-DIFF2-{Guid.NewGuid():N}"[..16], 500m, "USD", PaymentMethod.Card, idempotencyKey2);
            var result2 = await handler.Handle(cmd2, CancellationToken.None);
            Assert.True(result2.IsSuccess);
            paymentId2 = result2.Value;
        }

        Assert.NotEqual(paymentId1, paymentId2);

        // Verify two payments exist
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var payments = await db.Payments
                .AsNoTracking()
                .Where(p => p.IdempotencyKey == idempotencyKey1 || p.IdempotencyKey == idempotencyKey2)
                .ToListAsync();

            Assert.Equal(2, payments.Count);
        }
    }
}
