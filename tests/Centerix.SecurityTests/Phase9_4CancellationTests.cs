namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

public class Phase9_4CancellationTests
{
    private static string ErrMsg<T>(Result<T> r) =>
        r.IsSuccess ? "OK" : string.Join("; ", r.Errors?.Select(e => e.Code + ": " + e.Description) ?? []);

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? "tenant-1");
        currentTenant.IsAuthorized.Returns(true);
        return new AppDbContext(options, mediator, currentTenant);
    }

    private static Contract CreateContract(AppDbContext db, string tenantId, decimal monthly = 1000m, int months = 12)
    {
        var r = Contract.Create(Guid.NewGuid(), tenantId, "CNT-" + Guid.NewGuid().ToString("N")[..8], 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(months),
            months, monthly, monthly, "EGP", monthly * months);
        Assert.True(r.IsSuccess);
        var c = r.Value;
        c.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), c.Id, 1, monthly, "EGP", monthly, 1).Value);
        c.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), c.Id, 3, monthly * 3, "EGP", monthly, 2).Value);
        c.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), c.Id, 6, monthly * 6 * 0.87m, "EGP", monthly, 3).Value);
        c.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), c.Id, 12, monthly * 12 * 0.833m, "EGP", monthly, 4).Value);
        c.SubmitForApproval();
        c.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.Contracts.Add(c);
        db.SaveChanges();
        return c;
    }

    private static TenantPlan CreateSub(AppDbContext db, string tid, Guid cid, SubscriptionStatus st = SubscriptionStatus.Active)
    {
        var r = TenantPlan.Create(Guid.NewGuid(), tid, 1, 1000m, "EGP", 12, 0,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), false, st);
        Assert.True(r.IsSuccess);
        var s = r.Value;
        s.LinkToContract(cid);
        db.TenantPlans.Add(s);
        db.StampAddedTenantIds(tid);
        db.SaveChanges();
        return s;
    }

    private static Invoice CreateInv(AppDbContext db, string tid, Guid cid, decimal amt = 10000m)
    {
        var inv = Invoice.Create(Guid.NewGuid(), "INV-" + Guid.NewGuid().ToString("N")[..8],
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), amt, 0, 0, amt, contractId: cid).Value;
        inv.Issue(DateTime.UtcNow);
        db.Invoices.Add(inv);
        db.StampAddedTenantIds(tid);
        db.SaveChanges();
        return inv;
    }

    private static (Payment p, Invoice i) CreatePay(AppDbContext db, string tid, Guid cid, decimal amt)
    {
        var inv = CreateInv(db, tid, cid, amt);
        var pr = Payment.Create(Guid.NewGuid(), "PAY-" + Guid.NewGuid().ToString("N")[..8], amt, "EGP", PaymentMethod.Cash);
        Assert.True(pr.IsSuccess);
        var p = pr.Value;
        p.Complete(DateTime.UtcNow);
        var ar = PaymentAllocation.Create(Guid.NewGuid(), p.Id, inv.Id, amt, DateTime.UtcNow);
        Assert.True(ar.IsSuccess);
        db.Payments.Add(p);
        db.PaymentAllocations.Add(ar.Value);
        db.StampAddedTenantIds(tid);
        db.SaveChanges();
        return (p, inv);
    }

    private static Installment CreateInst(AppDbContext db, string tid, Guid cid, Guid sid, int seq, decimal amt, DateTime due)
    {
        var r = Installment.Create(Guid.NewGuid(), cid, seq, due, due.AddDays(-30), due, amt, "EGP", sid);
        Assert.True(r.IsSuccess);
        db.Installments.Add(r.Value);
        db.StampAddedTenantIds(tid);
        db.SaveChanges();
        return r.Value;
    }

    private static CancelSubscriptionHandler CreateHandler(AppDbContext db)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        var sync = Substitute.For<ITenantRegistrySync>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns("admin-1");
        var audit = Substitute.For<IAuditWriter>();
        var calc = new RefundCalculationService();
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(DateTimeOffset.UtcNow);
        return new CancelSubscriptionHandler(db, calc, guard, sync, user, audit, tp);
    }

    private static CancelSubscriptionHandler CreateForbiddenHandler(AppDbContext db)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Error.Forbidden("Platform.AdminRequired", "No."));
        var sync = Substitute.For<ITenantRegistrySync>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns("u1");
        var audit = Substitute.For<IAuditWriter>();
        var calc = new RefundCalculationService();
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(DateTimeOffset.UtcNow);
        return new CancelSubscriptionHandler(db, calc, guard, sync, user, audit, tp);
    }

    // ── Domain ──

    private static TenantPlan MkActive() => TenantPlan.Create(Guid.NewGuid(), "t1", 1, 100m, "USD", 12, 0,
        DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Active).Value;

    private static TenantPlan MkPending() => TenantPlan.Create(Guid.NewGuid(), "t1", 1, 100m, "USD", 12, 0,
        DateTime.UtcNow, false, SubscriptionStatus.Pending).Value;

    [Fact] public void Domain_Active_Cancelled() { var s = MkActive(); Assert.True(s.Cancel(DateTime.UtcNow).IsSuccess); Assert.Equal(SubscriptionStatus.Cancelled, s.Status); }
    [Fact] public void Domain_Pending_Cancelled() { var s = MkPending(); Assert.True(s.Cancel(DateTime.UtcNow).IsSuccess); Assert.Equal(SubscriptionStatus.Cancelled, s.Status); }

    [Fact]
    public void Domain_PastDue_Cancelled()
    {
        var s = MkActive();
        typeof(TenantPlan).GetMethod("MarkPastDue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(s, null);
        Assert.True(s.Cancel(DateTime.UtcNow).IsSuccess);
        Assert.Equal(SubscriptionStatus.Cancelled, s.Status);
    }

    [Fact]
    public void Domain_Suspended_Cancelled()
    {
        var s = MkActive();
        typeof(TenantPlan).GetMethod("SuspendFromObligation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(s, null);
        Assert.True(s.Cancel(DateTime.UtcNow).IsSuccess);
        Assert.Equal(SubscriptionStatus.Cancelled, s.Status);
    }

    [Fact]
    public void Domain_Expired_CannotCancel()
    {
        var s = TenantPlan.Create(Guid.NewGuid(), "t1", 1, 100m, "USD", 1, 0,
            DateTime.UtcNow.AddMonths(-3), false, SubscriptionStatus.Active).Value;
        s.MarkExpired(DateTime.UtcNow);
        Assert.False(s.Cancel(DateTime.UtcNow).IsSuccess);
    }

    [Fact]
    public void Domain_AlreadyCancelled_CannotCancelAgain()
    {
        var s = MkActive(); s.Cancel(DateTime.UtcNow);
        Assert.False(s.Cancel(DateTime.UtcNow).IsSuccess);
    }

    [Fact]
    public void Domain_BeforeStart_Fails()
    {
        var s = MkActive();
        Assert.False(s.Cancel(DateTime.UtcNow.AddMonths(-13)).IsSuccess);
    }

    [Fact]
    public void Domain_RaisesEvent()
    {
        var s = MkActive(); s.Cancel(DateTime.UtcNow);
        Assert.Contains(s.DomainEvents, e => e.GetType().Name == "TenantPlanCancelledEvent");
    }

    [Fact]
    public void Domain_PastEnd_CannotCancel()
    {
        var s = TenantPlan.Create(Guid.NewGuid(), "t1", 1, 100m, "USD", 1, 0,
            DateTime.UtcNow.AddMonths(-2), false, SubscriptionStatus.Active).Value;
        Assert.False(s.Cancel(DateTime.UtcNow).IsSuccess);
    }

    // ── Handler ──

    [Fact]
    public async Task Cancel_FullPayment_RefundCreated()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var cmd = new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "test");
        var r = await h.Handle(cmd, CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.True(r.Value.IsCancelled);
        Assert.NotNull(r.Value.Calculation);
        Assert.True(r.Value.RefundAmount > 0);
        Assert.NotNull(r.Value.RefundId);
    }

    [Fact]
    public async Task Cancel_NoPayment_NoRefund()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        var cmd = new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "test");
        var r = await h.Handle(cmd, CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.Equal(0, r.Value.RefundAmount);
        Assert.Null(r.Value.RefundId);
    }

    [Fact]
    public async Task Cancel_PartialPayment_CustomerOwes()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1", 1000m);
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 4000m);
        var cmd = new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "test");
        var r = await h.Handle(cmd, CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.Equal(0, r.Value.RefundAmount);
        Assert.True(r.Value.CustomerOutstandingAmount > 0);
    }

    [Fact]
    public async Task Cancel_PricingTiers_UsedCorrectly()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1", 1000m);
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var cmd = new CancelSubscriptionCommand(s.Id, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), "test");
        var r = await h.Handle(cmd, CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.NotNull(r.Value.Calculation);
        Assert.Equal(3, r.Value.Calculation!.ElapsedMonths);
        Assert.Equal(3000m, r.Value.Calculation.UsedSubscriptionAmount);
    }

    [Fact]
    public async Task Cancel_Idempotent()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var cmd = new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "test");
        var r1 = await h.Handle(cmd, CancellationToken.None);
        Assert.True(r1.IsSuccess, ErrMsg(r1));
        var r2 = await h.Handle(cmd, CancellationToken.None);
        Assert.True(r2.IsSuccess, ErrMsg(r2));
        Assert.False(r2.Value.IsCancelled);
        Assert.Single(db.Refunds.Where(x => x.SubscriptionId == s.Id).ToList());
    }

    [Fact]
    public async Task Cancel_NotFound()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var r = await h.Handle(new CancelSubscriptionCommand(Guid.NewGuid(), DateTime.UtcNow, "x"), CancellationToken.None);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public async Task Cancel_Forbidden()
    {
        using var db = CreateDbContext();
        var h = CreateForbiddenHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        var r = await h.Handle(new CancelSubscriptionCommand(s.Id, DateTime.UtcNow, "x"), CancellationToken.None);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public async Task Cancel_RefundPending()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var r = await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        var refund = await db.Refunds.FindAsync(r.Value.RefundId!.Value);
        Assert.NotNull(refund);
        Assert.Equal(RefundStatus.Pending, refund.Status);
    }

    [Fact]
    public async Task Cancel_PaymentImmutable()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        var (pay, _) = CreatePay(db, "tenant-1", c.Id, 12000m);
        await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        var pAfter = await db.Payments.FindAsync(pay.Id);
        Assert.Equal(12000m, pAfter!.Amount);
        Assert.Equal(PaymentStatus.Completed, pAfter.Status);
    }

    [Fact]
    public async Task Cancel_FutureInstallments_Cancelled()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var inst = CreateInst(db, "tenant-1", c.Id, s.Id, 1, 1000m, DateTime.UtcNow.AddMonths(7));
        await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        var iAfter = await db.Installments.FindAsync(inst.Id);
        Assert.Equal(InstallmentStatus.Cancelled, iAfter!.Status);
    }

    [Fact]
    public async Task Cancel_ContractImmutable()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        var cAfter = await db.Contracts.FindAsync(c.Id);
        Assert.Equal(ContractStatus.Active, cAfter!.Status);
    }

    [Fact]
    public async Task Cancel_InvoicePreserved()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        var (_, inv) = CreatePay(db, "tenant-1", c.Id, 12000m);
        await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        var iAfter = await db.Invoices.FindAsync(inv.Id);
        Assert.Equal(12000m, iAfter!.TotalAmount);
    }

    [Fact]
    public async Task Cancel_SharedPayment_Isolated()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var cA = CreateContract(db, "tenant-1");
        var cB = CreateContract(db, "tenant-1");
        var sA = CreateSub(db, "tenant-1", cA.Id);
        CreateSub(db, "tenant-1", cB.Id);
        var iA = CreateInv(db, "tenant-1", cA.Id, 6000m);
        var iB = CreateInv(db, "tenant-1", cB.Id, 4000m);
        var pr = Payment.Create(Guid.NewGuid(), "SHARED", 10000m, "EGP", PaymentMethod.Cash);
        Assert.True(pr.IsSuccess);
        var p = pr.Value; p.Complete(DateTime.UtcNow);
        var allocA = PaymentAllocation.Create(Guid.NewGuid(), p.Id, iA.Id, 6000m, DateTime.UtcNow).Value;
        var allocB = PaymentAllocation.Create(Guid.NewGuid(), p.Id, iB.Id, 4000m, DateTime.UtcNow).Value;
        db.Payments.Add(p);
        db.PaymentAllocations.Add(allocA);
        db.PaymentAllocations.Add(allocB);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();
        var r = await h.Handle(new CancelSubscriptionCommand(sA.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.Equal(6000m, r.Value.Calculation!.AmountActuallyPaid);
    }

    [Fact]
    public async Task Cancel_GiftRecovery()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1", 1000m);
        var b = ContractBenefit.Create(Guid.NewGuid(), c.Id, ContractBenefitType.PhysicalGift, "Gift", null, 1000m, "EGP").Value;
        b.MarkEligible(DateTime.UtcNow); b.MarkGranted(DateTime.UtcNow, "admin");
        c.AddBenefit(b);
        db.ContractBenefits.Add(b);
        db.SaveChanges();
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var r = await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.True(r.Value.Calculation!.RemainingBenefitValue > 0);
    }

    [Fact]
    public async Task Cancel_NonGrantedGift_NoRecovery()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1", 1000m);
        var b = ContractBenefit.Create(Guid.NewGuid(), c.Id, ContractBenefitType.PhysicalGift, "Gift", null, 1000m, "EGP").Value;
        b.MarkEligible(DateTime.UtcNow);
        c.AddBenefit(b);
        db.ContractBenefits.Add(b);
        db.SaveChanges();
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var r = await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.Equal(0, r.Value.Calculation!.ConsumedBenefitValue);
    }

    [Fact]
    public async Task Cancel_AuditWritten()
    {
        using var db = CreateDbContext();
        var audit = Substitute.For<IAuditWriter>();
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        var sync = Substitute.For<ITenantRegistrySync>();
        var user = Substitute.For<ICurrentUser>(); user.UserId.Returns("a1");
        var tp = Substitute.For<TimeProvider>(); tp.GetUtcNow().Returns(DateTimeOffset.UtcNow);
        var h = new CancelSubscriptionHandler(db, new RefundCalculationService(), guard, sync, user, audit, tp);
        var c = CreateContract(db, "tenant-1");
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 12000m);
        var r = await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        await audit.Received(1).WriteAsync(Arg.Is("Subscription.Cancel"), Arg.Is(nameof(TenantPlan)),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_Overpayment_Refund()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1", 1000m);
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 15000m);
        var r = await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.True(r.Value.RefundAmount > 0);
    }

    [Fact]
    public async Task Cancel_ZeroRefund_NoRecord()
    {
        using var db = CreateDbContext();
        var h = CreateHandler(db);
        var c = CreateContract(db, "tenant-1", 1000m);
        var s = CreateSub(db, "tenant-1", c.Id);
        CreatePay(db, "tenant-1", c.Id, 3000m);
        var r = await h.Handle(new CancelSubscriptionCommand(s.Id, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "x"), CancellationToken.None);
        Assert.True(r.IsSuccess, ErrMsg(r));
        Assert.Null(r.Value.RefundId);
        Assert.Equal(0, r.Value.RefundAmount);
    }
}


