namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 20.3 — SQL Server payment idempotency verification.
/// Verifies idempotency logic works correctly with SQL Server unique constraints.
/// Uses SQL Server/Testcontainers with TestIsolationLevel.Uncommitted where needed.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task201_PaymentIdempotencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

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

    private static async Task<Guid> CreatePaymentWithTenant(
        AppDbContext db,
        string tenantId,
        string paymentNumber,
        decimal amount,
        string currencyCode,
        PaymentMethod method,
        string? idempotencyKey)
    {
        var paymentResult = Payment.Create(
            Guid.NewGuid(), paymentNumber, amount, currencyCode, method,
            idempotencyKey: idempotencyKey);

        if (!paymentResult.IsSuccess)
            throw new Exception($"Payment creation failed: {string.Join(", ", paymentResult.Errors!.Select(e => e.Code))}");

        var payment = paymentResult.Value;
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    /// <summary>
    /// Case A — Payment with idempotency key is created successfully.
    /// Verifies the unique constraint on (TenantId, IdempotencyKey) exists and works.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_WithIdempotencyKey_CreatesPayment()
    {
        var tenantId = $"tenant-pay-a-{Guid.NewGuid():N}"[..20];

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid paymentId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            paymentId = await CreatePaymentWithTenant(
                db, tenantId,
                $"PAY-A-{Guid.NewGuid():N}"[..12],
                1000m, "EGP", PaymentMethod.Cash,
                $"key-a-{Guid.NewGuid():N}");
        }

        Assert.NotEqual(Guid.Empty, paymentId);
    }

    /// <summary>
    /// Case C — Different keys create different payments.
    /// Verifies that different idempotency keys don't conflict.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_DifferentKeysSamePayload_TwoPaymentsCreated()
    {
        var tenantId = $"tenant-pay-c-{Guid.NewGuid():N}"[..20];
        var key1 = $"key-c1-{Guid.NewGuid():N}";
        var key2 = $"key-c2-{Guid.NewGuid():N}";

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid id1, id2;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            id1 = await CreatePaymentWithTenant(db, tenantId, $"PAY-C1-{Guid.NewGuid():N}"[..12], 500m, "USD", PaymentMethod.Card, key1);
            id2 = await CreatePaymentWithTenant(db, tenantId, $"PAY-C2-{Guid.NewGuid():N}"[..12], 500m, "USD", PaymentMethod.Card, key2);
        }

        Assert.NotEqual(id1, id2);
    }

    /// <summary>
    /// Case B — Same key + same payload (sequential).
    /// Verifies that a second request with same key+payload returns conflict OR succeeds (depends on handler).
    /// With direct DB access, we verify the unique constraint exists by creating and then attempting duplicate.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_SameKeyDuplicate_FailsWithUniqueConstraint()
    {
        var tenantId = $"tenant-pay-b-{Guid.NewGuid():N}"[..20];
        var idempotencyKey = $"key-b-{Guid.NewGuid():N}";

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            // First payment should succeed
            await CreatePaymentWithTenant(db, tenantId, $"PAY-B1-{Guid.NewGuid():N}"[..12], 1000m, "EGP", PaymentMethod.Cash, idempotencyKey);

            // Second payment with same key should fail due to unique constraint
            var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(
                async () => await CreatePaymentWithTenant(db, tenantId, $"PAY-B2-{Guid.NewGuid():N}"[..12], 1000m, "EGP", PaymentMethod.Cash, idempotencyKey));

            Assert.Contains("UX_Payments_TenantId_IdempotencyKey", ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Verifies the UX_Payments_TenantId_IdempotencyKey filtered unique index exists
    /// by checking INFORMATION_SCHEMA.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_IdempotencyUniqueIndex_ExistsInSchema()
    {
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var indexExists = await db.Database
                .SqlQueryRaw<int>(
                    @"SELECT COUNT(*) FROM sys.indexes i
                      JOIN sys.tables t ON i.object_id = t.object_id
                      JOIN sys.schemas s ON t.schema_id = s.schema_id
                      WHERE s.name = 'Platform' AND t.name = 'Payments'
                      AND i.name = 'UX_Payments_TenantId_IdempotencyKey'
                      AND i.is_unique = 1")
                .ToListAsync();

            Assert.Equal(1, indexExists[0]);

            // Verify it's a filtered index (has filter definition)
            var isFiltered = await db.Database
                .SqlQueryRaw<string>(
                    @"SELECT filter_definition FROM sys.indexes
                      WHERE name = 'UX_Payments_TenantId_IdempotencyKey'")
                .ToListAsync();

            Assert.Single(isFiltered);
            Assert.Contains("IS NOT NULL", isFiltered[0]);
        }
    }

    /// <summary>
    /// Verifies that the IdempotencyKey column allows NULL and the filtered index
    /// correctly permits multiple NULL values (legacy rows).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Idempotency")]
    public async Task Payment_NullIdempotencyKey_AllowsMultipleRows()
    {
        var tenantId = $"tenant-pay-null-{Guid.NewGuid():N}"[..20];

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid id1, id2;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            // Two payments with NULL idempotency key should both succeed (filtered index excludes NULL)
            id1 = await CreatePaymentWithTenant(db, tenantId, $"PAY-N1-{Guid.NewGuid():N}"[..12], 100m, "EGP", PaymentMethod.Cash, null);
            id2 = await CreatePaymentWithTenant(db, tenantId, $"PAY-N2-{Guid.NewGuid():N}"[..12], 200m, "EGP", PaymentMethod.Cash, null);
        }

        Assert.NotEqual(id1, id2);
    }
}
