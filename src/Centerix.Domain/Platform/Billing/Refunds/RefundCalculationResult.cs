namespace Centerix.Domain.Platform.Billing.Refunds;

using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents the result of a cancellation refund calculation.
/// This is a deterministic, side-effect-free calculation that determines
/// the financial outcome of an early cancellation.
/// </summary>
/// <remarks>
/// The calculation follows these rules:
/// 1. Used subscription amount is based on historical Contract pricing tiers (NOT prorated).
/// 2. Gift recovery is based on unconsumed economic value (day-based calculation).
/// 3. Refund is based on actual successful payments (Payment + PaymentAllocation).
/// 4. Negative refund result means the customer owes money (outstanding amount).
/// </remarks>
public sealed record RefundCalculationResult
{
    /// <summary>The contract ID this calculation is for.</summary>
    public Guid ContractId { get; init; }

    /// <summary>The number of whole months elapsed since contract effective date.</summary>
    public int ElapsedMonths { get; init; }

    /// <summary>The total number of days elapsed since contract effective date.</summary>
    public int ElapsedDays { get; init; }

    /// <summary>The original contract duration in months.</summary>
    public int ContractDurationMonths { get; init; }

    /// <summary>The original contract duration in days.</summary>
    public int ContractDurationDays { get; init; }

    /// <summary>The amount of subscription value consumed (based on pricing tiers).</summary>
    public decimal UsedSubscriptionAmount { get; init; }

    /// <summary>The total value of benefits/gifts granted under the contract.</summary>
    public decimal TotalBenefitValue { get; init; }

    /// <summary>The consumed value of benefits/gifts (based on elapsed duration).</summary>
    public decimal ConsumedBenefitValue { get; init; }

    /// <summary>The remaining (unconsumed) value of benefits/gifts to be recovered.</summary>
    public decimal RemainingBenefitValue { get; init; }

    /// <summary>The total customer economic obligation (used subscription + remaining benefits).</summary>
    public decimal CustomerEconomicObligation { get; init; }

    /// <summary>The total amount actually paid by the customer (successful payments only).</summary>
    public decimal AmountActuallyPaid { get; init; }

    /// <summary>The refundable amount (AmountActuallyPaid - CustomerEconomicObligation).</summary>
    public decimal RefundableAmount { get; init; }

    /// <summary>The actual refund amount (0 if customer owes money).</summary>
    public decimal RefundAmount { get; init; }

    /// <summary>The outstanding amount the customer owes (0 if refund is due).</summary>
    public decimal CustomerOutstandingAmount { get; init; }

    /// <summary>Indicates whether the customer is entitled to a refund.</summary>
    public bool IsRefundDue => RefundAmount > 0;

    /// <summary>Indicates whether the customer owes money.</summary>
    public bool IsAmountOwed => CustomerOutstandingAmount > 0;

    /// <summary>The currency code for all monetary values.</summary>
    public string CurrencyCode { get; init; } = "EGP";

    /// <summary>Breakdown of payments that contribute to the amount actually paid.</summary>
    public IReadOnlyList<PaymentContribution> PaymentContributions { get; init; } = Array.Empty<PaymentContribution>();

    /// <summary>Breakdown of benefits and their consumption.</summary>
    public IReadOnlyList<BenefitContribution> BenefitContributions { get; init; } = Array.Empty<BenefitContribution>();
}

/// <summary>
/// Represents a payment's contribution to the amount actually paid.
/// </summary>
public sealed record PaymentContribution
{
    public Guid PaymentId { get; init; }
    public string PaymentNumber { get; init; } = default!;
    public decimal Amount { get; init; }
    public decimal AllocatedAmount { get; init; }
    public PaymentMethod Method { get; init; }
}

/// <summary>
/// Represents a benefit's contribution to the economic obligation.
/// </summary>
public sealed record BenefitContribution
{
    public Guid BenefitId { get; init; }
    public string Name { get; init; } = default!;
    public decimal ContractualValue { get; init; }
    public decimal ConsumedValue { get; init; }
    public decimal RemainingValue { get; init; }
    public bool IsRecoverable { get; init; }
}
