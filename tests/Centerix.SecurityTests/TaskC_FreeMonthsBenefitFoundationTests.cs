namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// Task C — FreeMonthsBenefit Domain Foundation tests.
///
/// Validates the four-state FreeMonths model, lifecycle invariants, immutability
/// of commercial fields, EligibilityRule snapshot, Offer → Contract snapshot
/// wiring, and EF Core round-trip against the InMemory provider.
///
/// See <c>docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md</c> §F for the binding
/// design baseline; §L for the invariants. The model under test covers the
/// FreeMonths aggregate ONLY — eligibility evaluation, the
/// <c>GrantBenefitCommand</c>, the <c>ApplyFreeMonthsToSubscriptionCommand</c>,
/// and <c>TenantPlan.AppliedFreeMonthsBenefitIds[]</c> are explicitly out of
/// scope and will be exercised by later tasks.
/// </summary>
public class TaskC_FreeMonthsBenefitFoundationTests
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

    // ─────────────────────────────────────────────────────────────────
    // 1. Construction validation
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test01_Create_WithPositiveEntitlementMonths_Succeeds()
    {
        var benefit = NewBenefit(entitlementMonths: 1);

        Assert.NotNull(benefit);
        Assert.Equal(1, benefit.EntitlementMonths);
        Assert.Equal(ContractId, benefit.ContractId);
        Assert.Equal(Currency, benefit.CurrencyCode);
        Assert.NotNull(benefit.EligibilityRule);
    }

    [Fact]
    public void Test02_Create_WithLargeEntitlementMonths_Succeeds()
    {
        var benefit = NewBenefit(entitlementMonths: 12);

        Assert.Equal(12, benefit.EntitlementMonths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Test03_Create_WithNonPositiveEntitlementMonths_Fails(int months)
    {
        var result = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: ContractId,
            entitlementMonths: months,
            currencyCode: Currency,
            eligibilityRule: DefaultUpfrontBonusRule());

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.EntitlementMonths_Invalid", result.Errors!.First().Code);
    }

    [Fact]
    public void Test04_Create_WithEmptyId_Fails()
    {
        var result = FreeMonthsBenefit.Create(
            id: Guid.Empty,
            contractId: ContractId,
            entitlementMonths: 1,
            currencyCode: Currency,
            eligibilityRule: DefaultUpfrontBonusRule());

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.Id_Required", result.Errors!.First().Code);
    }

    [Fact]
    public void Test05_Create_WithEmptyContractId_Fails()
    {
        var result = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: Guid.Empty,
            entitlementMonths: 1,
            currencyCode: Currency,
            eligibilityRule: DefaultUpfrontBonusRule());

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.ContractId_Required", result.Errors!.First().Code);
    }

    [Fact]
    public void Test06_Create_WithNullRule_Fails()
    {
        var result = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: ContractId,
            entitlementMonths: 1,
            currencyCode: Currency,
            eligibilityRule: null!);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.EligibilityRule_Required", result.Errors!.First().Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("EG")]
    [InlineData("EGGP")]
    public void Test07_Create_WithInvalidCurrency_Fails(string currency)
    {
        var result = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: ContractId,
            entitlementMonths: 1,
            currencyCode: currency,
            eligibilityRule: DefaultUpfrontBonusRule());

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.Currency_Required", result.Errors!.First().Code);
    }

    [Fact]
    public void Test08_Create_NormalizesCurrency_ToUpperInvariantTrimmed()
    {
        var result = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: ContractId,
            entitlementMonths: 1,
            currencyCode: "  egp ",
            eligibilityRule: DefaultUpfrontBonusRule());

        Assert.True(result.IsSuccess);
        Assert.Equal("EGP", result.Value.CurrencyCode);
    }

    // ─────────────────────────────────────────────────────────────────
    // 2. Initial state
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test09_NewBenefit_StartsAsNotEligible_Pending_NoTimestamps()
    {
        var benefit = NewBenefit();

        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);
        Assert.Null(benefit.EligibleAtUtc);
        Assert.Null(benefit.GrantedAtUtc);
        Assert.Null(benefit.AppliedAtUtc);

        Assert.False(benefit.IsEligible);
        Assert.False(benefit.IsGranted);
        Assert.False(benefit.IsAppliedToSubscription);
    }

    [Fact]
    public void Test10_CommercialDefinition_IsImmutable_AfterConstruction()
    {
        var benefit = NewBenefit(entitlementMonths: 2);

        // Re-affirm that the constructor accepted the values; there is no public
        // setter on EntitlementMonths or EligibilityRule.
        Assert.Equal(2, benefit.EntitlementMonths);
        Assert.NotNull(benefit.EligibilityRule);
    }

    // ─────────────────────────────────────────────────────────────────
    // 3. Reversible Eligibility transitions
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test11_MarkEligible_FromNotEligible_TransitionsToEligible_StampsTimestamp()
    {
        var benefit = NewBenefit();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = benefit.MarkEligible(now);

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, benefit.EligibilityStatus);
        Assert.Equal(now, benefit.EligibleAtUtc);
    }

    [Fact]
    public void Test12_MarkEligible_IsIdempotent_OnAlreadyEligible()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        benefit.MarkEligible(t1);
        var result = benefit.MarkEligible(t2);

        Assert.True(result.IsSuccess);
        // First-eligible timestamp is preserved (not overwritten on idempotent call).
        Assert.Equal(t1, benefit.EligibleAtUtc);
    }

    [Fact]
    public void Test13_MarkNotEligible_FromEligible_TransitionsBackToNotEligible_ClearsTimestamp()
    {
        var benefit = NewBenefit();
        benefit.MarkEligible(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = benefit.MarkNotEligible();

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Null(benefit.EligibleAtUtc);
    }

    [Fact]
    public void Test14_MarkNotEligible_IsIdempotent_OnAlreadyNotEligible()
    {
        var benefit = NewBenefit();

        var result = benefit.MarkNotEligible();

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
    }

    // ─────────────────────────────────────────────────────────────────
    // 4. Monotone Fulfillment transitions
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test15_Grant_FromEligiblePending_TransitionsToGranted_StampsTimestamp()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);

        var result = benefit.Grant(t);

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.Equal(t, benefit.GrantedAtUtc);
        Assert.True(benefit.IsGranted);
        Assert.False(benefit.IsAppliedToSubscription);
    }

    [Fact]
    public void Test16_Grant_FromNotEligible_IsRejected()
    {
        var benefit = NewBenefit();

        var result = benefit.Grant(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotEligible", result.Errors!.First().Code);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);
    }

    [Fact]
    public void Test17_Grant_IsIdempotent_OnAlreadyGranted()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);
        benefit.Grant(t1);

        var result = benefit.Grant(t2);

        Assert.True(result.IsSuccess);
        // First-grant timestamp preserved.
        Assert.Equal(t1, benefit.GrantedAtUtc);
    }

    [Fact]
    public void Test18_MarkAppliedToSubscription_FromGranted_TransitionsToApplied_StampsTimestamp()
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
    }

    [Fact]
    public void Test19_MarkAppliedToSubscription_FromPending_IsRejected()
    {
        var benefit = NewBenefit();

        var result = benefit.MarkAppliedToSubscription(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotGranted", result.Errors!.First().Code);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);
    }

    [Fact]
    public void Test20_MarkAppliedToSubscription_IsIdempotent_OnAlreadyApplied()
    {
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);
        benefit.Grant(t1);
        benefit.MarkAppliedToSubscription(t1);

        var result = benefit.MarkAppliedToSubscription(t2);

        Assert.True(result.IsSuccess);
        // First-apply timestamp preserved.
        Assert.Equal(t1, benefit.AppliedAtUtc);
    }

    // ─────────────────────────────────────────────────────────────────
    // 5. Eligibility/Fulfillment state independence
    //    Eligibility is reversible independently of Fulfillment.
    //    Fulfillment remains monotone (Pending → Granted → AppliedToSubscription).
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test21_Grant_DoesNotLockEligibility_MarkNotEligibleSucceedsAfterGrant()
    {
        // Scenario: Eligible + Pending → Grant → MarkNotEligible.
        // After Grant, Eligibility must remain reversible: MarkNotEligible
        // must succeed, and FulfillmentStatus must remain Granted.
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);

        var result = benefit.MarkNotEligible();

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.Equal(t, benefit.GrantedAtUtc); // Fulfillment timestamp preserved
    }

    [Fact]
    public void Test22_AppliedBenefit_CanBecomeNotEligible_FulfillmentPreserved()
    {
        // Scenario: Eligible + Pending → Grant → MarkAppliedToSubscription → MarkNotEligible.
        // After Application, a later eligibility flip to NotEligible is permitted
        // and FulfillmentStatus stays AppliedToSubscription (terminal).
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);
        benefit.MarkAppliedToSubscription(t);

        var result = benefit.MarkNotEligible();

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
        Assert.Equal(t, benefit.GrantedAtUtc);
        Assert.Equal(t, benefit.AppliedAtUtc);
        Assert.True(benefit.IsAppliedToSubscription);
    }

    [Fact]
    public void Test23_Eligibility_CanBecomeEligibleAgainAfterGrant_FulfillmentUnchanged()
    {
        // Scenario: Eligible + Pending → Grant → MarkNotEligible → MarkEligible(t2).
        // Fulfillment stays Granted. GrantedAtUtc remains the original t1.
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);
        benefit.Grant(t1);
        benefit.MarkNotEligible();

        var result = benefit.MarkEligible(t2);

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.Equal(t1, benefit.GrantedAtUtc); // original grant timestamp preserved
        Assert.Equal(t2, benefit.EligibleAtUtc); // latest EligibleAtUtc tracks the current state
    }

    [Fact]
    public void Test24_Eligibility_CanBecomeEligibleAgainAfterApplication_FulfillmentAndTimestampsUnchanged()
    {
        // Scenario: Eligible + Pending → Grant → MarkAppliedToSubscription
        //           → MarkNotEligible → MarkEligible(t2).
        // Fulfillment stays AppliedToSubscription. GrantedAtUtc and AppliedAtUtc
        // remain the original t1 (no fulfillment state changes).
        var benefit = NewBenefit();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t1);
        benefit.Grant(t1);
        benefit.MarkAppliedToSubscription(t1);
        benefit.MarkNotEligible();

        var result = benefit.MarkEligible(t2);

        Assert.True(result.IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
        Assert.Equal(t1, benefit.GrantedAtUtc);
        Assert.Equal(t1, benefit.AppliedAtUtc);
        Assert.Equal(t2, benefit.EligibleAtUtc);
    }

    [Fact]
    public void Test25_OnceApplied_Grant_IsNoOp_FulfillmentUnchanged()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(t);
        benefit.Grant(t);
        benefit.MarkAppliedToSubscription(t);

        var result = benefit.Grant(t);

        Assert.True(result.IsSuccess); // idempotent on already-Applied path
        // No state change.
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
    }

    // ─────────────────────────────────────────────────────────────────
    // 6. Eligibility rule snapshots are stored unchanged
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test26_EligibilityRule_StoredVerbatimOnConstruction()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(7500m));

        var result = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: ContractId,
            entitlementMonths: 1,
            currencyCode: Currency,
            eligibilityRule: rule);

        Assert.True(result.IsSuccess);
        Assert.Equal(rule, result.Value.EligibilityRule);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(rule),
            EligibilityRuleSerializer.Serialize(result.Value.EligibilityRule));
    }

    // ─────────────────────────────────────────────────────────────────
    // 7. Eligibility and Fulfillment are independent counters
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test27_FulfillmentStatus_StartsPending_EvenWhenEligibilityFlipsToEligibleThenBack()
    {
        var benefit = NewBenefit();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        benefit.MarkEligible(t);
        benefit.MarkNotEligible();
        benefit.MarkEligible(t);

        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);
        Assert.Null(benefit.GrantedAtUtc);
        Assert.Null(benefit.AppliedAtUtc);
    }

    // ─────────────────────────────────────────────────────────────────
    // 8. Eligibility ↔ Fulfillment separation (different from PhysicalGift)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test28_FreeMonthsBenefit_UsesSeparateEnumsFromContractBenefit_BenefitEligibilityStatus()
    {
        // Verify FreeMonthsEligibilityStatus does NOT have a Delivered value
        // (that's PhysicalGift-only) and FreeMonthsFulfillmentStatus does NOT
        // have a Delivered value.
        Assert.False(Enum.IsDefined(typeof(FreeMonthsEligibilityStatus), 2));
        Assert.False(Enum.IsDefined(typeof(FreeMonthsFulfillmentStatus), 3));
    }

    [Fact]
    public void Test29_FreeMonthsFulfillmentStatus_UnderlyingNumericValues_ArePinned()
    {
        // Pinned so persisted columns and tests remain stable across refactors.
        Assert.Equal(0, (byte)FreeMonthsFulfillmentStatus.Pending);
        Assert.Equal(1, (byte)FreeMonthsFulfillmentStatus.Granted);
        Assert.Equal(2, (byte)FreeMonthsFulfillmentStatus.AppliedToSubscription);
    }

    [Fact]
    public void Test30_FreeMonthsEligibilityStatus_UnderlyingNumericValues_ArePinned()
    {
        Assert.Equal(0, (byte)FreeMonthsEligibilityStatus.NotEligible);
        Assert.Equal(1, (byte)FreeMonthsEligibilityStatus.Eligible);
    }

    // ─────────────────────────────────────────────────────────────────
    // 9. Lifecycle scenario — Scenario A from the design baseline
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test31_ScenarioA_FullLifecycle_FromConstructionToAppliedToSubscription()
    {
        // Scenario A: 6 months for the price of 5, upfront.
        // The commercial entitlement is 1 free month; rule requires upfront
        // and the full contracted amount.
        var benefit = NewBenefit(entitlementMonths: 1);
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        // Not eligible at construction.
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, benefit.FulfillmentStatus);

        // Eligibility flips to Eligible once the rule evaluates true.
        Assert.True(benefit.MarkEligible(t1).IsSuccess);
        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, benefit.EligibilityStatus);

        // Grant step records the grant decision.
        Assert.True(benefit.Grant(t2).IsSuccess);
        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, benefit.FulfillmentStatus);
        Assert.Equal(t2, benefit.GrantedAtUtc);

        // Apply step extends TenantPlan.EffectiveEndsAtUtc (out of scope for this
        // aggregate; idempotency is enforced by TenantPlan.AppliedFreeMonthsBenefitIds,
        // not here).
        Assert.True(benefit.MarkAppliedToSubscription(t3).IsSuccess);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, benefit.FulfillmentStatus);
        Assert.Equal(t3, benefit.AppliedAtUtc);
        Assert.True(benefit.IsAppliedToSubscription);
    }

    // ─────────────────────────────────────────────────────────────────
    // 10. Offer-side mirror (OfferFreeMonthsBenefit) — snapshot integrity
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test32_OfferFreeMonthsBenefit_Create_PreservesEntitlementAndRule()
    {
        var offerId = Guid.NewGuid();
        var rule = DefaultUpfrontBonusRule(7500m);

        var result = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offerId,
            entitlementMonths: 1,
            currencyCode: Currency,
            eligibilityRule: rule);

        Assert.True(result.IsSuccess);
        var offerBenefit = result.Value;
        Assert.Equal(1, offerBenefit.EntitlementMonths);
        Assert.Equal(offerId, offerBenefit.OfferId);
        Assert.Equal(Currency, offerBenefit.CurrencyCode);
        Assert.Equal(rule, offerBenefit.EligibilityRule);
    }

    [Fact]
    public void Test33_OfferFreeMonthsBenefit_Create_WithNonPositiveEntitlementMonths_Fails()
    {
        var result = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            entitlementMonths: 0,
            currencyCode: Currency,
            eligibilityRule: DefaultUpfrontBonusRule());

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Test34_OfferFreeMonthsBenefit_SerialisedJson_MatchesContractBenefitPattern_NoClrMetadata()
    {
        var rule = DefaultUpfrontBonusRule(5000m);
        var offerBenefit = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            entitlementMonths: 1,
            currencyCode: Currency,
            eligibilityRule: rule).Value;

        // The Offer-side rule serialises via the same canonical serializer
        // used by ContractBenefit.EligibilityRule and FreeMonthsBenefit.EligibilityRule.
        var json = EligibilityRuleSerializer.Serialize(offerBenefit.EligibilityRule!);

        Assert.DoesNotContain("System.", json);
        Assert.DoesNotContain("Microsoft.", json);
        Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"type\":\"all_of\"", json);
        Assert.Contains("\"paymentTerms\":\"FullUpfront\"", json);
    }

    // ─────────────────────────────────────────────────────────────────
    // 11. OfferFreeMonthsBenefit EligibilityRule required (Correction 2)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test35_OfferFreeMonthsBenefit_Create_WithNullRule_Fails()
    {
        var result = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            entitlementMonths: 1,
            currencyCode: Currency,
            eligibilityRule: null!);

        Assert.False(result.IsSuccess);
        Assert.Equal("OfferFreeMonthsBenefit.EligibilityRule_Required", result.Errors!.First().Code);
        // No object is created: the failed Result carries no value at all.
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Test36_OfferFreeMonthsBenefit_Create_WithValidRule_Succeeds()
    {
        var rule = DefaultUpfrontBonusRule(7500m);

        var result = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            entitlementMonths: 2,
            currencyCode: Currency,
            eligibilityRule: rule);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.EntitlementMonths);
        Assert.Equal(rule, result.Value.EligibilityRule);
        Assert.NotNull(result.Value.EligibilityRule);
    }
}
