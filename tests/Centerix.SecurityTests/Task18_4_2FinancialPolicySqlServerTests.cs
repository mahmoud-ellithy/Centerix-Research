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
/// Task 18.4.2 SQL Server regression tests for the two approved financial policies,
/// executed against the real migrated SQL Server database through the real production handlers.
///
/// Policy 1 — credit-source eligibility (D-02):
///   Only credits with real customer economic value (Overpayment, SubscriptionChange) count
///   as eligible paid settlement. Granted/free/discretionary sources (ReferralReward,
///   Promotional, Compensation, Manual) must never become a new SubscriptionChange credit.
///
/// Policy 2 — refund after subscription change:
///   Value already converted into a SubscriptionChange credit must not be refunded again
///   as cash. Scenarios A (credit then refund), B (partial credit + partial refund) and
///   C (full the credit consumed) are covered.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task18_4_2FinancialPolicySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    public Task18_4_2FinancialPolicySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

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
    /// Seeds an old contract (12,000 = 12 months × 1,000) that started four months ago
    /// (consumed 4,000, unused 8,000), its Active subscription (optional), its issued
    /// 12,000 invoice and a Completed payment of <paramref name="paymentAmount"/> fully
    /// allocated to that invoice.
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
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        contract.SubmitForApproval();
        contract.Activate(startedAt);
        db.Contracts.Add(contract);

        Guid subscriptionId = Guid.Empty;
        if (withSubscription)
        {
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP",
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

    /// <summary>Seeds an Available Customer Credit of the requested source type (no application yet).</summary>
    private async Task<Guid> SeedAvailableCreditAsync(
        string tenantId, decimal amount, CreditSourceType sourceType, Guid? sourceId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var credit = TenantCredit.Create(
            Guid.NewGuid(), amount, sourceType, sourceId, "EGP",
            idempotencyKey: $"1842-{Guid.NewGuid():N}").Value;
        db.TenantCredits.Add(credit);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return credit.Id;
    }

    /// <summary>Seeds an extra Completed payment fully allocated to an existing invoice.</summary>
    private async Task<Guid> SeedAdditionalPaymentAsync(string tenantId, Guid invoiceId, decimal amount)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = Payment.Create(
            Guid.NewGuid(), $"PAY-{Guid.NewGuid():N}"[..16], amount, "EGP", PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);
        db.PaymentAllocations.Add(PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoiceId, amount, DateTime.UtcNow).Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return payment.Id;
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

    /// <summary>Runs the real CreateRefundHandler (derives the refund amount from the calculation service).</summary>
    private async Task<Result<Guid>> RunCreateRefundAsync(
        string tenantId, Guid contractId, Guid? subscriptionId, Guid? invoiceId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns("test-admin");
        var handler = new CreateRefundHandler(
            db, new RefundCalculationService(), currentUser, Substitute.For<IAuditWriter>());
        using var cts = new CancellationTokenSource(TestTimeout);
        return await handler.Handle(
            new CreateRefundCommand(
                $"REF-{Guid.NewGuid():N}"[..16], contractId, subscriptionId, invoiceId,
                "Task 18.4.2 refund-after-change test"),
            cts.Token);
    }

    /// <summary>Runs the real ExecuteRefundHandler against the refund's RefundAllocation chain.</summary>
    private async Task<Result<Updated>> RunExecuteRefundAsync(string tenantId, Guid refundId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns("test-admin");
        currentUser.IsAuthenticated.Returns(true);
        var handler = new ExecuteRefundHandler(db, currentUser, Substitute.For<IAuditWriter>());
        using var cts = new CancellationTokenSource(TestTimeout);
        return await handler.Handle(
            new ExecuteRefundCommand(refundId, $"1842-execute-{refundId:N}"), cts.Token);
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

    private async Task<int> CountRefundsAsync(string tenantId, Guid contractId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Refunds.AsNoTracking().CountAsync(r =>
            r.TenantId == tenantId && r.ContractId == contractId);
    }

    // ==================================================================
    // Policy 1 — credit-source eligibility in D-02
    // ==================================================================

    /// <summary>
    /// Eligible sources (real customer economic value): Overpayment and SubscriptionChange
    /// credits applied as settlement ARE counted — CreditApplication 4,000 + cash 3,000
    /// → eligible settlement 7,000 (binding bound, unused value is 8,000).
    /// An exclusion regression would produce 3,000 and fail.
    /// </summary>
    [Theory]
    [InlineData(CreditSourceType.Overpayment)]
    [InlineData(CreditSourceType.SubscriptionChange)]
    [Trait("Category", "SqlServer")]
    public async Task CreditSourceEligibility_EligibleSources_CountAsPaidSettlement(CreditSourceType sourceType)
    {
        var tenantId = $"B7C1E9D2-4A5F-4B6C-8D9E-0000000021{(int)sourceType:D2}";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1842ELA", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1842ELB", price: 2000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 3000m);

        var sourceId = sourceType == CreditSourceType.Overpayment ? graph.PaymentId : Guid.NewGuid();
        var creditId = await SeedAvailableCreditAsync(tenantId, 4000m, sourceType, sourceId);

        var applyResult = await RunApplyCreditAsync(
            tenantId, creditId, graph.InvoiceId, 4000m, $"1842-apply-{sourceType}");
        Assert.True(applyResult.IsSuccess,
            string.Join(", ", applyResult.Errors?.Select(e => e.Code) ?? []));

        var result = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        var newCredit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.NotNull(newCredit);

        // cash 3,000 + eligible credit application 4,000 = 7,000 (exclusion would be 3,000).
        Assert.Equal(7000m, newCredit!.Amount);
    }

    /// <summary>
    /// Non-eligible sources (granted/free/discretionary value): the CreditApplication exists
    /// and settled the invoice, but it is NOT customer-paid value — only the 3,000 cash counts,
    /// so the new SubscriptionChange credit is 3,000 and never 7,000.
    /// </summary>
    [Theory]
    [InlineData(CreditSourceType.ReferralReward)]
    [InlineData(CreditSourceType.Promotional)]
    [InlineData(CreditSourceType.Compensation)]
    [InlineData(CreditSourceType.Manual)]
    [Trait("Category", "SqlServer")]
    public async Task CreditSourceEligibility_GrantedSources_AreNotPaidSettlement(CreditSourceType sourceType)
    {
        var tenantId = $"B7C1E9D2-4A5F-4B6C-8D9E-0000000022{(int)sourceType:D2}";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1842NEA", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1842NEB", price: 2000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 3000m);

        var creditId = await SeedAvailableCreditAsync(tenantId, 4000m, sourceType, sourceId: null);

        var applyResult = await RunApplyCreditAsync(
            tenantId, creditId, graph.InvoiceId, 4000m, $"1842-apply-{sourceType}");
        Assert.True(applyResult.IsSuccess,
            string.Join(", ", applyResult.Errors?.Select(e => e.Code) ?? []));

        // The credit genuinely settled part of the invoice (financial movement happened)…
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var applications = await db.CreditApplications.AsNoTracking()
                .Where(ca => ca.CreditId == creditId).ToListAsync();
            Assert.Single(applications);
            Assert.Equal(4000m, applications[0].Amount);
        }

        var result = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        var newCredit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.NotNull(newCredit);

        // …but it is not customer-paid value: only the 3,000 cash is eligible (leak → 7,000).
        Assert.Equal(3000m, newCredit!.Amount);
    }

    /// <summary>
    /// Contract isolation: an eligible credit applied to ANOTHER contract's invoice of the
    /// same tenant must never increase this contract's eligible settlement (leak → 7,000).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task CreditSourceEligibility_EligibleCreditOnAnotherContract_DoesNotLeakIntoThisContract()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000002301";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P1842ISO", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1842ISN", price: 2000m, duration: 12);

        // Contract A — the one being upgraded (cash only).
        var graphA = await SeedOldContractAsync(tenantId, planId, paymentAmount: 3000m);

        // Contract B — same tenant, no subscription; an eligible credit settled its invoice.
        var graphB = await SeedOldContractAsync(tenantId, planId, paymentAmount: 3000m, withSubscription: false);
        var creditId = await SeedAvailableCreditAsync(
            tenantId, 4000m, CreditSourceType.Overpayment, graphB.PaymentId);
        var applyResult = await RunApplyCreditAsync(tenantId, creditId, graphB.InvoiceId, 4000m, "1842-apply-B");
        Assert.True(applyResult.IsSuccess,
            string.Join(", ", applyResult.Errors?.Select(e => e.Code) ?? []));

        var result = await RunChangePlanAsync(tenantId, graphA.SubscriptionId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        var newCredit = await FindSubscriptionChangeCreditAsync(tenantId, graphA.SubscriptionId);
        Assert.NotNull(newCredit);
        Assert.Equal(3000m, newCredit!.Amount);
    }

    // ==================================================================
    // Policy 2 — refund after subscription change
    // ==================================================================

    /// <summary>
    /// Scenario A — credit then refund.
    ///
    ///   Paid 12,000 → plan change → SubscriptionChange credit 8,000
    ///       (6,000 consumed by the new invoice, 2,000 still Available)
    ///   → later refund request on the old contract
    ///
    /// The refundable base (12,000 − 4,000 = 8,000) is fully covered by the already-issued
    /// credit → NO refund may be created (pre-fix: an 8,000 cash refund would be duplicated),
    /// and the unconsumed 2,000 credit is not converted into cash either.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task RefundAfterChange_ScenarioA_CreditThenRefund_SameValueNotRefundedTwice()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000002311";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1842A1", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1842A2", price: 1000m, duration: 6);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 12000m);

        var changeResult = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(changeResult.IsSuccess, string.Join(", ", changeResult.Errors?.Select(e => e.Code) ?? []));

        var credit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.NotNull(credit);
        Assert.Equal(8000m, credit!.Amount);
        Assert.Equal(2000m, credit.RemainingAmount);
        Assert.Equal(CreditStatus.PartiallyApplied, credit.Status);

        // Later refund request on the old contract: the credited value is not refundable cash.
        var refundResult = await RunCreateRefundAsync(
            tenantId, graph.ContractId, graph.SubscriptionId, graph.InvoiceId);
        Assert.False(refundResult.IsSuccess,
            "The credited value must not be refunded again as cash.");
        Assert.Contains(refundResult.Errors!, e => e.Code == RefundErrors.NoRefundDue(0m).Code);
        Assert.Equal(0, await CountRefundsAsync(tenantId, graph.ContractId));

        // The unconsumed credit remains usable credit — it was not re-routed into cash.
        var creditAfter = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.Equal(2000m, creditAfter!.RemainingAmount);
        Assert.Equal(CreditStatus.PartiallyApplied, creditAfter.Status);
    }

    /// <summary>
    /// Scenario B — partial credit + partial refund.
    ///
    ///   At change: paid 5,000 → SubscriptionChange credit 5,000 (fully consumed by the new invoice)
    ///   Later:     another 5,000 paid to the old contract's invoice (paid total 10,000)
    ///   Refund:    10,000 − 4,000 obligation − 5,000 credited = 1,000 genuinely refundable
    ///
    /// The refund is created through the real handler, executed through the real
    /// ExecuteRefundHandler, and settled with a 1,000 ledger entry.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task RefundAfterChange_ScenarioB_PartialCredit_PartialRefund_OnlyRemainingValueRefunded()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000002312";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1842B1", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1842B2", price: 2000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 5000m);

        var changeResult = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(changeResult.IsSuccess, string.Join(", ", changeResult.Errors?.Select(e => e.Code) ?? []));

        var credit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.NotNull(credit);
        Assert.Equal(5000m, credit!.Amount);
        Assert.Equal(0m, credit.RemainingAmount);
        Assert.Equal(CreditStatus.Applied, credit.Status);

        // A late installment lands on the old contract after the change.
        await SeedAdditionalPaymentAsync(tenantId, graph.InvoiceId, 5000m);

        var refundResult = await RunCreateRefundAsync(
            tenantId, graph.ContractId, graph.SubscriptionId, graph.InvoiceId);
        Assert.True(refundResult.IsSuccess,
            string.Join(", ", refundResult.Errors?.Select(e => e.Code) ?? []));

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundResult.Value);
            // Gross refundable would be 6,000; the issued credit reduces it to 1,000.
            Assert.Equal(1000m, refund.Amount);

            var allocations = await db.RefundAllocations.AsNoTracking()
                .Where(ra => ra.RefundId == refund.Id).ToListAsync();
            Assert.Equal(1000m, allocations.Sum(a => a.Amount));
        }

        // The remaining value is genuinely paid out — the real execution chain accepts it.
        var executeResult = await RunExecuteRefundAsync(tenantId, refundResult.Value);
        Assert.True(executeResult.IsSuccess,
            string.Join(", ", executeResult.Errors?.Select(e => e.Code) ?? []));

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundResult.Value);
            Assert.Equal(RefundStatus.Completed, refund.Status);

            var settlements = await db.CustomerLedgerEntries.AsNoTracking()
                .Where(e => e.EntryType == LedgerEntryType.RefundSettlement && e.RefundId == refund.Id)
                .ToListAsync();
            Assert.Single(settlements);
            Assert.Equal(1000m, settlements[0].Amount);
        }
    }

    /// <summary>
    /// Scenario C — full credit consumption.
    ///
    ///   Paid 12,000 → plan change → SubscriptionChange credit 8,000 fully applied to the
    ///   new invoice (Status = Applied, RemainingAmount = 0) → later refund request on the
    ///   old contract.
    ///
    /// No previously consumed/credited value may become refundable again: the refund is
    /// refused (pre-fix: 8,000 cash), no Refund row exists and the credit stays consumed.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task RefundAfterChange_ScenarioC_FullCreditConsumed_NoValueBecomesRefundableAgain()
    {
        var tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000002313";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P1842C1", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P1842C2", price: 2000m, duration: 12);

        var graph = await SeedOldContractAsync(tenantId, oldPlanId, paymentAmount: 12000m);

        var changeResult = await RunChangePlanAsync(tenantId, graph.SubscriptionId, newPlanId);
        Assert.True(changeResult.IsSuccess, string.Join(", ", changeResult.Errors?.Select(e => e.Code) ?? []));

        var credit = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.NotNull(credit);
        Assert.Equal(8000m, credit!.Amount);
        Assert.Equal(0m, credit.RemainingAmount);
        Assert.Equal(CreditStatus.Applied, credit.Status);

        var refundResult = await RunCreateRefundAsync(
            tenantId, graph.ContractId, graph.SubscriptionId, graph.InvoiceId);
        Assert.False(refundResult.IsSuccess,
            "Consumed credit must not reappear as a cash refund.");
        Assert.Contains(refundResult.Errors!, e => e.Code == RefundErrors.NoRefundDue(0m).Code);
        Assert.Equal(0, await CountRefundsAsync(tenantId, graph.ContractId));

        // The consumed credit remains consumed — nothing was silently restored.
        var creditAfter = await FindSubscriptionChangeCreditAsync(tenantId, graph.SubscriptionId);
        Assert.Equal(0m, creditAfter!.RemainingAmount);
        Assert.Equal(CreditStatus.Applied, creditAfter.Status);
    }
}
