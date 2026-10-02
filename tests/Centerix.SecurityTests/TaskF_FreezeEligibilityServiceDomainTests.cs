namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// Domain tests for the Task F Eligibility Rule Algebra evaluator.
///
/// Covers:
///   - EligibilityContext invariants
///   - Each primitive rule's pass / fail path
///   - Boundary tests for every primitive
///   - Composite (AllOf / AnyOf) short-circuit semantics
///   - Reason code stability
///   - Currency-aware AmountPaidAtLeast
///   - 23:00→00:00 elapsed-time boundary for DaysFromContractStartGte
/// </summary>
public class TaskF_FreezeEligibilityServiceDomainTests
{
    private const string TenantId = "tenant-f-domain";
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly Guid BenefitId = Guid.NewGuid();

    // Anchored UTC instants for boundary tests.
    private static readonly DateTime UtcNow = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

    private static EligibilityContext NewContext(
        ContractStatus status = ContractStatus.Active,
        PaymentTerms terms = PaymentTerms.Installments,
        DateTime? contractStartUtc = null,
        string contractCurrencyCode = "EGP",
        decimal contractedAmount = 1000m,
        int durationMonths = 12,
        bool hasOverdueInstallment = false,
        params CompletedPaymentFact[] completedPayments)
    {
        return EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: status,
            paymentTerms: terms,
            utcNow: UtcNow,
            contractStartUtc: contractStartUtc ?? UtcNow.AddDays(-60),
            contractCurrencyCode: contractCurrencyCode,
            contractedAmount: contractedAmount,
            contractDurationMonths: durationMonths,
            hasOverdueInstallment: hasOverdueInstallment,
            completedPayments: completedPayments is null || completedPayments.Length == 0
                ? Array.Empty<CompletedPaymentFact>()
                : completedPayments);
    }

    private static EligibilityContext NewContextWithPayments(
        IReadOnlyList<CompletedPaymentFact> completedPayments,
        ContractStatus status = ContractStatus.Active,
        PaymentTerms terms = PaymentTerms.Installments,
        DateTime? contractStartUtc = null,
        string contractCurrencyCode = "EGP",
        decimal contractedAmount = 1000m,
        int durationMonths = 12,
        bool hasOverdueInstallment = false)
    {
        return EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: status,
            paymentTerms: terms,
            utcNow: UtcNow,
            contractStartUtc: contractStartUtc ?? UtcNow.AddDays(-60),
            contractCurrencyCode: contractCurrencyCode,
            contractedAmount: contractedAmount,
            contractDurationMonths: durationMonths,
            hasOverdueInstallment: hasOverdueInstallment,
            completedPayments: completedPayments);
    }

    private static CompletedPaymentFact Fact(DateTime completedAt, decimal amount = 1000m,
        string currencyCode = "EGP", string method = "CASH", Guid? paymentId = null)
        => new(paymentId ?? Guid.NewGuid(), completedAt, amount,
            currencyCode.Trim().ToUpperInvariant(),
            method is null ? null : method.Trim().ToUpperInvariant());

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
            utcNow: UtcNow,
            contractStartUtc: UtcNow.AddDays(-30),
            contractCurrencyCode: "EGP",
            contractedAmount: 1000m,
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
            utcNow: UtcNow,
            contractStartUtc: UtcNow.AddDays(-30),
            contractCurrencyCode: "EGP",
            contractedAmount: 1000m,
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
            utcNow: new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Unspecified),
            contractStartUtc: UtcNow.AddDays(-30),
            contractCurrencyCode: "EGP",
            contractedAmount: 1000m,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Contains("UtcNow", ex.Message);
    }

    [Fact]
    public void TestF04_Context_RejectsNonUtcContractStartUtc()
    {
        var ex = Assert.Throws<ArgumentException>(() => EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            utcNow: UtcNow,
            contractStartUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            contractCurrencyCode: "EGP",
            contractedAmount: 1000m,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Contains("ContractStartUtc", ex.Message);
    }

    [Fact]
    public void TestF05_Context_RejectsInvalidCurrencyCode()
    {
        var ex = Assert.Throws<ArgumentException>(() => EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            utcNow: UtcNow,
            contractStartUtc: UtcNow.AddDays(-30),
            contractCurrencyCode: "EG", // too short
            contractedAmount: 1000m,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Contains("ContractCurrencyCode", ex.Message);
    }

    [Fact]
    public void TestF06_Context_RejectsNegativeContractedAmount()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            utcNow: UtcNow,
            contractStartUtc: UtcNow.AddDays(-30),
            contractCurrencyCode: "EGP",
            contractedAmount: -1m,
            contractDurationMonths: 12,
            hasOverdueInstallment: false));
        Assert.Equal("contractedAmount", ex.ParamName);
    }

    // ──────────────── ContractActive ────────────────

    [Fact]
    public void TestF10_ContractActive_True_WhenActive()
    {
        var rule = EligibilityRule.ContractActive();
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(status: ContractStatus.Active));
        Assert.True(result.IsEligible);
        Assert.Equal(WhyIneligible.None, result.Reason);
    }

    [Fact]
    public void TestF11_ContractActive_False_AllNonActiveStates()
    {
        var rule = EligibilityRule.ContractActive();
        var nonActiveStates = new[]
        {
            ContractStatus.Draft,
            ContractStatus.PendingApproval,
            ContractStatus.Suspended,
            ContractStatus.Terminated,
            ContractStatus.Expired
        };

        foreach (var status in nonActiveStates)
        {
            var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(status: status));
            Assert.False(result.IsEligible, $"status={status}");
            Assert.Equal(WhyIneligible.ContractNotActive, result.Reason);
            Assert.Equal("ContractActive", result.ReasonPath);
        }
    }

    // ──────────────── PaymentTermsEquals ────────────────

    [Fact]
    public void TestF12_PaymentTermsEquals_True_FullUpfrontMatchesFullUpfront()
    {
        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(terms: PaymentTerms.FullUpfront));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF13_PaymentTermsEquals_True_InstallmentsMatchesInstallments()
    {
        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(terms: PaymentTerms.Installments));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF14_PaymentTermsEquals_False_Mismatch()
    {
        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(terms: PaymentTerms.Installments));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.PaymentTermsMismatch, result.Reason);
        Assert.Equal("PaymentTermsEquals(FullUpfront)", result.ReasonPath);
    }

    // ──────────────── PaymentMethodEquals ────────────────

    [Fact]
    public void TestF15_PaymentMethodEquals_True_ExactMatch()
    {
        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var result = new EligibilityRuleEvaluator().Evaluate(rule,
            NewContext(completedPayments: Fact(UtcNow.AddHours(-1), method: "CARD")));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF16_PaymentMethodEquals_True_CanonicalCasing()
    {
        var rule = EligibilityRule.PaymentMethodEquals("card");
        var result = new EligibilityRuleEvaluator().Evaluate(rule,
            NewContext(completedPayments: Fact(UtcNow.AddHours(-1), method: "CARD")));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF17_PaymentMethodEquals_True_WhitespaceNormalization()
    {
        // Per Task B: Trim().ToUpperInvariant(). Rule expects "CARD"; the fact carries " card ".
        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var result = new EligibilityRuleEvaluator().Evaluate(rule,
            NewContext(completedPayments: Fact(UtcNow.AddHours(-1), method: "  card  ")));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF18_PaymentMethodEquals_False_Mismatch()
    {
        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var result = new EligibilityRuleEvaluator().Evaluate(rule,
            NewContext(completedPayments: Fact(UtcNow.AddHours(-1), method: "CASH")));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.PaymentMethodMismatch, result.Reason);
    }

    [Fact]
    public void TestF19_PaymentMethodEquals_False_NoPaymentFact()
    {
        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext());
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.PaymentMethodMismatch, result.Reason);
    }

    [Fact]
    public void TestF20_PaymentMethodEquals_True_AnyMatchAmongMultiple()
    {
        // Multiple payments: rule passes when at least one carries the matching method. We do NOT
        // silently pick "the latest payment wins".
        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContextWithPayments(
            new[]
            {
                Fact(UtcNow.AddHours(-5), method: "CASH"),
                Fact(UtcNow.AddHours(-4), method: "WALLET"),
                Fact(UtcNow.AddHours(-3), method: "INSTAPAY"),
                Fact(UtcNow.AddHours(-2), method: "CARD"),
                Fact(UtcNow.AddHours(-1), method: "CASH")
            }));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF21_PaymentMethodEquals_True_AnyMatchAmongMultiple_NotLatest()
    {
        // The latest payment does NOT carry the matching method, but an older one does.
        var rule = EligibilityRule.PaymentMethodEquals("CARD");
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContextWithPayments(
            new[]
            {
                Fact(UtcNow.AddHours(-3), method: "CARD"),
                Fact(UtcNow.AddHours(-1), method: "CASH")
            }));
        Assert.True(result.IsEligible);
    }

    // ──────────────── CompletedByUtc ────────────────

    [Fact]
    public void TestF30_CompletedByUtc_False_NoPayment_FutureDeadline()
    {
        // No completed payment + future deadline ⇒ must be FALSE. The deadline passing alone
        // does not satisfy the rule.
        var deadline = UtcNow.AddDays(1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext());
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.DeadlinePassed, result.Reason);
    }

    [Fact]
    public void TestF31_CompletedByUtc_True_CompletedBeforeDeadline()
    {
        var deadline = UtcNow.AddDays(1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            completedPayments: Fact(UtcNow.AddHours(-2))));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF32_CompletedByUtc_True_CompletedExactlyAtDeadline()
    {
        var deadline = UtcNow;
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            completedPayments: Fact(deadline)));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF33_CompletedByUtc_False_CompletedAfterDeadline()
    {
        var deadline = UtcNow.AddHours(-1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            completedPayments: Fact(UtcNow)));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.DeadlinePassed, result.Reason);
    }

    [Fact]
    public void TestF34_CompletedByUtc_True_AnyOneOfMultipleQualifies()
    {
        var deadline = UtcNow.AddHours(1);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContextWithPayments(
            new[]
            {
                Fact(deadline.AddHours(2)),     // too late
                Fact(deadline.AddHours(3)),     // too late
                Fact(deadline.AddMinutes(-30)), // qualifies
                Fact(deadline.AddHours(5))      // too late
            }));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF35_CompletedByUtc_DoesNotFabricate_FromContractStart()
    {
        // A completed payment does NOT exist; the contract-start is well before the deadline.
        // The rule MUST NOT silently fabricate a completion timestamp from the contract start.
        var deadline = UtcNow.AddDays(7);
        var rule = EligibilityRule.CompletedByUtc(deadline);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(contractStartUtc: UtcNow.AddDays(-30)));
        Assert.False(result.IsEligible);
    }

    // ──────────────── NoOverdueInstallment ────────────────

    [Fact]
    public void TestF40_NoOverdueInstallment_True_WhenNoOverdue()
    {
        var rule = EligibilityRule.NoOverdueInstallment();
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(hasOverdueInstallment: false));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF41_NoOverdueInstallment_False_WhenOverdue()
    {
        var rule = EligibilityRule.NoOverdueInstallment();
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(hasOverdueInstallment: true));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.OverdueInstallment, result.Reason);
    }

    // ──────────────── AmountPaidAtLeast ────────────────

    [Fact]
    public void TestF50_AmountPaidAtLeast_False_Zero()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(1m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext());
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.AmountBelowMinimum, result.Reason);
    }

    [Fact]
    public void TestF51_AmountPaidAtLeast_False_BelowThreshold()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            completedPayments: Fact(UtcNow.AddHours(-1), 999.99m)));
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void TestF52_AmountPaidAtLeast_True_ExactThreshold()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            completedPayments: Fact(UtcNow.AddHours(-1), 1000m)));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF53_AmountPaidAtLeast_True_AboveThreshold()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            completedPayments: Fact(UtcNow.AddHours(-1), 1500m)));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF54_AmountPaidAtLeast_True_SumAcrossMultiplePayments()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContextWithPayments(
            new[]
            {
                Fact(UtcNow.AddHours(-3), 600m),
                Fact(UtcNow.AddHours(-2), 200m),
                Fact(UtcNow.AddHours(-1), 200m)
            }));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF55_AmountPaidAtLeast_CurrencyMismatch_DoesNotContribute()
    {
        // The contract is in EGP. A payment in USD MUST NOT contribute to the sum.
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            contractCurrencyCode: "EGP",
            completedPayments: Fact(UtcNow.AddHours(-1), 1000m, currencyCode: "USD")));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.AmountBelowMinimum, result.Reason);
    }

    [Fact]
    public void TestF56_AmountPaidAtLeast_CurrencyMatch_Canonicalisation()
    {
        // Lowercase "egp" canonicalises to "EGP" via Trim().ToUpperInvariant().
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            contractCurrencyCode: "EGP",
            completedPayments: Fact(UtcNow.AddHours(-1), 1000m, currencyCode: "egp")));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF57_AmountPaidAtLeast_NoFiftyThousandEGPFallback()
    {
        // Sanity: a non-EGP contract in SAR cannot be satisfied by an EGP payment.
        var rule = EligibilityRule.AmountPaidAtLeast(1000m);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            contractCurrencyCode: "SAR",
            completedPayments: Fact(UtcNow.AddHours(-1), 5000m, currencyCode: "EGP")));
        Assert.False(result.IsEligible);
    }

    // ──────────────── DaysFromContractStartGte ────────────────

    [Fact]
    public void TestF60_DaysFromContractStartGte_True_ZeroDays_AtBoundary()
    {
        // days=0: a contract that started at or before UtcNow must pass.
        var rule = EligibilityRule.DaysFromContractStartGte(0);
        var start = UtcNow.AddMinutes(-1);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(contractStartUtc: start));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF61_DaysFromContractStartGte_False_BelowThreshold()
    {
        var rule = EligibilityRule.DaysFromContractStartGte(30);
        var start = UtcNow.AddDays(-29);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(contractStartUtc: start));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.DaysFromContractStartNotMet, result.Reason);
    }

    [Fact]
    public void TestF62_DaysFromContractStartGte_True_ExactThreshold()
    {
        var rule = EligibilityRule.DaysFromContractStartGte(30);
        var start = UtcNow.AddDays(-30);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(contractStartUtc: start));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF63_DaysFromContractStartGte_True_AboveThreshold()
    {
        var rule = EligibilityRule.DaysFromContractStartGte(30);
        var start = UtcNow.AddDays(-31);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(contractStartUtc: start));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF64_DaysFromContractStartGte_MidnightBoundary_False()
    {
        // Spec test: Start = 2026-10-01 23:00 UTC; Now = 2026-10-02 00:00 UTC. Elapsed = 1 hour.
        // DaysFromContractStartGte(1) MUST be FALSE. This is the canonical regression test
        // proving that .Date slicing is NOT used — only elapsed TimeSpan.
        var midnightCrossing = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        var startNightBefore = new DateTime(2026, 10, 1, 23, 0, 0, DateTimeKind.Utc);

        var rule = EligibilityRule.DaysFromContractStartGte(1);
        var ctx = EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            utcNow: midnightCrossing,
            contractStartUtc: startNightBefore,
            contractCurrencyCode: "EGP",
            contractedAmount: 1000m,
            contractDurationMonths: 12,
            hasOverdueInstallment: false);

        var result = new EligibilityRuleEvaluator().Evaluate(rule, ctx);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void TestF65_DaysFromContractStartGte_MidnightBoundary_True()
    {
        // Spec test: Start = 2026-10-01 23:00 UTC; Now = 2026-10-02 23:00 UTC. Elapsed = 24h.
        // DaysFromContractStartGte(1) MUST be TRUE.
        var exactly24hLater = new DateTime(2026, 10, 2, 23, 0, 0, DateTimeKind.Utc);
        var startNightBefore = new DateTime(2026, 10, 1, 23, 0, 0, DateTimeKind.Utc);

        var rule = EligibilityRule.DaysFromContractStartGte(1);
        var ctx = EligibilityContext.Create(
            tenantId: TenantId,
            contractId: ContractId,
            benefitId: BenefitId,
            contractStatus: ContractStatus.Active,
            paymentTerms: PaymentTerms.Installments,
            utcNow: exactly24hLater,
            contractStartUtc: startNightBefore,
            contractCurrencyCode: "EGP",
            contractedAmount: 1000m,
            contractDurationMonths: 12,
            hasOverdueInstallment: false);

        var result = new EligibilityRuleEvaluator().Evaluate(rule, ctx);
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF66_DaysFromContractStartGte_ExactElapsedHours()
    {
        // 1 day = 24 hours. elapsed = 23h59m ⇒ FALSE. elapsed = 24h00m ⇒ TRUE.
        var rule = EligibilityRule.DaysFromContractStartGte(1);
        var start = UtcNow.AddHours(-23).AddMinutes(-59);
        var resultBelow = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(contractStartUtc: start));
        Assert.False(resultBelow.IsEligible);

        var startExact = UtcNow.AddHours(-24);
        var resultExact = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(contractStartUtc: startExact));
        Assert.True(resultExact.IsEligible);
    }

    // ──────────────── DurationMonthsGte ────────────────

    [Fact]
    public void TestF70_DurationMonthsGte_True_ExactMonth()
    {
        var rule = EligibilityRule.DurationMonthsGte(12);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(durationMonths: 12));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF71_DurationMonthsGte_False_Below()
    {
        var rule = EligibilityRule.DurationMonthsGte(12);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(durationMonths: 11));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.DurationMonthsNotMet, result.Reason);
    }

    [Fact]
    public void TestF72_DurationMonthsGte_True_MultiMonth()
    {
        var rule = EligibilityRule.DurationMonthsGte(12);
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(durationMonths: 24));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF73_DurationMonthsGte_DoesNotTranslateToThirtyDayApproximation()
    {
        // Sanity: DurationMonths=1 must not be coerced to 30 days. A contract that is 1 month
        // long must satisfy DurationMonthsGte(1) but must NOT necessarily satisfy
        // DaysFromContractStartGte(30).
        var start = UtcNow.AddDays(-29); // 29 days elapsed, but duration is 1 month
        var ctx = NewContext(contractStartUtc: start, durationMonths: 1);

        var oneMonth = EligibilityRule.DurationMonthsGte(1);
        Assert.True(new EligibilityRuleEvaluator().Evaluate(oneMonth, ctx).IsEligible);

        // And DurationMonthsGte(2) is false (we only have a 1-month contract).
        var twoMonths = EligibilityRule.DurationMonthsGte(2);
        Assert.False(new EligibilityRuleEvaluator().Evaluate(twoMonths, ctx).IsEligible);
    }

    // ──────────────── Composites ────────────────

    [Fact]
    public void TestF80_AllOf_True_WhenAllChildrenPass()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment());

        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext());
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF81_AllOf_False_ShortCircuitOnFirstFailure_WithPath()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(1000m),
            EligibilityRule.NoOverdueInstallment());

        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext());
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.AmountBelowMinimum, result.Reason);
        Assert.Equal("AllOf[1].AmountPaidAtLeast(1000)", result.ReasonPath);
    }

    [Fact]
    public void TestF82_AnyOf_True_WhenAtLeastOneChildPasses()
    {
        var rule = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront));

        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(terms: PaymentTerms.FullUpfront));
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void TestF83_AnyOf_False_ReasonAnyOfNoChildPassed()
    {
        var rule = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront));

        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(terms: (PaymentTerms)999));
        Assert.False(result.IsEligible);
        Assert.Equal(WhyIneligible.AnyOfNoChildPassed, result.Reason);
    }

    [Fact]
    public void TestF84_NestedComposites()
    {
        // All-of(ContractActive, Any-of(PaymentTermsEquals(FullUpfront), AmountPaidAtLeast(1000)))
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.AmountPaidAtLeast(1000m)));

        // Terms=Installments, paid=1500 ⇒ AnyOf passes via AmountPaidAtLeast.
        var result = new EligibilityRuleEvaluator().Evaluate(rule, NewContext(
            terms: PaymentTerms.Installments,
            completedPayments: new[] { Fact(UtcNow.AddHours(-1), 1500m) }));
        Assert.True(result.IsEligible);
    }

    // ──────────────── Determinism ────────────────

    [Fact]
    public void TestF90_Evaluator_IsDeterministic_AcrossInvocations()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.AmountPaidAtLeast(1000m));

        var ctx = NewContext(completedPayments: Fact(UtcNow.AddHours(-1), 1000m));

        var r1 = new EligibilityRuleEvaluator().Evaluate(rule, ctx);
        var r2 = new EligibilityRuleEvaluator().Evaluate(rule, ctx);
        var r3 = new EligibilityRuleEvaluator().Evaluate(rule, ctx);

        Assert.Equal(r1.IsEligible, r2.IsEligible);
        Assert.Equal(r1.IsEligible, r3.IsEligible);
        Assert.Equal(r1.Reason, r2.Reason);
        Assert.Equal(r1.Reason, r3.Reason);
    }
}