namespace Centerix.Domain.Platform.Billing.BillingCycles;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;
using Centerix.Domain.Platform.Subscriptions;

/// <summary>
/// A billable service period tied to a Subscription.
/// A Subscription may have one or more BillingCycles over its lifetime.
/// </summary>
public class BillingCycle : AuditableEntity<Guid>
{
    /// <summary>
    /// EXPLICIT domain rule: the minimum billable unit of a BillingCycle period is one whole
    /// calendar month. Centerix sells and bills subscriptions in whole months, so a positive
    /// period that never reaches the next calendar month (e.g. 15 Jan → 28 Jan) still bills
    /// exactly one month instead of a fraction of a month.
    ///
    /// This is a deliberate pricing rule — NOT a fallback for bad input. An empty, inverted or
    /// otherwise invalid period is rejected by <see cref="Create"/> and
    /// <see cref="ComputeBillableMonths"/> before this rule can ever apply.
    /// </summary>
    public const int MinimumBillableMonths = 1;

    public byte[] RowVersion { get; private set; } = null!;

    public Guid SubscriptionId { get; private set; }
    public Subscriptions.TenantPlan Subscription { get; private set; } = default!;

    public DateTime PeriodStart { get; private set; }
    public DateTime PeriodEnd { get; private set; }

    public BillingCycleStatus Status { get; private set; }

    private BillingCycle() { }

    private BillingCycle(
        Guid id,
        string tenantId,
        Guid subscriptionId,
        DateTime periodStart,
        DateTime periodEnd,
        BillingCycleStatus status)
        : base(id)
    {
        TenantId = tenantId;
        SubscriptionId = subscriptionId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Status = status;
    }

    public static Result<BillingCycle> Create(
        Guid id,
        string tenantId,
        Guid subscriptionId,
        DateTime periodStart,
        DateTime periodEnd)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BillingCycleErrors.TenantIdRequired;

        if (subscriptionId == Guid.Empty)
            return BillingCycleErrors.SubscriptionIdRequired;

        if (periodStart == default)
            return BillingCycleErrors.PeriodStartRequired;

        if (periodEnd == default)
            return BillingCycleErrors.PeriodEndRequired;

        if (periodEnd <= periodStart)
            return BillingCycleErrors.InvalidPeriod;

