namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.Events;

/// <summary>
/// A commercial benefit (gift) granted as part of a Contract agreement.
/// Preserves its contractual financial value as an immutable snapshot so future
/// commercial configuration changes do not alter the contractual terms.
/// </summary>
/// <remarks>
/// Examples:
///   Physical Gift: Barcode Printer, Value = 1,500
///   Physical Gift: Desktop Computer, Value = 8,000
///
/// The financial invariant requires that the total value of all benefits under a Contract
/// must not exceed three months of the customer's contractual monthly value.
///
/// Delivery lifecycle: NotEligible → Eligible → Delivered
/// - NotEligible: configured on contract, conditions not yet met
/// - Eligible: contractual conditions satisfied, ready for delivery
/// - Delivered: physical gift handed to customer (IsGranted = true)
///
/// A benefit that was never delivered must not generate a recovery deduction.
/// Once delivered, the benefit's snapshot fields (Name, Value, Type, Currency) are immutable.
/// </remarks>
public class ContractBenefit : Entity
{
    public Guid Id { get; private set; }
    public Guid ContractId { get; private set; }

    /// <summary>Category/type of benefit (e.g., PhysicalGift, Service).</summary>
    public ContractBenefitType BenefitType { get; private set; }

    /// <summary>Human-readable name of the benefit.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Optional description providing additional detail.</summary>
    public string? Description { get; private set; }

    /// <summary>Contractual value of this benefit at the time of contract creation (immutable).</summary>
    public decimal ContractualValue { get; private set; }

    /// <summary>Currency code (ISO-4217, e.g., EGP, USD).</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>Delivery lifecycle status.</summary>
    public BenefitEligibilityStatus EligibilityStatus { get; private set; }

    /// <summary>UTC timestamp when the benefit became eligible.</summary>
    public DateTime? EligibleAtUtc { get; private set; }

    /// <summary>Whether this benefit has been actually granted/delivered to the tenant.</summary>
    public bool IsGranted { get; private set; }

    /// <summary>UTC timestamp when the benefit was granted/delivered.</summary>
    public DateTime? GrantedAtUtc { get; private set; }

    /// <summary>ID of the user who delivered the benefit (audit trail).</summary>
    public string? DeliveredBy { get; private set; }

    /// <summary>The Contract this benefit belongs to.</summary>
    public Contract Contract { get; private set; } = default!;

    private ContractBenefit() { }

    private ContractBenefit(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode)
    {
        Id = id;
        ContractId = contractId;
        BenefitType = benefitType;
        Name = name;
        Description = description;
        ContractualValue = contractualValue;
        CurrencyCode = currencyCode;
        EligibilityStatus = BenefitEligibilityStatus.NotEligible;
    }

    /// <summary>
    /// Creates a ContractBenefit with validated parameters.
    /// </summary>
    public static Result<ContractBenefit> Create(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode)
    {
        if (id == Guid.Empty)
            return ContractErrors.Benefit.IdRequired;

        if (contractId == Guid.Empty)
            return ContractErrors.Benefit.ContractIdRequired;

        if (string.IsNullOrWhiteSpace(name))
            return ContractErrors.Benefit.NameRequired;

        if (contractualValue < 0)
            return ContractErrors.Benefit.InvalidValue;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return ContractErrors.Benefit.InvalidCurrency;

        return new ContractBenefit(
            id,
            contractId,
            benefitType,
            name.Trim(),
            description?.Trim(),
            contractualValue,
            currencyCode.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Transitions the benefit from NotEligible to Eligible.
    /// Must be called before MarkGranted/Deliver for physical gifts.
    /// Idempotent: returns success if already eligible or delivered.
    /// </summary>
    public Result<Updated> MarkEligible(DateTime utcNow)
    {
        if (EligibilityStatus == BenefitEligibilityStatus.Delivered)
            return Result.Updated;

        if (EligibilityStatus == BenefitEligibilityStatus.Eligible)
            return Result.Updated;

        EligibilityStatus = BenefitEligibilityStatus.Eligible;
        EligibleAtUtc = utcNow;

        AddDomainEvent(new BenefitEligibleEvent(ContractId, Id, string.Empty));

        return Result.Updated;
    }

    /// <summary>
    /// Marks this benefit as granted/delivered to the tenant.
    /// For physical gifts, this records the physical handover.
    /// Idempotent: returns success if already granted.
    /// </summary>
    /// <remarks>
    /// Delivery creates an immutable audit record. Once delivered, the benefit's
    /// snapshot fields (Name, Value, Type, Currency) cannot be silently changed.
    /// </remarks>
    public Result<Updated> MarkGranted(DateTime utcNow, string? deliveredBy = null)
    {
        if (IsGranted)
            return Result.Updated;

        IsGranted = true;
        GrantedAtUtc = utcNow;
        DeliveredBy = deliveredBy;
        EligibilityStatus = BenefitEligibilityStatus.Delivered;

        AddDomainEvent(new BenefitDeliveredEvent(ContractId, Id, string.Empty, utcNow, deliveredBy));

        return Result.Updated;
    }

    /// <summary>
    /// Calculates the consumed value of this benefit based on elapsed contract duration.
    /// Uses day-based formula: ConsumedValue = ContractualValue × ElapsedDays / DurationDays
    /// </summary>
    /// <param name="elapsedDays">Days elapsed since contract effective date.</param>
    /// <param name="contractDurationDays">Total contract duration in days (EndsAtUtc - EffectiveAtUtc).</param>
    /// <returns>Consumed value, clamped to [0, ContractualValue].</returns>
    public decimal CalculateConsumedValue(int elapsedDays, int contractDurationDays)
    {
        if (ContractualValue <= 0)
            return 0;

        if (elapsedDays <= 0)
            return 0;

        if (contractDurationDays <= 0)
            return ContractualValue;

        if (elapsedDays >= contractDurationDays)
            return ContractualValue;

        var ratio = (decimal)elapsedDays / (decimal)contractDurationDays;
        return Math.Round(ContractualValue * ratio, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Calculates the remaining (unconsumed) value of this benefit.
    /// </summary>
    /// <param name="elapsedDays">Days elapsed since contract effective date.</param>
    /// <param name="contractDurationDays">Total contract duration in days.</param>
    /// <returns>Remaining value, always >= 0.</returns>
    public decimal CalculateRemainingValue(int elapsedDays, int contractDurationDays)
    {
        var consumed = CalculateConsumedValue(elapsedDays, contractDurationDays);
        return ContractualValue - consumed;
    }

    /// <summary>Whether this benefit is eligible for delivery.</summary>
    public bool IsEligibleForDelivery =>
        EligibilityStatus == BenefitEligibilityStatus.Eligible;

    /// <summary>Whether this benefit has been delivered.</summary>
    public bool IsDelivered => IsGranted;
}
