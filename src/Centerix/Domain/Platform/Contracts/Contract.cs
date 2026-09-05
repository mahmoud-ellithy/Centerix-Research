namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.Events;
using Centerix.Domain.Platform.Plans;

/// <summary>
/// A commercial agreement between the Platform and a Tenant.
/// Tenant-scoped aggregate that preserves the commercial terms agreed at contract creation.
/// </summary>
/// <remarks>
/// Conceptual model:
///   Plan = Commercial Product / Catalog
///   Contract = Actual commercial agreement with a specific Tenant
///   Subscription = Operational execution of the Contract
///
/// Historical integrity: Changing Plan prices, tiers, features, or promotions after Contract
/// creation MUST NOT alter the Contract's historical commercial terms. The Contract is a
/// commercial snapshot.
///
/// Lifecycle: Draft → PendingApproval → Active → Suspended → Expired / Terminated
/// </remarks>
public class Contract : AuditableEntity<Guid>
{
    /// <summary>Business-facing contract number/reference (unique per tenant).</summary>
    public string ContractNumber { get; private set; } = default!;

    /// <summary>Current status of the contract.</summary>
    public ContractStatus Status { get; private set; }

    /// <summary>Reference to the selected commercial Plan (catalog snapshot source).</summary>
    public int PlanId { get; private set; }

    /// <summary>Navigation to the Plan (global catalog, not tenant-scoped).</summary>
    public Plan Plan { get; private set; } = default!;

    /// <summary>Date the contract was created.</summary>
    public DateTime CreatedAtContractUtc { get; private set; }

    /// <summary>Contract effective/start date.</summary>
    public DateTime EffectiveAtUtc { get; private set; }

    /// <summary>Contract end date (natural expiry).</summary>
    public DateTime EndsAtUtc { get; private set; }

    /// <summary>Contract duration in calendar months.</summary>
    public int DurationMonths { get; private set; }

    /// <summary>Original/base monthly list price at contract creation (SNAPSHOT).</summary>
    public decimal MonthlyListPrice { get; private set; }

    /// <summary>Currency code for monetary values.</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>Final contracted amount (after discounts/promotions).</summary>
    public decimal ContractedAmount { get; private set; }

    /// <summary>Total discount amount applied to this contract.</summary>
    public decimal DiscountAmount { get; private set; }

    /// <summary>Promotion/discount description or reference (if applicable).</summary>
    public string? PromotionReference { get; private set; }

    /// <summary>Reason for termination/suspension (if applicable).</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Date when the contract was activated.</summary>
    public DateTime? ActivatedAtUtc { get; private set; }

    /// <summary>Date when the contract was terminated/expired.</summary>
    public DateTime? TerminatedAtUtc { get; private set; }

    /// <summary>Snapshot of pricing tiers selected for this contract.</summary>
    private readonly List<ContractPricingTier> _pricingTiers = [];
    public IReadOnlyList<ContractPricingTier> PricingTiers => _pricingTiers.AsReadOnly();

    /// <summary>Benefits/gifts granted under this contract.</summary>
    private readonly List<ContractBenefit> _benefits = [];
    public IReadOnlyList<ContractBenefit> Benefits => _benefits.AsReadOnly();

    /// <summary>Subscriptions executed under this contract.</summary>
    private readonly List<Subscriptions.TenantPlan> _subscriptions = [];
    public IReadOnlyList<Subscriptions.TenantPlan> Subscriptions => _subscriptions.AsReadOnly();

    private Contract() { }

