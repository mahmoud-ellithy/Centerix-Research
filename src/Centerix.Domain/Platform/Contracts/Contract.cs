namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.Events;
using Centerix.Domain.Platform.Subscriptions;

/// <summary>
/// Root aggregate for a commercial Contract between a Tenant and the platform.
/// Represents the actual commercial agreement (as opposed to the catalog Plan).
/// Preserves an immutable commercial snapshot so future Plan price changes
/// do not alter historical Contract terms.
/// </summary>
/// <remarks>
/// Commercial snapshot rationale:
/// - MonthlyListPrice, ContractualMonthlyValue, PricingTiers, ContractedAmount: the agreed
///   commercial terms; a later plan repricing must not change what the tenant pays under this Contract.
/// - ContractualMonthlyValue: the explicit monthly value used for the 3-month benefit cap calculation.
///   This is distinct from Plan list price and is frozen at contract creation.
/// - EffectiveAtUtc/EndsAtUtc, DurationMonths: the agreed contract term frozen at creation.
///
/// Lifecycle: Draft -> PendingApproval -> Active -> Suspended -> Expired/Terminated
///
/// A Contract can have multiple Subscriptions over its lifecycle/renewals.
/// </remarks>
public class Contract : AuditableEntity<Guid>
{
    /// <summary>Human-readable contract number/reference.</summary>
    public string ContractNumber { get; private set; } = default!;

    /// <summary>Current contract status (domain-controlled).</summary>
    public ContractStatus Status { get; private set; }

    /// <summary>Reference to the selected commercial Plan (catalog entity).</summary>
    public int PlanId { get; private set; }

    /// <summary>UTC date when the contract becomes effective.</summary>
    public DateTime EffectiveAtUtc { get; private set; }

    /// <summary>UTC date when the contract ends.</summary>
    public DateTime EndsAtUtc { get; private set; }

    /// <summary>Contract duration in calendar months.</summary>
    public int DurationMonths { get; private set; }

    /// <summary>
    /// Original/base monthly list price at the time of contract creation (immutable snapshot).
    /// Used for calculations when no specific pricing tier applies.
    /// </summary>
    public decimal MonthlyListPrice { get; private set; }

    /// <summary>
    /// The contractual monthly value used for the 3-month benefit cap calculation.
    /// This is an immutable snapshot value distinct from Plan list price.
    /// </summary>
    public decimal ContractualMonthlyValue { get; private set; }

    /// <summary>Currency code (ISO-4217, e.g., EGP, USD).</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>Final contracted amount (after discounts).</summary>
    public decimal ContractedAmount { get; private set; }

    /// <summary>Total discount amount applied to this contract.</summary>
    public decimal DiscountAmount { get; private set; }

    /// <summary>Optional reference to a promotion/discount that was applied.</summary>
    public string? PromotionReference { get; private set; }

    /// <summary>Snapshot of pricing tiers for this contract.</summary>
    private readonly List<ContractPricingTier> _pricingTiers = [];
    public IReadOnlyList<ContractPricingTier> PricingTiers => _pricingTiers.AsReadOnly();

    /// <summary>Benefits/gifts granted as part of this contract.</summary>
    private readonly List<ContractBenefit> _benefits = [];
    public IReadOnlyList<ContractBenefit> Benefits => _benefits.AsReadOnly();

    /// <summary>Subscriptions associated with this contract (operational execution).</summary>
    private readonly List<Subscriptions.TenantPlan> _subscriptions = [];
    public IReadOnlyList<Subscriptions.TenantPlan> Subscriptions => _subscriptions.AsReadOnly();

    private Contract() { }

    private Contract(
        Guid id,
        string tenantId,
        string contractNumber,
        ContractStatus status,
        int planId,
        DateTime effectiveAtUtc,
        DateTime endsAtUtc,
        int durationMonths,
        decimal monthlyListPrice,
        decimal contractualMonthlyValue,
        string currencyCode,
        decimal contractedAmount,
        decimal discountAmount,
        string? promotionReference)
        : base(id)
    {
        TenantId = tenantId;
        ContractNumber = contractNumber;
        Status = status;
        PlanId = planId;
        EffectiveAtUtc = effectiveAtUtc;
        EndsAtUtc = endsAtUtc;
        DurationMonths = durationMonths;
        MonthlyListPrice = monthlyListPrice;
        ContractualMonthlyValue = contractualMonthlyValue;
        CurrencyCode = currencyCode;
        ContractedAmount = contractedAmount;
        DiscountAmount = discountAmount;
        PromotionReference = promotionReference;
    }

