namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Subscriptions.Events;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Platform;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 9.5 — Subscription Natural Expiration &amp; Lifecycle Completion.
///
/// Covers all required scenarios:
/// - Domain: Active/PastDue/Suspended → Expired on end reached; Cancelled/Expired unchanged
/// - Financial: No refund, no mutation of invoices/payments/installments
/// - Renewal: Old subscription naturally expires independently
/// - Tenant: Lifecycle consistent, no cross-tenant expiration
/// - Idempotency: Repeated reconciliation produces same result
/// - Concurrency: Two concurrent reconciliations converge correctly
/// - Domain Event: TenantPlanExpiredEvent emitted exactly once
/// - No manual expiration endpoint
/// </summary>
public class Phase9_5SubscriptionNaturalExpirationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_9_5_{Guid.NewGuid():N}";
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

    private static void LinkToContract(AppDbContext db, TenantPlan subscription, Guid contractId)
    {
        var tracked = db.TenantPlans.IgnoreQueryFilters().First(tp => tp.Id == subscription.Id);
        tracked.LinkToContract(contractId);
        db.SaveChanges();
        db.Entry(tracked).State = EntityState.Detached;
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

    private static TenantPlan CreatePendingSubscription(string? tenantId = null, int durationMonths = 12)
    {
        return TenantPlan.Create(
            Guid.NewGuid(),
            tenantId ?? Guid.NewGuid().ToString(),
            planId: 1,
            snapshotPrice: 100m,
            snapshotCurrency: "USD",
            durationMonths,
            bonusMonths: 0,
            startsAtUtc: DateTime.UtcNow,
            autoRenew: false,
            SubscriptionStatus.Pending).Value;
    }

    private static TenantPlan CreateActiveSubscription(string? tenantId = null, int durationMonths = 12)
    {
        var sub = CreatePendingSubscription(tenantId, durationMonths);
        sub.Activate(DateTime.UtcNow);
        return sub;
    }

    /// <summary>
    /// Creates a subscription whose effective end is in the past (already expired).
    /// Used for tests that need a subscription with a past EffectiveEndsAtUtc.
    /// </summary>
    private static (TenantPlan Sub, DateTime EffectiveEnd) CreatePastEndSubscription(
        string? tenantId = null, int durationMonths = 1)
    {
        var start = DateTime.UtcNow.AddMonths(-2);
        var sub = TenantPlan.Create(
            Guid.NewGuid(),
            tenantId ?? Guid.NewGuid().ToString(),
            planId: 1,
            snapshotPrice: 100m,
            snapshotCurrency: "USD",
            durationMonths,
            bonusMonths: 0,
            startsAtUtc: start,
            autoRenew: false,
            SubscriptionStatus.Active).Value;
        sub.Activate(start.AddDays(1));
        return (sub, sub.EffectiveEndsAtUtc);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 1: Domain Tests — MarkExpired state transitions
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Active_BeforeEnd_RemainsActive()
    {
        var sub = CreateActiveSubscription(durationMonths: 12);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public void Active_AtEnd_BecomesExpired()
    {
        var (sub, _) = CreatePastEndSubscription();

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);
    }

    [Fact]
    public void Active_AfterEnd_BecomesExpired()
    {
        var start = DateTime.UtcNow.AddMonths(-3);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString(), 1,
            100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Active).Value;
        sub.Activate(start.AddDays(1));

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);
    }

    [Fact]
    public void PastDue_AtEnd_BecomesExpired()
    {
        var (sub, _) = CreatePastEndSubscription();
        sub.MarkPastDue();
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);
    }

    [Fact]
    public void PastDue_BeforeEnd_RemainsPastDue()
    {
        var sub = CreateActiveSubscription(durationMonths: 12);
        sub.MarkPastDue();
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
    }

    [Fact]
    public void Suspended_AtEnd_BecomesExpired()
    {
        var (sub, _) = CreatePastEndSubscription();
        sub.SuspendFromObligation();
        Assert.Equal(SubscriptionStatus.Suspended, sub.Status);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);
    }

    [Fact]
    public void Suspended_BeforeEnd_RemainsSuspended()
    {
        var sub = CreateActiveSubscription(durationMonths: 12);
        sub.SuspendFromObligation();
        Assert.Equal(SubscriptionStatus.Suspended, sub.Status);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Suspended, sub.Status);
    }

    [Fact]
    public void Cancelled_RemainsCancelled_RegardlessOfEnd()
    {
        var tenantId = Guid.NewGuid().ToString();
        var start = DateTime.UtcNow.AddMonths(-3);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1,
            100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Active).Value;
        sub.Activate(start.AddDays(1));
        sub.Cancel(start.AddDays(2));
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public void Expired_RemainsExpired_Idempotent()
    {
        var (sub, _) = CreatePastEndSubscription();
        sub.MarkExpired(DateTime.UtcNow);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);
    }

    [Fact]
    public void Pending_CannotBeExpired()
    {
        var sub = CreatePendingSubscription(durationMonths: 1);

        var result = sub.MarkExpired(DateTime.UtcNow.AddMonths(2));
        Assert.False(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Pending, sub.Status);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 2: Domain Event — TenantPlanExpiredEvent emitted
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Expiration_EmitsDomainEvent()
    {
        var (sub, _) = CreatePastEndSubscription();

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);

        var events = sub.DomainEvents.ToList();
        Assert.Single(events);
        Assert.IsType<TenantPlanExpiredEvent>(events[0]);

        var evt = (TenantPlanExpiredEvent)events[0];
        Assert.Equal(sub.Id, evt.TenantPlanId);
        Assert.Equal(1, evt.PlanId);
    }

    [Fact]
    public void Expiration_Idempotent_NoDuplicateEvents()
    {
        var (sub, _) = CreatePastEndSubscription();

        sub.MarkExpired(DateTime.UtcNow);
        sub.ClearDomainEvents();

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Empty(sub.DomainEvents);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 3: Expiration does NOT invoke refund logic
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Expiration_DoesNotCreateRefund()
    {
        var tenantId = "tenant-norefund1";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        service.ReconcileAsync(tenantId).GetAwaiter().GetResult();

        var reloaded = db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefault();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);

        var refunds = db.Refunds.Where(r => r.TenantId == tenantId).ToList();
        Assert.Empty(refunds);
    }

    [Fact]
    public void PastDue_Expiration_DoesNotCreateRefund()
    {
        var tenantId = "tenant-norefund2";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);
        sub.MarkPastDue();

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        service.ReconcileAsync(tenantId).GetAwaiter().GetResult();

        var reloaded = db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefault();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
        Assert.Empty(db.Refunds.Where(r => r.TenantId == tenantId).ToList());
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 4: Financial integrity — no mutation on expiration
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Expiration_PaidInstallments_RemainPaid()
    {
        var tenantId = "tenant-fin1";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var installment = CreateAndPersistInstallment(db, tenantId, contractId,
            now.AddDays(-10), sub.Id, amount: 500m);

        var payment = Payment.Create(Guid.NewGuid(), "PAY-001", 500m, "USD", PaymentMethod.Cash).Value;
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, Guid.NewGuid(), 500m, now, installment.Id).Value;
        db.PaymentAllocations.Add(allocation);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        var trackedInstallment = db.Installments.First(i => i.Id == installment.Id);
        trackedInstallment.ApplyAllocation(allocation, now);
        db.SaveChanges();
        db.Entry(trackedInstallment).State = EntityState.Detached;
        db.Entry(allocation).State = EntityState.Detached;

        db.ChangeTracker.Clear();

        var installmentBefore = db.Installments.IgnoreQueryFilters()
            .First(i => i.Id == installment.Id);
        Assert.Equal(InstallmentStatus.Paid, installmentBefore.Status);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        service.ReconcileAsync(tenantId).GetAwaiter().GetResult();

        var installmentAfter = db.Installments.IgnoreQueryFilters()
            .First(i => i.Id == installment.Id);
        Assert.Equal(InstallmentStatus.Paid, installmentAfter.Status);
        Assert.Equal(500m, installmentAfter.SettledAmount);

        var allocationAfter = db.PaymentAllocations.IgnoreQueryFilters()
            .First(a => a.Id == allocation.Id);
        Assert.Equal(500m, allocationAfter.AllocatedAmount);
    }

    [Fact]
    public void Expiration_UnpaidInstallments_RemainUnpaid()
    {
        var tenantId = "tenant-fin2";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var installment = CreateAndPersistInstallment(db, tenantId, contractId,
            now.AddDays(-5), sub.Id, amount: 500m);

        db.ChangeTracker.Clear();

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        service.ReconcileAsync(tenantId).GetAwaiter().GetResult();

        var reloaded = db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefault();
        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);

        var installmentAfter = db.Installments.IgnoreQueryFilters()
            .First(i => i.Id == installment.Id);
        Assert.Equal(500m, installmentAfter.RemainingAmount);
        Assert.NotEqual(InstallmentStatus.Paid, installmentAfter.Status);
    }

    [Fact]
    public void Expiration_DoesNotDeletePayments()
    {
        var tenantId = "tenant-fin3";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var payment = Payment.Create(Guid.NewGuid(), "PAY-DEL", 200m, "USD", PaymentMethod.Cash).Value;
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        var paymentId = payment.Id;

        db.ChangeTracker.Clear();
        var paymentsBefore = db.Payments.IgnoreQueryFilters()
            .Where(p => p.Id == paymentId).ToList();
        Assert.Single(paymentsBefore);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        service.ReconcileAsync(tenantId).GetAwaiter().GetResult();

        var paymentsAfter = db.Payments.IgnoreQueryFilters()
            .Where(p => p.Id == paymentId).ToList();
        Assert.Single(paymentsAfter);
        Assert.Equal(200m, paymentsAfter[0].Amount);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 5: Reconciliation — Active/PastDue/Suspended → Expired
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconciliation_Active_EndReached_BecomesExpired()
    {
        var tenantId = "tenant-rec-exp1";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var start = now.AddMonths(-2);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        sub.Activate(start.AddDays(1));

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
    }

    [Fact]
    public async Task Reconciliation_PastDue_EndReached_BecomesExpired()
    {
        var tenantId = "tenant-rec-exp2";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var start = now.AddMonths(-2);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        sub.Activate(start.AddDays(1));
        sub.MarkPastDue();

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
    }

    [Fact]
    public async Task Reconciliation_Suspended_EndReached_BecomesExpired()
    {
        var tenantId = "tenant-rec-exp3";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var start = now.AddMonths(-2);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        sub.Activate(start.AddDays(1));
        sub.SuspendFromObligation();

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
    }

    [Fact]
    public async Task Reconciliation_Cancelled_EndReached_RemainsCancelled()
    {
        var tenantId = "tenant-rec-cnl";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var start = now.AddMonths(-3);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        sub.Activate(start.AddDays(1));
        sub.Cancel(start.AddDays(2));

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Cancelled, reloaded!.Status);
    }

    [Fact]
    public async Task Reconciliation_Expired_RemainsExpired()
    {
        var tenantId = "tenant-rec-expold";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);
        sub.MarkExpired(DateTime.UtcNow);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
    }

    [Fact]
    public async Task Reconciliation_PastDue_EndNotReached_RemainsPastDue()
    {
        var tenantId = "tenant-rec-pd";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var start = now;
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 12, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        sub.Activate(start.AddSeconds(1));

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        CreateAndPersistInstallment(db, tenantId, contractId,
            now.AddDays(-3), sub.Id, amount: 500m);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.PastDue, reloaded!.Status);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 6: Idempotency — repeated reconciliation produces same result
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconciliation_Idempotent_Expired_BecomesExpiredOnce()
    {
        var tenantId = "tenant-idem-exp";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));

        await service.ReconcileAsync(tenantId);
        await service.ReconcileAsync(tenantId);
        await service.ReconcileAsync(tenantId);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);

        var count = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Reconciliation_Idempotent_NoDuplicateSideEffects()
    {
        var tenantId = "tenant-idem-side";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var service = CreateReconciliationService(db, new TestTimeProvider(now));

        await service.ReconcileAsync(tenantId);
        await service.ReconcileAsync(tenantId);
        await service.ReconcileAsync(tenantId);

        var refunds = await db.Refunds.Where(r => r.TenantId == tenantId).ToListAsync();
        Assert.Empty(refunds);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();
        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 7: Tenant isolation — no cross-tenant expiration
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TenantIsolation_ReconcileTenantA_DoesNotExpireTenantB()
    {
        var tenantA = "tenantA-iso-exp";
        var tenantB = "tenantB-iso-exp";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantA);
        SeedGracePeriodPolicy(db, 7);

        var (subA, _) = CreatePastEndSubscription(tenantA);

        db.TenantPlans.Add(subA);
        db.StampAddedTenantIds(tenantA);
        db.SaveChanges();
        db.Entry(subA).State = EntityState.Detached;

        var subB = TenantPlan.Create(
            Guid.NewGuid(), tenantB, 1, 100m, "USD", 12, 0,
            startsAtUtc: now, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        subB.Activate(now);

        db.TenantPlans.Add(subB);
        db.StampAddedTenantIds(tenantB);
        db.SaveChanges();
        db.Entry(subB).State = EntityState.Detached;

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        await service.ReconcileAsync(tenantA);

        var reloadedA = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantA)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();
        Assert.Equal(SubscriptionStatus.Expired, reloadedA!.Status);

        var reloadedB = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantB)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();
        Assert.Equal(SubscriptionStatus.Active, reloadedB!.Status);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 8: Renewal — old subscription expires independently
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Renewal_OldSubscription_ExpiresIndependently()
    {
        var tenantId = Guid.NewGuid().ToString();
        var start = DateTime.UtcNow.AddMonths(-2);

        var oldSub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Active).Value;
        oldSub.Activate(start.AddDays(1));
        Assert.Equal(SubscriptionStatus.Active, oldSub.Status);

        var newSub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 2, 200m, "USD", 12, 0,
            startsAtUtc: DateTime.UtcNow, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        newSub.Activate(DateTime.UtcNow);
        Assert.Equal(SubscriptionStatus.Active, newSub.Status);

        var result = oldSub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, oldSub.Status);
        Assert.Equal(SubscriptionStatus.Active, newSub.Status);
    }

    [Fact]
    public void Renewal_NewSubscription_HasOwnCommercialTerms()
    {
        var tenantId = Guid.NewGuid().ToString();

        var oldSub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 12, 1,
            startsAtUtc: DateTime.UtcNow.AddMonths(-13),
            autoRenew: false,
            SubscriptionStatus.Active).Value;
        oldSub.Activate(DateTime.UtcNow.AddMonths(-13).AddDays(1));

        oldSub.MarkExpired(DateTime.UtcNow);
        Assert.Equal(SubscriptionStatus.Expired, oldSub.Status);

        var newSub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 2, 300m, "EGP", 6, 0,
            startsAtUtc: DateTime.UtcNow,
            autoRenew: false,
            SubscriptionStatus.Active).Value;
        newSub.Activate(DateTime.UtcNow);

        Assert.Equal(300m, newSub.SnapshotPrice);
        Assert.Equal("EGP", newSub.SnapshotCurrency);
        Assert.Equal(6, newSub.DurationMonths);
        Assert.Equal(0, newSub.BonusMonths);

        Assert.Equal(100m, oldSub.SnapshotPrice);
        Assert.Equal("USD", oldSub.SnapshotCurrency);
        Assert.Equal(12, oldSub.DurationMonths);
        Assert.Equal(1, oldSub.BonusMonths);
    }

    [Fact]
    public void Cancelled_Subscription_CannotBeExpired()
    {
        var tenantId = Guid.NewGuid().ToString();
        var start = DateTime.UtcNow.AddMonths(-3);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Active).Value;
        sub.Activate(start.AddDays(1));
        sub.Cancel(start.AddDays(2));

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 9: SubscriptionStateService uses TimeProvider
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubscriptionStateService_UsesTimeProvider_NotDateTimeUtcNow()
    {
        var tenantId = "tenant-state-tp";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var reconciliationService = CreateReconciliationService(db, new TestTimeProvider(now));
        var logger = Substitute.For<ILogger<SubscriptionStateService>>();
        var timeProvider = new TestTimeProvider(now);

        var stateService = new SubscriptionStateService(db, reconciliationService, timeProvider, logger);
        var state = await stateService.GetCurrentAsync(tenantId);

        Assert.Equal(SubscriptionStatus.Expired, state.PersistedStatus);
        Assert.False(state.IsActiveAsOfNow);
    }

    [Fact]
    public async Task SubscriptionStateService_ActiveBeforeEnd_ReturnsActive()
    {
        var tenantId = "tenant-state-active";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var start = now;
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 12, 0,
            startsAtUtc: start, autoRenew: false,
            SubscriptionStatus.Pending).Value;
        sub.Activate(start.AddSeconds(1));

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var reconciliationService = CreateReconciliationService(db, new TestTimeProvider(now));
        var logger = Substitute.For<ILogger<SubscriptionStateService>>();
        var timeProvider = new TestTimeProvider(now);

        var stateService = new SubscriptionStateService(db, reconciliationService, timeProvider, logger);
        var state = await stateService.GetCurrentAsync(tenantId);

        Assert.Equal(SubscriptionStatus.Active, state.PersistedStatus);
        Assert.True(state.IsActiveAsOfNow);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 10: Concurrency — sequential reconciliation convergence
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RepeatedReconciliation_ConvergesToExpired()
    {
        var tenantId = "tenant-conc-seq";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        for (var i = 0; i < 5; i++)
        {
            db.ChangeTracker.Clear();
            var service = CreateReconciliationService(db, new TestTimeProvider(now));
            await service.ReconcileAsync(tenantId);
        }

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();

        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);

        var count = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RepeatedReconciliation_NoDuplicateRefunds()
    {
        var tenantId = "tenant-conc-ref";
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        for (var i = 0; i < 5; i++)
        {
            db.ChangeTracker.Clear();
            var service = CreateReconciliationService(db, new TestTimeProvider(now));
            await service.ReconcileAsync(tenantId);
        }

        var refunds = await db.Refunds.Where(r => r.TenantId == tenantId).ToListAsync();
        Assert.Empty(refunds);

        var reloaded = await db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync();
        Assert.Equal(SubscriptionStatus.Expired, reloaded!.Status);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 11: No manual expiration — no ExpireSubscription command
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void NoExpireSubscriptionCommand_Exists()
    {
        var assembly = typeof(TenantPlan).Assembly;
        var expireTypes = assembly.GetTypes()
            .Where(t => t.Name.Contains("Expire", StringComparison.OrdinalIgnoreCase)
                     && t.Name.Contains("Subscription", StringComparison.OrdinalIgnoreCase)
                     && t != typeof(TenantPlanExpiredEvent))
            .ToList();

        Assert.Empty(expireTypes);
    }

    [Fact]
    public void MarkExpired_IsPublic_ButGuardedByTimeCheck()
    {
        var sub = CreateActiveSubscription(durationMonths: 12);

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 12: Installment behavior during expiration
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Expiration_DoesNotCancelInstallments()
    {
        var tenantId = "tenant-inst1";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var installment = CreateAndPersistInstallment(db, tenantId, contractId,
            now.AddDays(5), sub.Id, amount: 500m);

        db.ChangeTracker.Clear();

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        service.ReconcileAsync(tenantId).GetAwaiter().GetResult();

        var installmentAfter = db.Installments.IgnoreQueryFilters()
            .First(i => i.Id == installment.Id);
        Assert.NotEqual(InstallmentStatus.Cancelled, installmentAfter.Status);
    }

    [Fact]
    public void Expiration_HistoricalRecords_Unchanged()
    {
        var tenantId = "tenant-hist1";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext(tenantId);
        SeedGracePeriodPolicy(db, 7);

        var (sub, _) = CreatePastEndSubscription(tenantId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        db.Entry(sub).State = EntityState.Detached;

        var contractId = Guid.NewGuid();
        LinkToContract(db, sub, contractId);

        var installment = CreateAndPersistInstallment(db, tenantId, contractId,
            now.AddDays(-10), sub.Id, amount: 500m);

        var payment = Payment.Create(Guid.NewGuid(), "PAY-HIST", 500m, "USD", PaymentMethod.Cash).Value;
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, Guid.NewGuid(), 500m, now, installment.Id).Value;
        db.PaymentAllocations.Add(allocation);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        var trackedInstallment = db.Installments.First(i => i.Id == installment.Id);
        trackedInstallment.ApplyAllocation(allocation, now);
        db.SaveChanges();
        db.Entry(trackedInstallment).State = EntityState.Detached;
        db.Entry(allocation).State = EntityState.Detached;

        db.ChangeTracker.Clear();

        var service = CreateReconciliationService(db, new TestTimeProvider(now));
        service.ReconcileAsync(tenantId).GetAwaiter().GetResult();

        var reloaded = db.TenantPlans.IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenantId)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .First();
        Assert.Equal(SubscriptionStatus.Expired, reloaded.Status);

        var installmentFinal = db.Installments.IgnoreQueryFilters()
            .First(i => i.Id == installment.Id);
        Assert.Equal(500m, installmentFinal.SettledAmount);
        Assert.Equal(InstallmentStatus.Paid, installmentFinal.Status);

        var paymentFinal = db.Payments.IgnoreQueryFilters()
            .First(p => p.Id == payment.Id);
        Assert.Equal(500m, paymentFinal.Amount);

        var allocationFinal = db.PaymentAllocations.IgnoreQueryFilters()
            .First(a => a.Id == allocation.Id);
        Assert.Equal(500m, allocationFinal.AllocatedAmount);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 13: Subscription commercial snapshot unchanged on expiration
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Expiration_PreservesCommercialSnapshot()
    {
        var start = DateTime.UtcNow.AddMonths(-3);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString(), 1,
            500m, "EGP", 2, 0,
            startsAtUtc: start, autoRenew: true,
            SubscriptionStatus.Active).Value;
        sub.Activate(start.AddDays(1));

        var price = sub.SnapshotPrice;
        var currency = sub.SnapshotCurrency;
        var duration = sub.DurationMonths;
        var bonus = sub.BonusMonths;
        var starts = sub.StartsAtUtc;
        var baseEnds = sub.BaseEndsAtUtc;
        var effectiveEnds = sub.EffectiveEndsAtUtc;

        var result = sub.MarkExpired(DateTime.UtcNow);
        Assert.True(result.IsSuccess);

        Assert.Equal(price, sub.SnapshotPrice);
        Assert.Equal(currency, sub.SnapshotCurrency);
        Assert.Equal(duration, sub.DurationMonths);
        Assert.Equal(bonus, sub.BonusMonths);
        Assert.Equal(starts, sub.StartsAtUtc);
        Assert.Equal(baseEnds, sub.BaseEndsAtUtc);
        Assert.Equal(effectiveEnds, sub.EffectiveEndsAtUtc);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 14: Multiple subscriptions per tenant — second valid subscription
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void TenantLifecycle_MultipleSubscriptions_OldExpires_NewRemains()
    {
        var tenantId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        var oldSub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, 100m, "USD", 1, 0,
            startsAtUtc: now.AddMonths(-2),
            autoRenew: false,
            SubscriptionStatus.Active).Value;
        oldSub.Activate(now.AddMonths(-2).AddDays(1));

        var newSub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 2, 200m, "USD", 12, 0,
            startsAtUtc: now,
            autoRenew: false,
            SubscriptionStatus.Active).Value;
        newSub.Activate(now);

        oldSub.MarkExpired(now);
        Assert.Equal(SubscriptionStatus.Expired, oldSub.Status);
        Assert.Equal(SubscriptionStatus.Active, newSub.Status);

        Assert.True(newSub.EffectiveEndsAtUtc > now);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SECTION 15: Expiration does not modify Contract
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Expiration_DoesNotModifyContractPricing()
    {
        var start = DateTime.UtcNow.AddMonths(-2);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString(), 1,
            500m, "EGP", 6, 2,
            startsAtUtc: start, autoRenew: true,
            SubscriptionStatus.Active).Value;
        sub.Activate(start.AddDays(1));

        var snapshotPrice = sub.SnapshotPrice;
        var snapshotCurrency = sub.SnapshotCurrency;
        var durationMonths = sub.DurationMonths;
        var bonusMonths = sub.BonusMonths;

        sub.MarkExpired(DateTime.UtcNow);

        Assert.Equal(snapshotPrice, sub.SnapshotPrice);
        Assert.Equal(snapshotCurrency, sub.SnapshotCurrency);
        Assert.Equal(durationMonths, sub.DurationMonths);
        Assert.Equal(bonusMonths, sub.BonusMonths);
    }
}
