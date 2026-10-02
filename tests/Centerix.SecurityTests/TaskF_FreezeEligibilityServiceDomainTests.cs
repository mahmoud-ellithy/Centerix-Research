namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// Domain tests for the Task F Eligibility Rule Algebra evaluator.
///
/// Covers:
///   - EligibilityContext invariants
///   - Each primitive rule's pass / fail path
///   - Composite (AllOf / AnyOf) short-circuit semantics
///   - Reason code stability
///   - EligibilityRule.IsEligible(context) public surface
/// </summary>
public class TaskF_FreezeEligibilityServiceDomainTests
{
    private const string TenantId = "tenant-f-domain";
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly Guid BenefitId = Guid.NewGuid();

    private static readonly DateTime UtcNow = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

    private static EligibilityContext NewContext(
        ContractStatus status = ContractStatus.Active,
        PaymentTerms terms = PaymentTerms.Installments,
        string? paymentMethod = "CARD",
        decimal amountPaid = 0m,
        decimal contractedAmount = 1000m,
        int daysFromStart = 0,
        int durationMonths = 12,
        bool hasOverdueInstallment = false,
        DateTime? completedByUtc = null)
    {
        return EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: status,
            paymentTerms: terms,
            paymentMethod: paymentMethod,
            utcNow: UtcNow,
            amountPaid: amountPaid,
            contractedAmount: contractedAmount,
            daysFromContractStart: daysFromStart,
            contractDurationMonths: durationMonths,
            hasOverdueInstallment: hasOverdueInstallment,
            completedByUtc: completedByUtc);
    }

    // ──────────────── Context construction guards ────────────────

    [Fact]
    public void TestF01_Context_RejectsBlankTenantId()
    {
        var ex = Assert.Throws<ArgumentException>(() => EligibilityContext.Create(
            tenantId: "",
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            paymentMethod: null,
            utcNow: UtcNow,
            amountPaid: 0m,
            contractedAmount: 0m,
            daysFromContractStart: 0,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Contains("TenantId", ex.Message);
    }

    [Fact]
    public void TestF02_Context_RejectsEmptyContractId()
    {
        var ex = Assert.Throws<ArgumentException>(() => EligibilityContext.Create(
            tenantId: TenantId,
            contractId: Guid.Empty,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            paymentMethod: null,
            utcNow: UtcNow,
            amountPaid: 0m,
            contractedAmount: 0m,
            daysFromContractStart: 0,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Contains("ContractId", ex.Message);
    }

    [Fact]
    public void TestF03_Context_RejectsNonUtcNow()
    {
        var ex = Assert.Throws<ArgumentException>(() => EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            paymentMethod: null,
            utcNow: new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Unspecified),
            amountPaid: 0m,
            contractedAmount: 0m,
            daysFromContractStart: 0,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Contains("UtcNow", ex.Message);
    }

    [Fact]
    public void TestF04_Context_RejectsNegativeAmountPaid()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            paymentMethod: null,
            utcNow: UtcNow,
            amountPaid: -1m,
            contractedAmount: 0m,
            daysFromContractStart: 0,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Equal("amountPaid", ex.ParamName);
    }

    [Fact]
    public void TestF05_Context_Properties_BagIsImmutable()
    {
        var mutableBag = new Dictionary<string, object> { ["k"] = 1 };
        var ctx = EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            paymentMethod: null,
            utcNow: UtcNow,
            amountPaid: 0m,
            contractedAmount: 0m,
            daysFromContractStart: 0,
            contractDurationMonths: 12,
            hasOverdueInstallment: false,
            properties: mutableBag);

        // Mutating the source dictionary does NOT mutate the context's view.
        mutableBag["k"] = 999;
        Assert.Equal(1, ctx.Properties["k"]);
    }

    // ──────────────── Primitive rules ────────────────

    [Fact]
    public void TestF10_ContractActive_True_WhenActive()
    {
        var rule = EligibilityRule.ContractActive();
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(status: ContractStatus.Active));
        Assert.True(result.IsEligible);
        Assert.Equal(WhyIneligible.None, result.Reason);
        Assert.Null(result.ReasonPath);
    }

    [Fact]
    public void TestF11_ContractActive_False_ReasonContractNotActive()
    {
        var rule = EligibilityRule.ContractActive();
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(status: ContractStatus.Suspended));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.ContractNotActive, result.Reason);
        Assert.Equal("ContractActive", result.ReasonPath);
    }

    [Fact]
    public void TestF12_NoOverdueInstallment_True_WhenNoOverdue()
    {
        var rule = EligibilityRule.NoOverdueInstallment();
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(hasOverdueInstallment: false));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF13_NoOverdueInstallment_False_ReasonOverdue()
    {
        var rule = EligibilityRule.NoOverdueInstallment();
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(hasOverdueInstallment: true));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.OverdueInstallment, result.Reason);
        Assert.Equal("NoOverdueInstallment", result.ReasonPath);
    }

    [Fact]
    public void TestF14_PaymentTermsEquals_True_WhenMatch()
    {
        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(terms: PaymentTerms.FullUpfront));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF15_PaymentTermsEquals_False_ReasonPaymentTermsMismatch()
    {
        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(terms: PaymentTerms.Installments));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.PaymentTermsMismatch, result.Reason);
        Assert.Equal("PaymentTermsEquals(FullUpfront)", result.ReasonPath);
    }

    [Fact]
    public void TestF16_PaymentMethodEquals_True_WhenMatch_CaseInsensitive()
    {
        var rule = EligibilityRule.PaymentMethodEquals("cash");
        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(paymentMethod: "  CASH "));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF17_PaymentMethodEquals_False_ReasonPaymentMethodMismatch()
    {
        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(paymentMethod: "CASH"));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.PaymentMethodMismatch, result.Reason);
        Assert.Equal("PaymentMethodEquals(\"CARD\")", result.ReasonPath);
    }

    [Fact]
    public void TestF18_CompletedByUtc_True_WhenBeforeDeadline()
    {
        var deadline = UtcNow.AddDays(1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(completedByUtc: deadline));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF19_CompletedByUtc_False_AfterDeadline_ReasonDeadlinePassed()
    {
        var deadline = UtcNow.AddMinutes(-1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(completedByUtc: deadline));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.DeadlinePassed, result.Reason);
    }

    [Fact]
    public void TestF20_AmountPaidAtLeast_True_WhenPaidEqualsAmount()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(amountPaid: 1000m));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF21_AmountPaidAtLeast_False_ReasonAmountBelowMinimum()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(amountPaid: 999.99m));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.AmountBelowMinimum, result.Reason);
    }

    [Fact]
    public void TestF22_DaysFromContractStartGte_True_WhenMet()
    {
        var rule = EligibilityRule.DaysFromContractStartGte(30);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(daysFromStart: 30));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF23_DaysFromContractStartGte_False_ReasonDaysNotMet()
    {
        var rule = EligibilityRule.DaysFromContractStartGte(30);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(daysFromStart: 29));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.DaysFromContractStartNotMet, result.Reason);
    }

    [Fact]
    public void TestF24_DurationMonthsGte_True_WhenMet()
    {
        var rule = EligibilityRule.DurationMonthsGte(12);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(durationMonths: 12));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF25_DurationMonthsGte_False_ReasonDurationNotMet()
    {
        var rule = EligibilityRule.DurationMonthsGte(12);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(durationMonths: 11));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.DurationMonthsNotMet, result.Reason);
    }

    // ──────────────── Composites ────────────────

    [Fact]
    public void TestF30_AllOf_True_WhenAllChildrenPass()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(1000m),
            EligibilityRule.NoOverdueInstallment());

        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(amountPaid: 1000m));

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF31_AllOf_False_ShortCircuitOnFirstFailure_WithPath()
    {
        // Composite contains 3 rules. The middle one fails; reason path must point at AllOf[1].
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(1000m),
            EligibilityRule.NoOverdueInstallment());

        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(amountPaid: 500m));

        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.AmountBelowMinimum, result.Reason);
        Assert.Equal("AllOf[1].AmountPaidAtLeast(1000)", result.ReasonPath);
    }

    [Fact]
    public void TestF32_AnyOf_True_WhenAtLeastOneChildPasses()
    {
        var rule = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront));

        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(terms: PaymentTerms.FullUpfront));

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF33_AnyOf_False_ReasonAnyOfNoChildPassed()
    {
        var rule = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront));

        // ContractStatus is Active so we still need to satisfy AnyOf but terms mismatch both.
        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(terms: (PaymentTerms)(uint)999));

        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.AnyOfNoChildPassed, result.Reason);
    }

    [Fact]
    public void TestF34_NestedComposites()
    {
        // All-of(Active, Any-of(PaymentTerms FullUpfront, AmountPaidAtLeast(1000)))
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.AmountPaidAtLeast(1000m)));

        // AmountPaid matches but terms are Installments: AnyOf passes via AmountPaidAtLeast.
        var result = new EligibilityRuleEvaluator().Evaluate(
            rule,
            NewContext(terms: PaymentTerms.Installments, amountPaid: 1500m));
        Assert.True(result.IsEligible);
    }

    // ──────────────── EligibilityRule.IsEligible(context) surface ────────────────

    [Fact]
    public void TestF40_RuleIsEligible_MethodReturnsTrue_WhenAllPass()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(1000m));

        Assert.True(rule.IsEligible(NewContext(amountPaid: 1000m)));
    }

    [Fact]
    public void TestF41_RuleIsEligible_MethodReturnsFalse_WhenAnyFails()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(1000m));

        Assert.False(rule.IsEligible(NewContext(amountPaid: 0m)));
    }

    [Fact]
    public void TestF42_RuleIsEligible_DoesNotMutateContext()
    {
        var ctx = NewContext(amountPaid: 1000m);
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);

        // First call
        Assert.True(rule.IsEligible(ctx));
        // Mutating what would normally be a side-effecting input — none — yields identical result.
        Assert.True(rule.IsEligible(ctx));
    }

    // ──────────────── Determinism ────────────────

    [Fact]
    public void TestF50_Evaluator_IsDeterministic_AcrossInvocations()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.AmountPaidAtLeast(1000m));

        var ctx = NewContext(amountPaid: 1000m);

        var r1 = new EligibilityRuleEvaluator().Evaluate(rule, ctx);
        var r2 = new EligibilityRuleEvaluator().Evaluate(rule, ctx);
        var r3 = new EligibilityRuleEvaluator().Evaluate(rule, ctx);

        Assert.Equal(r1.IsEligible, r2.IsEligible);
        Assert.Equal(r1.IsEligible, r3.IsEligible);
        Assert.Equal(r1.Reason, r2.Reason);
        Assert.Equal(r1.Reason, r3.Reason);
    }
}