    private Contract(
        Guid id,
        string tenantId,
        string contractNumber,
        int planId,
        DateTime effectiveAtUtc,
        DateTime endsAtUtc,
        int durationMonths,
        decimal monthlyListPrice,
        string currencyCode,
        decimal contractedAmount,
        decimal discountAmount,
        string? promotionReference = null)
        : base(id)
    {
        TenantId = tenantId;
        ContractNumber = contractNumber;
        PlanId = planId;
        Status = ContractStatus.Draft;
        CreatedAtContractUtc = DateTime.UtcNow;
        EffectiveAtUtc = effectiveAtUtc;
        EndsAtUtc = endsAtUtc;
        DurationMonths = durationMonths;
        MonthlyListPrice = monthlyListPrice;
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
        string currencyCode,
        decimal contractedAmount,
        decimal discountAmount = 0,
        string? promotionReference = null)
    {
        if (id == Guid.Empty)
            return ContractErrors.IdRequired;

        if (string.IsNullOrWhiteSpace(tenantId))
            return ContractErrors.TenantIdRequired;

        if (string.IsNullOrWhiteSpace(contractNumber))
            return ContractErrors.ContractNumberRequired;

        if (planId <= 0)
            return ContractErrors.PlanIdRequired;

        if (durationMonths <= 0)
            return ContractErrors.InvalidDuration;

        if (monthlyListPrice < 0)
            return ContractErrors.InvalidMonthlyListPrice;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return ContractErrors.InvalidCurrency;

        if (contractedAmount < 0)
            return ContractErrors.InvalidContractedAmount;

        if (discountAmount < 0)
            return ContractErrors.InvalidDiscountAmount;

        if (endsAtUtc <= effectiveAtUtc)
            return ContractErrors.InvalidDateRange;

        var contract = new Contract(
            id,
            tenantId.Trim(),
            contractNumber.Trim(),
            planId,
            effectiveAtUtc,
            endsAtUtc,
            durationMonths,
            monthlyListPrice,
            currencyCode.Trim().ToUpperInvariant(),
            contractedAmount,
            discountAmount,
            promotionReference);

        contract.AddDomainEvent(new ContractCreatedEvent(contract));

        return contract;
    }

    /// <summary>
    /// Submits the draft contract for approval.
    /// </summary>
    public Result<Updated> SubmitForApproval()
    {
        if (Status != ContractStatus.Draft)
            return ContractErrors.InvalidStateTransition(Status, "submit for approval");

        Status = ContractStatus.PendingApproval;
        StatusReason = null;

        AddDomainEvent(new ContractSubmittedEvent(Id));

        return Result.Updated;
    }

    /// <summary>
    /// Approves and activates the contract.
    /// </summary>
    public Result<Updated> Activate(DateTime activatedAtUtc, string? reason = null)
    {
        if (Status != ContractStatus.PendingApproval && Status != ContractStatus.Draft)
            return ContractErrors.InvalidStateTransition(Status, "activate");

        Status = ContractStatus.Active;
        ActivatedAtUtc = activatedAtUtc;
        StatusReason = reason;

        AddDomainEvent(new ContractActivatedEvent(Id));

        return Result.Updated;
    }

    /// <summary>
    /// Suspends the active contract.
    /// </summary>
    public Result<Updated> Suspend(string reason)
    {
        if (Status != ContractStatus.Active)
            return ContractErrors.InvalidStateTransition(Status, "suspend");

        if (string.IsNullOrWhiteSpace(reason))
            return ContractErrors.ReasonRequired;

        Status = ContractStatus.Suspended;
        StatusReason = reason.Trim();

        AddDomainEvent(new ContractSuspendedEvent(Id));

        return Result.Updated;
    }

    /// <summary>
    /// Reactivates a suspended contract.
    /// </summary>
    public Result<Updated> Reactivate(string? reason = null)
    {
        if (Status != ContractStatus.Suspended)
            return ContractErrors.InvalidStateTransition(Status, "reactivate");

        Status = ContractStatus.Active;
        StatusReason = reason;

        AddDomainEvent(new ContractReactivatedEvent(Id));

        return Result.Updated;
    }

    /// <summary>
    /// Terminates the contract before natural expiry.
    /// </summary>
    public Result<Updated> Terminate(DateTime terminatedAtUtc, string reason)
    {
        if (Status == ContractStatus.Terminated || Status == ContractStatus.Expired)
            return ContractErrors.InvalidStateTransition(Status, "terminate");

        if (string.IsNullOrWhiteSpace(reason))
            return ContractErrors.ReasonRequired;

        Status = ContractStatus.Terminated;
        TerminatedAtUtc = terminatedAtUtc;
        StatusReason = reason.Trim();

        AddDomainEvent(new ContractTerminatedEvent(Id));

        return Result.Updated;
    }

