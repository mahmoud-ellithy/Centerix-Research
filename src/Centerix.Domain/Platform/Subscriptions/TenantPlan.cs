namespace Centerix.Domain.Platform.Subscriptions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Subscriptions.Events;
using Centerix.Domain.Platform.Contracts;

/// <summary>
/// A tenant's commercial subscription: an immutable SNAPSHOT of the plan terms actually granted,
/// plus the lifecycle state of that grant. Historical rows are kept per tenant; the database
/// guarantees at most ONE non-terminal subscription (Active/Suspended) per tenant via a filtered
/// unique index.
/// </summary>
/// <remarks>
/// Commercial snapshot rationale (why each field is copied from Plan):
///  - <see cref="SnapshotPrice"/> + <see cref="SnapshotCurrency"/>: the agreed price; a later plan
///    repricing must not change what the tenant pays.
///  - <see cref="DurationMonths"/> + <see cref="BonusMonths"/>: the granted term including the
///    promotional bonus; changing a plan's bonus must not alter existing grants, and bonus must
///    stay auditable rather than being hidden inside a computed date.
///  - <see cref="BaseEndsAt"/> / <see cref="EffectiveEndsAt"/>: calendar-month arithmetic result
///    (StartsAt + DurationMonths, then + BonusMonths) frozen at creation so expiration decisions
///    never depend on re-deriving from mutable plan data.
///  - Limit fields (Max*): the effective limits of THIS grant; plan limit changes affect only
///    future subscriptions.
/// Feature entitlement is snapshotted separately in TenantPlanFeature rows (codes copied at
/// creation).
///
/// A Subscription (TenantPlan) may belong to a Contract (commercial agreement) via ContractId.
/// </remarks>
public class TenantPlan : AuditableEntity<Guid>
{
    // Optimistic-concurrency token: SQL Server rowversion (store-generated). The non-null
    // CLR default keeps the EF InMemory test provider's nullability checks happy.
    public byte[] RowVersion { get; private set; } = [];

    public int PlanId { get; private set; }

    /// <summary>
    /// The Contract this subscription executes. Optional: a subscription may exist
    /// without a contract in legacy/pre-contract scenarios. Null when not contract-linked.
    /// </summary>
    public Guid? ContractId { get; private set; }

    /// <summary>
    /// Navigation property to the associated Contract (commercial agreement).
    /// </summary>
    public Contracts.Contract? Contract { get; private set; }

    // TenantId is INHERITED from AuditableEntity<T> (IHasTenantId): it drives the global
    // tenant query filter and must not be shadowed. Platform-admin flows bypass the filter
    // explicitly (IgnoreQueryFilters) AFTER the platform boundary check.

    public decimal SnapshotPrice { get; private set; }
    public string SnapshotCurrency { get; private set; } = default!;
    public int DurationMonths { get; private set; }
    public int BonusMonths { get; private set; }

    public DateTime StartsAtUtc { get; private set; }

    /// <summary>StartsAtUtc + DurationMonths (calendar months). Frozen at creation/renewal.</summary>
    public DateTime BaseEndsAtUtc { get; private set; }

    /// <summary>BaseEndsAtUtc + BonusMonths (calendar months). AUTHORITATIVE access-expiration.</summary>
    public DateTime EffectiveEndsAtUtc { get; private set; }

    public bool AutoRenew { get; private set; }
    public SubscriptionStatus Status { get; private set; }

    /// <summary>True when a Platform Admin has commercially activated this subscription.</summary>
    public DateTime? ActivatedAtUtc { get; private set; }

    // ---- Limit snapshot: the effective limits of THIS grant. Plan limit changes affect only
    // future subscriptions; per-tenant overrides still take precedence at enforcement time.
    public int SnapshotMaxStudents { get; private set; }
    public int SnapshotMaxUsers { get; private set; }
    public int SnapshotMaxBranches { get; private set; }
    public int SnapshotMaxTeachers { get; private set; }
    public int SnapshotStorageGb { get; private set; }
    public int SnapshotSmsQuota { get; private set; }

    /// <summary>Snapshotted feature entitlement codes for THIS grant (TenantPlanFeature rows).</summary>
    private readonly List<TenantPlanFeature> _features = [];
    public IReadOnlyList<TenantPlanFeature> Features => _features.AsReadOnly();

    public Plans.Plan Plan { get; private set; } = default!;

    private TenantPlan() { }