        return new BillingCycle(id, tenantId.Trim(), subscriptionId, periodStart, periodEnd, BillingCycleStatus.Draft);
    }

    /// <summary>
    /// Number of billable whole calendar months in <paramref name="periodStart"/> →
    /// <paramref name="periodEnd"/>, using the same calendar-month arithmetic the commercial
    /// chain uses everywhere else (<see cref="Subscriptions.TenantPlan.AddCalendarMonths"/> /
    /// <c>DateTime.AddMonths</c>): the count is
    /// <c>(end.Year − start.Year) × 12 + (end.Month − start.Month)</c>, i.e. month
    /// anniversaries are counted by calendar month and the day-of-month is not part of the
    /// count (Jan 31 → Feb 28 and Jan 1 → Feb 1 are both exactly 1 month; Jan 1 → Mar 1 is 2).
    ///
    /// Rules, in order:
    ///  1. An empty or inverted period is INVALID and returns
    ///     <see cref="BillingCycleErrors.InvalidPeriod"/> — it is never coerced to one month.
    ///  2. A period that spans at least one month boundary returns that calendar-month count.
    ///     Because periodEnd &gt; periodStart, the count can never be negative.
    ///  3. A positive period that stays inside one calendar month returns
    ///     <see cref="MinimumBillableMonths"/> — the explicit minimum-billable-unit rule.
    /// </summary>
    public static Result<int> ComputeBillableMonths(DateTime periodStart, DateTime periodEnd)
    {
        // Rule 1 — invalid periods are rejected, not silently billed as one month.
        if (periodEnd <= periodStart)
            return BillingCycleErrors.InvalidPeriod;

        var months = ((periodEnd.Year - periodStart.Year) * 12) + (periodEnd.Month - periodStart.Month);

        // Rule 2 — defensive: with periodEnd > periodStart the difference cannot be negative,
        // but if it ever were, that is an invalid period (an error), never "one free month".
        if (months < 0)
            return BillingCycleErrors.InvalidPeriod;

        // Rule 3 — explicit minimum billable unit for sub-month periods (months == 0).
        return months >= MinimumBillableMonths ? months : MinimumBillableMonths;
    }

    /// <summary>Billable whole months of this cycle's period (see <see cref="ComputeBillableMonths"/>).</summary>
    public Result<int> GetBillableMonths() => ComputeBillableMonths(PeriodStart, PeriodEnd);

    /// <summary>
    /// Billable whole months of this cycle INSIDE the subscription's paid term
    /// [StartsAtUtc, BaseEndsAtUtc].
    ///
    /// Three ordered rules:
    ///  1. Free time is never billed. Time before StartsAtUtc was never purchased and time from
    ///     BaseEndsAtUtc to EffectiveEndsAtUtc is bonus entitlement (free), so billable time is
    ///     the intersection of the period with [StartsAtUtc, BaseEndsAtUtc].
    ///  2. When that intersection is empty BUT the cycle reaches past EffectiveEndsAtUtc, the
    ///     cycle does not belong to the current entitlement at all — it is a later (renewal)
    ///     term and is billed on its own period.
    ///  3. Otherwise the cycle lies entirely in free time (before the start, or inside bonus
    ///     months only) and returns <see cref="BillingCycleErrors.NoBillablePeriod"/>: there is
    ///     nothing to bill, reported explicitly instead of silently producing a zero-amount or
    ///     one-month invoice.
    /// </summary>
    public Result<int> GetBillableMonthsFor(Subscriptions.TenantPlan subscription)
    {
        if (subscription is null)
            return BillingCycleErrors.SubscriptionIdRequired;

        var paidStart = PeriodStart < subscription.StartsAtUtc ? subscription.StartsAtUtc : PeriodStart;
        var paidEnd = PeriodEnd > subscription.BaseEndsAtUtc ? subscription.BaseEndsAtUtc : PeriodEnd;

        if (paidEnd > paidStart)
            return ComputeBillableMonths(paidStart, paidEnd);

        if (PeriodEnd > subscription.EffectiveEndsAtUtc)
            return ComputeBillableMonths(PeriodStart, PeriodEnd);

        return BillingCycleErrors.NoBillablePeriod;
    }

    /// <summary>
    /// CRITICAL: identifies the FULL-TERM cycle of a subscription by PERIOD IDENTITY, never by
    /// duration equality. Another cycle can easily span the same number of months as the
    /// subscription (a second 12-month cycle a year later, or a cycle that happens to be 12
    /// months long) without being the paid term of this subscription.
    ///
    /// The paid term of a subscription is exactly [StartsAtUtc, BaseEndsAtUtc] — BaseEndsAtUtc,
    /// NOT EffectiveEndsAtUtc, because bonus months are free entitlement and are never billed.
    /// Only that exact period may take Contract amounts verbatim.
    /// </summary>
    public bool IsFullTermFor(Subscriptions.TenantPlan? subscription)
        => subscription is not null
           && PeriodStart == subscription.StartsAtUtc
           && PeriodEnd == subscription.BaseEndsAtUtc;

    public Result<Updated> MarkInvoiced()
    {
        if (Status != BillingCycleStatus.Draft)
            return BillingCycleErrors.InvalidStateTransition(Status, "mark as invoiced");

        Status = BillingCycleStatus.Invoiced;
        return Result.Updated;
    }

    public Result<Updated> MarkPaid()
    {
        if (Status != BillingCycleStatus.Invoiced)
            return BillingCycleErrors.InvalidStateTransition(Status, "mark as paid");

        Status = BillingCycleStatus.Paid;
        return Result.Updated;
    }

    public Result<Updated> Cancel()
    {
        if (Status is BillingCycleStatus.Paid or BillingCycleStatus.Cancelled)
            return BillingCycleErrors.InvalidStateTransition(Status, "cancel");

        Status = BillingCycleStatus.Cancelled;
        return Result.Updated;
    }
}
