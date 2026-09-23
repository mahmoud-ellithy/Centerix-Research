namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Common.Results;
using Xunit;

/// <summary>
/// Task 18.5 — domain-level tests for the economic-origin lineage on TenantCredit.
///
/// Coverage:
///   • CreateSubscriptionChange factory: immutable lineage composition + validation bounds
///     (0 &lt;= TransferredPaidAmount &lt;= Amount — the no-multiplication domain guard).
///   • Economic-origin classification (spec §9): direct customer-paid (Overpayment),
///     transferred customer-paid (SubscriptionChange), non-customer-paid (ReferralReward,
///     Promotional, Compensation, Manual → zero customer-paid economic value).
///   • Generic Create backward compatibility (legacy path defaults to direct origin).
///   • Lineage immutability across the credit lifecycle (consume/expire/revoke/reverse).
///   • Section-21 invariants at the domain level: RemainingAmount &lt;= Amount and
///     consumption can never exceed the remaining amount.
/// </summary>
public class Task18_5CreditEconomicOriginLineageTests
{
    private static readonly Guid SourceId = Guid.NewGuid();

    private static Result<TenantCredit> CreateSubChange(
        decimal amount, decimal transferred, string currency = "EGP") =>
        TenantCredit.CreateSubscriptionChange(
            Guid.NewGuid(), amount, SourceId, transferred, currency, $"185-{Guid.NewGuid():N}");

    // ==================================================================
    // CreateSubscriptionChange — lineage composition
    // ==================================================================

    [Fact]
    public void CreateSubscriptionChange_ValidLineage_PersistsComposition()
    {
        var result = CreateSubChange(8000m, 4000m);

        Assert.True(result.IsSuccess);
        var credit = result.Value;
        Assert.Equal(CreditSourceType.SubscriptionChange, credit.SourceType);
        Assert.Equal(SourceId, credit.SourceId);
        Assert.Equal(CreditStatus.Available, credit.Status);
        Assert.Equal(8000m, credit.Amount);
        Assert.Equal(8000m, credit.RemainingAmount);
        Assert.Equal(4000m, credit.TransferredPaidAmount);
        Assert.Equal(4000m, credit.DirectPaidAmount);
        Assert.Equal(8000m, credit.CustomerPaidEconomicValue);
    }

    [Fact]
    public void CreateSubscriptionChange_Boundaries_TransferredZeroAndEqualToAmount()
    {
        // Boundary 0: the whole credit is direct customer-paid origin.
        var direct = CreateSubChange(8000m, 0m);
        Assert.True(direct.IsSuccess);
        Assert.Equal(0m, direct.Value.TransferredPaidAmount);
        Assert.Equal(8000m, direct.Value.DirectPaidAmount);

        // Boundary = Amount: the whole credit is transferred origin (multi-generation tail).
        var fullyTransferred = CreateSubChange(8000m, 8000m);
        Assert.True(fullyTransferred.IsSuccess);
        Assert.Equal(8000m, fullyTransferred.Value.TransferredPaidAmount);
        Assert.Equal(0m, fullyTransferred.Value.DirectPaidAmount);
    }

    [Fact]
    public void CreateSubscriptionChange_RejectsLineageOutsideBounds()
    {
        // Transferred value below zero is meaningless.
        var negative = CreateSubChange(8000m, -0.01m);
        Assert.False(negative.IsSuccess);
        Assert.Contains(negative.Errors!, e => e.Code == TenantCreditErrors.InvalidTransferredPaidAmount.Code);

        // Transferred value above the credit amount would multiply customer-paid value.
        var excessive = CreateSubChange(8000m, 8000.01m);
        Assert.False(excessive.IsSuccess);
        Assert.Contains(excessive.Errors!, e => e.Code == TenantCreditErrors.InvalidTransferredPaidAmount.Code);

        // A credit must still carry a positive amount.
        var zeroAmount = CreateSubChange(0m, 0m);
        Assert.False(zeroAmount.IsSuccess);
        Assert.Contains(zeroAmount.Errors!, e => e.Code == TenantCreditErrors.InvalidAmount.Code);

        // Currency is required so lineage never crosses currencies silently.
        var noCurrency = CreateSubChange(100m, 0m, " ");
        Assert.False(noCurrency.IsSuccess);
        Assert.Contains(noCurrency.Errors!, e => e.Code == "TenantCredit.CurrencyRequired");
    }