    private TenantPlan(
        Guid id,
        string tenantId,
        int planId,
        decimal snapshotPrice,
        string snapshotCurrency,
        int durationMonths,
        int bonusMonths,
        DateTime startsAtUtc,
        bool autoRenew,
        SubscriptionStatus status,
        int maxStudents,
        int maxUsers,
        int maxBranches,
        int maxTeachers,
        int storageGb,
        int smsQuota)
        : base(id)
    {
        TenantId = tenantId;
        PlanId = planId;
        SnapshotPrice = snapshotPrice;
        SnapshotCurrency = snapshotCurrency;
        DurationMonths = durationMonths;
        BonusMonths = bonusMonths;
        StartsAtUtc = startsAtUtc;
        AutoRenew = autoRenew;
        Status = status;
        SnapshotMaxStudents = maxStudents;
        SnapshotMaxUsers = maxUsers;
        SnapshotMaxBranches = maxBranches;
        SnapshotMaxTeachers = maxTeachers;
        SnapshotStorageGb = storageGb;
        SnapshotSmsQuota = smsQuota;

        BaseEndsAtUtc = AddCalendarMonths(startsAtUtc, durationMonths);
        EffectiveEndsAtUtc = AddCalendarMonths(BaseEndsAtUtc, bonusMonths);
    }

    /// <summary>
    /// Creates a PENDING subscription carrying the exact commercial terms to be granted.
    /// Calendar-month semantics are used throughout (never 30-day approximations):
    /// Jan 31 + 1 month = Feb 28/29, exactly like billing systems expect.
    /// </summary>
    public static Result<TenantPlan> Create(
        Guid id,
        string tenantId,
        int planId,
        decimal snapshotPrice,
        string snapshotCurrency,
        int durationMonths,
        int bonusMonths,
        DateTime startsAtUtc,
        bool autoRenew = false,
        SubscriptionStatus status = SubscriptionStatus.Pending,
        int maxStudents = 0,
        int maxUsers = 0,
        int maxBranches = 0,
        int maxTeachers = 0,
        int storageGb = 0,
        int smsQuota = 0)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return TenantPlanErrors.TenantIdRequired;

        if (planId <= 0)
            return TenantPlanErrors.PlanIdRequired;

        if (snapshotPrice < 0)
            return TenantPlanErrors.SnapshotPriceInvalid;

        if (string.IsNullOrWhiteSpace(snapshotCurrency) || snapshotCurrency.Trim().Length != 3)
            return TenantPlanErrors.SnapshotCurrencyInvalid;

        if (durationMonths <= 0)
            return TenantPlanErrors.DurationInvalid;

        if (bonusMonths < 0)
            return TenantPlanErrors.BonusInvalid;

        if (startsAtUtc == default)
            return TenantPlanErrors.StartsAtRequired;

        if (!Enum.IsDefined(status))
            return TenantPlanErrors.StatusInvalid;

        if (maxStudents < 0 || maxUsers < 0 || maxBranches < 0 || maxTeachers < 0 || storageGb < 0 || smsQuota < 0)
            return TenantPlanErrors.SnapshotLimitsInvalid;

