namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

/// <summary>
/// Final commercial-integrity regression: the accepted Offer is the COMPLETE
/// authoritative commercial snapshot. CreateContractFromOfferHandler (real
/// production handler) must build the Contract exclusively from the persisted
/// Offer snapshot — mutating the Plan after Offer acceptance can never alter
/// the resulting Contract or its Subscription.
/// All tests execute the actual production handlers.
/// </summary>
public class CompleteOfferSnapshotIntegrityTests
{
    private static readonly DateTime FixedNow = new(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateDb(string tenantId, out ICurrentTenant tenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"CompleteOfferSnapshot_{Guid.NewGuid():N}")
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
        int bonusMonths,
        int maxStudents,
        int maxUsers,
        int maxBranches,
        int maxTeachers,
        int storageGb,
        int smsQuota,
        (int months, decimal price)[] tiers,
        int[] featureIds)
    {
        var plan = Plan.Create(
            id, $"PLAN-{id}-{Guid.NewGuid():N}"[..28], $"Plan {id}",
            1000m, maxStudents, maxUsers, maxBranches, maxTeachers,
            storageGb, smsQuota, true, null, "EGP", 12, bonusMonths).Value;

        foreach (var (months, price) in tiers)
        {
            plan.AddPricingTier(
                PlanPricingTier.Create(id * 1000 + months, id, months, price, months).Value);
        }

        db.Plans.Add(plan);
        db.SaveChanges();

        foreach (var featureId in featureIds)
        {
            var code = $"FEATURE_{featureId}";
            db.Features.Add(Feature.Create(featureId, code, $"{code} desc", "Platform").Value);
            db.PlanFeatures.Add(PlanFeature.Create(featureId + 100000, id, featureId, true).Value);
        }

        db.SaveChanges();
        return plan;
    }

    /// <summary>Mutates the plan to completely different commercial values.</summary>
    private static void MutatePlan(
        AppDbContext db,
        Plan plan,
        int[] newFeatureIds)
    {
        Assert.True(plan.Update(
            $"PLAN-{plan.Id}-MUTATED", "Mutated Plan",
            monthlyPrice: 9000m,
            maxStudents: 500,
            maxUsers: 100,
            maxBranches: 20,
            maxTeachers: 200,
            storageGB: 500,
            smsQuota: 10000,
            isActive: true,
            currencyCode: "EGP",
            durationMonths: 12,
            bonusMonths: 7).IsSuccess);

        var oldTiers = db.PlanPricingTiers.Where(t => t.PlanId == plan.Id).ToList();
        foreach (var tier in oldTiers)
        {
            db.PlanPricingTiers.Remove(tier);
        }

        db.PlanPricingTiers.Add(
            PlanPricingTier.Create(plan.Id * 1000 + 12, plan.Id, 12, 99999m, 12).Value);

        var oldFeatures = db.PlanFeatures.Where(f => f.PlanId == plan.Id).ToList();
        foreach (var pf in oldFeatures)
        {
            db.PlanFeatures.Remove(pf);
        }

        foreach (var featureId in newFeatureIds)
        {
            var code = $"FEATURE_{featureId}";
            if (!db.Features.Any(f => f.Id == featureId))
            {
                db.Features.Add(Feature.Create(featureId, code, $"{code} desc", "Platform").Value);
            }

            db.PlanFeatures.Add(PlanFeature.Create(featureId + 100000, plan.Id, featureId, true).Value);
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task PlanMutation_AfterOfferCalculation_ContractUsesOriginalOfferSnapshot()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out var tenant);

        // 1. Plan: BonusMonths=2, limits, features A,B, original tiers
        var plan = SeedPlan(
            db, 7001,
            bonusMonths: 2, maxStudents: 100, maxUsers: 10, maxBranches: 2,
            maxTeachers: 20, storageGb: 50, smsQuota: 1000,
            tiers: [(3, 2700m), (6, 5220m), (12, 10000m)],
            featureIds: [701, 702]);

        // 2. Calculate and persist the Offer (actual production handler)
        var calculateHandler = new CalculateAndPersistOfferHandler(
            db, new PromotionCalculationService(), tenant);
        var offerResult = await calculateHandler.Handle(
            new CalculateAndPersistOfferCommand(plan.Id, 12, FixedNow), CancellationToken.None);
        Assert.True(offerResult.IsSuccess);

        // 3. Accept the Offer (actual production handler)
        var acceptHandler = new AcceptOfferHandler(db, tenant);
        var acceptResult = await acceptHandler.Handle(
            new AcceptOfferCommand(offerResult.Value.Id), CancellationToken.None);
        Assert.True(acceptResult.IsSuccess);

        // 4. Mutate the Plan AFTER acceptance
        MutatePlan(db, plan, [703, 704]);

        // 5. REAL CreateContractFromOfferHandler
        var contractHandler = new CreateContractFromOfferHandler(db, tenant);
        var contractId = await contractHandler.Handle(
            new CreateContractFromOfferCommand(offerResult.Value.Id, "CTR-SNAPSHOT", FixedNow),
            CancellationToken.None);
        Assert.True(contractId.IsSuccess, string.Join(",", contractId.Errors?.Select(e => e.Description) ?? []));

        // 6. Contract must contain the ORIGINAL Offer snapshot values
        var contract = db.Contracts
            .Include(c => c.ContractFeatures)
            .Include(c => c.PricingTiers)
            .First(c => c.Id == contractId.Value);

        Assert.Equal(2, contract.BonusMonths);
        Assert.Equal(100, contract.MaxStudents);
        Assert.Equal(10, contract.MaxUsers);
        Assert.Equal(2, contract.MaxBranches);
        Assert.Equal(20, contract.MaxTeachers);
        Assert.Equal(50, contract.StorageGb);
        Assert.Equal(1000, contract.SmsQuota);
        Assert.Equal(12, contract.DurationMonths);
        Assert.Equal(1000m, contract.MonthlyListPrice);

        var featureCodes = contract.ContractFeatures.Select(f => f.FeatureCode).OrderBy(c => c).ToList();
        Assert.Equal(["FEATURE_701", "FEATURE_702"], featureCodes);

        // Original pricing tiers — NOT the mutated 99999m tier
        var tier12 = contract.PricingTiers.Single(t => t.DurationMonths == 12);
        Assert.Equal(10000m, tier12.TierPrice);
        Assert.Equal(2700m, contract.PricingTiers.Single(t => t.DurationMonths == 3).TierPrice);
        Assert.Equal(5220m, contract.PricingTiers.Single(t => t.DurationMonths == 6).TierPrice);

        // Period = Offer DurationMonths + BonusMonths (12 + 2), not 12 + 7
        Assert.Equal(TenantPlan.ComputeEffectiveEndsAtUtc(FixedNow, 12, 2), contract.EndsAtUtc);
    }

