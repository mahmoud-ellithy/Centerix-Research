namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Installments;
using Centerix.Application.Platform.Billing.Installments.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task 8.1 — Handler-level command tests for installment hardening.
/// Exercises AddInstallment, UpdateInstallment, and CancelInstallment
/// through the full MediatR pipeline with EF InMemory.
/// Uses a FakeCurrentTenant to provide tenant context for handlers.
/// </summary>
public class Phase8InstallmentHardeningCommandTests : IClassFixture<HardeningTestFactory>
{
    private readonly HardeningTestFactory _factory;
    private readonly IServiceScope _scope;
    private readonly IAppDbContext _dbContext;

    private const string TestTenantId = "test-tenant-hardening";

    public Phase8InstallmentHardeningCommandTests(HardeningTestFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
    }

    private IServiceScope scope => _scope;

    private IMediator Mediator => scope.ServiceProvider.GetRequiredService<IMediator>();

    // ═══════════════════════════════════════════════════════════════════
    // AddInstallment — Obligation Invariant via Handler
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallment_ExceedsContractObligation_Rejected()
    {
        var contract = await SeedContractAsync(contractedAmount: 10000m);
        var subscriptionId = await SeedSubscriptionAsync(contract.Id);

        // First installment: 5000 within contract period
        var cmd1 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 1, DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            5000m);
        var r1 = await Mediator.Send(cmd1);
        Assert.True(r1.IsSuccess);