        return new TenantPlan(
            id, tenantId.Trim(), planId, snapshotPrice, snapshotCurrency.Trim().ToUpperInvariant(),
            durationMonths, bonusMonths, startsAtUtc, autoRenew, status,
            maxStudents, maxUsers, maxBranches, maxTeachers, storageGb, smsQuota);
    }

    /// <summary>Returns the snapshotted limit for a canonical limit type, when defined.</summary>
    public int? GetSnapshotLimit(string limitType) => limitType switch
    {
        LimitTypeCodes.Students => SnapshotMaxStudents,
        LimitTypeCodes.Users => SnapshotMaxUsers,
        LimitTypeCodes.Branches => SnapshotMaxBranches,
        LimitTypeCodes.Teachers => SnapshotMaxTeachers,
        _ => null
    };

    /// <summary>Snapshots one entitled feature code onto this subscription.</summary>
    public Result<Updated> GrantFeature(string featureCode)
    {
        if (string.IsNullOrWhiteSpace(featureCode))
            return TenantPlanErrors.FeatureCodeRequired;

        var code = featureCode.Trim();
        if (_features.Any(f => f.FeatureCode.Equals(code, StringComparison.OrdinalIgnoreCase)))
            return TenantPlanErrors.FeatureAlreadyGranted;

        _features.Add(TenantPlanFeature.Create(Id, code));
        return Result.Updated;
    }

    /// <summary>Links this subscription to a Contract.</summary>
    public Result<Updated> LinkToContract(Guid contractId)
    {
        if (contractId == Guid.Empty)
            return Error.Validation("Subscription.InvalidContractId", "ContractId must not be empty.");

        ContractId = contractId;
        return Result.Updated;
    }

    /// <summary>Whether this subscription grants access as of <paramref name="utcNow"/>.</summary>
    public bool IsActiveAsOf(DateTime utcNow) =>
        Status == SubscriptionStatus.Active && utcNow < EffectiveEndsAtUtc;

    /// <summary>
    /// Commercially activates a PENDING subscription. Rejected when already expired as of
    /// <paramref name="utcNow"/>.
    /// </summary>
    public Result<Updated> Activate(DateTime utcNow)
    {
        if (Status == SubscriptionStatus.Active)
            return TenantPlanErrors.AlreadyActive;

        if (Status is not (SubscriptionStatus.Pending or SubscriptionStatus.Suspended))
            return TenantPlanErrors.InvalidStateTransition(Status, "activate");

        if (utcNow >= EffectiveEndsAtUtc)
            return TenantPlanErrors.AlreadyExpired;

        Status = SubscriptionStatus.Active;
        ActivatedAtUtc = utcNow;
        return Result.Updated;
    }

    /// <summary>
    /// Renews by appending term in CALENDAR MONTHS. The new period anchors at
    /// max(EffectiveEndsAtUtc, utcNow): renewing BEFORE expiry preserves remaining paid time
    /// (stacking), renewing AFTER expiry starts fresh from now. Cancelled subscriptions cannot
    /// be renewed. Bonus months may be granted per renewal and remain auditable on the row.
    /// </summary>
    public Result<Updated> Renew(int additionalMonths, int additionalBonusMonths, DateTime utcNow)
    {
        if (Status == SubscriptionStatus.Cancelled)
            return TenantPlanErrors.CannotRenewCancelled;

        if (additionalMonths <= 0)
            return TenantPlanErrors.DurationInvalid;

        if (additionalBonusMonths < 0)
            return TenantPlanErrors.BonusInvalid;

        var anchor = EffectiveEndsAtUtc > utcNow ? EffectiveEndsAtUtc : utcNow;

        DurationMonths += additionalMonths;
        BonusMonths += additionalBonusMonths;
        BaseEndsAtUtc = AddCalendarMonths(BaseEndsAtUtc, additionalMonths);
        EffectiveEndsAtUtc = AddCalendarMonths(anchor, additionalMonths + additionalBonusMonths);

        if (Status != SubscriptionStatus.Active)
            Status = SubscriptionStatus.Active;

        AddDomainEvent(new TenantPlanRenewedEvent(Id, PlanId));

        return Result.Updated;
    }

    /// <summary>
    /// Persists lazy expiration. Access decisions compare <see cref="EffectiveEndsAtUtc"/>
    /// directly and never depend on this having been called.
    /// </summary>
    public Result<Updated> MarkExpired(DateTime utcNow)
    {
        if (Status == SubscriptionStatus.Expired)
            return Result.Updated;

        if (Status != SubscriptionStatus.Active)
            return TenantPlanErrors.NotActive;

        if (utcNow < EffectiveEndsAtUtc)
            return TenantPlanErrors.NotYetExpired;

        Status = SubscriptionStatus.Expired;
        return Result.Updated;
    }

    public Result<Updated> Suspend()
    {
        if (Status != SubscriptionStatus.Active)
            return TenantPlanErrors.NotActive;

        Status = SubscriptionStatus.Suspended;
        return Result.Updated;
    }

    public Result<Updated> Reactivate(DateTime utcNow)
    {
        if (Status == SubscriptionStatus.Active)
            return TenantPlanErrors.AlreadyActive;

        if (Status is not (SubscriptionStatus.Suspended or SubscriptionStatus.Expired or SubscriptionStatus.Pending))
            return TenantPlanErrors.InvalidStateTransition(Status, "reactivate");

        if (utcNow >= EffectiveEndsAtUtc)
            return TenantPlanErrors.AlreadyExpired;

        Status = SubscriptionStatus.Active;
        return Result.Updated;
    }

    public Result<Updated> Cancel(DateTime utcNow)
    {
        if (Status == SubscriptionStatus.Cancelled)
            return TenantPlanErrors.AlreadyCancelledSubscription;

        if (Status == SubscriptionStatus.Expired || (Status == SubscriptionStatus.Active && utcNow >= EffectiveEndsAtUtc))
            return TenantPlanErrors.CannotCancelExpired;

        Status = SubscriptionStatus.Cancelled;

        AddDomainEvent(new Events.TenantPlanCancelledEvent(Id, PlanId));

        return Result.Updated;
    }

    /// <summary>EF navigation mutator for rehydration of the entitlement snapshot.</summary>
    internal void LoadFeatures(IEnumerable<TenantPlanFeature> features)
        => _features.AddRange(features);

    /// <summary>Calendar-month addition delegating to DateTime.AddMonths (clamping semantics).</summary>
    public static DateTime AddCalendarMonths(DateTime utcDate, int months) => utcDate.AddMonths(months);
}