    /// <summary>
    /// Creates a new Contract with validated commercial terms.
    /// </summary>
    public static Result<Contract> Create(
        Guid id,
        string tenantId,
        string contractNumber,
        int planId,
        DateTime effectiveAtUtc,
        DateTime endsAtUtc,
        int durationMonths,
        decimal monthlyListPrice,
        decimal contractualMonthlyValue,
        string currencyCode,
        decimal contractedAmount,
        decimal discountAmount = 0,
        string? promotionReference = null)
    {
        if (id == Guid.Empty)
            return ContractErrors.PricingTier.IdRequired;

        if (string.IsNullOrWhiteSpace(tenantId))
            return ContractErrors.TenantIdRequired;

        if (string.IsNullOrWhiteSpace(contractNumber))
            return ContractErrors.ContractNumberRequired;

        if (planId <= 0)
            return ContractErrors.PlanIdRequired;

        if (effectiveAtUtc == default)
            return ContractErrors.EffectiveAtRequired;

        if (durationMonths <= 0)
            return ContractErrors.DurationInvalid;

        if (monthlyListPrice < 0)
            return ContractErrors.MonthlyListPriceInvalid;

        if (contractualMonthlyValue < 0)
            return ContractErrors.ContractualMonthlyValueInvalid;

        if (contractedAmount < 0)
            return ContractErrors.ContractedAmountInvalid;

        if (discountAmount < 0)
            return ContractErrors.DiscountAmountInvalid;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return ContractErrors.CurrencyInvalid;

        if (endsAtUtc != default && endsAtUtc < effectiveAtUtc)
            return ContractErrors.EndsAtBeforeEffectiveAt;

        // Validate discount does not exceed the gross commercial base (monthly * duration)
        var grossValue = monthlyListPrice * durationMonths;
        if (discountAmount > grossValue)
            return ContractErrors.DiscountExceedsGrossValue;

        // Validate contracted amount is consistent: should not exceed gross value
        if (contractedAmount > grossValue)
            return ContractErrors.ContractedAmountExceedsGrossValue;

        var contract = new Contract(
            id,
            tenantId.Trim(),
            contractNumber.Trim(),
            ContractStatus.Draft,
            planId,
            effectiveAtUtc,
            endsAtUtc,
            durationMonths,
            monthlyListPrice,
            contractualMonthlyValue,
            currencyCode.Trim().ToUpperInvariant(),
            contractedAmount,
            discountAmount,
            promotionReference?.Trim());

        contract.AddDomainEvent(new ContractCreatedEvent(id, tenantId, planId, contractNumber));

        return contract;
    }

    // ---- Lifecycle transitions (domain-controlled) ----

    /// <summary>Submits a Draft contract for approval.</summary>
    public Result<Updated> SubmitForApproval()
    {
        if (Status != ContractStatus.Draft)
            return ContractErrors.InvalidStateTransition(Status, "submit for approval");

        Status = ContractStatus.PendingApproval;
        AddDomainEvent(new ContractSubmittedEvent(Id, TenantId!));
        return Result.Updated;
    }

    /// <summary>Activates a PendingApproval contract.</summary>
    public Result<Updated> Activate(DateTime utcNow)
    {
        if (Status != ContractStatus.PendingApproval)
            return ContractErrors.InvalidStateTransition(Status, "activate");

        Status = ContractStatus.Active;
        AddDomainEvent(new ContractActivatedEvent(Id, TenantId!, EffectiveAtUtc));
        return Result.Updated;
    }

    /// <summary>Suspends an Active contract.</summary>
    public Result<Updated> Suspend()
    {
        if (Status != ContractStatus.Active)
            return ContractErrors.InvalidStateTransition(Status, "suspend");

        Status = ContractStatus.Suspended;
        AddDomainEvent(new ContractSuspendedEvent(Id, TenantId!));
        return Result.Updated;
    }

    /// <summary>Reactivates a Suspended contract.</summary>
    public Result<Updated> Reactivate()
    {
        if (Status != ContractStatus.Suspended)
            return ContractErrors.InvalidStateTransition(Status, "reactivate");

        Status = ContractStatus.Active;
        AddDomainEvent(new ContractReactivatedEvent(Id, TenantId!));
        return Result.Updated;
    }

    /// <summary>Terminates a contract (cancels it before natural expiration).</summary>
    public Result<Updated> Terminate(DateTime utcNow)
    {
        if (Status is not (ContractStatus.Draft or ContractStatus.PendingApproval or ContractStatus.Active or ContractStatus.Suspended))
            return ContractErrors.InvalidStateTransition(Status, "terminate");

        Status = ContractStatus.Terminated;
        AddDomainEvent(new ContractTerminatedEvent(Id, TenantId!, utcNow));
        return Result.Updated;
    }

