namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
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
/// The PhysicalGift benefit tracks TWO independent state machines:
///
/// <list type="number">
///   <item>
///     <term>Eligibility (reversible)</term>
///     <description>
///       <see cref="EligibilityStatus"/> ∈ { NotEligible, Eligible }.
///       Updated by the eligibility evaluator; never moves fulfillment backward.
///     </description>
///   </item>
///   <item>
///     <term>Fulfillment (monotone)</term>
///     <description>
///       <see cref="FulfillmentStatus"/> ∈ { Pending, Granted, Delivered }.
///       Pending → Granted via <see cref="Grant"/>; Granted → Delivered via <see cref="Deliver"/>.
///       Once <c>Delivered</c>, terminal. PhysicalGift MUST NOT use
///       <see cref="FulfillmentStatus.AppliedToSubscription"/>.
///     </description>
///   </item>
/// </list>
///
/// A benefit that was never granted/delivered must not generate a recovery deduction.
/// Once granted, the benefit's snapshot fields (Name, Value, Type, Currency) are immutable.
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

    /// <summary>
    /// Reversible eligibility status. Independent of <see cref="FulfillmentStatus"/>:
    /// eligibility changes MUST NEVER move fulfillment backward.
    /// </summary>
    public BenefitEligibilityStatus EligibilityStatus { get; private set; }

    /// <summary>UTC timestamp when the benefit became eligible.</summary>
    public DateTime? EligibleAtUtc { get; private set; }

    /// <summary>
    /// Monotone fulfillment status for <c>ContractBenefit</c> PhysicalGift benefits.
    /// Lifecycle: Pending → Granted → Delivered. Terminal at <c>Delivered</c>.
    /// <see cref="FulfillmentStatus.AppliedToSubscription"/> is never valid here.
    /// </summary>
    public FulfillmentStatus FulfillmentStatus { get; private set; }

    /// <summary>UTC timestamp when the benefit was granted.</summary>
    public DateTime? GrantedAtUtc { get; private set; }

    /// <summary>ID of the user who granted the benefit (audit trail).</summary>
    public string? GrantedBy { get; private set; }

    /// <summary>UTC timestamp when the benefit was delivered (physical handover).</summary>
    public DateTime? DeliveredAtUtc { get; private set; }

    /// <summary>ID of the user who delivered the benefit (audit trail).</summary>
    public string? DeliveredBy { get; private set; }

    /// <summary>The Contract this benefit belongs to.</summary>
    public Contract Contract { get; private set; } = default!;

    /// <summary>
    /// The immutable commercial eligibility rule snapshot attached to this benefit.
    /// Describes WHAT conditions must hold for the benefit to become eligible.
    /// Nullable: existing benefits created before per-benefit rules were introduced
    /// carry a null rule; the global legacy rule continues to apply for those rows
    /// via the existing <see cref="Centerix.Infrastructure.Platform.Services.BenefitEligibilityService"/>.
    /// </summary>
    public EligibilityRule? EligibilityRule { get; private set; }

    private ContractBenefit() { }

    private ContractBenefit(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode,
        EligibilityRule? eligibilityRule)
    {
        Id = id;
        ContractId = contractId;
        BenefitType = benefitType;
        Name = name;
        Description = description;
        ContractualValue = contractualValue;
        CurrencyCode = currencyCode;
        EligibilityStatus = BenefitEligibilityStatus.NotEligible;
        FulfillmentStatus = FulfillmentStatus.Pending;
        EligibilityRule = eligibilityRule;
    }

    /// <summary>
    /// Creates a ContractBenefit with validated parameters.
    /// </summary>
    /// <param name="eligibilityRule">
    /// Optional commercial eligibility rule snapshot. When null, the legacy global rule
    /// continues to apply via <see cref="Centerix.Infrastructure.Platform.Services.BenefitEligibilityService"/>.
    /// </param>
    public static Result<ContractBenefit> Create(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode,
        EligibilityRule? eligibilityRule = null)
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
            currencyCode.Trim().ToUpperInvariant(),
            eligibilityRule);
    }

    /// <summary>
    /// Transitions the benefit from NotEligible to Eligible.
    /// Idempotent: returns success if already eligible. Does NOT mutate fulfillment.
    /// </summary>
    public Result<Updated> MarkEligible(DateTime utcNow, string? tenantId = null)
    {
        if (EligibilityStatus == BenefitEligibilityStatus.Eligible)
            return Result.Updated;

        EligibilityStatus = BenefitEligibilityStatus.Eligible;
        EligibleAtUtc = utcNow;

        AddDomainEvent(new BenefitEligibleEvent(ContractId, Id, tenantId ?? string.Empty));

        return Result.Updated;
    }

    /// <summary>
    /// Transitions the benefit from Eligible back to NotEligible.
    /// Idempotent. Does NOT mutate fulfillment — a delivered benefit remains
    /// delivered even if current eligibility flips back to NotEligible.
    /// </summary>
    public Result<Updated> MarkNotEligible()
    {
        if (EligibilityStatus == BenefitEligibilityStatus.NotEligible)
            return Result.Updated;

        EligibilityStatus = BenefitEligibilityStatus.NotEligible;
        return Result.Updated;
    }

    /// <summary>
    /// Records the explicit GRANT decision. Transitions fulfillment from Pending → Granted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules:
    /// <list type="bullet">
    ///   <item><description>BenefitType MUST be <see cref="ContractBenefitType.PhysicalGift"/>.</description></item>
    ///   <item><description><see cref="EligibilityStatus"/> MUST be <see cref="BenefitEligibilityStatus.Eligible"/>.</description></item>
    ///   <item><description><see cref="FulfillmentStatus"/> MUST be <see cref="FulfillmentStatus.Pending"/>.</description></item>
    ///   <item><description>Idempotent: if already Granted or Delivered, returns success without mutating fields.</description></item>
    ///   <item><description>Stamps <c>GrantedAtUtc</c> and <c>GrantedBy</c> on first transition.</description></item>
    ///   <item><description>Does NOT set Delivered; does NOT mutate <see cref="EligibilityStatus"/>.</description></item>
    /// </para>
    /// </remarks>
    public Result<Updated> Grant(DateTime utcNow, string? grantedBy = null, string? tenantId = null)
    {
        // Only PhysicalGift benefits participate in the grant/delivery lifecycle.
        if (BenefitType != ContractBenefitType.PhysicalGift)
            return ContractErrors.Benefit.OnlyPhysicalGiftCanBeDelivered;

        // Already granted or delivered — idempotent.
        if (FulfillmentStatus == FulfillmentStatus.Granted ||
            FulfillmentStatus == FulfillmentStatus.Delivered)
        {
            return Result.Updated;
        }

        // Must be eligible before grant.
        if (EligibilityStatus != BenefitEligibilityStatus.Eligible)
            return ContractErrors.Benefit.NotEligible;

        if (FulfillmentStatus != FulfillmentStatus.Pending)
            return ContractErrors.Benefit.InvalidFulfillmentTransition;

        FulfillmentStatus = FulfillmentStatus.Granted;
        GrantedAtUtc = utcNow;
        GrantedBy = grantedBy;

        AddDomainEvent(new BenefitGrantedEvent(ContractId, Id, tenantId ?? string.Empty, utcNow, grantedBy));

        return Result.Updated;
    }

    /// <summary>
    /// Records the physical DELIVERY. Transitions fulfillment from Granted → Delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules:
    /// <list type="bullet">
    ///   <item><description>BenefitType MUST be <see cref="ContractBenefitType.PhysicalGift"/>.</description></item>
    ///   <item><description><see cref="FulfillmentStatus"/> MUST be <see cref="FulfillmentStatus.Granted"/>.</description></item>
    ///   <item><description>Idempotent: if already Delivered, returns success without mutating fields.</description></item>
    ///   <item><description>Stamps <c>DeliveredAtUtc</c> and <c>DeliveredBy</c> on first transition.</description></item>
    ///   <item><description>Does NOT mutate <see cref="EligibilityStatus"/>.</description></item>
    ///   <item><description>Terminal — fulfillment MUST NOT move backward.</description></item>
    /// </para>
    /// </remarks>
    public Result<Updated> Deliver(DateTime utcNow, string? deliveredBy = null, string? tenantId = null)
    {
        if (BenefitType != ContractBenefitType.PhysicalGift)
            return ContractErrors.Benefit.OnlyPhysicalGiftCanBeDelivered;

        // Already delivered — idempotent.
        if (FulfillmentStatus == FulfillmentStatus.Delivered)
            return Result.Updated;

        // Must be granted before delivery.
        if (FulfillmentStatus != FulfillmentStatus.Granted)
            return ContractErrors.Benefit.NotGranted;

        FulfillmentStatus = FulfillmentStatus.Delivered;
        DeliveredAtUtc = utcNow;
        DeliveredBy = deliveredBy;

        AddDomainEvent(new BenefitDeliveredEvent(ContractId, Id, tenantId ?? string.Empty, utcNow, deliveredBy));

        return Result.Updated;
    }

    /// <summary>
    /// Calculates the consumed value of this benefit based on elapsed contract duration.
    /// Uses day-based formula: ConsumedValue = ContractualValue × ElapsedDays / DurationDays
    /// </summary>
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
    public decimal CalculateRemainingValue(int elapsedDays, int contractDurationDays)
    {
        var consumed = CalculateConsumedValue(elapsedDays, contractDurationDays);
        return ContractualValue - consumed;
    }

    /// <summary>Whether this benefit is eligible for grant.</summary>
    public bool IsEligibleForGrant =>
        BenefitType == ContractBenefitType.PhysicalGift
        && EligibilityStatus == BenefitEligibilityStatus.Eligible
        && FulfillmentStatus == FulfillmentStatus.Pending;

    /// <summary>
    /// Projection of <see cref="FulfillmentStatus"/>: true iff the benefit has been
    /// granted (or delivered). MUST NOT be set independently of <see cref="FulfillmentStatus"/>;
    /// the EF column is only kept for backward-compatible external readers.
    /// </summary>
    public bool IsGranted =>
        FulfillmentStatus == FulfillmentStatus.Granted
        || FulfillmentStatus == FulfillmentStatus.Delivered;

    /// <summary>Whether this benefit has been delivered. Projection of <see cref="FulfillmentStatus"/>.</summary>
    public bool IsDelivered => FulfillmentStatus == FulfillmentStatus.Delivered;
}