    [Fact]
    public void CreateSubscriptionChange_NormalizesCurrencyToInvariantUpper()
    {
        var result = CreateSubChange(100m, 40m, "egp");

        Assert.True(result.IsSuccess);
        Assert.Equal("EGP", result.Value.CurrencyCode);
    }

    // ==================================================================
    // Economic-origin classification (spec §9 — categories A, B, C)
    // ==================================================================

    [Fact]
    public void EconomicOrigin_Overpayment_IsDirectCustomerPaid()
    {
        var result = TenantCredit.Create(
            Guid.NewGuid(), 5000m, CreditSourceType.Overpayment, Guid.NewGuid(), "EGP");

        Assert.True(result.IsSuccess);
        var credit = result.Value;
        Assert.Equal(5000m, credit.CustomerPaidEconomicValue);
        Assert.Equal(5000m, credit.DirectPaidAmount);
        Assert.Equal(0m, credit.TransferredPaidAmount);
    }

    [Theory]
    [InlineData(6000d, 2000d, 4000d)]   // partial transfer
    [InlineData(6000d, 0d, 6000d)]      // fully direct
    [InlineData(6000d, 6000d, 0d)]      // fully transferred (3rd generation tail)
    public void EconomicOrigin_SubscriptionChange_SplitsDirectAndTransferred(
        double amount, double transferred, double expectedDirect)
    {
        var result = CreateSubChange((decimal)amount, (decimal)transferred);

        Assert.True(result.IsSuccess);
        var credit = result.Value;
        // The whole SubscriptionChange credit is customer-paid economic value…
        Assert.Equal((decimal)amount, credit.CustomerPaidEconomicValue);
        // …split into transferred vs direct origin without ever exceeding the amount.
        Assert.Equal((decimal)transferred, credit.TransferredPaidAmount);
        Assert.Equal((decimal)expectedDirect, credit.DirectPaidAmount);
        Assert.Equal(credit.TransferredPaidAmount + credit.DirectPaidAmount, credit.Amount);
    }

    [Theory]
    [InlineData(CreditSourceType.ReferralReward)]
    [InlineData(CreditSourceType.Promotional)]
    [InlineData(CreditSourceType.Compensation)]
    [InlineData(CreditSourceType.Manual)]
    public void EconomicOrigin_GrantedSources_CarryZeroCustomerPaidValue(CreditSourceType sourceType)
    {
        var result = TenantCredit.Create(
            Guid.NewGuid(), 4000m, sourceType, null, "EGP");

        Assert.True(result.IsSuccess);
        var credit = result.Value;
        // Granted/free/discretionary value never becomes customer-paid economic value.
        Assert.Equal(0m, credit.CustomerPaidEconomicValue);
        Assert.Equal(0m, credit.DirectPaidAmount);
        Assert.Equal(0m, credit.TransferredPaidAmount);
    }

    [Fact]
    public void GenericCreate_BackwardCompatible_DefaultsToDirectOrigin()
    {
        // The pre-18.5 generic factory keeps its exact behavior: no lineage composition
        // is known on the generic path, so eligible sources default to direct origin.
        var subChange = TenantCredit.Create(
            Guid.NewGuid(), 7000m, CreditSourceType.SubscriptionChange, SourceId, "EGP");
        Assert.True(subChange.IsSuccess);
        Assert.Equal(0m, subChange.Value.TransferredPaidAmount);
        Assert.Equal(7000m, subChange.Value.DirectPaidAmount);

        var overpayment = TenantCredit.Create(
            Guid.NewGuid(), 3000m, CreditSourceType.Overpayment, Guid.NewGuid(), "EGP");
        Assert.True(overpayment.IsSuccess);
        Assert.Equal(0m, overpayment.Value.TransferredPaidAmount);
        Assert.Equal(3000m, overpayment.Value.DirectPaidAmount);
    }

    // ==================================================================
    // Lineage immutability across the credit lifecycle
    // ==================================================================

