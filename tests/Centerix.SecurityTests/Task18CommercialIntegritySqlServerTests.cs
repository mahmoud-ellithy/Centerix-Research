namespace Centerix.SecurityTests;

using System.Data;
using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Domain.Platform.Promotions;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 18 commercial-integrity integration tests against real SQL Server:
/// #6/#7 — handler-based plan-mutation path (BonusMonths/ends alignment, feature + snapshot capture);
/// #9    — ChangeSubscriptionPlanCommand credit idempotency (key, single credit, amount, replay);
/// #10   — ledger running balance after plan-change credit: B2 == B0 - C - U.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task18CommercialIntegritySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    public Task18CommercialIntegritySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

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

    private static ChangeSubscriptionPlanHandler CreateHandler(IAppDbContext db)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);

        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(DateTimeOffset.UtcNow);

        return new ChangeSubscriptionPlanHandler(
            db,
            guard,
            new SubscriptionFactory(db),
            new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(),
            timeProvider);
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

    private async Task<int> EnsurePlanAsync(
        string codePrefix, decimal price = 1000m, int duration = 12, int bonusMonths = 0)
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

    private async Task<int> EnsurePlanWithFeatureAsync(string codePrefix, string featureCode, decimal price = 1000m, int duration = 12, int bonusMonths = 0)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = $"{codePrefix}_{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, code, "Plan", price, 100, 50, 10, 20, 100, 1000,
            true, null, "EGP", duration, bonusMonths).Value;
        db.Plans.Add(plan);

        var feature = Feature.Create(0, featureCode, "Snapshotted feature", "Platform").Value;
        db.Features.Add(feature);
        await db.SaveChangesAsync();

        plan.AddPlanFeature(PlanFeature.Create(0, plan.Id, feature.Id, true).Value);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>
    /// Seeds an Active old subscription linked to a fully-paid historical contract so the
    /// ChangeSubscriptionPlanCommand credit path (unused paid value) is reachable.
    /// Contract started 6 months ago, 12-month term, 1000 EGP/month, contracted 12000 EGP,
    /// fully paid. Unused paid value after 6 elapsed months is 6000 EGP (no pricing tiers).
    /// </summary>
    private async Task<(Guid SubscriptionId, Guid ContractId, Guid InvoiceId)> SeedPaidSubscriptionAsync(
        string tenantId, int planId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var startedAt = DateTime.UtcNow.AddMonths(-6);
        var endsAt = startedAt.AddMonths(12);

        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CTR-SEED-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            planId: planId,
            effectiveAtUtc: startedAt,
            endsAtUtc: endsAt,
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion);
        Assert.True(contractResult.IsSuccess, string.Join(", ", contractResult.Errors?.Select(e => e.Code) ?? []));
        var contract = contractResult.Value;
        contract.SubmitForApproval();
        contract.Activate(startedAt);
        db.Contracts.Add(contract);

        var subResult = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP", 12, 0,
            startedAt, false, SubscriptionStatus.Pending);
        Assert.True(subResult.IsSuccess);
        var sub = subResult.Value;
        sub.Activate(startedAt);
        Assert.True(sub.LinkToContract(contract.Id).IsSuccess);
        db.TenantPlans.Add(sub);

        var invoiceResult = Invoice.Create(
            Guid.NewGuid(),
            $"INV-SEED-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            DateOnly.FromDateTime(startedAt),
            DateOnly.FromDateTime(endsAt),
            subtotal: 12000m, discountAmount: 0m, taxAmount: 0m, totalAmount: 12000m,
            contractId: contract.Id);
        Assert.True(invoiceResult.IsSuccess);
        var invoice = invoiceResult.Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);

        var payment = Payment.Create(
            Guid.NewGuid(), $"PAY-SEED-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            12000m, "EGP", PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, 12000m, DateTime.UtcNow).Value;
        db.PaymentAllocations.Add(allocation);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return (sub.Id, contract.Id, invoice.Id);
    }

    private async Task<(Guid SubscriptionId, Guid ContractId)> SeedUnpaidSubscriptionAsync(
        string tenantId, int planId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var startedAt = DateTime.UtcNow.AddMonths(-6);
        var endsAt = startedAt.AddMonths(12);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CTR-UNP-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            planId: planId,
            effectiveAtUtc: startedAt,
            endsAtUtc: endsAt,
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        contract.SubmitForApproval();
        contract.Activate(startedAt);
        db.Contracts.Add(contract);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP", 12, 0,
            startedAt, false, SubscriptionStatus.Pending).Value;
        sub.Activate(startedAt);
        sub.LinkToContract(contract.Id);
        db.TenantPlans.Add(sub);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return (sub.Id, contract.Id);
    }

    private async Task<Result<Guid>> RunChangePlanAsync(string tenantId, Guid subscriptionId, int newPlanId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = CreateHandler(db);
        using var cts = new CancellationTokenSource(TestTimeout);
        return await handler.Handle(new ChangeSubscriptionPlanCommand(subscriptionId, newPlanId), cts.Token);
    }

    // ==================================================================
    // #6/#7 Handler-based plan-mutation integration
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_WithBonusMonths_ContractEndsAlignSequentially_WithSubscription()
    {
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000061";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18OLD", price: 1000m, duration: 12, bonusMonths: 0);
        var newPlanId = await EnsurePlanAsync("P18BON", price: 2000m, duration: 6, bonusMonths: 2);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var contract = await db.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == result.Value);
        Assert.NotNull(contract);
        Assert.Equal(6, contract.DurationMonths);
        Assert.Equal(2, contract.BonusMonths);
        Assert.Equal(Contract.CompleteEntitlementSnapshotVersion, contract.EntitlementSnapshotVersion);

        var newSub = await db.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == contract.Id);
        Assert.NotNull(newSub);

        var expectedEnds = TenantPlan.ComputeEffectiveEndsAtUtc(newSub.StartsAtUtc, 6, 2);
        Assert.Equal(expectedEnds, contract.EndsAtUtc);
        Assert.Equal(contract.EndsAtUtc, newSub.EffectiveEndsAtUtc);
        Assert.Equal(newSub.BaseEndsAtUtc.AddMonths(2), newSub.EffectiveEndsAtUtc);
        Assert.True(contract.ValidateSnapshotCompleteness().IsSuccess);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_SnapshotsFeaturesAndLimits_FromTargetPlan()
    {
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000062";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18FA", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanWithFeatureAsync("P18FB", "FEAT-SNAP-18", price: 3000m, duration: 12, bonusMonths: 1);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var contract = await db.Contracts.IgnoreQueryFilters()
            .Include(c => c.ContractFeatures)
            .FirstOrDefaultAsync(c => c.Id == result.Value);
        Assert.NotNull(contract);
        Assert.Equal(3000m, contract.MonthlyListPrice);
        Assert.Equal(1, contract.BonusMonths);
        Assert.Equal(100, contract.MaxStudents);
        Assert.Equal(1000, contract.SmsQuota);
        Assert.Contains(contract.ContractFeatures, f => f.FeatureCode == "FEAT-SNAP-18");

        var newSub = await db.TenantPlans.IgnoreQueryFilters()
            .Include(s => s.Features)
            .FirstOrDefaultAsync(s => s.ContractId == contract.Id);
        Assert.NotNull(newSub);
        Assert.Equal(3000m, newSub.SnapshotPrice);
        Assert.Equal(1, newSub.BonusMonths);
        Assert.Equal(100, newSub.SnapshotMaxStudents);
        Assert.Contains(newSub.Features, f => f.FeatureCode == "FEAT-SNAP-18");
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_AfterTargetPlanMutated_NewSubscriptionRetainsSnapshot()
    {
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000063";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18MC", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18MM", price: 2500m, duration: 12, bonusMonths: 3);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);
        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        // Mutate the target plan AFTER the change completed.
        using (var mutate = _env.Factory.Services.CreateScope())
        {
            var db = mutate.ServiceProvider.GetRequiredService<AppDbContext>();
            var plan = await db.Plans.FirstAsync(p => p.Id == newPlanId);
            var update = plan.Update(
                code: plan.Code,
                displayName: "Mutated After Change",
                monthlyPrice: 9999m,
                maxStudents: 1,
                maxUsers: 1,
                maxBranches: 1,
                maxTeachers: 1,
                storageGB: 1,
                smsQuota: 1,
                isActive: false);
            if (update.IsSuccess)
                await db.SaveChangesAsync();
        }

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var contract = await db2.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == result.Value);
        Assert.NotNull(contract);
        Assert.Equal(2500m, contract.MonthlyListPrice);
        Assert.Equal(3, contract.BonusMonths);

        var newSub = await db2.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == contract.Id);
        Assert.NotNull(newSub);
        Assert.Equal(SubscriptionStatus.Active, newSub.Status);
        Assert.Equal(2500m, newSub.SnapshotPrice);
        Assert.Equal(3, newSub.BonusMonths);
        Assert.Equal(100, newSub.SnapshotMaxStudents);
        Assert.Equal(1000, newSub.SnapshotSmsQuota);
    }

    // ==================================================================
    // #9 Credit idempotency (A–E) via ChangeSubscriptionPlanCommand
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_CreditIdempotencyKey_CreatedOnce_WithExpectedKey()
    {
        // A: successful change creates exactly one SubscriptionChange credit with the
        // production idempotency key sub-change-{subscriptionId:N}.
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000091";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18ID", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18IN", price: 2000m, duration: 6);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);
        var expectedKey = $"sub-change-{oldSubId:N}";

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credits = await db.TenantCredits.IgnoreQueryFilters()
            .Where(tc => tc.TenantId == tenantId
                      && tc.SourceType == CreditSourceType.SubscriptionChange
                      && tc.SourceId == oldSubId)
            .ToListAsync();

        Assert.Single(credits);
        Assert.Equal(expectedKey, credits[0].IdempotencyKey);
        Assert.True(credits[0].Amount > 0);
        Assert.Equal("EGP", credits[0].CurrencyCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_CreditAmount_IsMinOfUnusedValueAndPaidAmount()
    {
        // E: unused paid value after 6/12 months on a fully-paid 12000 contract is 6000;
        // creditAmount = min(unused=6000, paid=12000) = 6000.
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000092";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18CA", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18CB", price: 2000m, duration: 6);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credit = await db.TenantCredits.IgnoreQueryFilters()
            .FirstAsync(tc => tc.SourceType == CreditSourceType.SubscriptionChange
                           && tc.SourceId == oldSubId);

        Assert.Equal(6000m, credit.Amount);

        var applications = await db.CreditApplications.IgnoreQueryFilters()
            .Where(ca => ca.CreditId == credit.Id)
            .ToListAsync();
        Assert.Single(applications);
        Assert.Equal(6000m, applications[0].Amount);
        Assert.Equal($"subscription-change-{oldSubId:N}", applications[0].IdempotencyKey);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_UnpaidContract_CreatesNoCredit()
    {
        // B/E boundary: unused value exists but paidAmount == 0 → no credit, change still succeeds.
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000093";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18UP", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18UN", price: 2000m, duration: 6);

        var (oldSubId, _) = await SeedUnpaidSubscriptionAsync(tenantId, oldPlanId);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var creditCount = await db.TenantCredits.IgnoreQueryFilters()
            .CountAsync(tc => tc.SourceType == CreditSourceType.SubscriptionChange
                           && tc.SourceId == oldSubId);
        Assert.Equal(0, creditCount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_ReplayAfterExistingCredit_DoesNotCreateSecondCredit()
    {
        // D: pre-existing SubscriptionChange credit for the same SourceId → handler
        // takes the idempotent branch and does not insert another credit.
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000094";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18RP", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18RN", price: 2000m, duration: 6);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        using (var seedCredit = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(seedCredit.ServiceProvider, tenantId);
            var db = seedCredit.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = TenantCredit.Create(
                Guid.NewGuid(), 100m, CreditSourceType.SubscriptionChange,
                sourceId: oldSubId, currencyCode: "EGP",
                idempotencyKey: $"sub-change-{oldSubId:N}").Value;
            db.TenantCredits.Add(existing);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credits = await db2.TenantCredits.IgnoreQueryFilters()
            .Where(tc => tc.SourceType == CreditSourceType.SubscriptionChange
                      && tc.SourceId == oldSubId)
            .ToListAsync();
        Assert.Single(credits);
        Assert.Equal(100m, credits[0].Amount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_SequentialRetry_DoesNotDuplicateCredit()
    {
        // Test B (idempotent retry): the second attempt with the same logical operation
        // returns the EXISTING financial result (same contract id) and creates no
        // second credit, application, ledger entry, contract, subscription, or invoice.
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000095";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18SR", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18SN", price: 2000m, duration: 6);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        var first = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(first.IsSuccess, string.Join(", ", first.Errors?.Select(e => e.Code) ?? []));

        var second = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(second.IsSuccess, string.Join(", ", second.Errors?.Select(e => e.Code) ?? []));
        Assert.Equal(first.Value, second.Value);

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var creditCount = await db.TenantCredits.IgnoreQueryFilters()
            .CountAsync(tc => tc.SourceType == CreditSourceType.SubscriptionChange
                           && tc.SourceId == oldSubId);
        Assert.Equal(1, creditCount);

        var replayContracts = await db.Contracts.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == tenantId && c.PreviousSubscriptionId == oldSubId);
        Assert.Equal(1, replayContracts);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_ConcurrentRequests_CreateAtMostOneCredit()
    {
        // C (concurrency): SERIALIZABLE + eligibility + unique filtered index
        // UX_TenantCredits_TenantId_SourceType_SourceId → at most one credit per SourceId.
        // The loser observes/reuses the winner's financial result (same contract id)
        // instead of creating a second credit — so every success shares one id and
        // exactly one credit row exists.
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000096";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18CC", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18CN", price: 2000m, duration: 6);

        var (oldSubId, _, _) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        var results = new System.Collections.Concurrent.ConcurrentBag<Result<Guid>>();
        using var barrier = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var handler = CreateHandler(db);
            barrier.SignalAndWait(TestTimeout);
            var result = await handler.Handle(
                new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);
            results.Add(result);
        });
        await Task.WhenAll(tasks);

        var successes = results.Where(r => r.IsSuccess).Select(r => r.Value).ToList();
        Assert.NotEmpty(successes);
        Assert.Single(successes.Distinct());

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var creditCount = await db2.TenantCredits.IgnoreQueryFilters()
            .CountAsync(tc => tc.TenantId == tenantId
                           && tc.SourceType == CreditSourceType.SubscriptionChange
                           && tc.SourceId == oldSubId);
        Assert.Equal(1, creditCount);

        var contractCount = await db2.Contracts.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == tenantId && c.PreviousSubscriptionId == oldSubId);
        Assert.Equal(1, contractCount);
    }

    // ==================================================================
    // #10 Ledger: B2 == B0 - C - U after plan-change credit
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_LedgerBalance_SatisfiesB2EqualsB0MinusCMinusU()
    {
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000101";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18LG", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18LN", price: 2000m, duration: 6);

        var (oldSubId, _, oldInvoiceId) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        // B0: seed an invoice-charge ledger entry for the old invoice (RunningBalance = 12000).
        decimal b0;
        using (var seedLedger = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(seedLedger.ServiceProvider, tenantId);
            var db = seedLedger.ServiceProvider.GetRequiredService<AppDbContext>();
            var charge = CustomerLedgerEntry.CreateInvoiceCharge(
                Guid.NewGuid(), oldInvoiceId, 12000m, "EGP",
                previousBalance: 0m, DateTime.UtcNow).Value;
            db.CustomerLedgerEntries.Add(charge);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            b0 = charge.RunningBalance;
        }
        Assert.Equal(12000m, b0);

        var result = await RunChangePlanAsync(tenantId, oldSubId, newPlanId);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credit = await db2.TenantCredits.IgnoreQueryFilters()
            .FirstAsync(tc => tc.SourceType == CreditSourceType.SubscriptionChange
                           && tc.SourceId == oldSubId);
        var c = credit.Amount;
        Assert.Equal(6000m, c);

        var application = await db2.CreditApplications.IgnoreQueryFilters()
            .FirstAsync(ca => ca.CreditId == credit.Id);
        var u = application.Amount;
        Assert.Equal(c, u);

        var entries = await db2.CustomerLedgerEntries.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.RecordedAtUtc)
            .ThenBy(e => e.EntryType)
            .ThenBy(e => e.Id)
            .ToListAsync();

        var creditCreation = entries.Single(e => e.EntryType == LedgerEntryType.CreditCreation);
        var creditUsage = entries.Single(e => e.EntryType == LedgerEntryType.CreditUsage);

        // Sequential RunningBalance: B1 = B0 - C, B2 = B1 - U.
        Assert.Equal(b0 - c, creditCreation.RunningBalance);
        Assert.Equal(b0 - c - u, creditUsage.RunningBalance);

        // Reconstruction from immutable movements equals final RunningBalance.
        var reconstructed = entries.Sum(e => e.IsDebit ? e.Amount : -e.Amount);
        var expectedFinal = b0 - c - u;
        Assert.Equal(expectedFinal, reconstructed);
        Assert.Equal(expectedFinal, creditUsage.RunningBalance);
        Assert.Equal(expectedFinal, entries.Last().RunningBalance);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_ConcurrentCreditLedger_RemainsMathematicallyConsistent()
    {
        // Two concurrent plan changes on the same paid subscription: exactly one succeeds,
        // at most one credit pair is written, and the ledger still satisfies
        // reconstructed balance == final RunningBalance == B0 - C - U.
        const string tenantId = "B7C1E9D2-4A5F-4B6C-8D9E-000000000102";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("P18LC", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("P18LX", price: 2000m, duration: 6);

        var (oldSubId, _, oldInvoiceId) = await SeedPaidSubscriptionAsync(tenantId, oldPlanId);

        decimal b0;
        using (var seedLedger = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(seedLedger.ServiceProvider, tenantId);
            var db = seedLedger.ServiceProvider.GetRequiredService<AppDbContext>();
            var charge = CustomerLedgerEntry.CreateInvoiceCharge(
                Guid.NewGuid(), oldInvoiceId, 12000m, "EGP",
                previousBalance: 0m, DateTime.UtcNow).Value;
            db.CustomerLedgerEntries.Add(charge);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            b0 = charge.RunningBalance;
        }

        var successCount = 0;
        var results = new System.Collections.Concurrent.ConcurrentBag<Result<Guid>>();
        using var barrier = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var handler = CreateHandler(db);
            barrier.SignalAndWait(TestTimeout);
            var result = await handler.Handle(
                new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);
            results.Add(result);
            if (result.IsSuccess)
                Interlocked.Increment(ref successCount);
        });
        await Task.WhenAll(tasks);

        // The loser observes/reuses the winner's result: every success shares one
        // contract id and at most one credit pair is written.
        var successes = results.Where(r => r.IsSuccess).Select(r => r.Value).ToList();
        Assert.NotEmpty(successes);
        Assert.Single(successes.Distinct());

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var credits = await db2.TenantCredits.IgnoreQueryFilters()
            .Where(tc => tc.TenantId == tenantId
                      && tc.SourceType == CreditSourceType.SubscriptionChange
                      && tc.SourceId == oldSubId)
            .ToListAsync();
        Assert.Single(credits);

        var entries = await db2.CustomerLedgerEntries.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.RecordedAtUtc)
            .ThenBy(e => e.EntryType)
            .ThenBy(e => e.Id)
            .ToListAsync();

        var creditCreation = entries.Single(e => e.EntryType == LedgerEntryType.CreditCreation);
        var creditUsage = entries.Single(e => e.EntryType == LedgerEntryType.CreditUsage);
        var c = creditCreation.Amount;
        var u = creditUsage.Amount;

        Assert.Equal(b0 - c, creditCreation.RunningBalance);
        Assert.Equal(b0 - c - u, creditUsage.RunningBalance);

        var reconstructed = entries.Sum(e => e.IsDebit ? e.Amount : -e.Amount);
        Assert.Equal(b0 - c - u, reconstructed);
        Assert.Equal(reconstructed, entries.Last().RunningBalance);
    }
}
