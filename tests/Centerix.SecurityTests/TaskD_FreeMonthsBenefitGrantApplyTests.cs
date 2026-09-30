namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// Task D — FreeMonthsBenefit Grant &amp; Apply Domain Unit Tests.
///
/// Exercises the domain model transitions for:
///   1. Grant: Pending → Granted (and idempotent/terminal guards)
///   2. Apply: Granted → AppliedToSubscription (and idempotent/terminal guards)
///   3. Cross-state independence: Eligibility remains reversible after Grant/Apply
///
/// These are pure domain tests — no handler, no database.
///
/// See Task C foundation tests for: construction, eligibility, fulfillment lifecycle,
/// and immutability of commercial fields.
/// </summary>
public class TaskD_FreeMonthsBenefitGrantApplyTests
{
    private static readonly Guid ContractId = Guid.NewGuid();
    private const string Currency = "EGP";

    private static EligibilityRule DefaultUpfrontBonusRule(decimal contractedAmount = 5000m) =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(contractedAmount),
            EligibilityRule.NoOverdueInstallment());

    private static FreeMonthsBenefit NewBenefit(int entitlementMonths = 1) =>
        FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: ContractId,
            entitlementMonths: entitlementMonths,
            currencyCode: Currency,
            eligibilityRule: DefaultUpfrontBonusRule()).Value;

    // ═══════════════════════════════════════════════════════════════════
    // 1. Grant — Happy Path
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TestD01: Pending + Eligible → Granted</summary>
    [Fact]
    public void TestD01_Grant_FromEligiblePending_TransitionsToGranted()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t); // Eligible is prerequisite for Grant

        var result = benefit.Grant(t);

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.Equal(t, benefit.GrantedAtUtc);
        Assert.True(benefit.IsGranted);
        Assert.False(benefit.IsAppliedToSubscription);
    }

    /// <summary>TestD02: Grant stamps GrantedAtUtc</summary>
    [Fact]
    public void TestD02_Grant_StampsGrantedAtUtc()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);

        benefit.Grant(t2);

        Assert.Equal(t2, benefit.GrantedAtUtc);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. Grant — Rejection Guards
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TestD03: Pending + NotEligible → Grant rejected</summary>
    [Fact]
    public void TestD03_Grant_FromNotEligible_IsRejected()
    {
        // Benefit starts as NotEligible (NewBenefit helper creates NotEligible)
        var benefit = NewBenefit();

        var result = benefit.Grant(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotEligible", result.Errors!.First().Code);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);
        Assert.Null(benefit.GrantedAtUtc);
    }

    /// <summary>TestD04: Already Granted → Grant idempotent (success, no re-grant)</summary>
    [Fact]
    public void TestD04_Grant_Idempotent_OnAlreadyGranted_PreservesTimestamp()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);
        benefit.Grant(t1); // First grant

        var result = benefit.Grant(t2); // Retry

        Assert.True(result.IsSuccess);
        Assert.Equal(t1, benefit.GrantedAtUtc); // Original timestamp preserved
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus);
    }

    /// <summary>TestD05: AppliedToSubscription → Grant is no-op (idempotent on terminal)</summary>
    [Fact]
    public void TestD05_Grant_Idempotent_OnAppliedToSubscription_NoStateChange()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);
        benefit.MarkAppliedToSubscription(t);

        var result = benefit.Grant(t);

        Assert.True(result.IsSuccess); // Domain model returns Updated (idempotent)
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
        Assert.Equal(t, benefit.GrantedAtUtc);
        Assert.Equal(t, benefit.AppliedAtUtc);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. Grant — Invariants (no mutation of commercial fields)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TestD06: Grant does NOT modify EntitlementMonths</summary>
    [Fact]
    public void TestD06_Grant_DoesNotModifyEntitlementMonths()
    {
        var benefit = NewBenefit(entitlementMonths: 3);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);

        benefit.Grant(t);

        Assert.Equal(3, benefit.EntitlementMonths);
    }

    /// <summary>TestD07: Grant does NOT modify EligibilityRule</summary>
    [Fact]
    public void TestD07_Grant_DoesNotModifyEligibilityRule()
    {
        var rule = DefaultUpfrontBonusRule(7500m);
        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), ContractId, 2, "EGP", rule).Value;
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);

        benefit.Grant(t);

        Assert.Equal(rule, benefit.EligibilityRule);
    }

    /// <summary>TestD08: Grant does NOT modify EligibilityStatus</summary>
    [Fact]
    public void TestD08_Grant_DoesNotModifyEligibilityStatus()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t); // Now Eligible

        benefit.Grant(t);

        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, benefit.EligibilityStatus);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. Apply — Happy Path
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TestD09: Granted → AppliedToSubscription</summary>
    [Fact]
    public void TestD09_Apply_FromGranted_TransitionsToAppliedToSubscription()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);

        var result = benefit.MarkAppliedToSubscription(t);

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
        Assert.Equal(t, benefit.AppliedAtUtc);
        Assert.True(benefit.IsAppliedToSubscription);
        Assert.True(benefit.IsGranted); // AppliedToSubscription implies Granted
    }

    /// <summary>TestD10: Apply stamps AppliedAtUtc</summary>
    [Fact]
    public void TestD10_Apply_StampsAppliedAtUtc()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);
        benefit.Grant(t1);

        benefit.MarkAppliedToSubscription(t2);

        Assert.Equal(t2, benefit.AppliedAtUtc);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. Apply — Rejection Guards
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TestD11: Pending → Apply rejected (benefit must be Granted first)</summary>
    [Fact]
    public void TestD11_Apply_FromPending_IsRejected()
    {
        var benefit = NewBenefit(); // Starts Pending

        var result = benefit.MarkAppliedToSubscription(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotGranted", result.Errors!.First().Code);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);
    }

    /// <summary>TestD12: AppliedToSubscription → Apply idempotent (success, no re-apply)</summary>
    [Fact]
    public void TestD12_Apply_Idempotent_OnAlreadyApplied_PreservesTimestamp()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);
        benefit.Grant(t1);
        benefit.MarkAppliedToSubscription(t1); // First apply

        var result = benefit.MarkAppliedToSubscription(t2); // Retry

        Assert.True(result.IsSuccess);
        Assert.Equal(t1, benefit.AppliedAtUtc); // Original timestamp preserved
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Apply — Invariants
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TestD13: Apply does NOT modify EntitlementMonths</summary>
    [Fact]
    public void TestD13_Apply_DoesNotModifyEntitlementMonths()
    {
        var benefit = NewBenefit(entitlementMonths: 4);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);

        benefit.MarkAppliedToSubscription(t);

        Assert.Equal(4, benefit.EntitlementMonths);
    }

    /// <summary>TestD14: Apply does NOT modify EligibilityStatus (independence)</summary>
    [Fact]
    public void TestD14_Apply_DoesNotModifyEligibilityStatus()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);

        benefit.MarkAppliedToSubscription(t);

        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, benefit.EligibilityStatus);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. Fulfillment Lifecycle — Monotonicity
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TestD15: Full monotonic lifecycle: Pending → Granted → AppliedToSubscription</summary>
    [Fact]
    public void TestD15_Lifecycle_Monotonic_Pending_Granted_AppliedToSubscription()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);

        benefit.MarkEligible(t1);
        benefit.Grant(t1);
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus);

        benefit.MarkAppliedToSubscription(t2);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);

        // Verify all timestamps
        Assert.Equal(t1, benefit.GrantedAtUtc);
        Assert.Equal(t2, benefit.AppliedAtUtc);

        // No backwards transitions possible
        // Grant on AppliedToSubscription is a no-op (returns Updated/success) — no backwards transition to Pending
        Assert.True(benefit.Grant(t3).IsSuccess); // Domain allows Grant as no-op on terminal state
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
    }

    /// <summary>TestD16: Eligibility remains reversible after Grant</summary>
    [Fact]
    public void TestD16_Eligibility_RemainsReversible_AfterGrant()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);

        // Eligibility must still be reversible after grant
        var result = benefit.MarkNotEligible();

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus); // Fulfillment unchanged
        Assert.Equal(t, benefit.GrantedAtUtc); // Grant timestamp preserved
    }

    /// <summary>TestD17: Eligibility remains reversible after Apply (terminal state)</summary>
    [Fact]
    public void TestD17_Eligibility_RemainsReversible_AfterApply()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);
        benefit.MarkAppliedToSubscription(t);

        // Eligibility must still be reversible after application
        var result = benefit.MarkNotEligible();

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus); // Terminal
        Assert.Equal(t, benefit.GrantedAtUtc);
        Assert.Equal(t, benefit.AppliedAtUtc);
    }
}
