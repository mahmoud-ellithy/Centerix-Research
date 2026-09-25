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
    /// <summary>
    /// Snapshot version for contracts created by production paths with a fully
    /// populated commercial snapshot from an authoritative Plan/Offer source.
    /// </summary>
    public const int CompleteEntitlementSnapshotVersion = 1;

    /// <summary>
    /// Snapshot version for legacy/migration-era contracts whose snapshot was never
    /// backfilled. Never fabricate version 1 for these rows.
    /// </summary>
    public const int IncompleteEntitlementSnapshotVersion = 0;

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

    /// <summary>
    /// Gross contract amount before discounts (MonthlyListPrice × DurationMonths).
    /// Used as the Subtotal base for Invoice breakdown.
    /// </summary>
    public decimal GrossAmount { get; private set; }

    /// <summary>Final contracted amount (after discounts).</summary>
    public decimal ContractedAmount { get; private set; }

    /// <summary>Total discount amount applied to this contract.</summary>
    public decimal DiscountAmount { get; private set; }

    /// <summary>Optional reference to a promotion/discount that was applied.</summary>
    public string? PromotionReference { get; private set; }

    /// <summary>Reference to the Promotion entity that was applied (snapshot).</summary>
    public int? PromotionId { get; private set; }

    /// <summary>The type of promotion applied (e.g., "PercentageDiscount", "PayForXMonths").</summary>
    public string? PromotionType { get; private set; }

    /// <summary>
    /// For PayForXMonths promotions: the number of months the customer is charged for.
    /// null when not a PayForXMonths promotion.
    /// </summary>
    public int? ChargedMonths { get; private set; }

    /// <summary>
    /// Number of bonus months credited by the promotion at contract creation.
    /// Snapshot: Subscription uses this value, not the Plan's current BonusMonths.
    /// </summary>
    public int BonusMonths { get; private set; }

    /// <summary>Snapshot of Plan limits at contract creation. Used by SubscriptionFactory.</summary>
    public int MaxStudents { get; private set; }
    public int MaxUsers { get; private set; }
    public int MaxBranches { get; private set; }
    public int MaxTeachers { get; private set; }
    public int StorageGb { get; private set; }
    public int SmsQuota { get; private set; }

    /// <summary>
    /// Version marker for the entitlement snapshot. Indicates that the Contract
    /// has been fully populated with a complete commercial snapshot from the Plan.
    /// Value of 0 means incomplete/migration-era contract. Value of 1 means complete.
    /// Used by ValidateSnapshotCompleteness() to distinguish missing data from legitimate zeros.
    /// </summary>
    public int EntitlementSnapshotVersion { get; private set; }

    /// <summary>
    /// When this Contract is a renewal, references the previous Subscription (TenantPlan)
    /// that was renewed. Preserves renewal traceability without constraining the relationship
    /// to a specific Contract or requiring a versioning system.
    /// </summary>
    public Guid? PreviousSubscriptionId { get; private set; }

    /// <summary>Snapshot of pricing tiers for this contract.</summary>
    private readonly List<ContractPricingTier> _pricingTiers = [];
    public IReadOnlyList<ContractPricingTier> PricingTiers => _pricingTiers.AsReadOnly();

    /// <summary>Benefits/gifts granted as part of this contract.</summary>
    private readonly List<ContractBenefit> _benefits = [];
    public IReadOnlyList<ContractBenefit> Benefits => _benefits.AsReadOnly();

    /// <summary>Snapshot of feature entitlements copied from Plan at contract creation.</summary>
    private readonly List<ContractFeature> _contractFeatures = [];
    public IReadOnlyList<ContractFeature> ContractFeatures => _contractFeatures.AsReadOnly();

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
        decimal grossAmount,
        decimal contractedAmount,
        decimal discountAmount,
        string? promotionReference,
        int? promotionId,
        string? promotionType,
        int? chargedMonths,
        int bonusMonths,
        int maxStudents,
        int maxUsers,
        int maxBranches,
        int maxTeachers,
        int storageGb,
        int smsQuota,
        int entitlementSnapshotVersion)
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
        GrossAmount = grossAmount;
        ContractedAmount = contractedAmount;
        DiscountAmount = discountAmount;
        PromotionReference = promotionReference;
        PromotionId = promotionId;
        PromotionType = promotionType;
        ChargedMonths = chargedMonths;
        BonusMonths = bonusMonths;
        MaxStudents = maxStudents;
        MaxUsers = maxUsers;
        MaxBranches = maxBranches;
        MaxTeachers = maxTeachers;
        StorageGb = storageGb;
        SmsQuota = smsQuota;
        EntitlementSnapshotVersion = entitlementSnapshotVersion;
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
        decimal grossAmount,
        decimal contractedAmount,
        int entitlementSnapshotVersion,
        decimal discountAmount = 0,
        string? promotionReference = null,
        int? promotionId = null,
        string? promotionType = null,
        int? chargedMonths = null,
        int bonusMonths = 0,
        int maxStudents = 0,
        int maxUsers = 0,
        int maxBranches = 0,
        int maxTeachers = 0,
        int storageGb = 0,
        int smsQuota = 0)
    {
        if (id == Guid.Empty)
            return ContractErrors.PricingTier.IdRequired;

        if (entitlementSnapshotVersion < IncompleteEntitlementSnapshotVersion)
            return ContractErrors.SnapshotVersionInvalid(entitlementSnapshotVersion);

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

        if (grossAmount < 0)
            return Error.Validation("Contract.GrossAmount_Invalid", "Gross amount cannot be negative");

        if (contractedAmount < 0)
            return ContractErrors.ContractedAmountInvalid;

        if (discountAmount < 0)
            return ContractErrors.DiscountAmountInvalid;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return ContractErrors.CurrencyInvalid;

        if (endsAtUtc != default && endsAtUtc < effectiveAtUtc)
            return ContractErrors.EndsAtBeforeEffectiveAt;

        // Validate discount does not exceed the gross amount
        if (discountAmount > grossAmount)
            return ContractErrors.DiscountExceedsGrossValue;

        // Validate contracted amount is consistent: ContractedAmount = GrossAmount - DiscountAmount
        var expectedContractedAmount = grossAmount - discountAmount;
        if (Math.Abs(contractedAmount - expectedContractedAmount) > 0.01m)
            return Error.Validation("Contract.ContractedAmount_Inconsistent",
                $"ContractedAmount ({contractedAmount}) must equal GrossAmount ({grossAmount}) - DiscountAmount ({discountAmount})");

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
            grossAmount,
            contractedAmount,
            discountAmount,
            promotionReference?.Trim(),
            promotionId,
            promotionType,
            chargedMonths,
            bonusMonths,
            maxStudents,
            maxUsers,
            maxBranches,
            maxTeachers,
            storageGb,
            smsQuota,
            entitlementSnapshotVersion);

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

    /// <summary>
    /// Links this contract to the previous subscription that was renewed.
    /// Called during renewal to establish traceability.
    /// </summary>
    public Result<Updated> LinkToPreviousSubscription(Guid previousSubscriptionId)
    {
        if (previousSubscriptionId == Guid.Empty)
            return Error.Validation("Contract.PreviousSubscriptionId_Invalid", "Previous subscription ID must not be empty.");

        PreviousSubscriptionId = previousSubscriptionId;
        return Result.Updated;
    }

    /// <summary>
    /// Builds a subscription snapshot from this contract's own data.
    /// No Plan queries — all values are authoritative snapshots on the Contract.
    /// MonthlyCharge = GrossAmount / DurationMonths (actual monthly charge after discount).
    /// </summary>
    public SubscriptionSnapshot GetSubscriptionSnapshot()
    {
        // Calculate actual monthly charge: GrossAmount / DurationMonths
        // For no-discount contracts: GrossAmount = ContractedAmount, so MonthlyCharge = ContractedAmount / DurationMonths
        // For discounted contracts: GrossAmount = ContractedAmount + DiscountAmount, so MonthlyCharge = (ContractedAmount + DiscountAmount) / DurationMonths
        var monthlyCharge = DurationMonths > 0 ? GrossAmount / DurationMonths : 0m;

        return new SubscriptionSnapshot(
            MonthlyListPrice,
            monthlyCharge,
            ContractualMonthlyValue,
            CurrencyCode,
            DurationMonths,
            BonusMonths,
            ChargedMonths ?? 0,
            MaxStudents,
            MaxUsers,
            MaxBranches,
            MaxTeachers,
            StorageGb,
            SmsQuota,
            _contractFeatures.Select(f => f.FeatureCode).ToList());
    }

    /// <summary>
    /// Validates that this Contract contains a complete entitlement snapshot suitable
    /// for creating a Subscription. A Contract missing snapshot fields would silently
    /// create a Subscription with zero entitlements, which is almost always a bug.
    /// Preserves legitimate zero values only when the source Plan actually has zero entitlement.
    /// </summary>
    public Result<Updated> ValidateSnapshotCompleteness()
    {
        if (EntitlementSnapshotVersion < CompleteEntitlementSnapshotVersion)
            return ContractErrors.SnapshotIncomplete(
                $"EntitlementSnapshotVersion is {EntitlementSnapshotVersion}, expected >= {CompleteEntitlementSnapshotVersion}");

        if (MonthlyListPrice <= 0)
            return ContractErrors.SnapshotIncomplete("MonthlyListPrice must be positive");

        if (ContractualMonthlyValue <= 0)
            return ContractErrors.SnapshotIncomplete("ContractualMonthlyValue must be positive");

        if (string.IsNullOrWhiteSpace(CurrencyCode))
            return ContractErrors.SnapshotIncomplete("CurrencyCode is required");

        if (DurationMonths <= 0)
            return ContractErrors.SnapshotIncomplete("DurationMonths must be positive");

        if (EffectiveAtUtc == default)
            return ContractErrors.SnapshotIncomplete("EffectiveAtUtc must be set");

        if (BonusMonths < 0)
            return ContractErrors.SnapshotIncomplete("BonusMonths must not be negative");

        if (ChargedMonths is { } charged && (charged < 1 || charged > DurationMonths))
            return ContractErrors.SnapshotIncomplete(
                $"ChargedMonths is {ChargedMonths}, expected null or between 1 and {DurationMonths}");

        if (EndsAtUtc == default)
            return ContractErrors.SnapshotIncomplete("EndsAtUtc must be set");

        // Contract period must equal the authoritative calendar-month calculation
        // (sequential duration then bonus) so Contract.EndsAtUtc always matches the
        // Subscription.EffectiveEndsAtUtc derived from the same snapshot.
        var expectedEndsAtUtc = TenantPlan.ComputeEffectiveEndsAtUtc(EffectiveAtUtc, DurationMonths, BonusMonths);
        if (EndsAtUtc != expectedEndsAtUtc)
            return ContractErrors.SnapshotIncomplete(
                $"EndsAtUtc is {EndsAtUtc:O}, expected {expectedEndsAtUtc:O} " +
                $"(EffectiveAtUtc + {DurationMonths} months + {BonusMonths} bonus months)");

        if (GrossAmount < 0)
            return ContractErrors.SnapshotIncomplete("GrossAmount must not be negative");

        if (ContractedAmount < 0 || DiscountAmount < 0)
            return ContractErrors.SnapshotIncomplete("ContractedAmount and DiscountAmount must not be negative");

        // Validate the commercial invariant: ContractedAmount = GrossAmount - DiscountAmount
        var expectedContractedAmount = GrossAmount - DiscountAmount;
        if (Math.Abs(ContractedAmount - expectedContractedAmount) > 0.01m)
            return ContractErrors.SnapshotIncomplete(
                $"ContractedAmount ({ContractedAmount}) must equal GrossAmount ({GrossAmount}) - DiscountAmount ({DiscountAmount})");

        if (MaxStudents < 0 || MaxUsers < 0 || MaxBranches < 0 ||
            MaxTeachers < 0 || StorageGb < 0 || SmsQuota < 0)
            return ContractErrors.SnapshotIncomplete("Entitlement limits must not be negative");

        foreach (var feature in _contractFeatures)
        {
            if (string.IsNullOrWhiteSpace(feature.FeatureCode))
                return ContractErrors.SnapshotIncomplete("Contract feature codes must not be empty");
        }

        foreach (var tier in _pricingTiers)
        {
            if (tier.DurationMonths <= 0)
                return ContractErrors.SnapshotIncomplete("Pricing tier durations must be positive");

            if (tier.TierPrice < 0)
                return ContractErrors.SnapshotIncomplete("Pricing tier prices must not be negative");

            if (!string.Equals(tier.CurrencyCode, CurrencyCode, StringComparison.OrdinalIgnoreCase))
                return ContractErrors.SnapshotIncomplete(
                    $"Pricing tier currency must match contract currency '{CurrencyCode}'");
        }

        return Result.Updated;
    }

    /// <summary>
    /// Adds a feature snapshot from the Plan catalog at contract creation time.
    /// Rejects duplicate feature codes (case-insensitive) within this contract.
    /// </summary>
    public Result<Updated> AddContractFeature(ContractFeature feature)
    {
        if (feature == null) throw new ArgumentNullException(nameof(feature));

        if (string.IsNullOrWhiteSpace(feature.FeatureCode))
            return ContractErrors.FeatureCodeRequired;

        if (_contractFeatures.Any(f =>
                f.FeatureCode.Equals(feature.FeatureCode, StringComparison.OrdinalIgnoreCase)))
            return ContractErrors.DuplicateContractFeature(feature.FeatureCode);

        _contractFeatures.Add(feature);
        return Result.Updated;
    }

    /// <summary>EF navigation mutator for rehydration of contract features.</summary>
    internal void LoadContractFeatures(IEnumerable<ContractFeature> features)
        => _contractFeatures.AddRange(features);

    /// <summary>EF navigation mutator for rehydration of pricing tiers.</summary>
    internal void LoadPricingTiers(IEnumerable<ContractPricingTier> tiers)
        => _pricingTiers.AddRange(tiers);

    /// <summary>EF navigation mutator for rehydration of benefits.</summary>
    internal void LoadBenefits(IEnumerable<ContractBenefit> benefits)
        => _benefits.AddRange(benefits);
}
