namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

/// <summary>
/// Task B — Commercial Benefit EligibilityRule foundation tests.
///
/// Validates the closed algebraic rule foundation (§2), immutability (§5),
/// strong validation (§6), deterministic serialization (§7), canonical default
/// physical gift rule (§8), snapshot retention (§9, §10, §11), structural equality (§15),
/// and EF Core persistence with value conversion round-trip.
/// </summary>
public class TaskB_EligibilityRuleFoundationTests : IClassFixture<TaskAPaymentTermsTestFactory>
{
    private readonly TaskAPaymentTermsTestFactory _factory;

    public TaskB_EligibilityRuleFoundationTests(TaskAPaymentTermsTestFactory factory)
    {
        _factory = factory;
    }
    // ─────────────────────────────────────────────────────────────────
    // 1. Rule construction (Requirements 1 - 9)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test01_ContractActive_CanBeCreated()
    {
        var rule = EligibilityRule.ContractActive();

        Assert.NotNull(rule);
        Assert.IsType<ContractActiveRule>(rule);
    }

    [Fact]
    public void Test02_NoOverdueInstallment_CanBeCreated()
    {
        var rule = EligibilityRule.NoOverdueInstallment();

        Assert.NotNull(rule);
        Assert.IsType<NoOverdueInstallmentRule>(rule);
    }

    [Fact]
    public void Test03_PaymentTermsEq_FullUpfront_CanBeCreated()
    {
        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);