    [Fact]
    public void Lifecycle_PartialConsumption_PreservesImmutableLineage()
    {
        var credit = CreateSubChange(6000m, 4000m).Value;

        var consume = credit.ConsumeAmount(2500m);
        Assert.True(consume.IsSuccess);

        // Section 21: Credit.RemainingAmount <= Credit.Amount, always.
        Assert.Equal(3500m, credit.RemainingAmount);
        Assert.True(credit.RemainingAmount <= credit.Amount);

        // The lineage composition is immutable: consumption moves RemainingAmount only.
        Assert.Equal(4000m, credit.TransferredPaidAmount);
        Assert.Equal(2000m, credit.DirectPaidAmount);
        Assert.Equal(CreditStatus.PartiallyApplied, credit.Status);

        var consumeRest = credit.ConsumeAmount(3500m);
        Assert.True(consumeRest.IsSuccess);
        Assert.Equal(0m, credit.RemainingAmount);
        Assert.Equal(CreditStatus.Applied, credit.Status);
        Assert.Equal(4000m, credit.TransferredPaidAmount);
    }

    [Fact]
    public void Lifecycle_TerminalTransitions_PreserveImmutableLineage()
    {
        var expired = CreateSubChange(100m, 60m).Value;
        Assert.True(expired.Expire().IsSuccess);
        Assert.Equal(60m, expired.TransferredPaidAmount);

        var revoked = CreateSubChange(100m, 60m).Value;
        Assert.True(revoked.Revoke().IsSuccess);
        Assert.Equal(60m, revoked.TransferredPaidAmount);

        var reversed = CreateSubChange(100m, 60m).Value;
        Assert.True(reversed.Reverse(Guid.NewGuid()).IsSuccess);
        Assert.Equal(60m, reversed.TransferredPaidAmount);
    }

    // ==================================================================
    // Section 21 domain invariants
    // ==================================================================

    [Fact]
    public void ConsumeAmount_RejectsAmountBeyondRemaining()
    {
        var credit = CreateSubChange(2000m, 1000m).Value;

        // CreditApplication.Amount <= Credit.RemainingAmount before application:
        // an application larger than the remaining balance must be refused.
        var over = credit.ConsumeAmount(2000.01m);
        Assert.False(over.IsSuccess);
        Assert.Contains(over.Errors!, e => e.Code == TenantCreditErrors.InsufficientRemaining.Code);
        Assert.Equal(2000m, credit.RemainingAmount);

        var invalid = credit.ConsumeAmount(0m);
        Assert.False(invalid.IsSuccess);
    }

    [Fact]
    public void NoMultiplication_TransferredNeverExceedsAmount_AcrossGenerations()
    {
        // One original 4,000 paid value flows through three generations (each generation's
        // credit REPLACES the prior one — the prior credit is consumed as it settles the
        // old contract's invoice). The domain-level no-multiplication guards are:
        //   1. per generation: TransferredPaidAmount <= Amount <= the credit ever represents;
        //   2. the factory refuses any attribution beyond the credit's own amount.
        // (The historical Σ TransferredPaidAmount intentionally counts consumed value once
        // per generation and is bounded at each hop by what the prior generation actually
        // settled — that handler-level bound is proven by the SQL Server multi-generation
        // tests; the concurrent outstanding-value bound is Σ RemainingAmount.)
        var gen1 = CreateSubChange(4000m, 0m).Value;        // generation 1: direct paid origin
        var gen2 = CreateSubChange(4000m, 4000m).Value;     // generation 2: fully transferred
        var gen3 = CreateSubChange(4000m, 4000m).Value;     // generation 3: still fully transferred

        Assert.All(new[] { gen1, gen2, gen3 }, g => Assert.True(g.TransferredPaidAmount <= g.Amount));
        Assert.All(new[] { gen1, gen2, gen3 }, g => Assert.True(g.TransferredPaidAmount <= 4000m));
        Assert.All(new[] { gen1, gen2, gen3 }, g => Assert.Equal(4000m, g.CustomerPaidEconomicValue));

        // A would-be multiplying credit (transferred > amount) is not constructible.
        var multiply = CreateSubChange(4000m, 4000.01m);
        Assert.False(multiply.IsSuccess);
        Assert.Contains(multiply.Errors!, e => e.Code == TenantCreditErrors.InvalidTransferredPaidAmount.Code);
    }
}
