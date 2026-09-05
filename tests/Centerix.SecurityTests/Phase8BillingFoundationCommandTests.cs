namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// CODER TASK 2: Subscription & Billing Foundation command tests.
/// </summary>
public class Phase8BillingFoundationCommandTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? "tenant-test");
        currentTenant.IsAuthorized.Returns(tenantId != null);

        return new AppDbContext(options, mediator, currentTenant);
    }

    private static Plan CreateValidPlan()
    {
        var result = Plan.Create(
            id: 1,
            code: "TEST",
            displayName: "Test Plan",
            monthlyPrice: 1000m,
            maxStudents: 100,
            maxUsers: 10,
            maxBranches: 5,
            maxTeachers: 20,
            storageGB: 50,
            smsQuota: 1000,
            isActive: true,
            description: "Test plan",
            currencyCode: "EGP",
            durationMonths: 12,
            bonusMonths: 0);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Contract CreateValidContract(
        string tenantId = "tenant-1",
        int planId = 1,
        ContractStatus status = ContractStatus.Active)
    {
        var result = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: "CNT-" + Guid.NewGuid().ToString("N")[..8],
            planId: planId,
            effectiveAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 12000m);

        Assert.True(result.IsSuccess);
        var contract = result.Value;

        // Transition to desired status (Draft -> PendingApproval -> Active)
        if (status == ContractStatus.Active || status == ContractStatus.Suspended ||
            status == ContractStatus.Terminated || status == ContractStatus.Expired)
        {
            contract.SubmitForApproval();
            contract.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        return contract;
    }

    private static BillingCycle CreateValidBillingCycle(
        Guid subscriptionId,
        string tenantId = "tenant-1")
    {
        var result = BillingCycle.Create(
            Guid.NewGuid(),
            tenantId,
            subscriptionId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    // ------------------------------------------------------------------
    // CreateInvoiceFromBillingCycleCommand tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateInvoiceFromBillingCycle_DerivesAmountsFromSubscriptionSnapshot()
    {
        // Arrange
        var tenantId = "tenant-1";
        var dbContext = CreateDbContext(tenantId);
        dbContext.StampAddedTenantIds(tenantId);

        var plan = CreateValidPlan();
        dbContext.Plans.Add(plan);
        await dbContext.SaveChangesAsync();

        var subscription = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId,
            plan.Id,
            plan.MonthlyPrice,
            plan.CurrencyCode,
            plan.DurationMonths,
            plan.BonusMonths,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Value;

        subscription.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        dbContext.TenantPlans.Add(subscription);
        await dbContext.SaveChangesAsync();

        var billingCycle = CreateValidBillingCycle(subscription.Id, tenantId);
        dbContext.BillingCycles.Add(billingCycle);
        await dbContext.SaveChangesAsync();

        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(new DateTimeOffset(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));

        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId);

        var handler = new CreateInvoiceFromBillingCycleHandler(dbContext, currentTenant, timeProvider);
        var command = new CreateInvoiceFromBillingCycleCommand(billingCycle.Id);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var invoice = dbContext.Invoices.FirstOrDefault(i => i.BillingCycleId == billingCycle.Id);
        Assert.NotNull(invoice);
        // Subtotal = subscription.SnapshotPrice * 1 month (Jan)
        Assert.Equal(1000m, invoice.Subtotal);
        Assert.Equal(1000m, invoice.TotalAmount);
        Assert.Equal(subscription.Id, invoice.SubscriptionId);
        Assert.Null(invoice.ContractId); // Not linked to contract directly
    }

    [Fact]
    public async Task CreateInvoiceFromBillingCycle_BillingCycleNotFound_ReturnsNotFound()
    {
        // Arrange
        var tenantId = "tenant-1";
        var dbContext = CreateDbContext(tenantId);
        dbContext.StampAddedTenantIds(tenantId);

        var timeProvider = Substitute.For<TimeProvider>();

        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId);

        var handler = new CreateInvoiceFromBillingCycleHandler(dbContext, currentTenant, timeProvider);
        var command = new CreateInvoiceFromBillingCycleCommand(Guid.NewGuid());

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("BillingCycle.NotFound", result.Errors![0].Code);
    }

    // ------------------------------------------------------------------
    // CreateSubscriptionFromContractCommand tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateSubscriptionFromContract_ActiveContract_CreatesLinkedSubscription()
    {
        // Arrange
        var tenantId = "tenant-1";
        var dbContext = CreateDbContext(tenantId);
        dbContext.StampAddedTenantIds(tenantId);

        var plan = CreateValidPlan();
        dbContext.Plans.Add(plan);
        await dbContext.SaveChangesAsync();

        var contract = CreateValidContract(tenantId, plan.Id, ContractStatus.Active);
        dbContext.Contracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var platformAdminGuard = Substitute.For<IPlatformAdminGuard>();
        platformAdminGuard.EnsurePlatformAdmin().Returns(Result.Updated);

        var subscriptionFactory = Substitute.For<ISubscriptionFactory>();
        var mockSubscription = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId,
            plan.Id,
            plan.MonthlyPrice,
            plan.CurrencyCode,
            plan.DurationMonths,
            plan.BonusMonths,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Value;
        subscriptionFactory.CreateActivatedAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mockSubscription);

        var auditWriter = Substitute.For<IAuditWriter>();

        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var handler = new CreateSubscriptionFromContractHandler(
            dbContext,
            platformAdminGuard,
            subscriptionFactory,
            auditWriter,
            timeProvider);

        var command = new CreateSubscriptionFromContractCommand(contract.Id);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(Result.Created, result.Value);
    }

    [Fact]
    public async Task CreateSubscriptionFromContract_NonActiveContract_ReturnsConflict()
    {
        // Arrange
        var tenantId = "tenant-1";
        var dbContext = CreateDbContext(tenantId);
        dbContext.StampAddedTenantIds(tenantId);

        var plan = CreateValidPlan();
        dbContext.Plans.Add(plan);
        await dbContext.SaveChangesAsync();

        // Create a Draft contract (not Active)
        var contract = CreateValidContract(tenantId, plan.Id, ContractStatus.Draft);
        dbContext.Contracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var platformAdminGuard = Substitute.For<IPlatformAdminGuard>();
        platformAdminGuard.EnsurePlatformAdmin().Returns(Result.Updated);

        var subscriptionFactory = Substitute.For<ISubscriptionFactory>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var timeProvider = Substitute.For<TimeProvider>();

        var handler = new CreateSubscriptionFromContractHandler(
            dbContext,
            platformAdminGuard,
            subscriptionFactory,
            auditWriter,
            timeProvider);

        var command = new CreateSubscriptionFromContractCommand(contract.Id);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.NotActive", result.Errors![0].Code);
    }

    [Fact]
    public async Task CreateSubscriptionFromContract_ContractNotFound_ReturnsNotFound()
    {
        // Arrange
        var tenantId = "tenant-1";
        var dbContext = CreateDbContext(tenantId);
        dbContext.StampAddedTenantIds(tenantId);

        var platformAdminGuard = Substitute.For<IPlatformAdminGuard>();
        platformAdminGuard.EnsurePlatformAdmin().Returns(Result.Updated);

        var subscriptionFactory = Substitute.For<ISubscriptionFactory>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var timeProvider = Substitute.For<TimeProvider>();

        var handler = new CreateSubscriptionFromContractHandler(
            dbContext,
            platformAdminGuard,
            subscriptionFactory,
            auditWriter,
            timeProvider);

        var command = new CreateSubscriptionFromContractCommand(Guid.NewGuid());

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.NotFound", result.Errors![0].Code);
    }

    [Fact]
    public async Task CreateSubscriptionFromContract_NonPlatformAdmin_ReturnsForbidden()
    {
        // Arrange
        var tenantId = "tenant-1";
        var dbContext = CreateDbContext(tenantId);
        dbContext.StampAddedTenantIds(tenantId);

        var platformAdminGuard = Substitute.For<IPlatformAdminGuard>();
        platformAdminGuard.EnsurePlatformAdmin().Returns(Error.Failure("Auth.Forbidden", "Not a platform admin"));

        var subscriptionFactory = Substitute.For<ISubscriptionFactory>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var timeProvider = Substitute.For<TimeProvider>();

        var handler = new CreateSubscriptionFromContractHandler(
            dbContext,
            platformAdminGuard,
            subscriptionFactory,
            auditWriter,
            timeProvider);

        var command = new CreateSubscriptionFromContractCommand(Guid.NewGuid());

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Auth.Forbidden", result.Errors![0].Code);
    }
}
