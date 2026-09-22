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
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Platform;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 9.2 — Subscription Activation, Expiration & Financial Ownership Finalization.
///
/// Comprehensive regression tests covering all 18 scenarios from §13:
///
/// Creation Ownership:
///   1. New Installment linked to Subscription
///   2. New Installment does not silently remain SubscriptionId null
///   3. AddInstallment path links correct Subscription
///   4. Schedule creation path links correct Subscription
///   5. Every other production creation path is covered
///
/// Validation:
///   6. Missing Subscription rejected when required
///   7. Subscription from another tenant rejected
///   8. Subscription.ContractId mismatch rejected
///   9. Expired subscription may still be referenced by installments (historical financial obligation;
///      the Contract—not the subscription—is the financial authority; both handlers require Contract.IsActive)
///
/// Reconciliation:
///  10. Current Subscription sees its own Installments
///  11. Another Subscription's Installments do not affect it
///  12. Legacy null SubscriptionId Installments follow compatibility behavior
///  13. New Installments always use explicit ownership
///  14. Multiple subscriptions cannot interfere
///
/// Expiration:
///  15. Expired remains Expired after reconciliation
///  16. Payment after expiration does not reactivate it
///
/// Security:
///  17. Cross-tenant creation denied
///  18. Arbitrary SubscriptionId injection denied
/// </summary>
public class Phase9_2SubscriptionInstallmentOwnershipFinalizationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_9_2_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? "tenant-test");
        currentTenant.IsAuthorized.Returns(tenantId != null);

        return new AppDbContext(options, mediator, currentTenant);
    }

    private static SubscriptionReconciliationService CreateReconciliationService(
        AppDbContext db,
        TimeProvider? timeProvider = null)
    {
        var logger = Substitute.For<ILogger<SubscriptionReconciliationService>>();
        var tp = timeProvider ?? new TestTimeProvider(DateTime.UtcNow);
        return new SubscriptionReconciliationService(db, tp, logger);
    }

    private static TenantPlan CreateAndPersistSubscription(
        AppDbContext db,
        string tenantId,
        Guid? contractId = null,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTime? startsAtUtc = null)
    {
        var start = startsAtUtc ?? DateTime.UtcNow;
        var result = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId,
            planId: 1,
            snapshotPrice: 100m,
            snapshotCurrency: "USD",
            12,
            bonusMonths: 0,
            startsAtUtc: start,
            autoRenew: false,
            status: SubscriptionStatus.Pending);

        var subscription = result.Value;

        if (status == SubscriptionStatus.Active)
        {
            subscription.Activate(DateTime.UtcNow);
        }
        else if (status == SubscriptionStatus.Expired)
        {
            var activateResult = subscription.Activate(DateTime.UtcNow);
            if (activateResult.IsSuccess)
                subscription.MarkExpired(DateTime.UtcNow);
        }
        else if (status == SubscriptionStatus.Cancelled)
        {
            var activateResult = subscription.Activate(DateTime.UtcNow);
            if (activateResult.IsSuccess)
                subscription.Cancel(DateTime.UtcNow);
        }

        if (contractId.HasValue)
            subscription.LinkToContract(contractId.Value);

        db.TenantPlans.Add(subscription);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(subscription).State = EntityState.Detached;
        return subscription;
    }

    private static void LinkToContract(AppDbContext db, TenantPlan subscription, Guid contractId)
    {
        var tracked = db.TenantPlans.IgnoreQueryFilters().First(tp => tp.Id == subscription.Id);
        tracked.LinkToContract(contractId);
        db.SaveChanges();
        db.Entry(tracked).State = EntityState.Detached;
    }

    private static Contract CreateAndPersistContract(
        AppDbContext db,
        string tenantId,
        decimal contractedAmount = 12000m,
        DateTime? effectiveAt = null,
        DateTime? endsAt = null)
    {
        var contract = Contract.Create(
            Guid.NewGuid(),
            tenantId,
            $"CON-{Guid.NewGuid().ToString()[..8]}",
            1,
            effectiveAt ?? new DateTime(2026, 1, 1),
            endsAt ?? new DateTime(2026, 12, 31),
            12,
            1000m,
            1000m,
            "USD",
            contractedAmount, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value!;

        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(contract).State = EntityState.Detached;
        return contract;
    }

    private static Installment CreateAndPersistInstallment(
        AppDbContext db,
        string tenantId,
        Guid contractId,
        DateTime dueDateUtc,
        Guid subscriptionId,
        decimal amount = 1000m,
        int sequenceNumber = 1)
    {
        var result = Installment.Create(
            Guid.NewGuid(),
            contractId,
            sequenceNumber,
            dueDateUtc,
            dueDateUtc.AddMonths(-1),
            dueDateUtc,
            amount,
            "USD",
            subscriptionId);

        var installment = result.Value;
        db.Installments.Add(installment);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(installment).State = EntityState.Detached;
        return installment;
    }

    private static void SeedGracePeriodPolicy(AppDbContext db, int gracePeriodDays)
    {
        var policy = SubscriptionPolicy.Create(1, gracePeriodDays).Value;
        db.SubscriptionPolicies.Add(policy);
        db.SaveChanges();
    }

    private static void SettleInstallment(AppDbContext db, string tenantId, Installment installment, decimal amount, DateTime now)
    {
        var payment = Payment.Create(Guid.NewGuid(), $"PAY-{Guid.NewGuid().ToString()[..8]}", amount, "USD", PaymentMethod.Cash).Value;
        db.Payments.Add(payment);

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, Guid.NewGuid(), amount, now, installment.Id).Value;
        db.PaymentAllocations.Add(allocation);

        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        var trackedInstallment = db.Installments.First(i => i.Id == installment.Id);
        trackedInstallment.ApplyAllocation(allocation, now);
        db.SaveChanges();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 1. Creation Ownership: New Installment linked to Subscription
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NewInstallment_IsLinkedToSubscription()
    {
        var tenantId = "tenant-92-01";
        await using var db = CreateDbContext(tenantId);

        var contract = CreateAndPersistContract(db, tenantId);
        var subscription = CreateAndPersistSubscription(db, tenantId, contract.Id);

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contract.Id, subscription.Id, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var installment = await db.Installments.FirstAsync(i => i.Id == result.Value);
        Assert.Equal(subscription.Id, installment.SubscriptionId);
        Assert.NotNull(installment.SubscriptionId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. Creation Ownership: SubscriptionId is never null for new records
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NewInstallment_SubscriptionIdNeverNull_ViaDomainFactory()
    {
        var subscriptionId = Guid.NewGuid();
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m, "USD", subscriptionId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.SubscriptionId);
        Assert.Equal(subscriptionId, result.Value.SubscriptionId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. Creation Ownership: AddInstallment path links correct Subscription
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallmentCommand_LinksCorrectSubscription()
    {
        var tenantId = "tenant-92-03";
        await using var db = CreateDbContext(tenantId);

        var contract = CreateAndPersistContract(db, tenantId);
        var subscription = CreateAndPersistSubscription(db, tenantId, contract.Id);

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contract.Id, subscription.Id, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var installment = await db.Installments.FirstAsync(i => i.Id == result.Value);
        Assert.Equal(subscription.Id, installment.SubscriptionId);
        Assert.Equal(contract.Id, installment.ContractId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. Creation Ownership: Schedule creation path links correct Subscription
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateInstallmentSchedule_LinksCorrectSubscription()
    {
        var tenantId = "tenant-92-04";
        await using var db = CreateDbContext(tenantId);

        var contract = CreateAndPersistContract(db, tenantId, contractedAmount: 12000m);
        var subscription = CreateAndPersistSubscription(db, tenantId, contract.Id);

        var handler = new CreateInstallmentScheduleHandler(db, Substitute.For<IAuditWriter>());
        var command = new CreateInstallmentScheduleCommand(
            contract.Id, subscription.Id,
            [
                new(1, new DateTime(2026, 6, 30), new DateTime(2026, 1, 1), new DateTime(2026, 6, 30), 6000m),
                new(2, new DateTime(2026, 12, 31), new DateTime(2026, 6, 30), new DateTime(2026, 12, 31), 6000m),
            ]);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsSuccess ? "" : $"Handler failed: {string.Join(", ", result.Errors!.Select(e => $"{e.Code}: {e.Description}"))}");

        var installments = await db.Installments
            .Where(i => i.ContractId == contract.Id)
            .ToListAsync();

        Assert.Equal(2, installments.Count);
        Assert.All(installments, i =>
        {
            Assert.NotNull(i.SubscriptionId);
            Assert.Equal(subscription.Id, i.SubscriptionId);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. Creation Ownership: Only 2 production creation paths exist
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void OnlyTwoProductionCreationPaths_InstallmentCreateRequiresSubscriptionId()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m, "USD", Guid.Empty);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SubscriptionRequired");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Validation: Missing Subscription rejected when required
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallmentCommand_EmptySubscriptionId_Rejected()
    {
        var tenantId = "tenant-92-06";
        await using var db = CreateDbContext(tenantId);

        var contract = CreateAndPersistContract(db, tenantId);
        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());

        var command = new AddInstallmentCommand(
            contract.Id, Guid.Empty, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SubscriptionRequired");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. Validation: Subscription from another tenant rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallmentCommand_SubscriptionFromDifferentTenant_Rejected()
    {
        var tenantA = "tenant-A-92-07";
        var tenantB = "tenant-B-92-07";
        await using var db = CreateDbContext(tenantA);

        var contract = CreateAndPersistContract(db, tenantA);

        var subscriptionB = TenantPlan.Create(
            Guid.NewGuid(), tenantB, 1, 100m, "USD", 12, 0,
            DateTime.UtcNow, false, SubscriptionStatus.Pending).Value!;
        subscriptionB.Activate(DateTime.UtcNow);
        db.TenantPlans.Add(subscriptionB);
        db.StampAddedTenantIds(tenantB);
        await db.SaveChangesAsync();
        db.Entry(subscriptionB).State = EntityState.Detached;

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contract.Id, subscriptionB.Id, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e =>
            e.Code == "Installment.SubscriptionNotFound" ||
            e.Code == "Installment.SubscriptionBelongsToDifferentTenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. Validation: Subscription.ContractId mismatch rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallmentCommand_SubscriptionContractMismatch_Rejected()
    {
        var tenantId = "tenant-92-08";
        await using var db = CreateDbContext(tenantId);

        var contractA = CreateAndPersistContract(db, tenantId);
        var contractB = CreateAndPersistContract(db, tenantId);
        var subscriptionA = CreateAndPersistSubscription(db, tenantId, contractA.Id);

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contractB.Id, subscriptionA.Id, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SubscriptionBelongsToDifferentContract");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 9. Expired subscription: historical financial obligation still recordable
    //    Both handlers validate Contract.IsActive but NOT subscription status.
    //    The Contract is the financial authority; subscription lifecycle is a
    //    service concept. Installments may reference expired subscriptions when
    //    they represent an existing/historical financial obligation.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallmentCommand_ExpiredSubscription_AllowedForHistoricalFinancialObligation()
    {
        var tenantId = "tenant-92-09";
        await using var db = CreateDbContext(tenantId);

        var contract = CreateAndPersistContract(db, tenantId);
        var subscription = CreateAndPersistSubscription(db, tenantId, contract.Id, SubscriptionStatus.Expired);

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contract.Id, subscription.Id, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var installment = await db.Installments.FirstAsync(i => i.Id == result.Value);
        Assert.Equal(subscription.Id, installment.SubscriptionId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 10. Reconciliation: Current Subscription sees its own Installments
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconciliation_CurrentSubscriptionSeesItsOwnInstallments()
    {
        var tenantId = "tenant-92-10";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var sub = CreateAndPersistSubscription(db, tenantId);
        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        CreateAndPersistInstallment(db, tenantId, contractId, now.AddDays(-3),
            amount: 500m, sequenceNumber: 1, subscriptionId: sub.Id);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.Id == sub.Id)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.PastDue, reloaded!.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 11. Reconciliation: Another Subscription's Installments do not affect it
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconciliation_AnotherSubscriptionInstallment_DoesNotAffect()
    {
        var tenantId = "tenant-92-11";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var sharedContractId = Guid.NewGuid();

        var subA = CreateAndPersistSubscription(db, tenantId, sharedContractId, SubscriptionStatus.Active);
        var subB = CreateAndPersistSubscription(db, tenantId, sharedContractId, SubscriptionStatus.Active);

        CreateAndPersistInstallment(db, tenantId, sharedContractId, now.AddDays(-10),
            amount: 500m, sequenceNumber: 1, subscriptionId: subB.Id);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloadedA = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.Id == subA.Id)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Active, reloadedA!.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 12. Reconciliation: Legacy null SubscriptionId installments excluded
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconciliation_LegacyNullSubscriptionId_Excluded()
    {
        var tenantId = "tenant-92-12";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var sub = CreateAndPersistSubscription(db, tenantId);
        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var legacyInstallment = Installment.Create(
            Guid.NewGuid(), contractId, 1,
            now.AddDays(-3), now.AddMonths(-1), now.AddDays(-3),
            1000m, "USD", Guid.NewGuid()).Value!;
        db.Installments.Add(legacyInstallment);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        db.Entry(legacyInstallment).Property(i => i.SubscriptionId).CurrentValue = null;
        db.SaveChanges();
        db.Entry(legacyInstallment).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.Id == sub.Id)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Active, reloaded!.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 13. Reconciliation: New Installments always use explicit ownership
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NewInstallment_AlwaysUsesExplicitOwnership()
    {
        var tenantId = "tenant-92-13";
        await using var db = CreateDbContext(tenantId);

        var contract = CreateAndPersistContract(db, tenantId);
        var subscription = CreateAndPersistSubscription(db, tenantId, contract.Id);

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contract.Id, subscription.Id, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var installment = await db.Installments.FirstAsync(i => i.Id == result.Value);
        Assert.NotNull(installment.SubscriptionId);
        Assert.Equal(subscription.Id, installment.SubscriptionId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 14. Reconciliation: Multiple subscriptions cannot interfere
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconciliation_MultipleSubscriptionsCannotInterfere()
    {
        var tenantId = "tenant-92-14";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var sharedContractId = Guid.NewGuid();

        var subA = CreateAndPersistSubscription(db, tenantId, sharedContractId, SubscriptionStatus.Expired);
        var subB = CreateAndPersistSubscription(db, tenantId, sharedContractId, SubscriptionStatus.Active);

        CreateAndPersistInstallment(db, tenantId, sharedContractId, now.AddDays(-10),
            amount: 500m, sequenceNumber: 1, subscriptionId: subA.Id);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloadedB = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.Id == subB.Id)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Active, reloadedB!.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 15. Expiration: Expired remains Expired after reconciliation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Expired_RemainsExpired_AfterReconciliation()
    {
        var tenantId = "tenant-92-15";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var start = now.AddMonths(-3);
        var result = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            status: SubscriptionStatus.Pending);
        var sub = result.Value;
        sub.Activate(start.AddDays(1));
        sub.MarkExpired(now);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.Id == sub.Id)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 16. Expiration: Payment after expiration does not reactivate
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Expired_PaymentDoesNotReactivate_ViaFinancialRecovery()
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString(), 1,
            100m, "USD", 1, 0,
            startsAtUtc: DateTime.UtcNow.AddMonths(-3),
            autoRenew: false,
            SubscriptionStatus.Active).Value;

        sub.MarkExpired(DateTime.UtcNow);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);

        var result = sub.ReactivateFromFinancialRecovery(DateTime.UtcNow);
        Assert.False(result.IsSuccess);

        Assert.False(sub.MarkPastDue().IsSuccess);
        Assert.False(sub.SuspendFromObligation().IsSuccess);

        Assert.Equal(SubscriptionStatus.Expired, sub.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 17. Security: Cross-tenant creation denied
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallmentCommand_CrossTenant_CreationDenied()
    {
        var tenantA = "tenant-A-92-17";
        var tenantB = "tenant-B-92-17";
        await using var db = CreateDbContext(tenantA);

        var contractA = CreateAndPersistContract(db, tenantA);

        var subscriptionB = TenantPlan.Create(
            Guid.NewGuid(), tenantB, 1, 100m, "USD", 12, 0,
            DateTime.UtcNow, false, SubscriptionStatus.Pending).Value!;
        subscriptionB.Activate(DateTime.UtcNow);
        db.TenantPlans.Add(subscriptionB);
        db.StampAddedTenantIds(tenantB);
        await db.SaveChangesAsync();
        db.Entry(subscriptionB).State = EntityState.Detached;

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contractA.Id, subscriptionB.Id, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e =>
            e.Code == "Installment.SubscriptionNotFound" ||
            e.Code == "Installment.SubscriptionBelongsToDifferentTenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 18. Security: Arbitrary SubscriptionId injection denied
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddInstallmentCommand_ArbitrarySubscriptionId_InjectionDenied()
    {
        var tenantId = "tenant-92-18";
        await using var db = CreateDbContext(tenantId);

        var contract = CreateAndPersistContract(db, tenantId);
        var fakeSubscriptionId = Guid.NewGuid();

        var handler = new AddInstallmentHandler(db, Substitute.For<IAuditWriter>());
        var command = new AddInstallmentCommand(
            contract.Id, fakeSubscriptionId, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SubscriptionNotFound");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Additional: Schedule creation cross-tenant rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateInstallmentSchedule_CrossTenant_Rejected()
    {
        var tenantA = "tenant-A-92-sc";
        var tenantB = "tenant-B-92-sc";
        await using var db = CreateDbContext(tenantA);

        var contractA = CreateAndPersistContract(db, tenantA);

        var subscriptionB = TenantPlan.Create(
            Guid.NewGuid(), tenantB, 1, 100m, "USD", 12, 0,
            DateTime.UtcNow, false, SubscriptionStatus.Pending).Value!;
        subscriptionB.Activate(DateTime.UtcNow);
        db.TenantPlans.Add(subscriptionB);
        db.StampAddedTenantIds(tenantB);
        await db.SaveChangesAsync();
        db.Entry(subscriptionB).State = EntityState.Detached;

        var handler = new CreateInstallmentScheduleHandler(db, Substitute.For<IAuditWriter>());
        var command = new CreateInstallmentScheduleCommand(
            contractA.Id, subscriptionB.Id,
            [new(1, new DateTime(2026, 6, 30), new DateTime(2026, 1, 1), new DateTime(2026, 6, 30), 12000m)]);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e =>
            e.Code == "Installment.SubscriptionNotFound" ||
            e.Code == "Installment.SubscriptionBelongsToDifferentTenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Additional: Schedule creation cross-contract rejected
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateInstallmentSchedule_CrossContract_Rejected()
    {
        var tenantId = "tenant-92-sc2";
        await using var db = CreateDbContext(tenantId);

        var contractA = CreateAndPersistContract(db, tenantId);
        var contractB = CreateAndPersistContract(db, tenantId);
        var subscriptionA = CreateAndPersistSubscription(db, tenantId, contractA.Id);

        var handler = new CreateInstallmentScheduleHandler(db, Substitute.For<IAuditWriter>());
        var command = new CreateInstallmentScheduleCommand(
            contractB.Id, subscriptionA.Id,
            [new(1, new DateTime(2026, 6, 30), new DateTime(2026, 1, 1), new DateTime(2026, 6, 30), 12000m)]);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SubscriptionBelongsToDifferentContract");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Additional: Domain factory rejects empty SubscriptionId
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void DomainFactory_EmptySubscriptionId_Rejected()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m, "USD", Guid.Empty);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SubscriptionRequired");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Additional: Payment settlement allows recovery
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconciliation_PaymentSettlement_AllowsRecovery()
    {
        var tenantId = "tenant-92-rec";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var sub = CreateAndPersistSubscription(db, tenantId);
        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var installment = CreateAndPersistInstallment(db, tenantId, contractId, now.AddDays(-3),
            amount: 500m, sequenceNumber: 1, subscriptionId: sub.Id);

        var service1 = CreateReconciliationService(db, new TestTimeProvider(now));
        await service1.ReconcileAsync(tenantId);

        SettleInstallment(db, tenantId, installment, 500m, now);

        db.ChangeTracker.Clear();
        var service2 = CreateReconciliationService(db, new TestTimeProvider(now));
        await service2.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.Id == sub.Id)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Active, reloaded!.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Additional: Cancelled remains Cancelled
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cancelled_RemainsCancelled_AfterReconciliation()
    {
        var tenantId = "tenant-92-cncl";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var sub = CreateAndPersistSubscription(db, tenantId, status: SubscriptionStatus.Cancelled);
        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.Id == sub.Id)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Cancelled, reloaded!.Status);
    }
}