    [Fact]
    public async Task PlanMutation_AfterOfferAcceptance_SubscriptionInheritsContractOnly()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out var tenant);

        var plan = SeedPlan(
            db, 7002,
            bonusMonths: 2, maxStudents: 100, maxUsers: 10, maxBranches: 2,
            maxTeachers: 20, storageGb: 50, smsQuota: 1000,
            tiers: [(12, 10000m)],
            featureIds: [801, 802]);

        var calculateHandler = new CalculateAndPersistOfferHandler(
            db, new PromotionCalculationService(), tenant);
        var offerResult = await calculateHandler.Handle(
            new CalculateAndPersistOfferCommand(plan.Id, 12, FixedNow), CancellationToken.None);
        Assert.True(offerResult.IsSuccess);

        var acceptHandler = new AcceptOfferHandler(db, tenant);
        Assert.True((await acceptHandler.Handle(
            new AcceptOfferCommand(offerResult.Value.Id), CancellationToken.None)).IsSuccess);

        // Mutate plan BEFORE contract creation (accepted Offer is already frozen)
        MutatePlan(db, plan, [803, 804]);

        var contractHandler = new CreateContractFromOfferHandler(db, tenant);
        var contractId = await contractHandler.Handle(
            new CreateContractFromOfferCommand(offerResult.Value.Id, "CTR-SUB-SNAPSHOT", FixedNow),
            CancellationToken.None);
        Assert.True(contractId.IsSuccess);

        var contract = db.Contracts.First(c => c.Id == contractId.Value);
        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.True(contract.Activate(FixedNow).IsSuccess);
        db.SaveChanges();

        // Real subscription creation from the Contract
        var subHandler = new CreateSubscriptionFromContractHandler(
            db, AllowPlatformAdmin(), new SubscriptionFactory(db),
            Substitute.For<IAuditWriter>(), FixedTimeProvider());
        var subResult = await subHandler.Handle(
            new CreateSubscriptionFromContractCommand(contract.Id), CancellationToken.None);
        Assert.True(subResult.IsSuccess);

        // Subscription inherits from Contract only — not from the mutated Plan
        var sub = db.TenantPlans.First(s => s.ContractId == contract.Id);
        Assert.Equal(2, sub.BonusMonths);
        Assert.Equal(100, sub.SnapshotMaxStudents);
        Assert.Equal(10, sub.SnapshotMaxUsers);
        Assert.Equal(2, sub.SnapshotMaxBranches);
        Assert.Equal(20, sub.SnapshotMaxTeachers);
        Assert.Equal(50, sub.SnapshotStorageGb);
        Assert.Equal(1000, sub.SnapshotSmsQuota);
        Assert.Equal(contract.EffectiveAtUtc, sub.StartsAtUtc);
        Assert.Equal(contract.EndsAtUtc, sub.EffectiveEndsAtUtc);

        var subFeatureCodes = db.TenantPlanFeatures
            .Where(f => f.TenantPlanId == sub.Id)
            .Select(f => f.FeatureCode)
            .OrderBy(c => c)
            .ToList();
        Assert.Equal(["FEATURE_801", "FEATURE_802"], subFeatureCodes);
    }

    [Fact]
    public async Task LegacyOfferWithoutSnapshot_CannotBeConvertedToContract()
    {
        var tenantId = Guid.NewGuid().ToString();
        using var db = CreateDb(tenantId, out var tenant);

        var plan = SeedPlan(
            db, 7003,
            bonusMonths: 2, maxStudents: 100, maxUsers: 10, maxBranches: 2,
            maxTeachers: 20, storageGb: 50, smsQuota: 1000,
            tiers: [(12, 10000m)],
            featureIds: [901]);

        // Version-0 offer (legacy row with no entitlement snapshot) — never fabricated
        var offer = Offer.Create(
            Guid.NewGuid(), tenantId, plan.Id, 12,
            12000m, 0m, 12000m, 1000m, "EGP",
            calculatedAtUtc: FixedNow,
            expiresAtUtc: FixedNow.AddDays(1)).Value;
        Assert.True(offer.Accept(FixedNow).IsSuccess);
        db.Offers.Add(offer);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        var contractHandler = new CreateContractFromOfferHandler(db, tenant);
        var result = await contractHandler.Handle(
            new CreateContractFromOfferCommand(offer.Id, "CTR-LEGACY-OFFER", FixedNow),
            CancellationToken.None);

        // Historical values cannot be reconstructed from the mutable Plan —
        // the conversion must fail rather than fabricate commercial data.
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Offer.IncompleteSnapshot");
    }
}