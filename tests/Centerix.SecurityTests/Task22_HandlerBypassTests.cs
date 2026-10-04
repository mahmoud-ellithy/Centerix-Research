namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Tenants.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// T22 — Handler bypass tests: security-critical handlers must deny unauthorized direct
/// invocation AND leave the system state untouched (no financial/lifecycle mutation).
/// Controller protection alone is never the only boundary for these operations.
/// </summary>
public class Task22_HandlerBypassTests
{
    [Fact]
    public async Task AllocatePayment_Unauthorized_Denied_AndNoMutation()
    {
        using var db = CreateDbContext("tenant-t22");
        var auditWriter = Substitute.For<IAuditWriter>();
        var guard = DeniedGuard();

        var handler = new AllocatePaymentHandler(
            db, auditWriter, NullSubscriptionReconciliationService.Instance, guard);

        var result = await handler.Handle(
            new AllocatePaymentCommand(Guid.NewGuid(), Guid.NewGuid(), 500m),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Platform.AdminRequired", result.Errors!.First().Code);
        Assert.False(db.PaymentAllocations.Any(), "No payment allocation may be persisted on denial.");
        Assert.False(db.CustomerLedgerEntries.Any(), "No ledger entry may be persisted on denial.");
        await auditWriter.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default!);
    }

    [Fact]
    public async Task ApproveRefund_Unauthorized_Denied_AndRefundUnchanged()
    {
        using var db = CreateDbContext("tenant-t22");
        var auditWriter = Substitute.For<IAuditWriter>();
        var guard = DeniedGuard();

        var refund = Refund.Create(
            Guid.NewGuid(),
            "RF-T22-1",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            250m,
            "EGP",
            "customer request",
            "seed",
            DateTime.UtcNow).Value;
        db.Refunds.Add(refund);
        db.StampAddedTenantIds("tenant-t22");
        await db.SaveChangesAsync();

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns("attacker");

        var handler = new ApproveRefundHandler(db, currentUser, guard, auditWriter);
        var result = await handler.Handle(new ApproveRefundCommand(refund.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Platform.AdminRequired", result.Errors!.First().Code);

        var persisted = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refund.Id);
        Assert.Equal(RefundStatus.Pending, persisted.Status);
        await auditWriter.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default!);
    }

    [Fact]
    public async Task SuspendTenant_Unauthorized_Denied_AndTenantUnchanged()
    {
        using var db = CreateDbContext(null);
        var auditWriter = Substitute.For<IAuditWriter>();
        var registrySync = Substitute.For<ITenantRegistrySync>();
        var guard = DeniedGuard();

        var tenant = Tenant.Create(
            Guid.NewGuid(), "t22-tenant", "t22-tenant", "T22 Tenant", "EG", "EGP",
            "Africa/Cairo", "Owner", "User", $"owner_{Guid.NewGuid():N}@t22.test",
            IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var handler = new SuspendTenantHandler(db, guard, registrySync, auditWriter);
        var result = await handler.Handle(
            new SuspendTenantCommand(tenant.Id, "abuse"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Platform.AdminRequired", result.Errors!.First().Code);

        var persisted = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal(LifecycleStatus.PendingApproval, persisted.LifecycleStatus);
        await registrySync.DidNotReceiveWithAnyArgs().SyncLifecycleAsync(default!, default);
        await auditWriter.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default!);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static IPlatformAdminGuard DeniedGuard()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdminAsync(Arg.Any<CancellationToken>()).Returns(
            Error.Forbidden("Platform.AdminRequired",
                "This operation is restricted to platform administrators."));
        return guard;
    }

    private static AppDbContext CreateDbContext(string? tenantId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"T22Bypass_{Guid.NewGuid():N}")
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? string.Empty);
        currentTenant.IsAuthorized.Returns(tenantId is not null);

        return new AppDbContext(options, mediator, currentTenant);
    }
}