    /// <summary>Marks an Active contract as expired.</summary>
    public Result<Updated> MarkExpired(DateTime utcNow)
    {
        if (Status != ContractStatus.Active)
            return ContractErrors.InvalidStateTransition(Status, "mark as expired");

        Status = ContractStatus.Expired;
        AddDomainEvent(new ContractExpiredEvent(Id, TenantId!, utcNow));
        return Result.Updated;
    }

    // ---- Commercial operations ----

    /// <summary>
    /// Adds a pricing tier snapshot to this contract.
    /// Validates no duplicate duration exists within this contract.
    /// </summary>
    public Result<Updated> AddPricingTier(ContractPricingTier tier)
    {
        if (tier == null) throw new ArgumentNullException(nameof(tier));

        // Validate no duplicate duration within this contract
        if (_pricingTiers.Any(t => t.DurationMonths == tier.DurationMonths))
            return ContractErrors.PricingTier.DuplicateDuration(tier.DurationMonths);

        // Validate tier currency matches contract currency
        if (!string.Equals(tier.CurrencyCode, CurrencyCode, StringComparison.OrdinalIgnoreCase))
            return ContractErrors.PricingTier.CurrencyMismatch(CurrencyCode);

        _pricingTiers.Add(tier);
        return Result.Updated;
    }

    /// <summary>
    /// Adds a benefit/gift to this contract. Validates the financial invariant:
    /// total benefit value must not exceed three months of the contract's contractual monthly value.
    /// </summary>
    public Result<Updated> AddBenefit(ContractBenefit benefit)
    {
        if (benefit == null) throw new ArgumentNullException(nameof(benefit));

        // Validate benefit currency matches contract currency
        if (!string.Equals(benefit.CurrencyCode, CurrencyCode, StringComparison.OrdinalIgnoreCase))
            return ContractErrors.Benefit.CurrencyMismatch(CurrencyCode);

        var currentTotal = _benefits.Sum(b => b.ContractualValue);
        var threeMonthsValue = ContractualMonthlyValue * 3;

        if (currentTotal + benefit.ContractualValue > threeMonthsValue)
            return ContractErrors.BenefitExceedsLimit;

        _benefits.Add(benefit);
        return Result.Updated;
    }

    /// <summary>
    /// Calculates the number of elapsed months since the effective date.
    /// </summary>
    public int GetElapsedMonths(DateTime utcNow)
    {
        if (utcNow < EffectiveAtUtc)
            return 0;

        var months = (utcNow.Year - EffectiveAtUtc.Year) * 12 + utcNow.Month - EffectiveAtUtc.Month;
        if (utcNow.Day < EffectiveAtUtc.Day)
            months--;

        return Math.Max(0, months);
    }

    /// <summary>
    /// Gets the applicable pricing tier for the given number of elapsed months.
    /// Returns the highest tier whose duration is less than or equal to elapsed months.
    /// Returns null when elapsedMonths is less than the shortest tier duration.
    /// </summary>
    public ContractPricingTier? GetApplicableTier(int elapsedMonths)
    {
        if (elapsedMonths <= 0)
            return null;

        return _pricingTiers
            .Where(t => t.DurationMonths <= elapsedMonths)
            .OrderByDescending(t => t.DurationMonths)
            .FirstOrDefault();
    }

    /// <summary>
    /// Calculates the contractual value for the given number of elapsed months.
    /// Per the commercial spec:
    /// - elapsed=1: 1-month tier price
    /// - elapsed=2: monthly list price × 2 (no tier applied)
    /// - elapsed≥3: highest applicable tier price, or monthly × elapsed if no tier
    /// </summary>
    public decimal CalculateValueForElapsedMonths(int elapsedMonths)
    {
        if (elapsedMonths <= 0)
            return 0;

        // Special case per spec: elapsed=2 uses monthly pricing, not the 1-month tier.
        if (elapsedMonths == 2)
            return MonthlyListPrice * 2;

        var tier = GetApplicableTier(elapsedMonths);
        if (tier != null)
            return tier.TierPrice;

        // Fallback: no applicable tier, use monthly list price
        return MonthlyListPrice * elapsedMonths;
    }

    /// <summary>EF navigation mutator for rehydration of pricing tiers.</summary>
    internal void LoadPricingTiers(IEnumerable<ContractPricingTier> tiers)
        => _pricingTiers.AddRange(tiers);

    /// <summary>EF navigation mutator for rehydration of benefits.</summary>
    internal void LoadBenefits(IEnumerable<ContractBenefit> benefits)
        => _benefits.AddRange(benefits);
}
