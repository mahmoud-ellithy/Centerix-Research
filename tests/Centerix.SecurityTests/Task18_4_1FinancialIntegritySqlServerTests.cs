namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 18.4.1 SQL Server integration tests for the two remaining financial-integrity gaps.
///
/// F-18.4.4a — Customer Credit economic origin:
///   CreditApplications applied to the old contract's invoice ARE settlement
///   (they reduce Invoice.RemainingAmount and consume the credit exactly once), so
///   they participate in D-02 eligible paid settlement. The tests are SENSITIVE to
///   both failure modes: double counting (inflates the credit) and exclusion
///   (deflates it) — the expected credit catches both.
///
/// F-18.4.4b — Real refund chain:
///   Refunds are created through the genuine
///   Payment → PaymentAllocation → Invoice → Refund → RefundAllocation → Payment chain
///   and executed through the real ExecuteRefundHandler, proving that refunded money
///   is removed from D-02 eligible paid settlement.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task18_4_1FinancialIntegritySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    public Task18_4_1FinancialIntegritySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    /// <summary>Seeded old-contract financial graph identifiers.</summary>
    private sealed record OldContractGraph(
        Guid SubscriptionId,
        Guid ContractId,
        Guid InvoiceId,
        Guid PaymentId,
        string PaymentNumber);

    // ==================================================================
    // Helpers
    // ==================================================================

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, true);
    }

    private async Task SeedTenantAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
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

    private async Task<int> EnsurePlanAsync(string codePrefix, decimal price = 1000m, int duration = 12, int bonusMonths = 0)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = $"{codePrefix}_{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, code, "Plan", price, 100, 50, 10, 20, 100, 1000,
            true, null, "EGP", duration, bonusMonths).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>
    /// Seeds an old contract (12,000 = 12 months × 1,000) that started four months ago,
    /// its Active subscription, its issued 12,000 invoice, and a Completed payment of
    /// <paramref name="paymentAmount"/> fully allocated to that invoice.
    /// No pricing tiers are attached, so Contract.CalculateValueForElapsedMonths falls back to
    /// MonthlyListPrice × elapsedMonths → consumed 4,000, unused 8,000.
    /// </summary>
    private async Task<OldContractGraph> SeedOldContractAsync(
        string tenantId, int planId, decimal paymentAmount, bool withSubscription = true)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var startedAt = DateTime.UtcNow.AddMonths(-4);
        var endsAt = startedAt.AddMonths(12);

        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, $"CTR-{Guid.NewGuid():N}"[..16],
            planId, startedAt, endsAt, 12,
            1000m, 1000m, "EGP", 12000m, 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        contract.SubmitForApproval();
        contract.Activate(startedAt);
        db.Contracts.Add(contract);

        Guid subscriptionId = Guid.Empty;
        if (withSubscription)
        {
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP",
                12, 0, startedAt, false, SubscriptionStatus.Pending).Value;
            sub.Activate(startedAt);
            sub.LinkToContract(contract.Id);
            db.TenantPlans.Add(sub);
            subscriptionId = sub.Id;
        }

        var invoice = Invoice.Create(
            Guid.NewGuid(), $"INV-{Guid.NewGuid():N}"[..16],
            DateOnly.FromDateTime(startedAt), DateOnly.FromDateTime(endsAt),
            12000m, 0m, 0m, 12000m, contractId: contract.Id).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);

        var paymentNumber = $"PAY-{Guid.NewGuid():N}"[..16];
        var payment = Payment.Create(
            Guid.NewGuid(), paymentNumber, paymentAmount, "EGP", PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);
        db.PaymentAllocations.Add(PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, paymentAmount, DateTime.UtcNow).Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return new OldContractGraph(subscriptionId, contract.Id, invoice.Id, payment.Id, paymentNumber);
    }

    /// <summary>Seeds an Available Customer Credit (no CreditApplication yet).</summary>
    private async Task<Guid> SeedAvailableCreditAsync(
        string tenantId, decimal amount, CreditSourceType sourceType, Guid? sourceId, string? idempotencyKey = null)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var credit = TenantCredit.Create(
            Guid.NewGuid(), amount, sourceType, sourceId, "EGP", idempotencyKey).Value;
        db.TenantCredits.Add(credit);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return credit.Id;
    }

    /// <summary>Seeds a Pending refund sourced from a real Payment via a real RefundAllocation.</summary>
    private async Task<Guid> SeedPendingRefundAsync(
        string tenantId, OldContractGraph graph, Guid paymentId, decimal amount, string paymentNumber)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var refund = Refund.Create(
            Guid.NewGuid(), $"REF-{Guid.NewGuid():N}"[..16],
            graph.ContractId,
            graph.SubscriptionId == Guid.Empty ? null : graph.SubscriptionId,
            graph.InvoiceId,
            amount, "EGP", "Task 18.4.1 refund chain test", "test-admin", DateTime.UtcNow).Value;
        db.Refunds.Add(refund);

        db.RefundAllocations.Add(RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, paymentId, amount, PaymentMethod.Cash, "EGP", paymentNumber).Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return refund.Id;
    }

    /// <summary>Runs the real ChangeSubscriptionPlanHandler (upgrade/downgrade).</summary>
    private async Task<Result<Guid>> RunChangePlanAsync(string tenantId, Guid subscriptionId, int newPlanId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(DateTimeOffset.UtcNow);
        var handler = new ChangeSubscriptionPlanHandler(
            db, guard, new SubscriptionFactory(db), new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(), Substitute.For<IAuditWriter>(), timeProvider);
        using var cts = new CancellationTokenSource(TestTimeout);
        return await handler.Handle(new ChangeSubscriptionPlanCommand(subscriptionId, newPlanId), cts.Token);
    }

    /// <summary>Runs the real ExecuteRefundHandler against the seeded RefundAllocation chain.</summary>
    private async Task<Result<Updated>> RunExecuteRefundAsync(string tenantId, Guid refundId, string idempotencyKey)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns("test-admin");
        currentUser.IsAuthenticated.Returns(true);
        var platformAdminGuard = Substitute.For<IPlatformAdminGuard>();
        platformAdminGuard.EnsurePlatformAdmin().Returns(Result.Updated);
        var handler = new ExecuteRefundHandler(db, currentUser, platformAdminGuard, Substitute.For<IAuditWriter>());
        using var cts = new CancellationTokenSource(TestTimeout);
        return await handler.Handle(new ExecuteRefundCommand(refundId, idempotencyKey), cts.Token);
    }

    /// <summary>Runs the real ApplyCreditToInvoiceHandler (consumes credit, creates CreditApplication).</summary>
    private async Task<Result<Updated>> RunApplyCreditAsync(
        string tenantId, Guid creditId, Guid invoiceId, decimal amount, string idempotencyKey)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        using var cts = new CancellationTokenSource(TestTimeout);
        return await handler.Handle(
            new ApplyCreditToInvoiceCommand(creditId, invoiceId, amount, idempotencyKey), cts.Token);
    }

    private async Task<TenantCredit?> FindSubscriptionChangeCreditAsync(string tenantId, Guid oldSubscriptionId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.TenantCredits.AsNoTracking().FirstOrDefaultAsync(tc =>
            tc.TenantId == tenantId
            && tc.SourceType == CreditSourceType.SubscriptionChange
            && tc.SourceId == oldSubscriptionId);
    }

    // ==================================================================
    // F-18.4.4a — CreditApplication economic origin (double-count guard)
    // ==================================================================

    /// <summary>
    /// Proves a CreditApplication that settled the old contract's invoice is counted
    /// EXACTLY ONCE in D-02 eligible paid settlement.
    ///
    /// Financial chain:
    ///   Old contract value ........................ 12,000
    ///   Consumed (4 months × 1,000) ...............  4,000
    ///   Unused ....................................  8,000
    ///   Payment P1 (Completed) ....................  3,000
    ///   PaymentAllocation PA1 → old invoice .......  3,000
    ///   Customer Credit C1 (source = P1 overpay) ..  4,000
    ///   CreditApplication CA1 → old invoice .......  4,000   (invoice remaining was 9,000)
    ///   Eligible paid settlement ..................  7,000
    ///   SubscriptionChange credit = MIN(8,000, 7,000) = 7,000
    ///
    /// This assertion is SENSITIVE to both failure modes:
    ///   - double counting CA1 (3,000 + 4,000 + 4,000 = 11,000) → credit would be 8,000 (FAIL);
    ///   - excluding CA1 (3,000) → credit would be 3,000 (FAIL).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task F1844a_OverpaymentCredit_AppliedAsSettlement_CountedExactlyOnce()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001851";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1841A", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1841B", price: 2000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 3000m);

        // Economic origin: C1 is real cash the customer overpaid on an earlier invoice of
        // this tenant (AllocatePaymentCommand caps the allocation and credits the excess).
        var creditId = await SeedAvailableCreditAsync(
            tenantId, 4000m, CreditSourceType.Overpayment, graph.PaymentId, $"1841-c1-{graph.ContractId:N}");

        var applyResult = await RunApplyCreditAsync(tenantId, creditId, graph.InvoiceId, 4000m, "1841-apply-c1");
        Assert.True(applyResult.IsSuccess,
            string.Join(", ", applyResult.Errors?.Select(e => e.Code) ?? []));

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var credit = await db.TenantCredits.AsNoTracking().SingleAsync(tc => tc.Id == creditId);
            Assert.Equal(0m, credit.RemainingAmount);
            Assert.Equal(CreditStatus.Applied, credit.Status);

            var applications = await db.CreditApplications.AsNoTracking()
                .Where(ca => ca.CreditId == creditId).ToListAsync();
            Assert.Single(applications);
            Assert.Equal(4000m, applications[0].Amount);
            Assert.Equal(graph.InvoiceId, applications[0].InvoiceId);
        }

        var result = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        var newCredit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.NotNull(newCredit);

        // Settlement 7,000 binds (7,000 &lt; unused 8,000), so a double count would be visible.
        Assert.Equal(7000m, newCredit!.Amount);
        Assert.True(newCredit.Amount <= 8000m, "Upgrade credit must never exceed eligible unused value (8,000).");
        Assert.True(newCredit.Amount <= 7000m, "Upgrade credit must never exceed eligible paid settlement (7,000).");
    }

    /// <summary>
    /// Proves the mechanism that makes double counting structurally impossible: a consumed
    /// credit unit cannot settle a second invoice (TenantCredit.RemainingAmount is decremented
    /// and ConsumeAmount rejects any amount above the remaining balance).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task F1844a_ConsumedCredit_CannotSettleSecondInvoice_NoReUse()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001852";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P1841C", price: 1000m, duration: 12);

        var graphA = await SeedOldContractAsync(tenantId, planId, paymentAmount: 3000m, withSubscription: false);
        var graphB = await SeedOldContractAsync(tenantId, planId, paymentAmount: 3000m, withSubscription: false);

        var creditId = await SeedAvailableCreditAsync(
            tenantId, 4000m, CreditSourceType.Overpayment, graphA.PaymentId, $"1841-c2-{graphA.ContractId:N}");

        var firstApply = await RunApplyCreditAsync(tenantId, creditId, graphA.InvoiceId, 4000m, "1841-apply-a");
        Assert.True(firstApply.IsSuccess, string.Join(", ", firstApply.Errors?.Select(e => e.Code) ?? []));

        var secondApply = await RunApplyCreditAsync(tenantId, creditId, graphB.InvoiceId, 4000m, "1841-apply-b");
        Assert.False(secondApply.IsSuccess);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var applications = await db.CreditApplications.AsNoTracking()
            .Where(ca => ca.CreditId == creditId).ToListAsync();
        Assert.Single(applications);
        Assert.Equal(graphA.InvoiceId, applications[0].InvoiceId);

        var credit = await db.TenantCredits.AsNoTracking().SingleAsync(tc => tc.Id == creditId);
        Assert.Equal(0m, credit.RemainingAmount);
    }

    // ==================================================================
    // F-18.4.4b — Real Payment → RefundAllocation → Refund chain
    // ==================================================================

    /// <summary>
    /// Full refund of the only payment, executed through the REAL ExecuteRefundHandler
    /// against a REAL RefundAllocation → Payment link.
    ///
    /// Financial chain:
    ///   Old contract value ........................ 12,000
    ///   Consumed (4 months × 1,000) ...............  4,000
    ///   Unused ....................................  8,000
    ///   Payment P1 (Completed) ....................  5,000
    ///   PaymentAllocation PA1 → old invoice .......  5,000
    ///   Refund R1 (Completed) .....................  5,000
    ///   RefundAllocation RA1 → P1 .................  5,000
    ///   Eligible paid settlement = 5,000 − 5,000 ..      0
    ///   SubscriptionChange credit = MIN(8,000, 0) .      0  (no credit)
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task F1844b_FullRefund_ViaRefundAllocation_ProducesNoUpgradeCredit()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001853";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1841D", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1841E", price: 2000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 5000m);
        var refundId = await SeedPendingRefundAsync(
            tenantId, graph, graph.PaymentId, 5000m, graph.PaymentNumber);

        // The real handler validates: allocations exist and sum to the refund amount,
        // the payment is Completed / same tenant / same currency, and
        // allocation.Amount <= Payment.Amount − Σ other RefundAllocations.
        var executeResult = await RunExecuteRefundAsync(tenantId, refundId, "1841-refund-full");
        Assert.True(executeResult.IsSuccess,
            string.Join(", ", executeResult.Errors?.Select(e => e.Code) ?? []));

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
            Assert.Equal(RefundStatus.Completed, refund.Status);
            Assert.NotNull(refund.ExecutedAtUtc);

            // The genuine financial chain: RefundAllocation → original Payment.
            var allocation = await db.RefundAllocations.AsNoTracking()
                .SingleAsync(ra => ra.RefundId == refundId);
            Assert.Equal(graph.PaymentId, allocation.PaymentId);
            Assert.Equal(5000m, allocation.Amount);
            Assert.Equal("EGP", allocation.CurrencyCode);

            var refundSettlements = await db.CustomerLedgerEntries.AsNoTracking()
                .Where(e => e.EntryType == LedgerEntryType.RefundSettlement && e.RefundId == refundId)
                .ToListAsync();
            Assert.Single(refundSettlements);
            Assert.Equal(5000m, refundSettlements[0].Amount);
        }

        var result = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        // Unused value (8,000) exists, but nothing remains paid → no SubscriptionChange credit.
        var newCredit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.Null(newCredit);
    }

    /// <summary>
    /// Partial refund through the real chain: only the non-refunded portion of the payment
    /// counts as eligible paid settlement.
    ///
    /// Financial chain:
    ///   Old contract value ........................ 12,000
    ///   Consumed (4 months × 1,000) ...............  4,000
    ///   Unused ....................................  8,000
    ///   Payment P1 = 10,000 → PaymentAllocation .... 10,000
    ///   Refund R1 = 4,000  → RefundAllocation RA1 → P1 = 4,000
    ///   Eligible paid settlement = 10,000 − 4,000 . 6,000
    ///   SubscriptionChange credit = MIN(8,000, 6,000) = 6,000
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task F1844b_PartialRefund_ViaRefundAllocation_EligibleSettlementNetOfRefund()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001854";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1841F", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1841G", price: 2000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 10000m);
        var refundId = await SeedPendingRefundAsync(
            tenantId, graph, graph.PaymentId, 4000m, graph.PaymentNumber);

        var executeResult = await RunExecuteRefundAsync(tenantId, refundId, "1841-refund-partial");
        Assert.True(executeResult.IsSuccess,
            string.Join(", ", executeResult.Errors?.Select(e => e.Code) ?? []));

        var result = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        var newCredit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.NotNull(newCredit);
        Assert.Equal(6000m, newCredit!.Amount);
    }

    /// <summary>
    /// RefundAllocation integrity: SUM(RefundAllocations for a Payment) can never exceed
    /// Payment.Amount. The real ExecuteRefundHandler rejects the execution, so refunded value
    /// can never exceed the money actually received.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task F1844b_RefundAllocation_CannotExceedPaymentRefundableBalance()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001855";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1841H", price: 1000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 5000m);
        var refundId = await SeedPendingRefundAsync(
            tenantId, graph, graph.PaymentId, 6000m, graph.PaymentNumber);

        var executeResult = await RunExecuteRefundAsync(tenantId, refundId, "1841-refund-over");
        Assert.False(executeResult.IsSuccess,
            "A RefundAllocation above the payment refundable balance must be rejected.");

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
        Assert.NotEqual(RefundStatus.Completed, refund.Status);
        Assert.Null(refund.ExecutedAtUtc);

        var settlements = await db.CustomerLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == LedgerEntryType.RefundSettlement && e.RefundId == refundId)
            .ToListAsync();
        Assert.Empty(settlements);
    }

    /// <summary>
    /// Contract isolation: a refund executed against ANOTHER contract of the same tenant must
    /// never reduce this contract's eligible paid settlement. Both contracts are settled in cash
    /// by 10,000; only contract B carries a 4,000 refund.
    ///
    ///   Contract A: unused 8,000, settlement 10,000 → credit MIN(8,000, 10,000) = 8,000
    ///   Contract B: unused 8,000, settlement 10,000 − 4,000 = 6,000 (not part of A's calc)
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task F1844b_RefundOnAnotherContract_DoesNotReduceThisContractsSettlement()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000001856";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P1841I", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1841J", price: 2000m, duration: 12);

        // Contract A — the one being upgraded (only non-terminal subscription allowed per tenant).
        var graphA = await SeedOldContractAsync(tenantId, planId, paymentAmount: 10000m);

        // Contract B — same tenant, no subscription (so the single-active-subscription
        // unique index is respected), carries a real executed refund of 4,000.
        var graphB = await SeedOldContractAsync(tenantId, planId, paymentAmount: 10000m, withSubscription: false);
        var refundB = await SeedPendingRefundAsync(
            tenantId, graphB, graphB.PaymentId, 4000m, graphB.PaymentNumber);

        var executeResult = await RunExecuteRefundAsync(tenantId, refundB, "1841-refund-other");
        Assert.True(executeResult.IsSuccess,
            string.Join(", ", executeResult.Errors?.Select(e => e.Code) ?? []));

        var result = await RunChangePlanAsync(tenantId, graphA.SubscriptionId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        var newCredit = await FindSubscriptionChangeCreditAsync(tenantId, graphA.SubscriptionId);
        Assert.NotNull(newCredit);

        // 8,000 proves contract B's refund did NOT leak into contract A's settlement
        // (a tenant-wide refund sum would have produced MIN(8,000, 6,000) = 6,000).
        Assert.Equal(8000m, newCredit!.Amount);
    }
}
