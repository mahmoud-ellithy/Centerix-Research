namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// Final commercial-integrity hardening regressions (InMemory + actual handlers +
/// actual DbContext persistence). Every critical invariant test invokes the real
/// production handler — never a manual re-implementation of production logic.
/// </summary>
public class Task18FinalCommercialHardeningTests
{
    private static readonly DateTime FixedNow = new(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AppDbContext CreateDb(string tenantId, out ICurrentTenant tenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"T18Final_{Guid.NewGuid():N}")
            .Options;

        var stub = Substitute.For<ICurrentTenant>();
        stub.TenantId.Returns(tenantId);
        stub.IsAuthorized.Returns(true);
        tenant = stub;

        return new AppDbContext(options, Substitute.For<IMediator>(), stub);
    }

    private static TimeProvider FixedTimeProvider()
    {
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(new DateTimeOffset(FixedNow));
        return tp;
    }

    private static IPlatformAdminGuard AllowPlatformAdmin()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        return guard;
    }

    private static Plan SeedPlan(
        AppDbContext db,
        int id,
        decimal monthlyPrice = 1000m,
        int durationMonths = 12,
        int bonusMonths = 0,
        int maxStudents = 100,
        int maxUsers = 50,
        int maxBranches = 10,
        int maxTeachers = 20,
        int storageGb = 100,
        int smsQuota = 1000,
        bool isActive = true)
    {
        var plan = Plan.Create(
            id, $"PLAN-{id}-{Guid.NewGuid():N}"[..28], $"Plan {id}",
            monthlyPrice, maxStudents, maxUsers, maxBranches, maxTeachers,
            storageGb, smsQuota, isActive, null, "EGP", durationMonths, bonusMonths).Value;
        db.Plans.Add(plan);
        db.SaveChanges();
        return plan;
    }

    private static string SeedFeature(AppDbContext db, int id, string code, int planId, bool enabled = true)
    {
        // Insert the catalog rows directly: touching the already-persisted Plan aggregate
        // in the same InMemory context trips its rowversion concurrency token.
        // EF relationship fixup still populates plan.PlanFeatures for handler queries.
        var feature = Feature.Create(id, code, $"{code} desc", "Platform").Value;
        db.Features.Add(feature);
        db.PlanFeatures.Add(PlanFeature.Create(id + 100000, planId, feature.Id, enabled).Value);
        db.SaveChanges();
        return code;
    }

    private static Offer SeedAcceptedOffer(AppDbContext db, string tenantId, Plan plan, int? durationMonths = null)
    {
        var duration = durationMonths ?? plan.DurationMonths;
        var offer = Offer.Create(
            Guid.NewGuid(), tenantId, plan.Id,
            durationMonths: duration,
            baseAmount: plan.MonthlyPrice * duration,
            discountAmount: 0m,
            finalAmount: plan.MonthlyPrice * duration,
            monthlyListPrice: plan.MonthlyPrice,
            currencyCode: "EGP",
            calculatedAtUtc: DateTime.UtcNow,
            expiresAtUtc: DateTime.UtcNow.AddDays(1)).Value;
        Assert.True(offer.Accept(DateTime.UtcNow).IsSuccess);
        db.Offers.Add(offer);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        return offer;
    }

    private static void ActivateContract(AppDbContext db, Contract contract)
    {
        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.True(contract.Activate(FixedNow).IsSuccess);
        db.SaveChanges();
    }

    private static CreateSubscriptionFromContractHandler CreateSubscriptionHandler(AppDbContext db)
        => new(db, AllowPlatformAdmin(), new SubscriptionFactory(db),
            Substitute.For<IAuditWriter>(), FixedTimeProvider());

    // ------------------------------------------------------------------
    // #1 Snapshot versioning: no silent completeness
    // ------------------------------------------------------------------

    [Fact]
    public void SnapshotVersion_NegativeVersion_FailsCreation()
    {
        var result = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEG", 1,
            FixedNow, FixedNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: -1);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Contract.SnapshotVersion_Invalid");
    }

    [Fact]
    public void SnapshotVersion_Zero_AllowsCreation_ButCannotBecomeSubscription()
    {
        // Legacy/migration-era rows keep version 0: creatable for historical
        // reconstruction, but ValidateSnapshotCompleteness rejects them so they
        // can never silently produce a Subscription.
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-LEGACY", 1,
            FixedNow, FixedNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.IncompleteEntitlementSnapshotVersion).Value;

        Assert.Equal(0, contract.EntitlementSnapshotVersion);
        var validation = contract.ValidateSnapshotCompleteness();
        Assert.False(validation.IsSuccess);
        Assert.Contains(validation.Errors!, e => e.Code == "Contract.SnapshotIncomplete");
    }

    [Fact]
    public void SnapshotVersion_LegitimateZeros_RemainValid()
    {
        // MaxStudents/MaxUsers/SmsQuota/BonusMonths == 0 are legitimate entitlements.
        // The version marker (not zero-value heuristics) distinguishes real zeros
        // from a missing snapshot.
        var effective = FixedNow;
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-ZERO", 1,
            effective,
            TenantPlan.ComputeEffectiveEndsAtUtc(effective, 12, 0),
            12,
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            bonusMonths: 0, maxStudents: 0, maxUsers: 0, maxBranches: 0,
            maxTeachers: 0, storageGb: 0, smsQuota: 0).Value;

        Assert.True(contract.ValidateSnapshotCompleteness().IsSuccess);
    }

    [Fact]
    public void SnapshotVersion_MisalignedEnds_FailsValidation()
    {
        // Duration 12 + bonus 3 must end at effective + 12 + 3 (sequential), not +12.
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-MISALIGN", 1,
            FixedNow, FixedNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            bonusMonths: 3).Value;

        var validation = contract.ValidateSnapshotCompleteness();
        Assert.False(validation.IsSuccess);
        Assert.Contains(validation.Errors!, e => e.Code == "Contract.SnapshotIncomplete");
    }

    // ------------------------------------------------------------------
    // #4 Contract -> Subscription start alignment (actual handler)
    // ------------------------------------------------------------------

    private async Task<Contract> SeedActiveAlignedContractAsync(
        AppDbContext db, string tenantId, DateTime effectiveAt, int durationMonths = 12, int bonusMonths = 0)
    {
        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(effectiveAt, durationMonths, bonusMonths);
        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, $"CTR-{Guid.NewGuid():N}"[..12],
            1, effectiveAt, endsAt, durationMonths,
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            bonusMonths: bonusMonths).Value;
        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        ActivateContract(db, contract);
        return contract;
    }

    [Fact]
    public async Task Subscription_StartAlignment_ImmediateContract_StartsAtEffectiveDate()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var contract = await SeedActiveAlignedContractAsync(db, tenantId, FixedNow);
        var handler = CreateSubscriptionHandler(db);

        var result = await handler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var sub = db.TenantPlans.First(s => s.ContractId == contract.Id);
        Assert.Equal(contract.EffectiveAtUtc, sub.StartsAtUtc);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task Subscription_StartAlignment_FutureContract_CreatesPendingSubscription()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var effective = FixedNow.AddDays(30);
        var contract = await SeedActiveAlignedContractAsync(db, tenantId, effective);
        var handler = CreateSubscriptionHandler(db);

        var result = await handler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var sub = db.TenantPlans.First(s => s.ContractId == contract.Id);
        Assert.Equal(effective, sub.StartsAtUtc);
        Assert.Equal(SubscriptionStatus.Pending, sub.Status);
    }

    [Fact]
    public async Task Subscription_StartAlignment_HistoricalContract_StartsAtEffectiveDate()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var effective = FixedNow.AddDays(-30);
        var contract = await SeedActiveAlignedContractAsync(db, tenantId, effective);
        var handler = CreateSubscriptionHandler(db);

        var result = await handler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var sub = db.TenantPlans.First(s => s.ContractId == contract.Id);
        Assert.Equal(effective, sub.StartsAtUtc);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task Subscription_MisalignedContract_RejectedBeforePersistence()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        // Version 1 but EndsAtUtc disagrees with the authoritative calculation.
        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, "CTR-BAD-END", 1,
            FixedNow, FixedNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            bonusMonths: 3).Value;
        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        ActivateContract(db, contract);

        var handler = CreateSubscriptionHandler(db);
        var result = await handler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(db.TenantPlans);
    }

    // ------------------------------------------------------------------
    // #5 End alignment through the production Offer -> Contract -> Subscription chain
    // ------------------------------------------------------------------

    [Fact]
    public async Task Subscription_EndAlignment_OfferChain_UsesActualHandlers()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out var tenant);

        var plan = SeedPlan(db, 501, monthlyPrice: 2000m, durationMonths: 12, bonusMonths: 2);
        var offer = SeedAcceptedOffer(db, tenantId, plan);

        var effective = FixedNow;
        var offerHandler = new CreateContractFromOfferHandler(db, tenant);
        var contractId = await offerHandler.Handle(
            new CreateContractFromOfferCommand(offer.Id, "CTR-OFFER-ALIGN", effective),
            CancellationToken.None);
        Assert.True(contractId.IsSuccess);

        var contract = db.Contracts.First(c => c.Id == contractId.Value);
        ActivateContract(db, contract);

        var subHandler = CreateSubscriptionHandler(db);
        var subResult = await subHandler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);
        Assert.True(subResult.IsSuccess);

        var sub = db.TenantPlans.First(s => s.ContractId == contract.Id);
        var expectedEnds = TenantPlan.ComputeEffectiveEndsAtUtc(effective, 12, 2);
        Assert.Equal(expectedEnds, contract.EndsAtUtc);
        Assert.Equal(contract.EffectiveAtUtc, sub.StartsAtUtc);
        Assert.Equal(contract.EndsAtUtc, sub.EffectiveEndsAtUtc);
    }

    // ------------------------------------------------------------------
    // #6 BonusMonths snapshot regression (Plan 2 -> Contract 2 -> mutate to 7 -> sub 2)
    // ------------------------------------------------------------------

    [Fact]
    public async Task BonusMonths_FlowsContractToSubscription_NotFromMutatedPlan()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out var tenant);

        var plan = SeedPlan(db, 502, monthlyPrice: 1000m, durationMonths: 12, bonusMonths: 2);
        var offer = SeedAcceptedOffer(db, tenantId, plan);

        var offerHandler = new CreateContractFromOfferHandler(db, tenant);
        var contractId = await offerHandler.Handle(
            new CreateContractFromOfferCommand(offer.Id, "CTR-BONUS", FixedNow),
            CancellationToken.None);
        Assert.True(contractId.IsSuccess);

        var contract = db.Contracts.First(c => c.Id == contractId.Value);
        Assert.Equal(2, contract.BonusMonths);

        // Mutate the Plan AFTER the Contract exists.
        Assert.True(plan.Update(plan.Code, plan.DisplayName, plan.MonthlyPrice,
            plan.MaxStudents, plan.MaxUsers, plan.MaxBranches, plan.MaxTeachers,
            plan.StorageGB, plan.SMSQuota, true, bonusMonths: 7).IsSuccess);
        await db.SaveChangesAsync();
        Assert.Equal(7, plan.BonusMonths);

        ActivateContract(db, contract);
        var subHandler = CreateSubscriptionHandler(db);
        var subResult = await subHandler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);
        Assert.True(subResult.IsSuccess);

        var sub = db.TenantPlans.First(s => s.ContractId == contract.Id);
        Assert.Equal(2, sub.BonusMonths);
    }

    // ------------------------------------------------------------------
    // #7 Full Plan mutation regression through actual handlers
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlanMutation_FullSnapshotRegression_ViaActualHandlers()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out var tenant);

        var plan = SeedPlan(db, 503, monthlyPrice: 1000m, durationMonths: 12, bonusMonths: 2,
            maxStudents: 100, maxUsers: 50, maxBranches: 10, maxTeachers: 20,
            storageGb: 100, smsQuota: 1000);
        SeedFeature(db, 50301, "FEAT-KEEP-503", plan.Id, enabled: true);
        SeedFeature(db, 50302, "FEAT-DROP-503", plan.Id, enabled: true);

        var offer = SeedAcceptedOffer(db, tenantId, plan);
        var offerHandler = new CreateContractFromOfferHandler(db, tenant);
        var contractId = await offerHandler.Handle(
            new CreateContractFromOfferCommand(offer.Id, "CTR-MUTATE", FixedNow),
            CancellationToken.None);
        Assert.True(contractId.IsSuccess);
        var contract = db.Contracts.Include(c => c.ContractFeatures)
            .First(c => c.Id == contractId.Value);
        Assert.Equal(2, contract.ContractFeatures.Count);

        // Mutate EVERYTHING on the Plan after the Contract snapshot was taken.
        Assert.True(plan.Update(plan.Code, "Mutated", 9999m, 1, 1, 1, 1, 1, 1,
            false, bonusMonths: 9).IsSuccess);
        foreach (var pf in db.PlanFeatures.Where(p => p.PlanId == plan.Id))
            Assert.True(pf.Disable().IsSuccess);
        await db.SaveChangesAsync();

        ActivateContract(db, contract);
        var subHandler = CreateSubscriptionHandler(db);
        var subResult = await subHandler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);
        Assert.True(subResult.IsSuccess, string.Join(",", subResult.Errors?.Select(e => e.Code) ?? []));

        // Subscription equals the Contract snapshot — not the mutated Plan.
        var sub = db.TenantPlans.Include(s => s.Features)
            .First(s => s.ContractId == contract.Id);
        Assert.Equal(1000m, sub.SnapshotPrice);
        Assert.Equal(2, sub.BonusMonths);
        Assert.Equal(100, sub.SnapshotMaxStudents);
        Assert.Equal(50, sub.SnapshotMaxUsers);
        Assert.Equal(10, sub.SnapshotMaxBranches);
        Assert.Equal(20, sub.SnapshotMaxTeachers);
        Assert.Equal(100, sub.SnapshotStorageGb);
        Assert.Equal(1000, sub.SnapshotSmsQuota);
        Assert.Equal(2, sub.Features.Count);
        Assert.Contains(sub.Features, f => f.FeatureCode == "FEAT-KEEP-503");
        Assert.Contains(sub.Features, f => f.FeatureCode == "FEAT-DROP-503");
    }

    // ------------------------------------------------------------------
    // #8 ContractFeature normalization + duplicates
    // ------------------------------------------------------------------

    [Fact]
    public void ContractFeature_NormalizesConsistently()
    {
        var contractId = Guid.NewGuid();
        var a = ContractFeature.Create(contractId, " students ").Value;
        var b = ContractFeature.Create(contractId, "STUDENTS").Value;
        var c = ContractFeature.Create(contractId, "Students").Value;

        Assert.Equal("STUDENTS", a.FeatureCode);
        Assert.Equal("STUDENTS", b.FeatureCode);
        Assert.Equal("STUDENTS", c.FeatureCode);
    }

    [Fact]
    public void ContractFeature_NullEmptyWhitespace_Rejected()
    {
        var contractId = Guid.NewGuid();
        Assert.False(ContractFeature.Create(contractId, null!).IsSuccess);
        Assert.False(ContractFeature.Create(contractId, string.Empty).IsSuccess);
        Assert.False(ContractFeature.Create(contractId, "   ").IsSuccess);
    }

    [Fact]
    public void ContractFeature_DuplicateNormalizedCode_RejectedBeforePersistence()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-FEAT", 1,
            FixedNow, FixedNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.True(contract.AddContractFeature(
            ContractFeature.Create(contract.Id, "students").Value).IsSuccess);
        var duplicate = contract.AddContractFeature(
            ContractFeature.Create(contract.Id, " STUDENTS ").Value);
        Assert.False(duplicate.IsSuccess);
        Assert.Contains(duplicate.Errors!, e => e.Code == "Contract.DuplicateFeature");
        Assert.Single(contract.ContractFeatures);
    }

    // ------------------------------------------------------------------
    // #9 Customer credit exactness (domain primitives + handler-level A/B/D/E)
    // ------------------------------------------------------------------

    [Fact]
    public void Credit_InvoiceLargerThanCredit_Applies300_LeavesBalance200()
    {
        // credit = 300, invoice remaining = 500 -> application = 300, invoice balance = 200.
        var credit = TenantCredit.Create(
            Guid.NewGuid(), 300m, CreditSourceType.SubscriptionChange,
            Guid.NewGuid(), "EGP", "key-d-1").Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-D", DateOnly.FromDateTime(FixedNow),
            DateOnly.FromDateTime(FixedNow.AddMonths(1)),
            500m, 0m, 0m, 500m).Value;

        var remaining = invoice.GetRemainingAmount();
        var applicationAmount = Math.Min(credit.Amount, remaining);
        Assert.Equal(300m, applicationAmount);

        var app = CreditApplication.Create(
            Guid.NewGuid(), credit.Id, invoice.Id, applicationAmount, FixedNow, "key-d-1").Value;
        Assert.True(credit.ConsumeAmount(applicationAmount).IsSuccess);

        Assert.Equal(300m, app.Amount);
        Assert.Equal(0m, credit.RemainingAmount);
        Assert.Equal(200m, invoice.TotalAmount - app.Amount);
    }

    [Fact]
    public void Credit_InvoiceSmallerThanCredit_Applies300_LeavesCredit200()
    {
        // credit = 500, invoice remaining = 300 -> application = 300, remaining credit = 200.
        var credit = TenantCredit.Create(
            Guid.NewGuid(), 500m, CreditSourceType.SubscriptionChange,
            Guid.NewGuid(), "EGP", "key-e-1").Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-E", DateOnly.FromDateTime(FixedNow),
            DateOnly.FromDateTime(FixedNow.AddMonths(1)),
            300m, 0m, 0m, 300m).Value;

        var applicationAmount = Math.Min(credit.Amount, invoice.GetRemainingAmount());
        Assert.Equal(300m, applicationAmount);

        var app = CreditApplication.Create(
            Guid.NewGuid(), credit.Id, invoice.Id, applicationAmount, FixedNow, "key-e-1").Value;
        Assert.True(credit.ConsumeAmount(applicationAmount).IsSuccess);

        Assert.Equal(300m, app.Amount);
        Assert.Equal(200m, credit.RemainingAmount);
    }

    private async Task<(Guid SubscriptionId, Guid ContractId)> SeedPaidSubscriptionAsync(
        AppDbContext db, string tenantId, int planId,
        decimal monthlyPrice, decimal paidAmount, int monthsAgo, int durationMonths = 12)
    {
        var startedAt = FixedNow.AddMonths(-monthsAgo);
        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(startedAt, durationMonths, 0);

        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, $"CTR-PAID-{Guid.NewGuid():N}"[..16],
            planId, startedAt, endsAt, durationMonths,
            monthlyPrice, monthlyPrice, "EGP", monthlyPrice * durationMonths,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.True(contract.Activate(startedAt).IsSuccess);
        db.Contracts.Add(contract);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, monthlyPrice, "EGP",
            durationMonths, 0, startedAt, false, SubscriptionStatus.Pending).Value;
        Assert.True(sub.Activate(startedAt).IsSuccess);
        Assert.True(sub.LinkToContract(contract.Id).IsSuccess);
        db.TenantPlans.Add(sub);

        var invoice = Invoice.Create(
            Guid.NewGuid(), $"INV-PAID-{Guid.NewGuid():N}"[..16],
            DateOnly.FromDateTime(startedAt), DateOnly.FromDateTime(endsAt),
            monthlyPrice * durationMonths, 0m, 0m, monthlyPrice * durationMonths,
            contractId: contract.Id).Value;
        Assert.True(invoice.Issue(FixedNow).IsSuccess);
        db.Invoices.Add(invoice);

        var payment = Payment.Create(
            Guid.NewGuid(), $"PAY-{Guid.NewGuid():N}"[..16],
            paidAmount, "EGP", PaymentMethod.Cash).Value;
        Assert.True(payment.Complete(FixedNow).IsSuccess);
        db.Payments.Add(payment);
        db.PaymentAllocations.Add(PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, paidAmount, FixedNow).Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return (sub.Id, contract.Id);
    }

    private static ChangeSubscriptionPlanHandler CreateChangePlanHandler(AppDbContext db)
        => new(db, AllowPlatformAdmin(), new SubscriptionFactory(db),
            new PromotionCalculationService(), Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(), FixedTimeProvider());

    [Fact]
    public async Task Credit_ChangePlan_FirstRequest_CreatesExactlyOneOfEach()
    {
        // Test A: exactly one TenantCredit, one CreditApplication, one
        // CreditCreation and one CreditUsage ledger entry.
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var oldPlanId = SeedPlan(db, 601, monthlyPrice: 1000m).Id;
        var newPlanId = SeedPlan(db, 602, monthlyPrice: 2000m, durationMonths: 6).Id;
        var (oldSubId, _) = await SeedPaidSubscriptionAsync(
            db, tenantId, oldPlanId, monthlyPrice: 1000m, paidAmount: 12000m, monthsAgo: 6);

        var result = await CreateChangePlanHandler(db).Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);
        Assert.True(result.IsSuccess, string.Join(",", result.Errors?.Select(e => e.Code) ?? []));

        var credits = db.TenantCredits
            .Where(tc => tc.SourceType == CreditSourceType.SubscriptionChange && tc.SourceId == oldSubId)
            .ToList();
        Assert.Single(credits);
        var credit = credits[0];
        Assert.Equal(6000m, credit.Amount);
        Assert.Equal($"sub-change-{oldSubId:N}", credit.IdempotencyKey);

        var applications = db.CreditApplications.Where(ca => ca.CreditId == credit.Id).ToList();
        Assert.Single(applications);
        Assert.Equal(6000m, applications[0].Amount);
        Assert.Equal($"subscription-change-{oldSubId:N}", applications[0].IdempotencyKey);

        var creations = db.CustomerLedgerEntries
            .Where(e => e.EntryType == LedgerEntryType.CreditCreation).ToList();
        var usages = db.CustomerLedgerEntries
            .Where(e => e.EntryType == LedgerEntryType.CreditUsage).ToList();
        Assert.Single(creations);
        Assert.Single(usages);
        Assert.Equal(0m - 6000m, creations[0].RunningBalance);
        Assert.Equal(0m - 6000m - 6000m, usages[0].RunningBalance);
    }

    [Fact]
    public async Task Credit_ChangePlan_Retry_ReturnsExistingResult_CreatesNothingNew()
    {
        // Test B: same logical operation retried returns the existing financial
        // result and creates no second credit/application/ledger rows.
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var oldPlanId = SeedPlan(db, 611, monthlyPrice: 1000m).Id;
        var newPlanId = SeedPlan(db, 612, monthlyPrice: 2000m, durationMonths: 6).Id;
        var (oldSubId, _) = await SeedPaidSubscriptionAsync(
            db, tenantId, oldPlanId, monthlyPrice: 1000m, paidAmount: 12000m, monthsAgo: 6);

        var handler = CreateChangePlanHandler(db);
        var first = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);
        Assert.True(first.IsSuccess);

        var baseline = (
            Credits: db.TenantCredits.Count(),
            Applications: db.CreditApplications.Count(),
            Ledger: db.CustomerLedgerEntries.Count(),
            Contracts: db.Contracts.Count(),
            Subscriptions: db.TenantPlans.Count(),
            Invoices: db.Invoices.Count());

        var second = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(baseline.Credits, db.TenantCredits.Count());
        Assert.Equal(baseline.Applications, db.CreditApplications.Count());
        Assert.Equal(baseline.Ledger, db.CustomerLedgerEntries.Count());
        Assert.Equal(baseline.Contracts, db.Contracts.Count());
        Assert.Equal(baseline.Subscriptions, db.TenantPlans.Count());
        Assert.Equal(baseline.Invoices, db.Invoices.Count());
    }

    [Fact]
    public async Task Credit_ChangePlan_InvoiceLargerThanCredit_CapsApplication()
    {
        // Test D via the production handler: credit 300, new invoice 500
        // -> application 300, invoice balance 200.
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var oldPlanId = SeedPlan(db, 621, monthlyPrice: 1000m).Id;
        var newPlanId = SeedPlan(db, 622, monthlyPrice: 500m, durationMonths: 1).Id;
        var (oldSubId, _) = await SeedPaidSubscriptionAsync(
            db, tenantId, oldPlanId, monthlyPrice: 1000m, paidAmount: 300m, monthsAgo: 6);

        var result = await CreateChangePlanHandler(db).Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);
        Assert.True(result.IsSuccess, string.Join(",", result.Errors?.Select(e => e.Code) ?? []));

        var credit = db.TenantCredits.Single(
            tc => tc.SourceType == CreditSourceType.SubscriptionChange && tc.SourceId == oldSubId);
        Assert.Equal(300m, credit.Amount);

        var application = db.CreditApplications.Single(ca => ca.CreditId == credit.Id);
        Assert.Equal(300m, application.Amount);

        var invoice = db.Invoices.Single(i => i.Id == application.InvoiceId);
        Assert.Equal(500m, invoice.TotalAmount);
        Assert.Equal(200m, invoice.TotalAmount - application.Amount);
        Assert.Equal(0m, credit.RemainingAmount);
    }

    [Fact]
    public async Task Credit_ChangePlan_InvoiceSmallerThanCredit_PreservesRemainder()
    {
        // Test E via the production handler: credit 500, new invoice 300
        // -> application 300, remaining credit 200.
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var oldPlanId = SeedPlan(db, 631, monthlyPrice: 1000m).Id;
        var newPlanId = SeedPlan(db, 632, monthlyPrice: 300m, durationMonths: 1).Id;
        var (oldSubId, _) = await SeedPaidSubscriptionAsync(
            db, tenantId, oldPlanId, monthlyPrice: 1000m, paidAmount: 500m, monthsAgo: 6);

        var result = await CreateChangePlanHandler(db).Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);
        Assert.True(result.IsSuccess, string.Join(",", result.Errors?.Select(e => e.Code) ?? []));

        var credit = db.TenantCredits.Single(
            tc => tc.SourceType == CreditSourceType.SubscriptionChange && tc.SourceId == oldSubId);
        Assert.Equal(500m, credit.Amount);

        var application = db.CreditApplications.Single(ca => ca.CreditId == credit.Id);
        Assert.Equal(300m, application.Amount);
        Assert.Equal(200m, credit.RemainingAmount);
    }

    // ------------------------------------------------------------------
    // #10 Ledger RunningBalance: B2 == B0 - C - U
    // ------------------------------------------------------------------

    [Fact]
    public void Ledger_CreditSequence_SatisfiesB2EqualsB0MinusCMinusU()
    {
        const decimal b0 = 12000m;
        const decimal c = 6000m;
        const decimal u = 6000m;

        var creation = CustomerLedgerEntry.CreateCreditCreation(
            Guid.NewGuid(), Guid.NewGuid(), c, "EGP", b0, FixedNow).Value;
        Assert.Equal(b0 - c, creation.RunningBalance);

        var usage = CustomerLedgerEntry.CreateCreditUsage(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            u, "EGP", creation.RunningBalance, FixedNow).Value;

        Assert.Equal(b0 - c - u, usage.RunningBalance);
    }

    // ------------------------------------------------------------------
    // #5 Change/Renew end alignment through actual handlers
    // ------------------------------------------------------------------

    [Fact]
    public async Task ChangePlan_EndAlignment_ContractMatchesSubscription()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var oldPlanId = SeedPlan(db, 641, monthlyPrice: 1000m).Id;
        var newPlanId = SeedPlan(db, 642, monthlyPrice: 2000m, durationMonths: 6, bonusMonths: 2).Id;
        var (oldSubId, _) = await SeedPaidSubscriptionAsync(
            db, tenantId, oldPlanId, monthlyPrice: 1000m, paidAmount: 12000m, monthsAgo: 6);

        var result = await CreateChangePlanHandler(db).Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId), CancellationToken.None);
        Assert.True(result.IsSuccess, string.Join(",", result.Errors?.Select(e => e.Code) ?? []));

        var contract = db.Contracts.First(c => c.Id == result.Value);
        var sub = db.TenantPlans.First(s => s.ContractId == contract.Id);
        Assert.Equal(contract.EffectiveAtUtc, sub.StartsAtUtc);
        Assert.Equal(contract.EndsAtUtc, sub.EffectiveEndsAtUtc);
        Assert.Equal(
            TenantPlan.ComputeEffectiveEndsAtUtc(sub.StartsAtUtc, 6, 2),
            contract.EndsAtUtc);
    }

    [Fact]
    public async Task Renew_EndAlignment_ContractMatchesSubscription()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out _);

        var planId = SeedPlan(db, 651, monthlyPrice: 1000m, durationMonths: 12, bonusMonths: 1).Id;

        // Expired old subscription so the renewal starts immediately.
        var startedAt = FixedNow.AddMonths(-13);
        var oldEnds = TenantPlan.ComputeEffectiveEndsAtUtc(startedAt, 12, 1);
        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, "CTR-RENEW-OLD", planId,
            startedAt, oldEnds, 12, 1000m, 1000m, "EGP", 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            bonusMonths: 1).Value;
        db.Contracts.Add(contract);

        var oldSub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 12, 1,
            startedAt, false, SubscriptionStatus.Pending).Value;
        Assert.True(oldSub.Activate(startedAt).IsSuccess);
        Assert.True(oldSub.LinkToContract(contract.Id).IsSuccess);
        Assert.True(oldSub.MarkExpired(FixedNow).IsSuccess);
        db.TenantPlans.Add(oldSub);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var handler = new RenewSubscriptionOfferHandler(
            db, AllowPlatformAdmin(), new SubscriptionFactory(db),
            new PromotionCalculationService(), Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(), FixedTimeProvider());

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(oldSub.Id), CancellationToken.None);
        Assert.True(result.IsSuccess, string.Join(",", result.Errors?.Select(e => e.Code) ?? []));

        var newContract = db.Contracts.First(c => c.Id == result.Value);
        var newSub = db.TenantPlans.First(s => s.ContractId == newContract.Id);
        Assert.Equal(newContract.EffectiveAtUtc, newSub.StartsAtUtc);
        Assert.Equal(newContract.EndsAtUtc, newSub.EffectiveEndsAtUtc);
    }
}