    /// <summary>
    /// Marks the contract as expired (reached natural end of term).
    /// </summary>
    public Result<Updated> MarkExpired(DateTime expiredAtUtc)
    {
        if (Status == ContractStatus.Terminated || Status == ContractStatus.Expired)
            return ContractErrors.InvalidStateTransition(Status, "expire");

        if (expiredAtUtc < EndsAtUtc)
            return ContractErrors.NotYetExpired;

        Status = ContractStatus.Expired;
        TerminatedAtUtc = expiredAtUtc;
        StatusReason = "Natural expiry";

        AddDomainEvent(new ContractExpiredEvent(Id));

        return Result.Updated;
    }

    /// <summary>
    /// Adds a pricing tier snapshot to this contract.
    /// </summary>
    public Result<Updated> AddPricingTier(ContractPricingTier tier)
    {
        if (Status != ContractStatus.Draft)
            return ContractErrors.CannotModifyAfterDraft;

        if (_pricingTiers.Any(t => t.DurationMonths == tier.DurationMonths))
            return ContractErrors.PricingTier.DuplicateDuration;

        _pricingTiers.Add(tier);

        return Result.Updated;
    }

    /// <summary>
    /// Adds a benefit/gift to this contract.
    /// </summary>
    public Result<Updated> AddBenefit(ContractBenefit benefit)
    {
        if (Status != ContractStatus.Draft)
            return ContractErrors.CannotModifyAfterDraft;

        // Validate gift financial invariant: total benefits cannot exceed 3 months of subscription value
        var totalBenefitValue = _benefits.Sum(b => b.ContractualValue) + benefit.ContractualValue;
        var threeMonthsValue = MonthlyListPrice * 3;

        if (totalBenefitValue > threeMonthsValue)
            return ContractErrors.Benefit.ExceedsMaximumValue(totalBenefitValue, threeMonthsValue);

        _benefits.Add(benefit);

        return Result.Updated;
    }

    /// <summary>
    /// Gets the maximum allowed benefit value (3 months of subscription value).
    /// </summary>
    public decimal GetMaximumBenefitValue() => MonthlyListPrice * 3;

    /// <summary>
    /// Gets the total value of all benefits granted under this contract.
    /// </summary>
    public decimal GetTotalBenefitValue() => _benefits.Sum(b => b.ContractualValue);

    /// <summary>
    /// Calculates the elapsed months since contract effective date.
    /// Used for pricing tier determination.
    /// </summary>
    public int GetElapsedMonths(DateTime asOfUtc)
    {
        if (asOfUtc < EffectiveAtUtc)
            return 0;

        var months = 0;
        var current = EffectiveAtUtc;
        while (current.AddMonths(1) <= asOfUtc)
        {
            months++;
            current = current.AddMonths(1);
        }
        return months;
    }

    /// <summary>
    /// Gets the applicable pricing tier for the given number of elapsed months.
    /// Returns the highest tier whose duration is less than or equal to elapsed months.
    /// The 1-month tier only applies when elapsedMonths == 1 (otherwise

    /// <summary>
    /// Calculates the contractual value for a given number of elapsed months.
    /// Uses the tier that best fits, or falls back to monthly list price × months.
    /// </summary>
    public decimal CalculateValueForElapsedMonths(int elapsedMonths)
    {
        if (elapsedMonths <= 0)
            return 0;

        var tier = GetApplicableTier(elapsedMonths);
        if (tier is not null)
            return tier.TierPrice;

        // No tier found (e.g., 2 months when only 1/3/6/12 tiers exist)
        // Fall back to monthly list price × elapsed months
        return MonthlyListPrice * elapsedMonths;
    }
}