        Assert.NotNull(rule);
        var ptRule = Assert.IsType<PaymentTermsEqualsRule>(rule);
        Assert.Equal(PaymentTerms.FullUpfront, ptRule.PaymentTerms);
    }

    [Fact]
    public void Test04_PaymentTermsEq_Installments_CanBeCreated()
    {
        var rule = EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments);

        Assert.NotNull(rule);
        var ptRule = Assert.IsType<PaymentTermsEqualsRule>(rule);
        Assert.Equal(PaymentTerms.Installments, ptRule.PaymentTerms);
    }

    [Fact]
    public void Test05_PaymentMethodEq_Cash_CanBeCreated_AndCanonicalised()
    {
        // Canonical form: Trim().ToUpperInvariant() — see PrimitiveRules.cs
        var rule = EligibilityRule.PaymentMethodEquals("  Cash  ");

        Assert.NotNull(rule);
        var pmRule = Assert.IsType<PaymentMethodEqualsRule>(rule);
        Assert.Equal("CASH", pmRule.PaymentMethod);
    }

    [Fact]
    public void Test06_CompletedByUtc_ValidUtc_CanBeCreated()
    {
        var utc = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var rule = EligibilityRule.CompletedByUtc(utc);

        Assert.NotNull(rule);
        var cRule = Assert.IsType<CompletedByUtcRule>(rule);
        Assert.Equal(utc, cRule.CompletedBy);
    }

    [Fact]
    public void Test07_AmountPaidAtLeast_Zero_IsValid()
    {
        var rule = EligibilityRule.AmountPaidAtLeast(0m);

        Assert.NotNull(rule);
        var aRule = Assert.IsType<AmountPaidAtLeastRule>(rule);
        Assert.Equal(0m, aRule.Amount);
    }

    [Fact]
    public void Test08_DaysFromContractStartGte_Zero_IsValid()
    {
        var rule = EligibilityRule.DaysFromContractStartGte(0);

        Assert.NotNull(rule);
        var dRule = Assert.IsType<DaysFromContractStartGteRule>(rule);
        Assert.Equal(0, dRule.Days);
    }

    [Fact]
    public void Test09_DurationMonthsGte_Zero_IsValid()
    {
        var rule = EligibilityRule.DurationMonthsGte(0);

        Assert.NotNull(rule);
        var mRule = Assert.IsType<DurationMonthsGteRule>(rule);
        Assert.Equal(0, mRule.Months);
    }

    // ─────────────────────────────────────────────────────────────────
    // 2. Invalid values (Requirements 10 - 18)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test10_AllOf_WithZeroRules_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => EligibilityRule.AllOf());
        Assert.Throws<ArgumentException>(() => EligibilityRule.AllOf(Array.Empty<EligibilityRule>()));
    }

    [Fact]
    public void Test11_AnyOf_WithZeroRules_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => EligibilityRule.AnyOf());
        Assert.Throws<ArgumentException>(() => EligibilityRule.AnyOf(Array.Empty<EligibilityRule>()));
    }

    [Fact]
    public void Test12_NullChildRule_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            EligibilityRule.AllOf(EligibilityRule.ContractActive(), null!));

        Assert.Throws<ArgumentException>(() =>
            EligibilityRule.AnyOf(null!, EligibilityRule.NoOverdueInstallment()));
    }

    [Fact]
    public void Test13_InvalidPaymentTermsEnumValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            EligibilityRule.PaymentTermsEquals((PaymentTerms)999));
    }

    [Fact]
    public void Test14_NegativeAmountPaidAtLeast_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EligibilityRule.AmountPaidAtLeast(-0.01m));
    }

    [Fact]
    public void Test15_NegativeDaysFromContractStartGte_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EligibilityRule.DaysFromContractStartGte(-1));
    }

    [Fact]
    public void Test16_NegativeDurationMonthsGte_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EligibilityRule.DurationMonthsGte(-1));
    }

    [Fact]
    public void Test17_EmptyOrWhitespacePaymentMethod_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => EligibilityRule.PaymentMethodEquals(""));
        Assert.Throws<ArgumentException>(() => EligibilityRule.PaymentMethodEquals("   "));
        Assert.Throws<ArgumentException>(() => EligibilityRule.PaymentMethodEquals(null!));
    }

    [Fact]
    public void Test18_NonUtcCompletedByUtc_IsRejected()
    {
        var localTime = DateTime.Now;
        Assert.Throws<ArgumentException>(() => EligibilityRule.CompletedByUtc(localTime));

        var unspecifiedTime = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Throws<ArgumentException>(() => EligibilityRule.CompletedByUtc(unspecifiedTime));
    }

    // ─────────────────────────────────────────────────────────────────
    // 3. Composition (Requirements 19 - 21)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test19_NestedAllOf_Works()
    {
        var inner = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment());

        var outer = EligibilityRule.AllOf(
            inner,
            EligibilityRule.AmountPaidAtLeast(1000m));

        var outerAllOf = Assert.IsType<AllOfRule>(outer);
        Assert.Equal(2, outerAllOf.Rules.Count);
        Assert.IsType<AllOfRule>(outerAllOf.Rules[0]);
        Assert.IsType<AmountPaidAtLeastRule>(outerAllOf.Rules[1]);
    }

    [Fact]
    public void Test20_NestedAnyOf_Works()
    {
        var inner = EligibilityRule.AnyOf(
            EligibilityRule.PaymentMethodEquals("Cash"),
            EligibilityRule.PaymentMethodEquals("BankTransfer"));

        var outer = EligibilityRule.AnyOf(
            inner,
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront));

        var outerAnyOf = Assert.IsType<AnyOfRule>(outer);
        Assert.Equal(2, outerAnyOf.Rules.Count);
        Assert.IsType<AnyOfRule>(outerAnyOf.Rules[0]);
        Assert.IsType<PaymentTermsEqualsRule>(outerAnyOf.Rules[1]);
    }

    [Fact]
    public void Test21_MixedNestedRules_Work()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("Cash")),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var allOf = Assert.IsType<AllOfRule>(rule);
        Assert.Equal(3, allOf.Rules.Count);
        Assert.IsType<ContractActiveRule>(allOf.Rules[0]);
        var anyOf = Assert.IsType<AnyOfRule>(allOf.Rules[1]);
        Assert.Equal(2, anyOf.Rules.Count);
        Assert.IsType<PaymentTermsEqualsRule>(anyOf.Rules[0]);
        Assert.IsType<PaymentMethodEqualsRule>(anyOf.Rules[1]);
        Assert.IsType<AmountPaidAtLeastRule>(allOf.Rules[2]);
    }

    // ─────────────────────────────────────────────────────────────────
    // 4. Equality (Requirements 22 - 25)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test22_EquivalentPrimitiveRules_AreEqual()
    {
        var r1 = EligibilityRule.AmountPaidAtLeast(5000m);
        var r2 = EligibilityRule.AmountPaidAtLeast(5000m);
        Assert.Equal(r1, r2);
        Assert.Equal(r1.GetHashCode(), r2.GetHashCode());

        var active1 = EligibilityRule.ContractActive();
        var active2 = EligibilityRule.ContractActive();
        Assert.Equal(active1, active2);
        Assert.Equal(active1.GetHashCode(), active2.GetHashCode());

        var pt1 = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var pt2 = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        Assert.Equal(pt1, pt2);
        Assert.Equal(pt1.GetHashCode(), pt2.GetHashCode());

        var pm1 = EligibilityRule.PaymentMethodEquals("Cash");
        var pm2 = EligibilityRule.PaymentMethodEquals("cash"); // case-insensitive
        Assert.Equal(pm1, pm2);
        Assert.Equal(pm1.GetHashCode(), pm2.GetHashCode());
    }

    [Fact]
    public void Test23_EquivalentNestedRules_AreEqual()
    {
        var tree1 = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("Cash")),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var tree2 = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("cash")),
            EligibilityRule.AmountPaidAtLeast(5000m));

        Assert.Equal(tree1, tree2);
        Assert.Equal(tree1.GetHashCode(), tree2.GetHashCode());
    }

    [Fact]
    public void Test24_DifferentParameterValues_AreNotEqual()
    {
        var a1 = EligibilityRule.AmountPaidAtLeast(100m);
        var a2 = EligibilityRule.AmountPaidAtLeast(200m);
        Assert.NotEqual(a1, a2);

        var d1 = EligibilityRule.DaysFromContractStartGte(30);
        var d2 = EligibilityRule.DaysFromContractStartGte(60);
        Assert.NotEqual(d1, d2);

        var m1 = EligibilityRule.DurationMonthsGte(6);
        var m2 = EligibilityRule.DurationMonthsGte(12);
        Assert.NotEqual(m1, m2);

        var pt1 = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var pt2 = EligibilityRule.PaymentTermsEquals(PaymentTerms.Installments);
        Assert.NotEqual(pt1, pt2);
    }

    [Fact]
    public void Test25_DifferentRuleTypes_AreNotEqual()
    {
        var r1 = EligibilityRule.ContractActive();
        var r2 = EligibilityRule.NoOverdueInstallment();
        Assert.NotEqual(r1, r2);

        var a = EligibilityRule.AmountPaidAtLeast(12m);
        var m = EligibilityRule.DurationMonthsGte(12);
        Assert.NotEqual(a, m);
    }

    // ─────────────────────────────────────────────────────────────────
    // 5. Serialization (Requirements 26 - 32)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test26_EveryRuleType_Serializes()
    {
        var utc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        EligibilityRule[] rules =
        [
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.PaymentMethodEquals("Cash"),
            EligibilityRule.CompletedByUtc(utc),
            EligibilityRule.AmountPaidAtLeast(1000m),
            EligibilityRule.DaysFromContractStartGte(15),
            EligibilityRule.DurationMonthsGte(12),
            EligibilityRule.AllOf(EligibilityRule.ContractActive(), EligibilityRule.NoOverdueInstallment()),
            EligibilityRule.AnyOf(EligibilityRule.ContractActive(), EligibilityRule.NoOverdueInstallment())
        ];

        foreach (var rule in rules)
        {
            var json = EligibilityRuleSerializer.Serialize(rule);
            Assert.False(string.IsNullOrWhiteSpace(json));
        }
    }

    [Fact]
    public void Test27_EveryRuleType_Deserializes()
    {
        var utc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        EligibilityRule[] originalRules =
        [
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.PaymentMethodEquals("Cash"),
            EligibilityRule.CompletedByUtc(utc),
            EligibilityRule.AmountPaidAtLeast(1000m),
            EligibilityRule.DaysFromContractStartGte(15),
            EligibilityRule.DurationMonthsGte(12),
            EligibilityRule.AllOf(EligibilityRule.ContractActive(), EligibilityRule.NoOverdueInstallment()),
            EligibilityRule.AnyOf(EligibilityRule.ContractActive(), EligibilityRule.NoOverdueInstallment())
        ];

        foreach (var original in originalRules)
        {
            var json = EligibilityRuleSerializer.Serialize(original);
            var deserialized = EligibilityRuleSerializer.Deserialize(json);
            Assert.Equal(original, deserialized);
        }
    }

    [Fact]
    public void Test28_NestedRules_RoundTrip()
    {
        var complexRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("CreditCard")),
            EligibilityRule.AmountPaidAtLeast(2500m),
            EligibilityRule.DaysFromContractStartGte(30),
            EligibilityRule.NoOverdueInstallment());

        var json = EligibilityRuleSerializer.Serialize(complexRule);
        var roundTripped = EligibilityRuleSerializer.Deserialize(json);

        Assert.Equal(complexRule, roundTripped);
    }

    [Fact]
    public void Test29_Serialization_IsDeterministic()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var json1 = EligibilityRuleSerializer.Serialize(rule);
        var json2 = EligibilityRuleSerializer.Serialize(rule);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void Test30_SerializedRule_DoesNotContainClrTypeNames()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var json = EligibilityRuleSerializer.Serialize(rule);

        Assert.DoesNotContain("Centerix", json);
        Assert.DoesNotContain("ContractActiveRule", json);
        Assert.DoesNotContain("PaymentTermsEqualsRule", json);
        Assert.DoesNotContain("AmountPaidAtLeastRule", json);
        Assert.DoesNotContain("AllOfRule", json);
        Assert.DoesNotContain("System.", json);
    }

    [Fact]
    public void Test31_InvalidSerializedRule_IsRejectedSafely()
    {
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize(""));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("   "));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{ invalid json }"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\": \"unknown_expression\"}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"missing_type\": true}"));
    }

    [Fact]
    public void Test32_NoExecutableCodeCanBeSuppliedThroughSerializedRepresentation()
    {
        // Malicious attempt to inject executable C# code or reflection type names
        var maliciousJson = "{\"type\": \"System.Diagnostics.Process\", \"command\": \"calc.exe\"}";
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize(maliciousJson));
    }

    // ─────────────────────────────────────────────────────────────────
    // 6. Default physical gift rule (Requirements 33 - 35)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test33_DefaultPhysicalGiftRule_ContainsExactlyExpectedChildRules()
    {
        var rule = EligibilityRule.DefaultPhysicalGiftRule(5000m);

        var allOf = Assert.IsType<AllOfRule>(rule);
        Assert.Equal(3, allOf.Rules.Count);

        Assert.IsType<ContractActiveRule>(allOf.Rules[0]);
        var amountRule = Assert.IsType<AmountPaidAtLeastRule>(allOf.Rules[1]);
        Assert.Equal(5000m, amountRule.Amount);
        Assert.IsType<NoOverdueInstallmentRule>(allOf.Rules[2]);
    }

    [Fact]
    public void Test34_DefaultPhysicalGiftRule_DoesNotContainPaymentTerms()
    {
        var rule = EligibilityRule.DefaultPhysicalGiftRule(5000m);

        var allOf = Assert.IsType<AllOfRule>(rule);
        Assert.DoesNotContain(allOf.Rules, r => r is PaymentTermsEqualsRule);
    }

    [Fact]
    public void Test35_CreatingDefaultPhysicalGiftRule_DoesNotInspectContractOrPromotionType()
    {
        // Pure domain factory execution — requires no DB, no Contract instance, no PromotionType
        var rule = EligibilityRule.DefaultPhysicalGiftRule(1000m);

        Assert.NotNull(rule);
        var allOf = Assert.IsType<AllOfRule>(rule);
        Assert.Equal(3, allOf.Rules.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // 7. Snapshot (Requirements 36 - 39)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test36_OfferBenefit_StoresEligibilityRuleSnapshot()
    {
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront));

        var offerBenefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Test Barcode Printer",
            description: "Industrial printer",
            contractualValue: 1500m,
            currencyCode: "EGP",
            eligibilityRule: rule).Value;

        Assert.NotNull(offerBenefit.EligibilityRule);
        Assert.Equal(rule, offerBenefit.EligibilityRule);

        // Also test nullable rule for backwards compatibility
        var legacyOfferBenefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Legacy Benefit",
            description: null,
            contractualValue: 500m,
            currencyCode: "EGP").Value;

        Assert.Null(legacyOfferBenefit.EligibilityRule);
    }

    [Fact]
    public void Test37_ContractBenefit_StoresEligibilityRuleSnapshot()
    {
        var rule = EligibilityRule.DefaultPhysicalGiftRule(3000m);

        var contractBenefit = ContractBenefit.Create(
            id: Guid.NewGuid(),
            contractId: Guid.NewGuid(),
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Test Gift",
            description: null,
            contractualValue: 3000m,
            currencyCode: "EGP",
            eligibilityRule: rule).Value;

        Assert.NotNull(contractBenefit.EligibilityRule);
        Assert.Equal(rule, contractBenefit.EligibilityRule);

        // Also test nullable rule for backwards compatibility
        var legacyBenefit = ContractBenefit.Create(
            id: Guid.NewGuid(),
            contractId: Guid.NewGuid(),
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Legacy Gift",
            description: null,
            contractualValue: 1000m,
            currencyCode: "EGP").Value;

        Assert.Null(legacyBenefit.EligibilityRule);
    }

    [Fact]
    public void Test38_OfferToContract_PreservesEligibilityRuleSnapshotExactly()
    {
        var expectedRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(10000m));

        var offerBenefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            benefitType: ContractBenefitType.PhysicalGift,
            name: "VIP Gift",
            description: "High value gift",
            contractualValue: 2000m,
            currencyCode: "EGP",
            eligibilityRule: expectedRule).Value;

        // Simulate Offer -> Contract snapshot copying as done in CreateContractFromOfferCommand
        var contractBenefit = ContractBenefit.Create(
            id: Guid.NewGuid(),
            contractId: Guid.NewGuid(),
            benefitType: offerBenefit.BenefitType,
            name: offerBenefit.Name,
            description: offerBenefit.Description,
            contractualValue: offerBenefit.ContractualValue,
            currencyCode: offerBenefit.CurrencyCode,
            eligibilityRule: offerBenefit.EligibilityRule).Value;

        Assert.NotNull(contractBenefit.EligibilityRule);
        Assert.Equal(expectedRule, contractBenefit.EligibilityRule);
    }

    [Fact]
    public void Test39_MutatingOriginalInMemoryList_CannotMutateStoredCompositeRule()
    {
        var mutableList = new List<EligibilityRule>
        {
            EligibilityRule.ContractActive(),
            EligibilityRule.NoOverdueInstallment()
        };

        var allOf = (AllOfRule)EligibilityRule.AllOf(mutableList);
        Assert.Equal(2, allOf.Rules.Count);

        // Mutating the original input list must NOT affect the defensive copy inside AllOfRule
        mutableList.Add(EligibilityRule.AmountPaidAtLeast(5000m));
        Assert.Equal(2, allOf.Rules.Count);

        mutableList.Clear();
        Assert.Equal(2, allOf.Rules.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // 8. EF Core Persistence Round-Trip (Requirements 12 & 20)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test40_OfferBenefit_EligibilityRule_PersistsAndRoundTripsInDatabase()
    {
        var offerId = Guid.NewGuid();
        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(10000m));

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var offer = Offer.Create(
                id: offerId,
                tenantId: "tenant-paymentterms",
                planId: 1,
                durationMonths: 12,
                baseAmount: 12000m,
                discountAmount: 0m,
                finalAmount: 12000m,
                monthlyListPrice: 1000m,
                currencyCode: "EGP",
                promotionId: null,
                promotionName: null,
                promotionCode: null,
                promotionType: "None",
                discountPercentage: null,
                chargedMonths: null,
                calculatedAtUtc: DateTime.UtcNow,
                expiresAtUtc: DateTime.UtcNow.AddDays(7),
                bonusMonths: 0,
                maxStudents: 100,
                maxUsers: 5,
                maxBranches: 1,
                maxTeachers: 10,
                storageGb: 10,
                smsQuota: 100,
                entitlementSnapshotVersion: 1,
                paymentTerms: PaymentTerms.FullUpfront).Value;

            var benefitWithRule = OfferBenefit.Create(
                id: Guid.NewGuid(),
                offerId: offerId,
                benefitType: ContractBenefitType.PhysicalGift,
                name: "Printer Gift",
                description: null,
                contractualValue: 1500m,
                currencyCode: "EGP",
                eligibilityRule: rule).Value;

            var benefitWithoutRule = OfferBenefit.Create(
                id: Guid.NewGuid(),
                offerId: offerId,
                benefitType: ContractBenefitType.PhysicalGift,
                name: "Legacy Gift",
                description: null,
                contractualValue: 500m,
                currencyCode: "EGP",
                eligibilityRule: null).Value;

            offer.AddBenefit(benefitWithRule);
            offer.AddBenefit(benefitWithoutRule);

            dbContext.Offers.Add(offer);
            await dbContext.SaveChangesAsync();
        }

        // Re-read in a completely new scope
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var loadedOffer = await dbContext.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == offerId);

            Assert.NotNull(loadedOffer);
            Assert.Equal(2, loadedOffer.Benefits.Count);

            var loadedWithRule = loadedOffer.Benefits.First(b => b.Name == "Printer Gift");
            Assert.NotNull(loadedWithRule.EligibilityRule);
            Assert.Equal(rule, loadedWithRule.EligibilityRule);

            var loadedWithoutRule = loadedOffer.Benefits.First(b => b.Name == "Legacy Gift");
            Assert.Null(loadedWithoutRule.EligibilityRule);
        }
    }

    [Fact]
    public async Task Test41_ContractBenefit_EligibilityRule_PersistsAndRoundTripsInDatabase()
    {
        var contractId = Guid.NewGuid();
        var rule = EligibilityRule.DefaultPhysicalGiftRule(6000m);

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var contract = Contract.Create(
                id: contractId,
                tenantId: "tenant-paymentterms",
                contractNumber: $"CNT-{Guid.NewGuid():N}"[..12],
                planId: 1,
                effectiveAtUtc: DateTime.UtcNow,
                endsAtUtc: DateTime.UtcNow.AddYears(1),
                durationMonths: 12,
                monthlyListPrice: 1000m,
                contractualMonthlyValue: 1000m,
                currencyCode: "EGP",
                grossAmount: 12000m,
                contractedAmount: 12000m,
                entitlementSnapshotVersion: 1,
                paymentTerms: PaymentTerms.Installments).Value;

            var benefitWithRule = ContractBenefit.Create(
                id: Guid.NewGuid(),
                contractId: contractId,
                benefitType: ContractBenefitType.PhysicalGift,
                name: "Scanner Gift",
                description: null,
                contractualValue: 2000m,
                currencyCode: "EGP",
                eligibilityRule: rule).Value;

            var benefitWithoutRule = ContractBenefit.Create(
                id: Guid.NewGuid(),
                contractId: contractId,
                benefitType: ContractBenefitType.PhysicalGift,
                name: "Legacy Contract Gift",
                description: null,
                contractualValue: 500m,
                currencyCode: "EGP",
                eligibilityRule: null).Value;

            contract.AddBenefit(benefitWithRule);
            contract.AddBenefit(benefitWithoutRule);

            dbContext.Contracts.Add(contract);
            await dbContext.SaveChangesAsync();
        }

        // Re-read in a completely new scope
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var loadedContract = await dbContext.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.Benefits)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId);

            Assert.NotNull(loadedContract);
            Assert.Equal(2, loadedContract.Benefits.Count);

            var loadedWithRule = loadedContract.Benefits.First(b => b.Name == "Scanner Gift");
            Assert.NotNull(loadedWithRule.EligibilityRule);
            Assert.Equal(rule, loadedWithRule.EligibilityRule);

            var loadedWithoutRule = loadedContract.Benefits.First(b => b.Name == "Legacy Contract Gift");
            Assert.Null(loadedWithoutRule.EligibilityRule);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 9. Additional invariants: determinism, ordering, immutability,
    //    hardened deserialization (§5, §7, §15, §31)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test42_EquivalentRules_BuiltIndependently_ProduceIdenticalSerializedText()
    {
        var first = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("Cash")),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var second = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("Cash")),
            EligibilityRule.AmountPaidAtLeast(5000m));

        Assert.Equal(first, second);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(first),
            EligibilityRuleSerializer.Serialize(second));
    }

    [Fact]
    public void Test43_CanonicalPropertyOrdering_IsFixed_NotReflectionDependent()
    {
        // Key ordering is a serializer contract ("type" always precedes the payload key),
        // independent of declaration or reflection order, and output is never indented.
        var json = EligibilityRuleSerializer.Serialize(EligibilityRule.AllOf(
            EligibilityRule.AmountPaidAtLeast(5000m),
            EligibilityRule.DurationMonthsGte(12)));

        var typeIndex = json.IndexOf("\"type\"", StringComparison.Ordinal);
        var rulesIndex = json.IndexOf("\"rules\"", StringComparison.Ordinal);
        var amountIndex = json.IndexOf("\"amount\"", StringComparison.Ordinal);

        Assert.StartsWith("{\"type\":", json, StringComparison.Ordinal);
        Assert.True(rulesIndex > typeIndex);

        var amountTypeIndex = json.IndexOf("\"amount_paid_at_least\"", StringComparison.Ordinal);
        Assert.True(amountTypeIndex > rulesIndex);
        Assert.True(amountIndex > amountTypeIndex);

        // Compact + repeated serialization is byte-stable
        Assert.Equal(json, EligibilityRuleSerializer.Serialize(
            EligibilityRuleSerializer.Deserialize(json)));
    }

    [Fact]
    public void Test44_Deserialization_IsTextBasedNotObjectReferenceBased_AndIgnoresKeyOrder()
    {
        var handWritten = "{ \"rules\" : [ { \"amount\" : 100, \"type\" : \"amount_paid_at_least\" }, " +
                          "{ \"type\" : \"contract_active\" } ], \"type\" : \"all_of\" }";

        var parsed = EligibilityRuleSerializer.Deserialize(handWritten);

        Assert.Equal(
            EligibilityRule.AllOf(
                EligibilityRule.AmountPaidAtLeast(100m),
                EligibilityRule.ContractActive()),
            parsed);
    }

    [Fact]
    public void Test45_AllOf_PreservesChildOrder_AndDoesNotSortOrDeduplicate()
    {
        var active = EligibilityRule.ContractActive();
        var noOverdue = EligibilityRule.NoOverdueInstallment();

        var duplicates = (AllOfRule)EligibilityRule.AllOf(active, noOverdue, active);
        Assert.Equal(3, duplicates.Rules.Count);
        Assert.Equal(active, duplicates.Rules[0]);
        Assert.Equal(noOverdue, duplicates.Rules[1]);
        Assert.Equal(active, duplicates.Rules[2]);

        // Reordering is a semantically different commercial rule, not a normalization.
        var oneOrder = EligibilityRule.AllOf(active, noOverdue);
        var otherOrder = EligibilityRule.AllOf(noOverdue, active);
        Assert.NotEqual(oneOrder, otherOrder);
        Assert.NotEqual(
            EligibilityRuleSerializer.Serialize(oneOrder),
            EligibilityRuleSerializer.Serialize(otherOrder));
    }

    [Fact]
    public void Test46_AllRuleTypes_AreImmutable_NoPublicSetters_NoMutableCollections()
    {
        var ruleTypes = typeof(EligibilityRule).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(EligibilityRule).IsAssignableFrom(t))
            .ToList();

        // The closed algebra: exactly the ten specified rule types, nothing speculative.
        Assert.Equal(10, ruleTypes.Count);

        foreach (var type in ruleTypes)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(prop.CanWrite, $"{type.Name}.{prop.Name} must not be publicly settable.");
                Assert.False(prop.PropertyType.IsArray,
                    $"{type.Name}.{prop.Name} must not expose a mutable array.");
                Assert.False(prop.PropertyType.IsGenericType &&
                             prop.PropertyType.GetGenericTypeDefinition() == typeof(List<>),
                    $"{type.Name}.{prop.Name} must expose a read-only collection contract.");
            }
        }
    }

    [Fact]
    public void Test47_NestedEmptyRulesArray_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => EligibilityRuleSerializer.Deserialize("{\"type\":\"all_of\",\"rules\":[]}"));
        Assert.Throws<ArgumentException>(
            () => EligibilityRuleSerializer.Deserialize(
                "{\"type\":\"any_of\",\"rules\":[{\"type\":\"all_of\",\"rules\":[]}]}"));
    }

    [Fact]
    public void Test48_MalformedPayloads_AreRejectedAsArgumentException()
    {
        // Missing / wrong-shaped composite children
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"all_of\"}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"all_of\",\"rules\":\"nope\"}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"all_of\",\"rules\":[\"nope\"]}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"all_of\",\"rules\":[{\"type\":\"bogus_leaf\"}]}"));

        // Wrong JSON value kinds must surface as ArgumentException, never as a raw serializer exception
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"all_of\",\"rules\":5}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":5}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"payment_method_eq\",\"paymentMethod\":123}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"payment_terms_eq\",\"paymentTerms\":null}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"completed_by_utc\",\"completedByUtc\":\"2026-06-01\"}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"amount_paid_at_least\",\"amount\":\"abc\"}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("{\"type\":\"duration_months_gte\",\"months\":\"many\"}"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("[1,2,3]"));
        Assert.Throws<ArgumentException>(() => EligibilityRuleSerializer.Deserialize("null"));
    }

    [Fact]
    public void Test49_Serialization_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => EligibilityRuleSerializer.Serialize(null!));
    }

    [Fact]
    public void Test50_DefaultPhysicalGiftRule_EqualsIndependentlyConstructedExpectedRule()
    {
        var factoryRule = EligibilityRule.DefaultPhysicalGiftRule(4999.99m);

        var expected = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(4999.99m),
            EligibilityRule.NoOverdueInstallment());

        Assert.Equal(expected, factoryRule);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(expected),
            EligibilityRuleSerializer.Serialize(factoryRule));
    }

    [Fact]
    public void Test51_DefaultPhysicalGiftRule_UsesSuppliedContractedAmountVerbatim()
    {
        // The threshold is the supplied value exactly: no rounding, no re-derivation,
        // and no dependency on live contract state.
        foreach (var amount in new[] { 0m, 0.01m, 4999.99m, 1234567.89m })
        {
            var allOf = (AllOfRule)EligibilityRule.DefaultPhysicalGiftRule(amount);
            var amountRule = Assert.IsType<AmountPaidAtLeastRule>(allOf.Rules[1]);
            Assert.Equal(amount, amountRule.Amount);
        }
    }

    [Fact]
    public void Test52_PaymentMethodRule_TrimsInput_AndRejectsBlankValues()
    {
        var rule = (PaymentMethodEqualsRule)EligibilityRule.PaymentMethodEquals("  Cash  ");
        Assert.Equal("CASH", rule.PaymentMethod);

        Assert.Throws<ArgumentException>(() => EligibilityRule.PaymentMethodEquals("   "));
        Assert.Throws<ArgumentException>(() => EligibilityRule.PaymentMethodEquals(null!));
    }

    // ─────────────────────────────────────────────────────────────────
    // 10. Task B.1 — PaymentMethod canonicalisation & equality/serialisation invariant
    //    All rule types must satisfy: equal rules ⇒ identical serialised JSON.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Test53_PaymentMethod_IsCanonicalised_CaseAndWhitespaceVariantsCollapseToSingleStoredValue()
    {
        var cash1 = (PaymentMethodEqualsRule)EligibilityRule.PaymentMethodEquals("Cash");
        var cash2 = (PaymentMethodEqualsRule)EligibilityRule.PaymentMethodEquals(" cash ");
        var cash3 = (PaymentMethodEqualsRule)EligibilityRule.PaymentMethodEquals("CASH");
        var cash4 = (PaymentMethodEqualsRule)EligibilityRule.PaymentMethodEquals("\tCash\n");

        // Canonical stored value
        Assert.Equal("CASH", cash1.PaymentMethod);
        Assert.Equal("CASH", cash2.PaymentMethod);
        Assert.Equal("CASH", cash3.PaymentMethod);
        Assert.Equal("CASH", cash4.PaymentMethod);

        // Structural equality is preserved
        Assert.Equal(cash1, cash2);
        Assert.Equal(cash2, cash3);
        Assert.Equal(cash3, cash4);
        Assert.Equal(cash1, cash4);

        // Equal rules must share the same hash code
        Assert.Equal(cash1.GetHashCode(), cash2.GetHashCode());
        Assert.Equal(cash2.GetHashCode(), cash3.GetHashCode());
        Assert.Equal(cash3.GetHashCode(), cash4.GetHashCode());

        // Equal rules must produce byte-identical serialised JSON
        var json1 = EligibilityRuleSerializer.Serialize(cash1);
        var json2 = EligibilityRuleSerializer.Serialize(cash2);
        var json3 = EligibilityRuleSerializer.Serialize(cash3);
        var json4 = EligibilityRuleSerializer.Serialize(cash4);

        Assert.Equal(json1, json2);
        Assert.Equal(json2, json3);
        Assert.Equal(json3, json4);

        // Round-trip through the serializer must keep canonical form
        var roundTrip = (PaymentMethodEqualsRule)EligibilityRuleSerializer.Deserialize(json1);
        Assert.Equal("CASH", roundTrip.PaymentMethod);
        Assert.Equal(cash1, roundTrip);
    }

    [Fact]
    public void Test54_DifferentPaymentMethods_AreNotEqual_AndProduceDifferentJson()
    {
        var cash = EligibilityRule.PaymentMethodEquals("Cash");
        var card = EligibilityRule.PaymentMethodEquals("Card");
        var transfer = EligibilityRule.PaymentMethodEquals("BankTransfer");

        Assert.NotEqual(cash, card);
        Assert.NotEqual(cash, transfer);
        Assert.NotEqual(card, transfer);

        Assert.NotEqual(
            EligibilityRuleSerializer.Serialize(cash),
            EligibilityRuleSerializer.Serialize(card));

        // PaymentMethodEqualsRule must NOT equal PaymentTermsEqualsRule (different rule types)
        var ptRule = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        Assert.NotEqual(cash, ptRule);
    }

    [Fact]
    public void Test55_EqualityImpliesIdenticalJson_ForAllTenRuleTypes()
    {
        // Build each of the 10 rule types from two independent constructions and assert
        // that structural equality implies byte-identical serialised JSON. This enforces the
        // canonical-snapshot invariant for every rule type, not just PaymentMethodEqualsRule.

        // 1. AllOf
        var allOf1 = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(100m),
            EligibilityRule.NoOverdueInstallment());
        var allOf2 = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(100m),
            EligibilityRule.NoOverdueInstallment());
        Assert.Equal(allOf1, allOf2);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(allOf1),
            EligibilityRuleSerializer.Serialize(allOf2));

        // 2. AnyOf
        var anyOf1 = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.PaymentMethodEquals("Cash"));
        var anyOf2 = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.PaymentMethodEquals("CASH"));
        Assert.Equal(anyOf1, anyOf2);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(anyOf1),
            EligibilityRuleSerializer.Serialize(anyOf2));

        // 3. ContractActive
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(EligibilityRule.ContractActive()),
            EligibilityRuleSerializer.Serialize(EligibilityRule.ContractActive()));

        // 4. PaymentTermsEqualsRule (must use enum-defined value, serialised by enum name)
        var pt1 = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        var pt2 = EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront);
        Assert.Equal(pt1, pt2);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(pt1),
            EligibilityRuleSerializer.Serialize(pt2));

        // 5. PaymentMethodEqualsRule (canonical form invariant — explicit assertion here)
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(EligibilityRule.PaymentMethodEquals("Cash")),
            EligibilityRuleSerializer.Serialize(EligibilityRule.PaymentMethodEquals("CASH")));

        // 6. CompletedByUtcRule (round-trip kind is preserved)
        var utc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var c1 = EligibilityRule.CompletedByUtc(utc);
        var c2 = EligibilityRule.CompletedByUtc(utc);
        Assert.Equal(c1, c2);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(c1),
            EligibilityRuleSerializer.Serialize(c2));

        // 7. NoOverdueInstallment
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(EligibilityRule.NoOverdueInstallment()),
            EligibilityRuleSerializer.Serialize(EligibilityRule.NoOverdueInstallment()));

        // 8. AmountPaidAtLeast
        var a1 = EligibilityRule.AmountPaidAtLeast(5000m);
        var a2 = EligibilityRule.AmountPaidAtLeast(5000m);
        Assert.Equal(a1, a2);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(a1),
            EligibilityRuleSerializer.Serialize(a2));

        // 9. DaysFromContractStartGte
        var d1 = EligibilityRule.DaysFromContractStartGte(30);
        var d2 = EligibilityRule.DaysFromContractStartGte(30);
        Assert.Equal(d1, d2);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(d1),
            EligibilityRuleSerializer.Serialize(d2));

        // 10. DurationMonthsGte
        var m1 = EligibilityRule.DurationMonthsGte(12);
        var m2 = EligibilityRule.DurationMonthsGte(12);
        Assert.Equal(m1, m2);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(m1),
            EligibilityRuleSerializer.Serialize(m2));

        // Deep nested composite — round-trips through JSON
        var nested = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("  cash  ")),
            EligibilityRule.AmountPaidAtLeast(5000m));
        var nestedCopy = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("CASH")),
            EligibilityRule.AmountPaidAtLeast(5000m));
        Assert.Equal(nested, nestedCopy);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(nested),
            EligibilityRuleSerializer.Serialize(nestedCopy));
    }
}