        // Second installment: 5000 within contract period → total = 10000 = contract amount
        var cmd2 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 2, DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 5, 1), new DateTime(2026, 8, 31),
            5000m);
        var r2 = await Mediator.Send(cmd2);
        Assert.True(r2.IsSuccess);

        // Third installment: 1000 within contract period → total would be 11000 > 10000 → REJECT
        var cmd3 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 3, DateTime.UtcNow.AddDays(90),
            new DateTime(2026, 9, 1), new DateTime(2026, 12, 31),
            1000m);
        var r3 = await Mediator.Send(cmd3);
        Assert.False(r3.IsSuccess);
        Assert.Contains(r3.Errors!, e => e.Code == "Installment.Schedule_ExceedsContractObligation");
    }

    [Fact]
    public async Task AddInstallment_ValidIncrementalInstallment_Succeeds()
    {
        var contract = await SeedContractAsync(contractedAmount: 12000m);
        var subscriptionId = await SeedSubscriptionAsync(contract.Id);

        var cmd = new AddInstallmentCommand(
            contract.Id, subscriptionId, 1, DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 6, 30),
            4000m);
        var result = await Mediator.Send(cmd);
        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value);
    }

    [Fact]
    public async Task AddInstallment_OverlappingPeriod_Rejected()
    {
        var contract = await SeedContractAsync();
        var subscriptionId = await SeedSubscriptionAsync(contract.Id);

        var cmd1 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 1, DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);
        var r1 = await Mediator.Send(cmd1);
        Assert.True(r1.IsSuccess);

        var cmd2 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 2, DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 3, 1), new DateTime(2026, 6, 30),
            3000m);
        var r2 = await Mediator.Send(cmd2);
        Assert.False(r2.IsSuccess);
        Assert.Contains(r2.Errors!, e => e.Code == "Installment.OverlappingPeriod");
    }

    [Fact]
    public async Task AddInstallment_DuplicatePeriod_Rejected()
    {
        var contract = await SeedContractAsync();
        var subscriptionId = await SeedSubscriptionAsync(contract.Id);

        var cmd1 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 1, DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 6, 30),
            6000m);
        var r1 = await Mediator.Send(cmd1);
        Assert.True(r1.IsSuccess);

        var cmd2 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 2, DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 1, 1), new DateTime(2026, 6, 30),
            4000m);
        var r2 = await Mediator.Send(cmd2);
        Assert.False(r2.IsSuccess);
        Assert.Contains(r2.Errors!, e => e.Code == "Installment.OverlappingPeriod");
    }

    [Fact]
    public async Task AddInstallment_PeriodBeyondContract_Rejected()
    {
        var contract = await SeedContractAsync();
        var subscriptionId = await SeedSubscriptionAsync(contract.Id);

        var cmd = new AddInstallmentCommand(
            contract.Id, subscriptionId, 1, DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2027, 6, 30),
            4000m);
        var result = await Mediator.Send(cmd);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CoveredPeriod_ExceedsContract");
    }

    [Fact]
    public async Task AddInstallment_DuplicateSequenceNumber_Rejected()
    {
        var contract = await SeedContractAsync();
        var subscriptionId = await SeedSubscriptionAsync(contract.Id);

        var cmd1 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 1, DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 6, 30),
            4000m);
        await Mediator.Send(cmd1);

        var cmd2 = new AddInstallmentCommand(
            contract.Id, subscriptionId, 1, DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 7, 1), new DateTime(2026, 12, 31),
            6000m);
        var result = await Mediator.Send(cmd2);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.DuplicateSequenceNumber");
    }

    // ═══════════════════════════════════════════════════════════════════
    // UpdateInstallment — Status Guard via Handler
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateInstallment_PendingInstallment_Succeeds()
    {
        var contract = await SeedContractAsync();
        var installmentId = await SeedInstallmentAsync(contract.Id, Guid.NewGuid());

        var cmd = new UpdateInstallmentCommand(
            installmentId,
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            5000m);
        var result = await Mediator.Send(cmd);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task UpdateInstallment_NonPending_Rejected()
    {
        var contract = await SeedContractAsync();
        var installmentId = await SeedInstallmentAsync(contract.Id, Guid.NewGuid());

        var installment = await _dbContext.Installments
            .FirstAsync(i => i.Id == installmentId);
        installment.ApplyAllocation(
            PaymentAllocation.Create(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                2000m, DateTime.UtcNow).Value!,
            DateTime.UtcNow);
        _dbContext.StampAddedTenantIds(TestTenantId);
        await _dbContext.SaveChangesAsync();

        var cmd = new UpdateInstallmentCommand(
            installmentId,
            DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 2, 1), new DateTime(2026, 5, 31),
            5000m);
        var result = await Mediator.Send(cmd);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task UpdateInstallment_ExceedsContractObligation_Rejected()
    {
        var contract = await SeedContractAsync(contractedAmount: 10000m);

        var i1 = await SeedInstallmentAsync(contract.Id, Guid.NewGuid(), amount: 5000m,
            start: new DateTime(2026, 1, 1), end: new DateTime(2026, 6, 30), seq: 1);
        var i2 = await SeedInstallmentAsync(contract.Id, Guid.NewGuid(), amount: 5000m,
            start: new DateTime(2026, 7, 1), end: new DateTime(2026, 12, 31), seq: 2);

        var cmd = new UpdateInstallmentCommand(
            i1, DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 1, 1), new DateTime(2026, 6, 30),
            6000m);
        var result = await Mediator.Send(cmd);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.Schedule_ExceedsContractObligation");
    }

    [Fact]
    public async Task UpdateInstallment_OverlappingOtherPeriod_Rejected()
    {
        var contract = await SeedContractAsync();

        var i1 = await SeedInstallmentAsync(contract.Id, Guid.NewGuid(),
            start: new DateTime(2026, 1, 1), end: new DateTime(2026, 4, 30), seq: 1);
        var i2 = await SeedInstallmentAsync(contract.Id, Guid.NewGuid(),
            start: new DateTime(2026, 5, 1), end: new DateTime(2026, 8, 31), seq: 2);

        var cmd = new UpdateInstallmentCommand(
            i2, DateTime.UtcNow.AddDays(60),
            new DateTime(2026, 3, 1), new DateTime(2026, 6, 30),
            4000m);
        var result = await Mediator.Send(cmd);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.OverlappingPeriod");
    }

    // ═══════════════════════════════════════════════════════════════════
    // CancelInstallment via Handler
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelInstallment_NoAllocations_Succeeds()
    {
        var contract = await SeedContractAsync();
        var installmentId = await SeedInstallmentAsync(contract.Id, Guid.NewGuid());

        var result = await Mediator.Send(new CancelInstallmentCommand(installmentId));
        Assert.True(result.IsSuccess);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Settlement & Status via Handler
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Installment_SettledAmountDerived_FromAllocations()
    {
        var contract = await SeedContractAsync();
        var installmentId = await SeedInstallmentAsync(contract.Id, Guid.NewGuid(), amount: 4000m);

        var installment = await _dbContext.Installments
            .Include(i => i.PaymentAllocations)
            .FirstAsync(i => i.Id == installmentId);

        Assert.Equal(0m, installment.SettledAmount);
        Assert.Equal(4000m, installment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Pending, installment.Status);

        installment.ApplyAllocation(
            PaymentAllocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                2000m, DateTime.UtcNow).Value!,
            DateTime.UtcNow);
        _dbContext.StampAddedTenantIds(TestTenantId);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(2000m, installment.SettledAmount);
        Assert.Equal(2000m, installment.RemainingAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Error Catalog
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void NewErrorCodes_AreWellFormed()
    {
        var errors = new[]
        {
            InstallmentErrors.CannotUpdateNonPending,
            InstallmentErrors.ScheduleExceedsContractObligation(15000m, 10000m),
            InstallmentErrors.AmountWouldCorruptSettlement,
        };

        foreach (var error in errors)
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Code));
            Assert.StartsWith("Installment.", error.Code);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Migration Verification (schema-level)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Migration_InstallmentTableExists_InModel()
    {
        var entityType = _dbContext.Installments.EntityType;
        Assert.NotNull(entityType);
        Assert.Equal("Installments", entityType.GetTableName());
    }

    [Fact]
    public void Migration_PaymentAllocationHasInstallmentId_InModel()
    {
        var entityType = _dbContext.PaymentAllocations.EntityType;
        Assert.NotNull(entityType);
        var property = entityType.FindProperty("InstallmentId");
        Assert.NotNull(property);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private async Task<Contract> SeedContractAsync(decimal contractedAmount = 12000m)
    {
        var contract = Contract.Create(
            Guid.NewGuid(),
            TestTenantId,
            $"CON-{Guid.NewGuid().ToString()[..8]}",
            1,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            12,
            1000m,
            1000m,
            "EGP",
            contractedAmount, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion);

        contract.Value!.SubmitForApproval();
        contract.Value!.Activate(DateTime.UtcNow);

        _dbContext.Contracts.Add(contract.Value!);
        await _dbContext.SaveChangesAsync();

        return contract.Value!;
    }

    private async Task<Guid> SeedInstallmentAsync(
        Guid contractId,
        Guid subscriptionId,
        decimal amount = 4000m,
        DateTime? start = null,
        DateTime? end = null,
        int seq = 1)
    {
        var installment = Installment.Create(
            Guid.NewGuid(),
            contractId,
            seq,
            DateTime.UtcNow.AddDays(30),
            start ?? new DateTime(2026, 1, 1),
            end ?? new DateTime(2026, 4, 30),
            amount,
            "EGP",
            subscriptionId: subscriptionId).Value!;

        _dbContext.Installments.Add(installment);
        _dbContext.StampAddedTenantIds(TestTenantId);
        await _dbContext.SaveChangesAsync();

        return installment.Id;
    }

    private async Task<Guid> SeedSubscriptionAsync(Guid contractId)
    {
        var subscription = Domain.Platform.Subscriptions.TenantPlan.Create(
            Guid.NewGuid(),
            TestTenantId,
            1,
            1000m,
            "EGP",
            12,
            0,
            new DateTime(2026, 1, 1),
            status: Domain.Platform.Subscriptions.Enums.SubscriptionStatus.Active).Value!;

        subscription.LinkToContract(contractId);

        _dbContext.TenantPlans.Add(subscription);
        _dbContext.StampAddedTenantIds(TestTenantId);
        await _dbContext.SaveChangesAsync();

        return subscription.Id;
    }
}

/// <summary>
/// Test-specific WebApplicationFactory that registers a FakeCurrentTenant
/// providing a fixed tenant ID for handler-level tests.
/// </summary>
public class HardeningTestFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Replace ICurrentTenant with a FakeCurrentTenant that provides
            // a fixed tenant ID, allowing handlers to pass tenant validation.
            var existingDescriptor = services.FirstOrDefault(
                d => d.ServiceType == typeof(ICurrentTenant));
            if (existingDescriptor != null)
                services.Remove(existingDescriptor);

            services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant("test-tenant-hardening"));
        });
    }
}

/// <summary>
/// Test double for ICurrentTenant that provides a fixed tenant ID.
/// </summary>
internal class FakeCurrentTenant : ICurrentTenant
{
    private readonly string _tenantId;

    public FakeCurrentTenant(string tenantId)
    {
        _tenantId = tenantId;
    }

    public string TenantId => _tenantId;
    public string ResolvedTenantId => _tenantId;
    public bool IsAuthorized => true;
    public bool IsResolved => true;
    public bool IsActive => true;
    public DateTime? ValidUpTo => null;
    public void AuthorizeTenant() { }
}
